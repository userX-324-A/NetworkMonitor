using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net; // Added for IPAddress
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using NetworkMonitorService.Models; // <<< Added using directive
using Microsoft.Extensions.DependencyInjection; // Added for IServiceScopeFactory
using Microsoft.EntityFrameworkCore; // Added for DbContext/logging errors
using EFCore.BulkExtensions; // <<< ADDING BACK using statement
// using NetworkMonitorService.Wfp; // Commented out - WfpController is in base namespace
using System.Text.Json; // For serialization in DTO
using System.Threading; // Added for Timer and Interlocked
using System.IO.Pipes; // Added for Named Pipes
using System.Text; // Added for Encoding
using System; 
using System.Threading.Tasks; 
using System.Linq; 
using System.Collections.Generic; // Added for List
using System.Runtime.InteropServices; // For DllImport
using NetworkMonitorService.Services; // <<< Add using for Services

namespace NetworkMonitorService;

// +++ Add ProcessDiskStats class definition +++
public class ProcessDiskStats
{
    public int ProcessId { get; }
    public string ProcessName { get; set; } = "Unknown";

    // Use fields suitable for Interlocked.Add (must be long)
    private long _totalBytesRead;
    private long _totalBytesWritten;
    private long _intervalBytesRead;
    private long _intervalBytesWritten;
    // <<< Added fields for Ops counts and File tracking
    private long _intervalReadOps;
    private long _intervalWriteOps;
    private readonly ConcurrentDictionary<string, long> _intervalReadFileCounts = new();
    private readonly ConcurrentDictionary<string, long> _intervalWriteFileCounts = new();

    public DateTime LastActivity { get; private set; }

    public ProcessDiskStats(int processId)
    {
        ProcessId = processId;
        LastActivity = DateTime.UtcNow;
        // Initialize fields to 0
        _totalBytesRead = 0;
        _totalBytesWritten = 0;
        _intervalBytesRead = 0;
        _intervalBytesWritten = 0;
    }

    // <<< Modified to accept filename and track ops/files
    public void AddBytesRead(long bytes, string? fileName)
    {
        Interlocked.Add(ref _totalBytesRead, bytes);
        Interlocked.Add(ref _intervalBytesRead, bytes);
        Interlocked.Increment(ref _intervalReadOps);
        if (!string.IsNullOrEmpty(fileName)) // Only track non-empty filenames
        {
            _intervalReadFileCounts.AddOrUpdate(fileName, 1, (key, currentCount) => currentCount + 1);
        }
        LastActivity = DateTime.UtcNow;
    }

    // <<< Modified to accept filename and track ops/files
    public void AddBytesWritten(long bytes, string? fileName)
    {
        Interlocked.Add(ref _totalBytesWritten, bytes);
        Interlocked.Add(ref _intervalBytesWritten, bytes);
        Interlocked.Increment(ref _intervalWriteOps);
        if (!string.IsNullOrEmpty(fileName)) // Only track non-empty filenames
        {
            _intervalWriteFileCounts.AddOrUpdate(fileName, 1, (key, currentCount) => currentCount + 1);
        }
        LastActivity = DateTime.UtcNow;
    }

    // <<< Renamed, calculates/resets Ops and Top Files
    // public (long read, long written) GetAndResetIntervalCounts()
    public (long readBytes, long writtenBytes, long readOps, long writeOps, string? topReadFile, string? topWriteFile) GetAndResetIntervalStats()
    {
        long currentReadBytes = Interlocked.Exchange(ref _intervalBytesRead, 0);
        long currentWrittenBytes = Interlocked.Exchange(ref _intervalBytesWritten, 0);
        long currentReadOps = Interlocked.Exchange(ref _intervalReadOps, 0);
        long currentWriteOps = Interlocked.Exchange(ref _intervalWriteOps, 0);

        // Determine Top Read File
        string? topReadFile = null;
        if (!_intervalReadFileCounts.IsEmpty)
        {
            var readSnapshot = _intervalReadFileCounts.ToArray();
            _intervalReadFileCounts.Clear();
            topReadFile = readSnapshot.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key;
        }

        // Determine Top Write File
        string? topWriteFile = null;
        if (!_intervalWriteFileCounts.IsEmpty)
        {
            var writeSnapshot = _intervalWriteFileCounts.ToArray();
            _intervalWriteFileCounts.Clear();
            topWriteFile = writeSnapshot.OrderByDescending(kvp => kvp.Value).FirstOrDefault().Key;
        }

        return (currentReadBytes, currentWrittenBytes, currentReadOps, currentWriteOps, topReadFile, topWriteFile);
    }

