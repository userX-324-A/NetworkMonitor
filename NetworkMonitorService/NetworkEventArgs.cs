using System;
using System.Net;

namespace NetworkMonitorService;

public class NetworkEventArgs : EventArgs
{
    public DateTime Timestamp { get; }
    public int ProcessId { get; }
    public int Size { get; }
    public bool IsSend { get; }
    public IPAddress RemoteAddress { get; }
    // Potential future additions: LocalAddress, LocalPort, RemotePort

    public NetworkEventArgs(DateTime timestamp, int processId, int size, bool isSend, IPAddress remoteAddress)
    {
        Timestamp = timestamp;
        ProcessId = processId;
        Size = size;
        IsSend = isSend;
        RemoteAddress = remoteAddress;
    }
} 