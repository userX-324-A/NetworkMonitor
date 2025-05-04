using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace NetworkMonitorService.Services;

public class EtwMonitorService : IEtwMonitorService
{
    private readonly ILogger<EtwMonitorService> _logger;
    private TraceEventSession? _etwSession;
    private TraceEventDispatcher? _etwSource;
    private Task? _etwProcessingTask;
    private CancellationTokenSource? _etwTaskCts;
    private CancellationToken _serviceStoppingToken; // Overall service token

    private Timer? _restartTimer;
    private readonly TimeSpan _restartInterval = TimeSpan.FromMinutes(60); // Restart ETW session every hour

    public event EventHandler<NetworkEventArgs>? NetworkPacketSent;
    public event EventHandler<NetworkEventArgs>? NetworkPacketReceived;
    public event EventHandler<DiskEventArgs>? FileReadOperation;
    public event EventHandler<DiskEventArgs>? FileWriteOperation;

    public EtwMonitorService(ILogger<EtwMonitorService> logger)
    {
        _logger = logger;
        _logger.LogInformation("EtwMonitorService created.");
    }

    public Task StartMonitoringAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ETW Monitoring starting...");
        _serviceStoppingToken = stoppingToken;

        // Ensure cleanup of any previous session artifacts, although constructor should be clean
        StopAndCleanupSessionResources();

        // Start the first ETW session
        _etwProcessingTask = StartNewEtwSessionAsync();

        // Setup timer for periodic restarts
        _logger.LogDebug("Setting up ETW restart timer...");
        // Dispose existing timer before creating a new one (important if StartMonitoringAsync could be called again)
        _restartTimer?.Dispose();
        _restartTimer = new Timer(RestartTimerCallback, null, _restartInterval, _restartInterval);
        // Register the timer for disposal when the service stops
        _serviceStoppingToken.Register(() =>
        {
            _logger.LogInformation("Service stopping, disposing ETW restart timer.");
            _restartTimer?.Dispose();
        });
        _logger.LogDebug("ETW restart timer set up.");

