using System.Collections.Generic;
using System.ComponentModel; // Required for INotifyPropertyChanged

namespace NetworkMonitor.Shared.DTOs // Updated namespace
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

        // Note: DiskWrite seems out of place in a network stats DTO.
        // Consider removing it or creating a combined DTO if necessary.
        private long _diskWrite; 
        public long DiskWrite { get => _diskWrite; set { if (_diskWrite != value) { _diskWrite = value; OnPropertyChanged(nameof(DiskWrite)); } } }


        public event PropertyChangedEventHandler? PropertyChanged;

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