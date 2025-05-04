using System;

namespace NetworkMonitorService.Models
{
    /// <summary>
    /// Base class for all log entries stored in the database.
    /// Contains common properties like Timestamp, ProcessId, and ProcessName.
    /// </summary>
    public abstract class LogEntryBase
    {
        public int Id { get; set; } // Primary key
        public DateTime Timestamp { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = "Unknown"; // Default value
    }
} 