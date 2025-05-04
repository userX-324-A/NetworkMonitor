using System.Collections.Generic;
using System.ComponentModel; // Required for INotifyPropertyChanged

namespace NetworkMonitor.Shared.DTOs // Updated namespace
{
    /// <summary>
    /// Data Transfer Object for representing disk I/O statistics for a single process.
    /// Implements INotifyPropertyChanged for UI binding updates.
    /// </summary>
    public class ProcessDiskStatsDto : INotifyPropertyChanged
    {
        private int _processId;
        public int ProcessId
        {
            get => _processId;
            set { if (_processId != value) { _processId = value; OnPropertyChanged(nameof(ProcessId)); } }
        }

        private string _processName = string.Empty;
        public string ProcessName
        {
            get => _processName;
            set { if (_processName != value) { _processName = value; OnPropertyChanged(nameof(ProcessName)); } }
        }

        private long _totalBytesRead;
        public long TotalBytesRead
        {
            get => _totalBytesRead;
            set { if (_totalBytesRead != value) { _totalBytesRead = value; OnPropertyChanged(nameof(TotalBytesRead)); } }
        }

        private long _totalBytesWritten;
        public long TotalBytesWritten
        {
            get => _totalBytesWritten;
            set { if (_totalBytesWritten != value) { _totalBytesWritten = value; OnPropertyChanged(nameof(TotalBytesWritten)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Wrapper object expected from the /stats/disk API endpoint.
    /// </summary>
    public class AllProcessDiskStatsDto
    {
        // Ensure the property name "Stats" matches the JSON returned by the service API
        public List<ProcessDiskStatsDto> Stats { get; set; } = new List<ProcessDiskStatsDto>();
    }
} 