using System.Collections.Generic;
using System.ComponentModel; // Required for INotifyPropertyChanged

namespace NetworkMonitorService
{
    // Data Transfer Object for sending process stats via IPC
    public class ProcessStatsDto : INotifyPropertyChanged
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

        private long _totalBytesSent;
        public long TotalBytesSent
        {
            get => _totalBytesSent;
            set { if (_totalBytesSent != value) { _totalBytesSent = value; OnPropertyChanged(nameof(TotalBytesSent)); } }
        }

        private long _totalBytesReceived;
        public long TotalBytesReceived
        {
            get => _totalBytesReceived;
            set { if (_totalBytesReceived != value) { _totalBytesReceived = value; OnPropertyChanged(nameof(TotalBytesReceived)); } }
        }

        public long DiskRead { get; set; }
        public long DiskWrite { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        // Added null check for safety
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // Wrapper object to send a list of DTOs
    public class AllProcessStatsDto
    {
        public List<ProcessStatsDto> Stats { get; set; } = new List<ProcessStatsDto>();
    }
} 