using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace NetworkMonitorService.Domain;

/// <summary>
/// Stores network statistics for a single process.
/// </summary>
public class ProcessNetworkStats
{
    public int ProcessId { get; }
    public string ProcessName { get; set; } = "Unknown";

    // Use fields suitable for Interlocked.Add (must be long)
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _intervalBytesSent;
    private long _intervalBytesReceived;
    private readonly ConcurrentDictionary<string, long> _intervalBytesPerRemoteIp = new();

    public DateTime LastActivity { get; private set; }

    public ProcessNetworkStats(int processId)
    {
        ProcessId = processId;
        LastActivity = DateTime.UtcNow;
        // Initialize fields to 0
        _totalBytesSent = 0;
        _totalBytesReceived = 0;
        _intervalBytesSent = 0;
        _intervalBytesReceived = 0;
    }

    // Updated method to handle remote IP tracking
    public void UpdateStats(int size, bool isSend, string remoteIp)
    {
        if (isSend)
        {
            Interlocked.Add(ref _totalBytesSent, size);
            Interlocked.Add(ref _intervalBytesSent, size);
        }
        else
        {
            Interlocked.Add(ref _totalBytesReceived, size);
            Interlocked.Add(ref _intervalBytesReceived, size);
        }

        // Track interval bytes per remote IP
        if (!string.IsNullOrEmpty(remoteIp))
        {
            _intervalBytesPerRemoteIp.AddOrUpdate(remoteIp, size, (key, currentBytes) => currentBytes + size);
        }
        LastActivity = DateTime.UtcNow;
    }

    // Renamed method, calculates and resets interval counts AND determines top IP
    public (long sent, long received, string? topRemoteIp) GetAndResetIntervalStats()
    {
        long currentSent = Interlocked.Exchange(ref _intervalBytesSent, 0);
        long currentReceived = Interlocked.Exchange(ref _intervalBytesReceived, 0);

        // Determine Top Remote IP based on bytes transferred in the interval
        string? topIp = null;
        if (!_intervalBytesPerRemoteIp.IsEmpty)
        {
            // Take snapshot and clear atomically
            var ipSnapshot = _intervalBytesPerRemoteIp.ToArray(); 
            _intervalBytesPerRemoteIp.Clear(); // Clear after snapshot
            topIp = ipSnapshot.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key;
        }

        return (currentSent, currentReceived, topIp);
    }

    public long GetTotalBytesSent() => Volatile.Read(ref _totalBytesSent);
    public long GetTotalBytesReceived() => Volatile.Read(ref _totalBytesReceived);

    /// <summary>
    /// Atomically resets the total sent and received byte counters to zero.
    /// </summary>
    public void ResetTotalCounts()
    {
        Interlocked.Exchange(ref _totalBytesSent, 0);
        Interlocked.Exchange(ref _totalBytesReceived, 0);
        // Note: Interval counts and IP tracking are reset by GetAndResetIntervalStats
    }
} 