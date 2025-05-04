using System;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkMonitorService.Services;

public interface IEtwMonitorService : IDisposable
{
    /// <summary>
    /// Raised when a TCP packet is sent.
    /// </summary>
    event EventHandler<NetworkEventArgs>? NetworkPacketSent;

    /// <summary>
    /// Raised when a TCP packet is received.
    /// </summary>
    event EventHandler<NetworkEventArgs>? NetworkPacketReceived;

    /// <summary>
    /// Raised when a file read operation occurs.
    /// </summary>
    event EventHandler<DiskEventArgs>? FileReadOperation;

    /// <summary>
    /// Raised when a file write operation occurs.
    /// </summary>
    event EventHandler<DiskEventArgs>? FileWriteOperation;

    /// <summary>
    /// Starts the ETW monitoring session and event processing.
    /// </summary>
    /// <param name="stoppingToken">A token to signal when the service should stop monitoring.</param>
    /// <returns>A task that represents the asynchronous start operation.</returns>
    Task StartMonitoringAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Asynchronously stops the ETW monitoring session.
    /// </summary>
    /// <returns>A task that represents the asynchronous stop operation.</returns>
    Task StopMonitoringAsync(); // Optional: May not be needed if Dispose handles all cleanup
} 