        _logger.LogInformation("ETW Monitoring startup sequence complete.");
        // Return the processing task so the caller can optionally await it (e.g., for initial setup completion)
        // Although background services typically don't await this directly.
        return _etwProcessingTask ?? Task.CompletedTask;
    }

    public async Task StopMonitoringAsync()
    {
        _logger.LogInformation("ETW Monitoring stopping asynchronously...");
        // Cancel the processing task first
        if (_etwTaskCts != null && !_etwTaskCts.IsCancellationRequested)
        {
            _logger.LogDebug("Cancelling current ETW processing task...");
            await _etwTaskCts.CancelAsync(); // Use async cancellation
        }

        // Wait briefly for the task to finish
        if (_etwProcessingTask != null && !_etwProcessingTask.IsCompleted)
        {
            _logger.LogDebug("Waiting briefly for current ETW task completion (max 2s). Status: {Status}", _etwProcessingTask.Status);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var completedTask = await Task.WhenAny(_etwProcessingTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (completedTask != _etwProcessingTask)
            {
                _logger.LogWarning("ETW processing task did not complete within the 2s cancellation timeout after StopMonitoringAsync.");
            }
            else
            {
                _logger.LogDebug("ETW processing task completed after cancellation signal during stop. Status: {Status}", _etwProcessingTask.Status);
            }
        }

        // Stop and dispose session resources
        StopAndCleanupSessionResources();
        _logger.LogInformation("ETW Monitoring stopped asynchronously.");
    }

    private Task StartNewEtwSessionAsync()
    {
        _logger.LogInformation("Starting new ETW monitoring session setup...");
        string sessionName = "NetworkMonitorServiceSession";

        // Clean up any residual resources *before* creating new ones
        StopAndCleanupSessionResources();

        try
        {
            _logger.LogDebug("Creating TraceEventSession...");
            // Ensure the session is created cleanly
            _etwSession = new TraceEventSession(sessionName, TraceEventSessionOptions.Create);
            _logger.LogDebug("TraceEventSession created.");

            // Create a new CTS linked to the main service stopping token for this specific session run
            _etwTaskCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceStoppingToken);

            // Register the main token to also cancel our specific task CTS if service stops
            _serviceStoppingToken.Register(() => _etwTaskCts?.Cancel());

            _logger.LogDebug("Enabling Kernel Providers (NetworkTCPIP, FileIO, FileIOInit)... Awaiting admin rights if first time.");
            _etwSession.EnableKernelProvider(
                KernelTraceEventParser.Keywords.NetworkTCPIP |
                KernelTraceEventParser.Keywords.FileIO |
                KernelTraceEventParser.Keywords.FileIOInit
            );
            _logger.LogDebug("Kernel Providers enabled.");

            _etwSource = _etwSession.Source;
            if (_etwSource == null) throw new InvalidOperationException("ETW Session Source is null after enabling providers.");
            _logger.LogDebug("ETW Session Source acquired.");

            var kernelSource = _etwSource.Kernel;
            if (kernelSource == null) throw new InvalidOperationException("Kernel Source is null after acquiring session source.");
            _logger.LogDebug("Kernel Source acquired.");

            // Subscribe handlers
            _logger.LogDebug("Subscribing to Kernel events...");
            kernelSource.TcpIpRecv += HandleTcpIpRecv;
            kernelSource.TcpIpSend += HandleTcpIpSend;
            kernelSource.FileIORead += HandleFileIoRead;
            kernelSource.FileIOWrite += HandleFileIoWrite;
            _logger.LogDebug("Kernel event subscriptions complete.");

            // Start processing on a background thread, passing the specific CTS token
            _logger.LogInformation("Starting ETW event processing task...");
            var processingTask = Task.Run(() =>
            {
                try
                {
                    _logger.LogInformation("ETW Task: Calling Source.Process()...");
                    _etwSource.Process(); // This blocks until stopped or session disposed
                    _logger.LogInformation("ETW Task: Source.Process() completed normally (session stopped or disposed).");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("ETW processing task cancelled (expected during restart or shutdown).");
                }
                catch (Exception ex)
                {
                    // Avoid logging ObjectDisposedException if it's due to cancellation/disposal
                    if (!(ex is ObjectDisposedException && (_etwTaskCts?.IsCancellationRequested ?? true)))
                    {
                         _logger.LogError(ex, "Error during ETW event processing in background task.");
                    } else {
                         _logger.LogInformation("ETW Task: ObjectDisposedException caught during cancellation/disposal, likely benign.");
                    }
                }
                finally
                {
                    _logger.LogInformation("ETW event processing task finished execution.");
                    // Cleanup after processing stops, regardless of reason
                    // StopAndCleanupSessionResources(); // Careful: Avoid recursion if called from Dispose/Stop
                }
            }, _etwTaskCts.Token); // Pass the specific token for this task

            return processingTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ETW session. Check permissions and previous session state.");
            StopAndCleanupSessionResources(); // Clean up on initialization failure
            return Task.FromException(ex);
        }
    }

    private void HandleTcpIpSend(TcpIpSendTraceData data)
    {
        // Raise event instead of calling UpdateStats
        NetworkPacketSent?.Invoke(this, new NetworkEventArgs(data.TimeStamp, data.ProcessID, data.size, isSend: true, data.daddr));
    }

    private void HandleTcpIpRecv(TcpIpTraceData data)
    {
        // Raise event instead of calling UpdateStats
        NetworkPacketReceived?.Invoke(this, new NetworkEventArgs(data.TimeStamp, data.ProcessID, data.size, isSend: false, data.saddr));
    }

    private void HandleFileIoRead(FileIOReadWriteTraceData data)
    {
        if (data.ProcessID == 0) return;
        // Raise event instead of interacting with _processDiskStats
        FileReadOperation?.Invoke(this, new DiskEventArgs(data.TimeStamp, data.ProcessID, data.IoSize, isRead: true, data.FileName));
    }

    private void HandleFileIoWrite(FileIOReadWriteTraceData data)
    {
        if (data.ProcessID == 0) return;
        // Raise event instead of interacting with _processDiskStats
        FileWriteOperation?.Invoke(this, new DiskEventArgs(data.TimeStamp, data.ProcessID, data.IoSize, isRead: false, data.FileName));
    }

    private void RestartTimerCallback(object? state)
    {
        _logger.LogInformation("Restart timer triggered. Initiating ETW session restart.");
        // Fire and forget - errors handled within RestartEtwSessionAsync
        _ = RestartEtwSessionAsync();
    }

    private async Task RestartEtwSessionAsync()
    {
        _logger.LogWarning("--- Attempting to restart ETW session --- ");
        try
        {
            // 1. Cancel the current processing task
            if (_etwTaskCts != null && !_etwTaskCts.IsCancellationRequested)
            {
                _logger.LogDebug("Cancelling current ETW processing task for restart...");
                _etwTaskCts.Cancel(); // Use synchronous cancel here to ensure signal is sent before proceeding
            }

            // 2. Wait briefly for the task to finish
            if (_etwProcessingTask != null && !_etwProcessingTask.IsCompleted)
            {
                _logger.LogDebug("Waiting briefly for current ETW task completion during restart (max 2s). Status: {Status}", _etwProcessingTask.Status);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                 // Link the timeout with the overall service stop token in case service stops during restart wait
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _serviceStoppingToken);
                try
                {
                     await _etwProcessingTask.WaitAsync(linkedCts.Token);
                     _logger.LogDebug("ETW processing task completed after cancellation signal during restart. Status: {Status}", _etwProcessingTask.Status);
                }
                catch(OperationCanceledException) // Includes TaskCanceledException
                {
                     if (_serviceStoppingToken.IsCancellationRequested)
                         _logger.LogWarning("Service shutdown requested while waiting for ETW task completion during restart.");
                     else if (timeoutCts.IsCancellationRequested)
                         _logger.LogWarning("ETW processing task did not complete within the 2s cancellation timeout during restart.");
                     else // Task itself was cancelled by _etwTaskCts.Cancel() - this is expected
                         _logger.LogDebug("ETW processing task cancelled as expected during restart.");
                }
                catch(Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error while waiting for ETW task completion during restart.");
                }
            }

            // 3. Stop and dispose the current session resources explicitly
            _logger.LogDebug("Stopping and disposing current ETW resources before restart...");
            StopAndCleanupSessionResources(); // This handles disposal and nulling out fields

            // Delay slightly before starting new session - helps if OS resources need freeing
            await Task.Delay(TimeSpan.FromMilliseconds(100), _serviceStoppingToken);
            if (_serviceStoppingToken.IsCancellationRequested)
            {
                 _logger.LogInformation("Service stopping during restart delay, aborting restart.");
                 return;
            }

            _logger.LogInformation("ETW Monitoring stopped. Starting new session after restart...");

            // 4. Start a new session
            _etwProcessingTask = StartNewEtwSessionAsync();

            _logger.LogInformation("--- New ETW session started successfully after restart --- ");
        }
        catch (OperationCanceledException) when (_serviceStoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("ETW session restart aborted due to service stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "--- Failed to restart ETW session --- ");
            // Ensure cleanup is attempted even if restart fails
            StopAndCleanupSessionResources();
             // Consider implications: monitoring will be stopped until next restart attempt or service restart.
        }
    }

    /// <summary>
    /// Stops ETW listening and disposes related resources. Safe to call multiple times.
    /// </summary>
    private void StopAndCleanupSessionResources()
    {
        _logger.LogDebug("Stopping ETW monitoring and cleaning up session resources...");

        // Note: No explicit lock here, assumes Dispose/StopMonitoringAsync/Restart don't run concurrently
        // in a problematic way. If race conditions observed, a lock might be needed around access.

        // 1. Unsubscribe events first
        if (_etwSource?.Kernel != null)
        {
            _logger.LogDebug("Unsubscribing from Kernel ETW events.");
            try { _etwSource.Kernel.TcpIpRecv -= HandleTcpIpRecv; } catch (Exception ex) { _logger.LogTrace(ex, "Ignoring error unsubscribing TcpIpRecv."); }
            try { _etwSource.Kernel.TcpIpSend -= HandleTcpIpSend; } catch (Exception ex) { _logger.LogTrace(ex, "Ignoring error unsubscribing TcpIpSend."); }
            try { _etwSource.Kernel.FileIORead -= HandleFileIoRead; } catch (Exception ex) { _logger.LogTrace(ex, "Ignoring error unsubscribing FileIORead."); }
            try { _etwSource.Kernel.FileIOWrite -= HandleFileIoWrite; } catch (Exception ex) { _logger.LogTrace(ex, "Ignoring error unsubscribing FileIOWrite."); }
        }

        // 2. Dispose the source (which should stop the Process() loop)
        if (_etwSource != null)
        {
            _logger.LogDebug("Disposing ETW Source...");
            try { _etwSource.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Error disposing ETW source."); }
            _etwSource = null;
        }

        // 3. Dispose the session
        if (_etwSession != null)
        {
            _logger.LogDebug("Disposing ETW Session...");
            try { _etwSession.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Error disposing ETW session."); }
            _etwSession = null;
        }

        // 4. Dispose the cancellation token source for the task
         if (_etwTaskCts != null)
        {
             _logger.LogDebug("Disposing ETW Task CTS...");
             try { _etwTaskCts.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Error disposing ETW task CTS."); }
            _etwTaskCts = null;
        }

        // 5. Null out the task reference (don't dispose the task itself)
        _etwProcessingTask = null;

        _logger.LogDebug("ETW session resource cleanup finished.");
    }


    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logger.LogInformation("Disposing EtwMonitorService...");
            // Stop monitoring and clean up ETW resources
            StopAndCleanupSessionResources();

            // Dispose timers
            _restartTimer?.Dispose();
            _restartTimer = null;
            _logger.LogInformation("EtwMonitorService disposed.");
        }
    }
} 