using System.Threading;
using System.Collections.Concurrent;
using System; // Added for DateTime

namespace NetworkMonitorService
{
    public class ProcessNetworkStats
    {
        public int ProcessId { get; }
        public string ProcessName { get; set; } = "Unknown"; // Default until resolved

        // Total accumulated bytes since process start (or service observation start)
        private long _totalBytesSent;
        private long _totalBytesReceived;

        // Bytes accumulated since last DB log interval reset
        private long _intervalBytesSent;
        private long _intervalBytesReceived;

        // Track bytes per remote IP within the current interval
        public ConcurrentDictionary<string, (long BytesSent, long BytesReceived)> BytesPerRemoteIp { get; } = new();

        // Timestamp of the last network activity recorded for this process
        public DateTime LastActivityTimestamp { get; private set; } // Added Property

        public ProcessNetworkStats(int processId)
        {
            ProcessId = processId;
            // Initialize all counters to 0
            _totalBytesSent = 0;
            _totalBytesReceived = 0;
            _intervalBytesSent = 0;
            _intervalBytesReceived = 0;
            LastActivityTimestamp = DateTime.UtcNow; // Initialize timestamp
        }

        // Called by ETW handlers
        public void AddBytesSent(long bytes)
        {
            Interlocked.Add(ref _totalBytesSent, bytes);
            Interlocked.Add(ref _intervalBytesSent, bytes);
            LastActivityTimestamp = DateTime.UtcNow; // Update timestamp
        }

        // Called by ETW handlers
        public void AddBytesReceived(long bytes)
        {
            Interlocked.Add(ref _totalBytesReceived, bytes);
            Interlocked.Add(ref _intervalBytesReceived, bytes);
            LastActivityTimestamp = DateTime.UtcNow; // Update timestamp
        }

        // Method to update stats including remote IP
        public void UpdateStats(long bytes, bool isSend, string remoteIp)
        {
            if (string.IsNullOrEmpty(remoteIp)) return; // Don't track if IP is unknown/missing

            if (isSend)
            {
                Interlocked.Add(ref _totalBytesSent, bytes);
                Interlocked.Add(ref _intervalBytesSent, bytes);
                BytesPerRemoteIp.AddOrUpdate(remoteIp,
                    (bytes, 0), // Add new IP with sent bytes
                    (key, existingValue) => (existingValue.BytesSent + bytes, existingValue.BytesReceived)); // Update existing
            }
            else
            {
                Interlocked.Add(ref _totalBytesReceived, bytes);
                Interlocked.Add(ref _intervalBytesReceived, bytes);
                BytesPerRemoteIp.AddOrUpdate(remoteIp,
                    (0, bytes), // Add new IP with received bytes
                    (key, existingValue) => (existingValue.BytesSent, existingValue.BytesReceived + bytes)); // Update existing
            }

            LastActivityTimestamp = DateTime.UtcNow; // Update timestamp on any activity
        }

        // Called by database logging timer (LogNetworkStatsToDb)
        // Gets and resets INTERVAL counts AND determines/resets Top Remote IP
        public (long sent, long received, string? topIp) GetAndResetIntervalStats()
        {
            long currentSent = Interlocked.Exchange(ref _intervalBytesSent, 0);
            long currentReceived = Interlocked.Exchange(ref _intervalBytesReceived, 0);

            // Determine Top IP from snapshot before clearing
            string? topRemoteIp = null;
            if (!BytesPerRemoteIp.IsEmpty)
            {
                long maxBytes = -1;
                // Create a snapshot to avoid issues if dictionary is modified during iteration (though unlikely here)
                var ipSnapshot = BytesPerRemoteIp.ToArray(); 
                BytesPerRemoteIp.Clear(); // Clear the source dictionary after snapshot

                foreach (var kvp in ipSnapshot)
                {
                    long totalBytes = kvp.Value.BytesSent + kvp.Value.BytesReceived;
                    if (totalBytes > maxBytes)
                    {
                        maxBytes = totalBytes;
                        topRemoteIp = kvp.Key;
                    }
                }
            }
            // Else: if BytesPerRemoteIp was empty, topRemoteIp remains null

            return (currentSent, currentReceived, topRemoteIp);
        }

        // Called by pipe server handler (GetCurrentStatsForDto)
        // Gets TOTAL counts without resetting anything
        public long GetTotalBytesSent() => Volatile.Read(ref _totalBytesSent);
        public long GetTotalBytesReceived() => Volatile.Read(ref _totalBytesReceived);

        /// <summary>
        /// Atomically resets the total sent and received byte counters to zero.
        /// Typically called for periodic total resets (e.g., daily).
        /// </summary>
        public void ResetTotalCounts()
        {
            Interlocked.Exchange(ref _totalBytesSent, 0);
            Interlocked.Exchange(ref _totalBytesReceived, 0);
            // Note: Interval counters and IP stats are managed by GetAndResetIntervalStats
        }
    }
} 