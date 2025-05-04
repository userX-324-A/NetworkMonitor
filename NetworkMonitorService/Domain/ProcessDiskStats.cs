using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace NetworkMonitorService.Domain;

// Moved from Worker.cs / Models folder
public class ProcessDiskStats
{
    public int ProcessId { get; }
    public string ProcessName { get; set; } = "Unknown";

    // Use fields suitable for Interlocked.Add (must be long)
    private long _totalBytesRead;
    private long _totalBytesWritten;
    private long _intervalBytesRead;
    private long _intervalBytesWritten;
    // <<< Added fields for Ops counts and File tracking
    private long _intervalReadOps;
    private long _intervalWriteOps;
    private readonly ConcurrentDictionary<string, long> _intervalReadFileCounts = new();
    private readonly ConcurrentDictionary<string, long> _intervalWriteFileCounts = new();

    public DateTime LastActivity { get; private set; }

    public ProcessDiskStats(int processId)
    {
        ProcessId = processId;
        LastActivity = DateTime.UtcNow;
        // Initialize fields to 0
        _totalBytesRead = 0;
        _totalBytesWritten = 0;
        _intervalBytesRead = 0;
        _intervalBytesWritten = 0;
        _intervalReadOps = 0; // Initialize Ops
        _intervalWriteOps = 0; // Initialize Ops
    }

    // <<< Modified to accept filename and track ops/files
    public void AddBytesRead(long bytes, string? fileName)
    {
        Interlocked.Add(ref _totalBytesRead, bytes);
        Interlocked.Add(ref _intervalBytesRead, bytes);
        Interlocked.Increment(ref _intervalReadOps);
        if (!string.IsNullOrEmpty(fileName)) // Only track non-empty filenames
        {
            _intervalReadFileCounts.AddOrUpdate(fileName, 1, (key, currentCount) => currentCount + 1);
        }
        LastActivity = DateTime.UtcNow;
    }

    // <<< Modified to accept filename and track ops/files
    public void AddBytesWritten(long bytes, string? fileName)
    {
        Interlocked.Add(ref _totalBytesWritten, bytes);
        Interlocked.Add(ref _intervalBytesWritten, bytes);
        Interlocked.Increment(ref _intervalWriteOps);
        if (!string.IsNullOrEmpty(fileName)) // Only track non-empty filenames
        {
            _intervalWriteFileCounts.AddOrUpdate(fileName, 1, (key, currentCount) => currentCount + 1);
        }
        LastActivity = DateTime.UtcNow;
    }

    // <<< Renamed, calculates/resets Ops and Top Files
    public (long readBytes, long writtenBytes, long readOps, long writeOps, string? topReadFile, string? topWriteFile) GetAndResetIntervalStats()
    {
        long currentReadBytes = Interlocked.Exchange(ref _intervalBytesRead, 0);
        long currentWrittenBytes = Interlocked.Exchange(ref _intervalBytesWritten, 0);
        long currentReadOps = Interlocked.Exchange(ref _intervalReadOps, 0);
        long currentWriteOps = Interlocked.Exchange(ref _intervalWriteOps, 0);

        // Determine Top Read File
        string? topReadFile = null;
        if (!_intervalReadFileCounts.IsEmpty)
        {
            // Take snapshot and clear atomically (or as close as possible)
            var readSnapshot = _intervalReadFileCounts.ToArray();
            _intervalReadFileCounts.Clear(); // Clear after snapshot
            topReadFile = readSnapshot.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key;
        }

        // Determine Top Write File
        string? topWriteFile = null;
        if (!_intervalWriteFileCounts.IsEmpty)
        {
            // Take snapshot and clear atomically
            var writeSnapshot = _intervalWriteFileCounts.ToArray();
            _intervalWriteFileCounts.Clear(); // Clear after snapshot
            topWriteFile = writeSnapshot.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key;
        }

        return (currentReadBytes, currentWrittenBytes, currentReadOps, currentWriteOps, topReadFile, topWriteFile);
    }

    public long GetTotalBytesRead() => Volatile.Read(ref _totalBytesRead);
    public long GetTotalBytesWritten() => Volatile.Read(ref _totalBytesWritten);

    /// <summary>
    /// Atomically resets the total read and written byte counters to zero.
    /// </summary>
    public void ResetTotalCounts()
    {
        Interlocked.Exchange(ref _totalBytesRead, 0);
        Interlocked.Exchange(ref _totalBytesWritten, 0);
        // Note: Interval counts are reset by GetAndResetIntervalStats
        // Note: Total Ops/File counts are not stored, only interval
    }
} 