using System;
using System.ComponentModel.DataAnnotations; // Required for [Key] attribute

namespace NetworkMonitorService.Models // Ensure this namespace matches the directory structure
{
    public class NetworkUsageLog : LogEntryBase // Inherit from LogEntryBase
    {
        // REMOVED Id, Timestamp, ProcessId, ProcessName (inherited from LogEntryBase)

        public long BytesSent { get; set; } // Delta for the interval

        public long BytesReceived { get; set; } // Delta for the interval

        public string? TopRemoteIpAddress { get; set; } // IP with most total bytes in the interval

        // REMOVED fields for Disk I/O
        // public long? BytesRead { get; set; } 
        // public long? BytesWritten { get; set; }
    }
} 