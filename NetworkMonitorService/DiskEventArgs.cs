using System;

namespace NetworkMonitorService;

public class DiskEventArgs : EventArgs
{
    public DateTime Timestamp { get; }
    public int ProcessId { get; }
    public long IoSize { get; }
    public bool IsRead { get; }
    public string? FileName { get; }
    // Potential future additions: FileKey, Irp

    public DiskEventArgs(DateTime timestamp, int processId, long ioSize, bool isRead, string? fileName)
    {
        Timestamp = timestamp;
        ProcessId = processId;
        IoSize = ioSize;
        IsRead = isRead;
        FileName = fileName;
    }
} 