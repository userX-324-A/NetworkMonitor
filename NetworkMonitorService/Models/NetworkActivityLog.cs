using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NetworkMonitorService.Models
{
    /// <summary>
    /// Represents a network activity log entry in the database.
    /// </summary>
    public class NetworkActivityLog : LogEntryBase
    {
        public long BytesSent { get; set; }

        public long BytesReceived { get; set; }

        [MaxLength(45)] // Max length for IPv4/IPv6 string
        public string? TopRemoteIpAddress { get; set; }
    }
} 