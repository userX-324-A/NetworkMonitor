using System.Collections.Concurrent;
using System.Threading;

namespace NetworkMonitorService.Models
{
    /// <summary>
    /// Tracks disk I/O statistics for a single process.
    /// Uses Interlocked for thread-safe updates.
    /// </summary>
    public class ProcessDiskStats
    {
        public int ProcessId { get; }
        // ProcessName can be updated if resolved later
        public string ProcessName { get; set; } = "Unknown"; 

        private long _totalBytesRead;
        private long _totalBytesWritten;
        private long _intervalBytesRead;
        private long _intervalBytesWritten;

        // Added counters for operations
        private long _totalReadOps;
        private long _totalWriteOps;
        private long _intervalReadOps;
        private long _intervalWriteOps;

        // Added dictionaries to track bytes per file within an interval
        // Key: FileName, Value: Bytes
        private readonly ConcurrentDictionary<string, long> _intervalBytesReadFile = new();
        private readonly ConcurrentDictionary<string, long> _intervalBytesWriteFile = new();

        public ProcessDiskStats(int processId)
        {
            ProcessId = processId;
        }

        /// <summary>
        /// Updates the disk I/O counters.
        /// </summary>
        /// <param name="size">The number of bytes read or written.</param>
        /// <param name="isRead">True if the operation was a read, false if it was a write.</param>
        /// <param name="fileName">The name of the file involved in the operation (optional).</param>
        public void UpdateStats(long size, bool isRead, string? fileName)
        {
            if (isRead)
            {
                Interlocked.Add(ref _totalBytesRead, size);
                Interlocked.Add(ref _intervalBytesRead, size);
                Interlocked.Increment(ref _totalReadOps);      // Increment read op count
                Interlocked.Increment(ref _intervalReadOps); // Increment interval read op count

                // Update file-specific read bytes if filename is provided
                if (!string.IsNullOrEmpty(fileName))
                {
                    _intervalBytesReadFile.AddOrUpdate(fileName, size, (key, existingValue) => existingValue + size);
                }
            }
            else
            {
                Interlocked.Add(ref _totalBytesWritten, size);
                Interlocked.Add(ref _intervalBytesWritten, size);
                Interlocked.Increment(ref _totalWriteOps);     // Increment write op count
                Interlocked.Increment(ref _intervalWriteOps); // Increment interval write op count

                // Update file-specific write bytes if filename is provided
                if (!string.IsNullOrEmpty(fileName))
                {
                    _intervalBytesWriteFile.AddOrUpdate(fileName, size, (key, existingValue) => existingValue + size);
                }
            }
        }

        /// <summary>
        /// Gets the total bytes read since monitoring started.
        /// </summary>
        public long GetTotalBytesRead() => Interlocked.Read(ref _totalBytesRead);

        /// <summary>
        /// Gets the total bytes written since monitoring started.
        /// </summary>
        public long GetTotalBytesWritten() => Interlocked.Read(ref _totalBytesWritten);

        /// <summary>
        /// Gets the bytes read/written, operation counts, and top files during the last interval
        /// and resets the interval counters and file tracking dictionaries.
        /// </summary>
        /// <returns>A tuple containing (intervalBytesRead, intervalBytesWritten, intervalReadOps, intervalWriteOps, topReadFile, topWriteFile).</returns>
        public (long BytesRead, long BytesWritten, long ReadOps, long WriteOps, string? TopReadFile, string? TopWriteFile) GetAndResetIntervalCounts()
        {
            long readBytes = Interlocked.Exchange(ref _intervalBytesRead, 0);
            long writtenBytes = Interlocked.Exchange(ref _intervalBytesWritten, 0);
            long readOps = Interlocked.Exchange(ref _intervalReadOps, 0);
            long writeOps = Interlocked.Exchange(ref _intervalWriteOps, 0);

            // Determine Top Read File
            string? topReadFile = null;
            long maxReadBytes = -1;
            var readFilesSnapshot = _intervalBytesReadFile.ToList(); // Snapshot for iteration
            _intervalBytesReadFile.Clear(); // Clear original dictionary
            foreach (var kvp in readFilesSnapshot)
            {
                if (kvp.Value > maxReadBytes)
                {
                    maxReadBytes = kvp.Value;
                    topReadFile = kvp.Key;
                }
            }

            // Determine Top Write File
            string? topWriteFile = null;
            long maxWriteBytes = -1;
            var writeFilesSnapshot = _intervalBytesWriteFile.ToList(); // Snapshot for iteration
            _intervalBytesWriteFile.Clear(); // Clear original dictionary
            foreach (var kvp in writeFilesSnapshot)
            {
                if (kvp.Value > maxWriteBytes)
                {
                    maxWriteBytes = kvp.Value;
                    topWriteFile = kvp.Key;
                }
            }

            return (readBytes, writtenBytes, readOps, writeOps, topReadFile, topWriteFile);
        }
    }
} 