    public long GetTotalBytesRead() => Volatile.Read(ref _totalBytesRead);
    public long GetTotalBytesWritten() => Volatile.Read(ref _totalBytesWritten);

    /// <summary>
    /// Atomically resets the total read and written byte counters to zero.
    /// </summary>
    public void ResetTotalCounts()
    {
        Interlocked.Exchange(ref _totalBytesRead, 0);
        Interlocked.Exchange(ref _totalBytesWritten, 0);
    }
}
// +++ End ProcessDiskStats class definition +++

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceScopeFactory _scopeFactory; // Ensure this is injected
    private readonly IMonitorControlService _monitorControlService; // <<< Inject control service
    private TraceEventSession? _etwSession;
    private TraceEventDispatcher? _etwSource; // <<< Added field for ETW source
    private Timer? _loggingTimer;
    private Timer? _dailyResetTimer; // <<< Added timer for daily resets
    private readonly TimeSpan _logInterval = TimeSpan.FromMinutes(1); // Log every minute
    private readonly TimeSpan _cleanupInterval; // Cleanup entries older than 10 minutes
    private readonly IServiceProvider _serviceProvider; // Inject IServiceProvider to resolve DbContext scope
    private readonly ConcurrentDictionary<int, ProcessNetworkStats> _processStats = new();
    private readonly ConcurrentDictionary<int, ProcessDiskStats> _processDiskStats = new();
    private readonly TimeSpan _persistenceInterval = TimeSpan.FromMinutes(1); // Persist every 1 minute
    private readonly TimeSpan _inactivityThreshold = TimeSpan.FromMinutes(10); // Remove processes inactive for 10 minutes

    public Worker(ILogger<Worker> logger,
                  IServiceScopeFactory scopeFactory,
                  IServiceProvider serviceProvider,
                  IMonitorControlService monitorControlService) // <<< Add service to constructor
    {
        _logger = logger;
        _scopeFactory = scopeFactory; // Assign injected factory
        _monitorControlService = monitorControlService; // <<< Assign injected service
        _cleanupInterval = TimeSpan.FromMinutes(10); // Cleanup entries older than 10 minutes
        _serviceProvider = serviceProvider;

        // Register the reset action with the control service
        _monitorControlService.TriggerResetTotalCounts = PerformManualReset; // <<< Assign method to Action
        _logger.LogInformation("Worker created and reset action registered with MonitorControlService.");

        // Initialize and start timers
        // InitializeTimers(); // Removed call to non-existent method
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker starting...");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Initializing ETW session for network monitoring.");

        string sessionName = "NetworkMonitorServiceSession"; // Must be unique
        // TraceEventDispatcher? etwSource = null; // <<< Removed local variable declaration

        try
        {
            _logger.LogDebug("Creating TraceEventSession...");
            _etwSession = new TraceEventSession(sessionName, TraceEventSessionOptions.Create);
            _logger.LogDebug("TraceEventSession created. Is session null? {IsNull}", _etwSession == null);

            // Use a cancellation token source linked to the service stopping token
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            // Register cancellation callback to dispose the session and timer
            stoppingToken.Register(() =>
            {
                _logger.LogInformation("Stopping token received, disposing resources.");
                _loggingTimer?.Change(Timeout.Infinite, 0); // Stop the timer
                StopMonitoring();
                cts.Cancel(); // Signal cancellation to the processing task
            });

            _logger.LogDebug("Enabling Kernel Providers (NetworkTCPIP, FileIO, FileIOInit)...");
            // Enable Kernel Network Events AND File I/O Events
            _etwSession!.EnableKernelProvider(
                KernelTraceEventParser.Keywords.NetworkTCPIP |
                KernelTraceEventParser.Keywords.FileIO |       // Use FileIO for file name info
                KernelTraceEventParser.Keywords.FileIOInit    // Use FileIOInit for file name info
                );
            _logger.LogDebug("Kernel Providers enabled.");
            _logger.LogInformation("*** DIAGNOSTIC: Kernel providers enabled successfully. ***");

            // Check if Source is available
            if (_etwSession == null)
            {
                _logger.LogError("ETW Session is null before trying to access Source.");
                throw new InvalidOperationException("ETW Session became null unexpectedly.");
            }
            // etwSource = _etwSession.Source; // <<< Assign to class field instead
            _etwSource = _etwSession.Source;
            _logger.LogDebug("ETW Session Source acquired. Is source null? {IsNull}", _etwSource == null);

            if (_etwSource == null)
            {
                _logger.LogError("ETW Session Source is null, cannot subscribe to events.");
                throw new InvalidOperationException("ETW Session Source is null after enabling providers.");
            }

            // Check if Kernel source is available
            // var kernelSource = etwSource.Kernel; // <<< Use class field
            var kernelSource = _etwSource.Kernel;
             _logger.LogDebug("Kernel Source acquired. Is kernel source null? {IsNull}", kernelSource == null);

             if (kernelSource == null)
             {
                 _logger.LogError("Kernel Source is null, cannot subscribe to kernel events.");
                 throw new InvalidOperationException("Kernel Source is null after acquiring session source.");
             }

            // Set up direct event handlers
             _logger.LogDebug("Subscribing to Kernel events (TcpIpRecv, TcpIpSend)...");
            kernelSource.TcpIpRecv += HandleTcpIpRecv;
            kernelSource.TcpIpSend += HandleTcpIpSend;

            // Subscribe to specific Disk IO events
            _logger.LogDebug("Subscribing to FileIO events (FileIORead, FileIOWrite)...");
            kernelSource.FileIORead += HandleFileIoRead;   // Use FileIORead
            kernelSource.FileIOWrite += HandleFileIoWrite; // Use FileIOWrite

            _logger.LogInformation("*** DIAGNOSTIC: Subscribed FileIORead/Write handlers. ***");

             _logger.LogDebug("Kernel event subscriptions complete.");

            // Set up periodic logging timer
             _logger.LogDebug("Setting up logging timer...");
            _loggingTimer = new Timer(LogStats, null, _logInterval, _logInterval);
             _logger.LogDebug("Logging timer set up.");

            // Set up daily reset timer
            _logger.LogDebug("Setting up daily reset timer...");
            ScheduleDailyResetTimer(stoppingToken); // <<< Schedule the daily timer
            _logger.LogDebug("Daily reset timer scheduled.");

            // Process events on a separate thread
            _logger.LogInformation("Starting ETW event processing task...");
            var etwTask = Task.Run(() =>
            {
                // Capture the source for use in the task
                // var sourceToProcess = etwSource; // <<< Use class field directly, it's thread-safe
                try
                {
                     _logger.LogInformation("ETW Task: Checking if _etwSource is null..."); // <<< Use field
                     if (_etwSource != null) // <<< Use field
                     {
                         _logger.LogInformation("*** DIAGNOSTIC: ETW Task starting Source.Process()... ***");
                         _logger.LogInformation("ETW Task: Calling Source.Process()...");
                         _etwSource.Process(); // <<< Use field
                         _logger.LogInformation("ETW Task: Source.Process() completed normally.");
                         _logger.LogInformation("*** DIAGNOSTIC: ETW Task Source.Process() finished normally. ***");
                     }
                     else
                     {
                         _logger.LogError("ETW Task: _etwSource is null, cannot process events."); // <<< Use field
                     }
                }
                catch (OperationCanceledException) { 
                    _logger.LogInformation("ETW processing cancelled.");
                    _logger.LogWarning("*** DIAGNOSTIC: ETW Task Source.Process() cancelled. ***");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during ETW event processing.");
                    _logger.LogError(ex, "*** DIAGNOSTIC: ETW Task Source.Process() threw exception. ***");
                }
                finally
                {
                    _logger.LogInformation("ETW event processing stopped.");
                    _logger.LogInformation("*** DIAGNOSTIC: ETW Task finally block reached. ***");
                    // Ensure session is disposed if Process() exits unexpectedly
                    StopMonitoring(); 
                }
            }, cts.Token); 

            // Wait for only the ETW task to complete or cancellation
            await etwTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ETW session. Ensure the service runs with administrative privileges.");
            // Consider calling StopMonitoring here as well, although Dispose should handle it
            StopMonitoring(); // <<< Add cleanup on initialization failure
            return; // Exit if session creation fails
        }
        finally
        {
            // <<< No need to dispose _etwSource here, it's handled by StopAsync/Dispose -> StopMonitoring
             _logger.LogInformation("ExecuteAsync finished.");
        }
    }

    // --- Public methods for API access ---

    /// <summary>
    /// Retrieves the current snapshot of network statistics for all monitored processes.
    /// </summary>
    /// <returns>An AllProcessStatsDto containing the statistics.</returns>
    public AllProcessStatsDto GetCurrentStatsForDto()
    {
        var result = new AllProcessStatsDto();
        foreach (var kvp in _processStats)
        {
            var stats = kvp.Value;
            if (stats.ProcessName == "Unknown")
            { TryResolveProcessName(stats); }

            result.Stats.Add(new ProcessStatsDto
            {
                ProcessId = stats.ProcessId,
                ProcessName = stats.ProcessName,
                TotalBytesSent = stats.GetTotalBytesSent(), 
                TotalBytesReceived = stats.GetTotalBytesReceived(),
            });
        }
        return result;
    }

    /// <summary>
    /// Retrieves the current snapshot of disk I/O statistics for all monitored processes.
    /// </summary>
    /// <returns>An AllProcessDiskStatsDto containing the statistics.</returns>
    public AllProcessDiskStatsDto GetCurrentDiskStatsForDto()
    {
        var result = new AllProcessDiskStatsDto();
        foreach (var kvp in _processDiskStats) // Iterate through disk stats
        {
            var stats = kvp.Value;
            if (stats.ProcessName == "Unknown")
            { TryResolveProcessName(stats); } // Resolve name if needed

            result.Stats.Add(new ProcessDiskStatsDto
            {
                ProcessId = stats.ProcessId,
                ProcessName = stats.ProcessName,
                TotalBytesRead = stats.GetTotalBytesRead(), // Use method from ProcessDiskStats
                TotalBytesWritten = stats.GetTotalBytesWritten() // Use method from ProcessDiskStats
            });
        }
        return result;
    }

    // --- ETW Handling and Logging (mostly unchanged) ---

    private void HandleTcpIpSend(TcpIpSendTraceData data)
    {
        // Pass remote IP address (destination for send)
        UpdateStats(data.ProcessID, data.size, isSend: true, data.daddr.ToString());
    }

    private void HandleTcpIpRecv(TcpIpTraceData data)
    {
        // Pass remote IP address (source for receive)
        UpdateStats(data.ProcessID, data.size, isSend: false, data.saddr.ToString());
    }

    private void HandleFileIoRead(FileIOReadWriteTraceData data)
    {
        if (data.ProcessID == 0) return; // Ignore idle process or system events if necessary

        var stats = _processDiskStats.GetOrAdd(data.ProcessID, id => new ProcessDiskStats(id));
        // stats.AddBytesRead(data.IoSize); // <<< Pass filename
        stats.AddBytesRead(data.IoSize, data.FileName); // <<< Pass filename

        // TryResolveProcessName(stats); // Optionally resolve name here if needed immediately
    }

    private void HandleFileIoWrite(FileIOReadWriteTraceData data)
    {
        if (data.ProcessID == 0) return; // Ignore idle process or system events if necessary

        var stats = _processDiskStats.GetOrAdd(data.ProcessID, id => new ProcessDiskStats(id));
        // stats.AddBytesWritten(data.IoSize); // <<< Pass filename
        stats.AddBytesWritten(data.IoSize, data.FileName); // <<< Pass filename

        // TryResolveProcessName(stats); // Optionally resolve name here if needed immediately
    }

    // Centralized method to update statistics for a process
    private void UpdateStats(int processId, int size, bool isSend, string remoteIp)
    {
        // Get or create the stats object for this process
        var stats = _processStats.GetOrAdd(processId, pid => 
        {
            var newStats = new ProcessNetworkStats(pid);
            TryResolveProcessName(newStats); // Attempt initial resolution
            return newStats;
        });

        // Use the new method in ProcessNetworkStats that handles remote IP
        stats.UpdateStats(size, isSend, remoteIp);
    }

    // Modified TryResolveProcessName to accept ProcessDiskStats as well
    private void TryResolveProcessName(object statsObj)
    {
        int processId = 0;
        Action<string> setName = null;

        if (statsObj is ProcessNetworkStats netStats)
        {
            processId = netStats.ProcessId;
            setName = (name) => netStats.ProcessName = name;
        }
        else if (statsObj is ProcessDiskStats diskStats) // <<< Added handling for ProcessDiskStats
        {
            processId = diskStats.ProcessId;
            setName = (name) => diskStats.ProcessName = name;
        }
        else
        {
            _logger.LogWarning("TryResolveProcessName called with unexpected type: {Type}", statsObj?.GetType().Name);
            return;
        }


        if (processId == 0) return; // Don't try to resolve PID 0

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process != null && !string.IsNullOrEmpty(process.ProcessName))
            {
                // Use null-coalescing operator ?? to provide a default value if ProcessName is null
                setName?.Invoke(process.ProcessName ?? "Unknown"); // <<< Fix CS8600 warning
                // _logger.LogDebug($"Resolved PID {processId} to Name: {process.ProcessName}");
            }
        }
        catch (ArgumentException)
        {
            // Process likely exited, ignore or log as needed
             _logger.LogDebug("Failed to resolve process name for PID {ProcessId} (Process likely exited).", processId);
            // Optionally remove the entry here if the process has exited
            // _processStats.TryRemove(processId, out _);
             // Optionally set name to indicate exit? e.g., setName?.Invoke("<exited>");
        }
        catch (Exception ex) // Catch other potential exceptions (e.g., Win32Exception for access denied)
        {
            _logger.LogError(ex, "Error resolving process name for PID {ProcessId}", processId);
        }
    }

    // Renamed from LogNetworkStats to LogStats to include Disk IO
    // This is the Timer callback - must remain synchronous (void)
    private void LogStats(object? state)
    {
        _logger.LogInformation("Logging statistics...");
        // Trigger the async logging method without awaiting it here
        // Fire-and-forget pattern - errors logged within LogStatsAsync
        _ = LogStatsAsync(); 
    }

    // Contains the actual async logging logic
    private async Task LogStatsAsync()
    {
        _logger.LogInformation("--- Starting periodic log cycle ---");
        var now = DateTime.UtcNow;
        var logEntries = new List<LogEntryBase>(); // Base class or interface for common properties

        // +++ Log dictionary counts +++
        _logger.LogInformation("Current Stats Counts - Network: {NetCount}, Disk: {DiskCount}", _processStats.Count, _processDiskStats.Count);

        // +++ Log GC Memory Info +++
        var gcInfo = GC.GetGCMemoryInfo();
        _logger.LogInformation("GC Memory Info - HeapSize: {HeapSizeBytes}, Committed: {CommittedBytes}, MemoryLoad: {MemoryLoadBytes}, Available: {TotalAvailableBytes}, HighMemoryLoadThreshold: {HighMemoryLoadThresholdBytes}",
            gcInfo.HeapSizeBytes,
            gcInfo.TotalCommittedBytes,
            gcInfo.MemoryLoadBytes,
            gcInfo.TotalAvailableMemoryBytes,
            gcInfo.HighMemoryLoadThresholdBytes);


        // --- Process Network Stats ---
        _logger.LogDebug("Processing Network Stats for logging. Count: {Count}", _processStats.Count);
        var networkStatsToLog = new List<NetworkUsageLog>(); // <<< Correct class name
        List<int> inactiveNetworkPids = new();

        foreach (var kvp in _processStats)
        {
            var processId = kvp.Key;
            var stats = kvp.Value;

            // Resolve name if still unknown
            if (stats.ProcessName == "Unknown")
            {
                TryResolveProcessName(stats);
            }

            // Check for inactivity
            // Use LastActivityTimestamp from ProcessNetworkStats
            if ((now - stats.LastActivityTimestamp) > _inactivityThreshold)
            {
                inactiveNetworkPids.Add(processId);
                continue; // Don't log inactive processes immediately, remove them later
            }


            var (intervalSent, intervalReceived, topIp) = stats.GetAndResetIntervalStats(); // <<< Use new method

            // Only log if there was activity in the interval
            if (intervalSent > 0 || intervalReceived > 0)
            {
                 _logger.LogDebug("Logging Network activity for PID {ProcessId} ({ProcessName}): Sent={Sent}, Recv={Received}",
                     processId, stats.ProcessName, intervalSent, intervalReceived);

                var entry = new NetworkUsageLog
                {
                    Timestamp = now,
                    ProcessId = processId,
                    ProcessName = stats.ProcessName ?? "Unknown",
                    BytesSent = intervalSent,
                    BytesReceived = intervalReceived,
                    // Optionally log TotalBytes if needed, but interval is more common for time-series
                    // TotalBytesSent = stats.GetTotalBytesSent(),
                    // TotalBytesReceived = stats.GetTotalBytesReceived(),

                    // Aggregate and reset BytesPerRemoteIp
                    TopRemoteIpAddress = topIp // <<< Assign top IP from new method result
                };
                networkStatsToLog.Add(entry);
            } else {
                 _logger.LogTrace("No network activity in interval for PID {ProcessId} ({ProcessName}). Skipping log.", processId, stats.ProcessName);
            }
        }
        logEntries.AddRange(networkStatsToLog);

        // Remove inactive network processes
        foreach (var pid in inactiveNetworkPids)
        {
            if (_processStats.TryRemove(pid, out var removedStats))
            {
                _logger.LogInformation("Removed inactive network process: PID {ProcessId} ({ProcessName})", pid, removedStats.ProcessName);
            }
        }
        _logger.LogDebug("Finished processing Network Stats. Active: {ActiveCount}, Inactive Removed: {InactiveCount}", _processStats.Count, inactiveNetworkPids.Count);


        // --- Process Disk Stats ---
        _logger.LogDebug("Processing Disk Stats for logging. Count: {Count}", _processDiskStats.Count);
        var diskStatsToLog = new List<DiskActivityLog>(); // Assuming DiskActivityLog model exists // <<< Correct class name
        List<int> inactiveDiskPids = new();

        foreach (var kvp in _processDiskStats)
        {
            var processId = kvp.Key;
            var stats = kvp.Value; // This is ProcessDiskStats

            // Resolve name if still unknown
            if (stats.ProcessName == "Unknown")
            {
                TryResolveProcessName(stats);
            }

            // Check for inactivity (using LastActivity from ProcessDiskStats)
             if ((now - stats.LastActivity) > _inactivityThreshold)
            {
                inactiveDiskPids.Add(processId);
                continue; // Don't log inactive processes immediately, remove them later
            }


            var (intervalRead, intervalWritten, intervalReadOps, intervalWriteOps, topReadFile, topWriteFile) = stats.GetAndResetIntervalStats(); // <<< Use new method

            // Only log if there was activity in the interval
            if (intervalRead > 0 || intervalWritten > 0)
            {
                 _logger.LogDebug("Logging Disk activity for PID {ProcessId} ({ProcessName}): Read={Read}, Written={Written}",
                     processId, stats.ProcessName, intervalRead, intervalWritten);

                // Assuming a DiskLogEntry model exists similar to NetworkLogEntry // <<< Correct class name
                 var entry = new DiskActivityLog
                 {
                     Timestamp = now,
                     ProcessId = processId,
                     ProcessName = stats.ProcessName ?? "Unknown",
                     BytesRead = intervalRead,
                     BytesWritten = intervalWritten,
                     // <<< Populate Ops and Top Files
                     ReadOperations = intervalReadOps,
                     WriteOperations = intervalWriteOps,
                     TopReadFile = topReadFile,
                     TopWriteFile = topWriteFile
                     // Optionally log totals
                     // TotalBytesRead = stats.GetTotalBytesRead(),
                     // TotalBytesWritten = stats.GetTotalBytesWritten()
                 };
                 diskStatsToLog.Add(entry);
            } else {
                 _logger.LogTrace("No disk activity in interval for PID {ProcessId} ({ProcessName}). Skipping log.", processId, stats.ProcessName);
            }
        }
         logEntries.AddRange(diskStatsToLog); // Add disk logs to the common list

        // Remove inactive disk processes
        foreach (var pid in inactiveDiskPids)
        {
            if (_processDiskStats.TryRemove(pid, out var removedStats))
            {
                _logger.LogInformation("Removed inactive disk process: PID {ProcessId} ({ProcessName})", pid, removedStats.ProcessName);
            }
        }
         _logger.LogDebug("Finished processing Disk Stats. Active: {ActiveCount}, Inactive Removed: {InactiveCount}", _processDiskStats.Count, inactiveDiskPids.Count);

        // --- Database Persistence ---
        if (logEntries.Any())
        {
            _logger.LogInformation("Attempting to log {Count} entries to the database.", logEntries.Count);
            try // <<< Added try block
            {
                await LogToDatabaseAsync(logEntries); // Pass combined list
                _logger.LogInformation("Successfully logged {Count} entries to the database.", logEntries.Count);

                // <<< Clear dictionaries ONLY on successful logging (Now handled within respective loops using GetAndReset)
                // The GetAndResetIntervalCounts methods already reset the interval counts atomically.
                // The removal of inactive processes handles cleanup. No need to clear the entire dictionaries here.
                // _processStats.Clear(); // <<< REMOVED
                // _processDiskStats.Clear(); // <<< REMOVED
            }
            catch (Exception ex) // <<< Added catch block
            {
                _logger.LogError(ex, "Failed to log stats batch to the database. Stats for this interval may be lost or retried next cycle depending on GetAndReset behavior.");
                // IMPORTANT: GetAndResetIntervalCounts already reset the counters.
                // If the DB operation fails, the data for this interval IS LOST.
                // Consider alternative strategies if data loss on DB failure is unacceptable:
                // 1. Don't reset counts until AFTER successful DB write (more complex state management).
                // 2. Queue failed batches for retry (requires persistent queue).
            }
        }
        else
        {
            _logger.LogInformation("No activity detected in the log interval.");
        }
         _logger.LogInformation("--- Finished periodic log cycle ---");
    }

    private async Task LogToDatabaseAsync(List<LogEntryBase> logEntries)
    {
        if (!logEntries.Any())
        {
            _logger.LogDebug("No log entries to save to database.");
            return;
        }

        // Separate entries by type for bulk operations
        var networkEntries = logEntries.OfType<NetworkUsageLog>().ToList(); // <<< Correct class name
        var diskEntries = logEntries.OfType<DiskActivityLog>().ToList(); // Assuming DiskActivityLog inherits LogEntryBase // <<< Correct class name

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NetworkMonitorDbContext>();

        _logger.LogDebug("Logging {NetworkCount} network entries and {DiskCount} disk entries.", networkEntries.Count, diskEntries.Count);

        // Use a transaction for atomicity across different entry types // <<< Removed manual transaction
        // using var transaction = await dbContext.Database.BeginTransactionAsync();

        // <<< Get the execution strategy
        var strategy = dbContext.Database.CreateExecutionStrategy();

        // <<< Execute using the strategy
        await strategy.ExecuteAsync(async () => 
        { 
            // <<< Use a transaction *within* the strategy's execution
            using var transaction = await dbContext.Database.BeginTransactionAsync();
            try
            {
                // Perform bulk operations within the transaction
                if (networkEntries.Any())
                {
                    await dbContext.BulkInsertOrUpdateAsync(networkEntries);
                    _logger.LogDebug("Bulk inserted/updated {Count} network entries.", networkEntries.Count);
                }

                if (diskEntries.Any())
                {
                     // Ensure dbContext has DbSet<DiskLogEntry> configured // <<< Correct class name
                     // Ensure dbContext has DbSet<DiskActivityLog> configured // <<< Correct class name
                    await dbContext.BulkInsertOrUpdateAsync(diskEntries);
                     _logger.LogDebug("Bulk inserted/updated {Count} disk entries.", diskEntries.Count);
                }

                await transaction.CommitAsync();
                _logger.LogInformation("Successfully committed {TotalCount} log entries to the database.", logEntries.Count);
            }

            catch (Exception ex)
            {
                _logger.LogError(ex, "Database bulk operation failed. Rolling back transaction.");
                await transaction.RollbackAsync();
                // Re-throw the exception so LogStatsAsync knows the operation failed
                throw;
            }
        });
    }

    // Optional UDP Handlers
    // private void HandleUdpIpSend(UdpIpTraceData data)
    // {
    //     _logger.LogTrace($"PID: {data.ProcessID} UDP SENT {data.size} bytes to {data.daddr}:{data.dport}");
    // }
    //
    // private void HandleUdpIpRecv(UdpIpTraceData data)
    // {
    //     _logger.LogTrace($"PID: {data.ProcessID} UDP RECV {data.size} bytes from {data.saddr}:{data.sport}");
    // }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker stopping...");
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _logger.LogInformation("Worker disposing...");
        StopMonitoring(); // Ensure ETW is stopped and disposed
        _loggingTimer?.Dispose();
        _dailyResetTimer?.Dispose(); // <<< Dispose the daily reset timer
        base.Dispose();
        GC.SuppressFinalize(this); // Suppress finalization
    }

    private void StopMonitoring()
    {
        _logger.LogInformation("Stopping ETW monitoring session...");
        try
        {
            // Unsubscribe events first to prevent potential race conditions during disposal
            if (_etwSource?.Kernel != null)
            {
                _logger.LogDebug("Unsubscribing from Kernel ETW events.");
                _etwSource.Kernel.TcpIpRecv -= HandleTcpIpRecv;
                _etwSource.Kernel.TcpIpSend -= HandleTcpIpSend;
                _etwSource.Kernel.FileIORead -= HandleFileIoRead;
                _etwSource.Kernel.FileIOWrite -= HandleFileIoWrite;
            }
            else {
                 _logger.LogDebug("ETW Source or Kernel was null, skipping event unsubscription.");
            }

            // Dispose the source
            if (_etwSource != null)
            {
                 _logger.LogDebug("Disposing ETW Source...");
                _etwSource.Dispose();
                _etwSource = null; // Set to null after disposal
                 _logger.LogDebug("ETW Source disposed.");
            }

            // Dispose the session
            if (_etwSession != null)
            {
                 _logger.LogDebug("Disposing ETW Session...");
                _etwSession.Dispose();
                _etwSession = null; // Set to null after disposal
                 _logger.LogDebug("ETW Session disposed.");
            }
        }
        catch (Exception ex)
        {
            // Log errors during disposal but don't prevent other cleanup
            _logger.LogError(ex, "Error during ETW session disposal.");
        }
        _logger.LogInformation("ETW monitoring session stopped.");
    }

    public AllProcessStatsDto GetAllStats()
    {
        // Select from _processStats (which contains ProcessNetworkStats)
        var statsList = _processStats.Values.Select(s => new ProcessStatsDto // Create the DTO
        {
            ProcessId = s.ProcessId,
            ProcessName = s.ProcessName,
            TotalBytesSent = s.GetTotalBytesSent(), // Use getter method
            TotalBytesReceived = s.GetTotalBytesReceived(), // Use getter method
        }).ToList();

        return new AllProcessStatsDto { Stats = statsList };
    }

    // --- Helper Methods ---

    private string? GetProcessNameById(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process?.ProcessName;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Could not resolve name for PID {processId}");
            return null;
        }
    }

    // Helper to check for Admin privileges (Example only - needs robust implementation)
    // private static bool IsAdministrator()
    // {
    //     try
    //     {
    //         using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
    //         {
    //             var principal = new System.Security.Principal.WindowsPrincipal(identity);
    //             return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    //         }
    //     }
    //     catch (Exception ex)
    //     {
    //         // Log error
    //         Console.WriteLine($"Error checking admin status: {ex.Message}"); // Replace with logging
    //         return false;
    //     }
    // }

    // --- Timer Callbacks & Control ---

    private void ResetDailyTotalsCallback(object? state)
    {
        // This callback is only responsible for calling the reset logic
        // and rescheduling the timer.
        PerformManualReset(); // <<< Call the shared reset logic

        // Reschedule the timer for the next midnight
        try
        {
            ScheduleDailyResetTimer(CancellationToken.None); // Use CancellationToken.None as stoppingToken isn't available here directly
            _logger.LogInformation("Daily reset timer rescheduled for next midnight.");
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to reschedule daily reset timer from callback.");
             // Consider implications if rescheduling fails
        }
    }

    /// <summary>
    /// Performs the actual reset of total counters for all tracked processes.
    /// Can be called by the daily timer or manually via the control service.
    /// </summary>
    private void PerformManualReset()
    {
        _logger.LogInformation("--- Performing Reset of Total Counters and Clearing Process Lists ---"); // Updated log message
        try
        {
            int networkCount = _processStats.Count; // Get count before clearing
            _processStats.Clear(); // Clear the network stats dictionary
            _logger.LogInformation("Cleared {Count} processes from network stats.", networkCount);

            int diskCount = _processDiskStats.Count; // Get count before clearing
            _processDiskStats.Clear(); // Clear the disk stats dictionary
            _logger.LogInformation("Cleared {Count} processes from disk stats.", diskCount);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during reset/clear operation."); // Updated log message
        }
    }

    private void ScheduleDailyResetTimer(CancellationToken stoppingToken)
    {
        try
        {
            var now = DateTime.Now;
            var midnight = now.Date.AddDays(1); // Next midnight
            var timeToMidnight = midnight - now;

            if (timeToMidnight <= TimeSpan.Zero) // Should only happen if clock changes dramatically
            {
                timeToMidnight = TimeSpan.FromDays(1); // Schedule for tomorrow's midnight
                _logger.LogWarning("Calculated time to midnight was negative or zero. Scheduling for 24 hours from now.");
            }

            _logger.LogInformation("Scheduling next daily total counter reset in {TimeToMidnight}", timeToMidnight);

            // Dispose existing timer before creating a new one
             _dailyResetTimer?.Dispose();

            // Create and schedule the timer. It will fire once after 'timeToMidnight'.
            // Pass CancellationToken.None to the Timer constructor's state if stoppingToken is not needed in the callback itself
            _dailyResetTimer = new Timer(ResetDailyTotalsCallback, null, timeToMidnight, Timeout.InfiniteTimeSpan); // Fire once, then reschedule in callback

            // Ensure the timer is disposed if the service stops before it fires
            if (stoppingToken.CanBeCanceled)
            {
                stoppingToken.Register(() => _dailyResetTimer?.Dispose());
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to schedule the daily reset timer.");
             _dailyResetTimer?.Dispose(); // Ensure disposal on error
             _dailyResetTimer = null;
        }
    }
}
