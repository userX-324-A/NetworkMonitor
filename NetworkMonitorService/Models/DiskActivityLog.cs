using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NetworkMonitorService.Models
{
    /// <summary>
    /// Represents a disk activity log entry in the database.
    /// </summary>
    public class DiskActivityLog : LogEntryBase // Inherit from LogEntryBase
    {
        /// <summary>
        /// Total bytes read during the interval.
        /// </summary>
        public long BytesRead { get; set; }

        /// <summary>
        /// Total bytes written during the interval.
        /// </summary>
        public long BytesWritten { get; set; }

        /// <summary>
        /// Number of read operations during the interval.
        /// </summary>
        public long ReadOperations { get; set; } // Keep original name from refactoring

        /// <summary>
        /// Number of write operations during the interval.
        /// </summary>
        public long WriteOperations { get; set; } // Keep original name from refactoring

        /// <summary>
        /// The file path most frequently read during the interval.
        /// </summary>
        public string? TopReadFile { get; set; }

        /// <summary>
        /// The file path most frequently written to during the interval.
        /// </summary>
        public string? TopWriteFile { get; set; }

        // Add other relevant aggregated metrics if needed, e.g., 
        // - Average read size
        // - Average write size
        // - Number of files created
        // - Number of files deleted
    }
} 