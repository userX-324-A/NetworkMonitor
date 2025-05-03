using System.Collections.Generic;
using System.ComponentModel; // Required for potential future INotifyPropertyChanged, though not strictly needed for basic DTO

namespace NetworkMonitorService
{
    /// <summary>
    /// Data Transfer Object for representing disk I/O statistics for a single process.
    /// </summary>
    public class ProcessDiskStatsDto // Consider adding INotifyPropertyChanged if UI binding is planned
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public long TotalBytesRead { get; set; }
        public long TotalBytesWritten { get; set; }
        // Add other relevant stats if needed, e.g., IntervalBytesRead/Written
    }

    /// <summary>
    /// Wrapper object to send a list of ProcessDiskStatsDto objects via API.
    /// </summary>
    public class AllProcessDiskStatsDto
    {
        public List<ProcessDiskStatsDto> Stats { get; set; } = new List<ProcessDiskStatsDto>();
    }
} 