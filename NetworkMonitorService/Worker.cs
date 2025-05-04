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
using Microsoft.Extensions.Configuration; // <<< Add for IConfiguration

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
    private readonly IConfiguration _configuration; // <<< Add IConfiguration field
    private TraceEventSession? _etwSession;
    private TraceEventDispatcher? _etwSource; // <<< Added field for ETW source
    private Timer? _loggingTimer;
    private Timer? _dailyResetTimer; // <<< Added timer for daily resets
    private Timer? _restartTimer;
    private readonly TimeSpan _logInterval = TimeSpan.FromMinutes(1); // Log every minute
    private readonly TimeSpan _restartInterval = TimeSpan.FromMinutes(15); //TimeSpan.FromHours(1); // Restart ETW session every hour
    private readonly IServiceProvider _serviceProvider; // Inject IServiceProvider to resolve DbContext scope
    private readonly ConcurrentDictionary<int, ProcessNetworkStats> _processStats = new();
    private readonly ConcurrentDictionary<int, ProcessDiskStats> _processDiskStats = new();
    private readonly ConcurrentDictionary<int, (string Name, DateTime Timestamp)> _processNameCache = new(); // <<< Cache field with Timestamp
    private readonly TimeSpan _processNameCacheDuration = TimeSpan.FromMinutes(5); // <<< Cache validity duration
    private readonly TimeSpan _persistenceInterval = TimeSpan.FromMinutes(1); // Persist every 1 minute
    private Task? _etwProcessingTask;
    private CancellationTokenSource? _etwTaskCts;
    private CancellationToken _serviceStoppingToken; // To store the main stopping token
    private int _dataRetentionDays; // <<< Field to store retention period

    public Worker(ILogger<Worker> logger,
                  IServiceScopeFactory scopeFactory,
                  IServiceProvider serviceProvider,
                  IMonitorControlService monitorControlService,
                  IConfiguration configuration) // <<< Add IConfiguration injection
    {
        _logger = logger;
        _scopeFactory = scopeFactory; // Assign injected factory
        _monitorControlService = monitorControlService; // <<< Assign injected service
        _serviceProvider = serviceProvider;
        _configuration = configuration; // <<< Assign injected configuration

        // Read data retention period
        _dataRetentionDays = _configuration.GetValue<int?>("DataRetentionDays") ?? 30; // Default to 30 if not found
        _logger.LogInformation("Data retention period set to {RetentionDays} days.", _dataRetentionDays);

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
        _logger.LogInformation("Worker executing...");
        _serviceStoppingToken = stoppingToken; // Store the token

        try
        {
            // Start initial ETW monitoring
            _etwProcessingTask = StartEtwMonitoringAsync(); 

            // Set up periodic logging timer
            _logger.LogDebug("Setting up logging timer...");
            _loggingTimer = new Timer(LogStats, null, _logInterval, _logInterval);
            _logger.LogDebug("Logging timer set up.");

            // Set up daily reset timer
            _logger.LogDebug("Setting up daily reset timer...");
            ScheduleDailyResetTimer(); 
            _logger.LogDebug("Daily reset timer scheduled.");

            // Set up periodic ETW restart timer
             _logger.LogDebug("Setting up ETW restart timer...");
             _restartTimer = new Timer(RestartTimerCallback, null, _restartInterval, _restartInterval);
             _logger.LogDebug("ETW restart timer set up.");

            // Keep the service alive while background tasks run
            _logger.LogInformation("Worker running. Waiting for stop signal...");
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // When Task.Delay is cancelled by stoppingToken
            _logger.LogInformation("Worker execution canceled gracefully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception during worker execution. The service might stop.");
            // Consider stopping the host application depending on the severity
            StopMonitoring(); // Ensure ETW cleanup even on unexpected errors
        }
        finally
        {
            _logger.LogInformation("Worker ExecuteAsync finishing.");
            // StopMonitoring() is called by Dispose, which should be triggered by host shutdown
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

    private Task StartEtwMonitoringAsync()
    {
        _logger.LogInformation("Starting new ETW monitoring session setup...");
        string sessionName = "NetworkMonitorServiceSession"; // Base name

        // Clean up any previous resources explicitly, though StopMonitoring should handle this
        _etwTaskCts?.Cancel(); // Cancel previous task if any
        _etwTaskCts?.Dispose();
        StopMonitoring(); // Ensure previous session is fully disposed

        try
        {
            _logger.LogDebug("Creating TraceEventSession...");
            _etwSession = new TraceEventSession(sessionName, TraceEventSessionOptions.Create);
            _logger.LogDebug("TraceEventSession created.");

            // Create a new CTS linked to the main service stopping token
            _etwTaskCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceStoppingToken);

            // Register cancellation for the main service token to also cancel our specific task CTS
            // This ensures shutdown signals propagate correctly
            _serviceStoppingToken.Register(() => _etwTaskCts?.Cancel()); 

            _logger.LogDebug("Enabling Kernel Providers (NetworkTCPIP, FileIO, FileIOInit)... Awaiting admin rights if first time.");
            // Enable Kernel Network Events AND File I/O Events
            _etwSession!.EnableKernelProvider(
                KernelTraceEventParser.Keywords.NetworkTCPIP |
                KernelTraceEventParser.Keywords.FileIO |       
                KernelTraceEventParser.Keywords.FileIOInit    
                );
            _logger.LogDebug("Kernel Providers enabled.");

            // Get the source
            _etwSource = _etwSession.Source;
            if (_etwSource == null)
            {
                throw new InvalidOperationException("ETW Session Source is null after enabling providers.");
            }
            _logger.LogDebug("ETW Session Source acquired.");

            // Get Kernel source
            var kernelSource = _etwSource.Kernel;
            if (kernelSource == null)
            {
                throw new InvalidOperationException("Kernel Source is null after acquiring session source.");
            }
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
            return Task.Run(() =>
            {
                try
                {
                    _logger.LogInformation("ETW Task: Calling Source.Process()...");
                    _etwSource.Process(); // This blocks until stopped or session disposed
                    _logger.LogInformation("ETW Task: Source.Process() completed normally.");
                }
                catch (OperationCanceledException) 
                {
                    _logger.LogInformation("ETW processing task cancelled (expected during restart or shutdown).");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during ETW event processing in background task.");
                    // Consider if session needs disposal here - StopMonitoring might be called by outer handler
                }
                finally
                {
                    _logger.LogInformation("ETW event processing task finished execution.");
                }
            }, _etwTaskCts.Token); // Pass the specific token for this task
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ETW session. Check permissions and previous session state.");
            StopMonitoring(); // Clean up on initialization failure
            // Return a completed task with error state or rethrow? 
            // Returning a faulted task allows ExecuteAsync to potentially log it if awaited (but we don't await now)
            return Task.FromException(ex); 
        }
    }

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
        string? currentName = null; // <<< Variable to hold current name

        if (statsObj is ProcessNetworkStats netStats)
        {
            processId = netStats.ProcessId;
            setName = (name) => netStats.ProcessName = name;
            currentName = netStats.ProcessName; // <<< Get current name
        }
        else if (statsObj is ProcessDiskStats diskStats) // <<< Added handling for ProcessDiskStats
        {
            processId = diskStats.ProcessId;
            setName = (name) => diskStats.ProcessName = name;
            currentName = diskStats.ProcessName; // <<< Get current name
        }
        else
        {
            _logger.LogWarning("TryResolveProcessName called with unexpected type: {Type}", statsObj?.GetType().Name);
            return;
        }


        if (processId == 0) return; // Don't try to resolve PID 0

        // <<< Check cache first
        if (_processNameCache.TryGetValue(processId, out var cacheEntry)) // <<< Get tuple
        {
            // Check if the cached entry is still valid
            if ((DateTime.UtcNow - cacheEntry.Timestamp) < _processNameCacheDuration)
            {
                // Optional: Add a check here to see if the process still exists
                // using Process.GetProcessById(processId) if PID reuse is a concern.
                // For simplicity, we'll use the cached value directly for now.
                if (currentName == "Unknown" || currentName != cacheEntry.Name) // Update if unknown or different
                {
                     setName?.Invoke(cacheEntry.Name);
                     // _logger.LogDebug($"Using fresh cached name for PID {processId}: {cacheEntry.Name}");
                }
                return; // Found fresh entry in cache, no need to query system
            }
            else
            {
                // Cache entry expired, will proceed to resolve below
                _logger.LogTrace("Cached name for PID {ProcessId} expired. Attempting re-resolution.", processId);
            }
        }

        // <<< If not in cache OR expired, try to resolve
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process != null && !string.IsNullOrEmpty(process.ProcessName))
            {
                // Use null-coalescing operator ?? to provide a default value if ProcessName is null
                string resolvedName = process.ProcessName ?? "Unknown"; // <<< Fix CS8600 warning & store name
                setName?.Invoke(resolvedName);
                // _logger.LogDebug($"Resolved PID {processId} to Name: {process.ProcessName}");

                // <<< Add or Update cache with current timestamp
                var newCacheEntry = (Name: resolvedName, Timestamp: DateTime.UtcNow);
                _processNameCache.AddOrUpdate(processId, newCacheEntry, (key, oldEntry) => newCacheEntry);
            }
            // <<< Else: Process exists but name is null/empty - maybe cache "Unknown" or specific marker?
            // else if (process != null) {
            //    _processNameCache.TryAdd(processId, "Unknown (Resolved Empty)"); // Example
            // }
        }
        catch (ArgumentException)
        {
            // Process likely exited, ignore or log as needed
             _logger.LogDebug("Failed to resolve process name for PID {ProcessId} (Process likely exited).", processId);
            // Optionally remove the entry here if the process has exited
            // _processStats.TryRemove(processId, out _);
            // Optionally set name to indicate exit? e.g., setName?.Invoke("<exited>");

             // <<< Cache that the process likely exited (optional, prevents repeated lookups)
             // _processNameCache.TryAdd(processId, ("<exited>", DateTime.UtcNow)); // Example with timestamp
        }
        catch (Exception ex) // Catch other potential exceptions (e.g., Win32Exception for access denied)
        {
            _logger.LogError(ex, "Error resolving process name for PID {ProcessId}", processId);
            // <<< Optionally cache error state?
            // _processNameCache.TryAdd(processId, ("<error>", DateTime.UtcNow)); // Example with timestamp
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

        foreach (var kvp in _processStats)
        {
            var processId = kvp.Key;
            var stats = kvp.Value;

            // Resolve name if still unknown
            if (stats.ProcessName == "Unknown")
            {
                TryResolveProcessName(stats);
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

        _logger.LogDebug("Finished processing Network Stats. Active: {ActiveCount}", _processStats.Count);


        // --- Process Disk Stats ---
        _logger.LogDebug("Processing Disk Stats for logging. Count: {Count}", _processDiskStats.Count);
        var diskStatsToLog = new List<DiskActivityLog>(); // Assuming DiskActivityLog model exists // <<< Correct class name

        foreach (var kvp in _processDiskStats)
        {
            var processId = kvp.Key;
            var stats = kvp.Value; // This is ProcessDiskStats

            // Resolve name if still unknown
            if (stats.ProcessName == "Unknown")
            {
                TryResolveProcessName(stats);
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

        _logger.LogDebug("Finished processing Disk Stats. Active: {ActiveCount}", _processDiskStats.Count);

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
                CleanupStaleProcesses();
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

    // +++ Add new cleanup method +++
    private void CleanupStaleProcesses()
    {
        _logger.LogDebug("--- Starting Stale Process Cleanup --- Current Counts - Network: {NetCount}, Disk: {DiskCount}", _processStats.Count, _processDiskStats.Count);
        int networkRemovedCount = 0;
        int diskRemovedCount = 0;

        // Use Keys.ToList() to avoid modifying the collection while iterating
        var currentNetworkPids = _processStats.Keys.ToList(); 
        foreach (var pid in currentNetworkPids)
        {
            if (pid == 0) continue; // Skip PID 0
            try
            {
                // Attempt to get the process. If it doesn't exist, ArgumentException is thrown.
                using var process = Process.GetProcessById(pid);
                // Optional: Could add checks here based on process.HasExited, but ArgumentException is sufficient for non-existence
            }
            catch (ArgumentException)
            {
                // Process doesn't exist, remove it from stats
                if (_processStats.TryRemove(pid, out var removedStats))
                {
                    _logger.LogInformation("Removing stale network process entry: PID {ProcessId} ({ProcessName})", pid, removedStats.ProcessName ?? "Unknown");
                    networkRemovedCount++;
                }
            }
            catch (Exception ex) // Catch other potential errors (e.g., permission denied)
            {
                _logger.LogWarning(ex, "Error checking process status for PID {ProcessId} during network cleanup. Entry will not be removed.", pid);
            }
        }

        var currentDiskPids = _processDiskStats.Keys.ToList();
        foreach (var pid in currentDiskPids)
        {
             if (pid == 0) continue; // Skip PID 0
            try
            {
                using var process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                // Process doesn't exist, remove it from disk stats
                if (_processDiskStats.TryRemove(pid, out var removedStats))
                {
                    _logger.LogInformation("Removing stale disk process entry: PID {ProcessId} ({ProcessName})", pid, removedStats.ProcessName ?? "Unknown");
                    diskRemovedCount++;
                }
            }
             catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking process status for PID {ProcessId} during disk cleanup. Entry will not be removed.", pid);
            }
        }

        _logger.LogDebug("--- Finished Stale Process Cleanup --- Removed Network: {NetworkRemoved}, Disk: {DiskRemoved}. Remaining - Network: {NetCount}, Disk: {DiskCount}", 
            networkRemovedCount, diskRemovedCount, _processStats.Count, _processDiskStats.Count);
    }
    // +++ End new cleanup method +++

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
        // Note: No explicit lock here, assumes Dispose and Restart don't run concurrently in a problematic way.
        // If race conditions observed, a lock might be needed around access to _etwSession/_etwSource/_etwTaskCts
        try
        {
            // Unsubscribe events first to prevent potential race conditions during disposal
            if (_etwSource?.Kernel != null)
            {
                _logger.LogDebug("Unsubscribing from Kernel ETW events.");
                try { _etwSource.Kernel.TcpIpRecv -= HandleTcpIpRecv; } catch { /* Ignore */ }
                try { _etwSource.Kernel.TcpIpSend -= HandleTcpIpSend; } catch { /* Ignore */ }
                try { _etwSource.Kernel.FileIORead -= HandleFileIoRead; } catch { /* Ignore */ }
                try { _etwSource.Kernel.FileIOWrite -= HandleFileIoWrite; } catch { /* Ignore */ }
            }
            else
            {
                _logger.LogDebug("ETW Source or Kernel was null, skipping event unsubscription.");
            }

            // Dispose the source
            if (_etwSource != null)
            {
                _logger.LogDebug("Disposing ETW Source...");
                try { _etwSource.Dispose(); } catch (Exception ex) { _logger.LogError(ex, "Error disposing ETW source."); }
                _etwSource = null; // Set to null after disposal attempt
                _logger.LogDebug("ETW Source disposal attempt finished.");
            }

            // Dispose the session
            if (_etwSession != null)
            {
                _logger.LogDebug("Disposing ETW Session...");
                try { _etwSession.Dispose(); } catch (Exception ex) { _logger.LogError(ex, "Error disposing ETW session."); }
                _etwSession = null; // Set to null after disposal attempt
                _logger.LogDebug("ETW Session disposal attempt finished.");
            }
        }
        catch (Exception ex)
        {
            // Log errors during disposal but don't prevent other cleanup
            _logger.LogError(ex, "Error during ETW session StopMonitoring routine.");
        }
        _logger.LogInformation("ETW monitoring session stop attempt finished.");
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
                _logger.LogDebug("Cancelling current ETW processing task...");
                _etwTaskCts.Cancel();
            }

            // 2. Wait briefly for the task to finish (optional but good practice)
            if (_etwProcessingTask != null && !_etwProcessingTask.IsCompleted)
            {
                _logger.LogDebug("Waiting briefly for current ETW task completion (max 2s). Status: {Status}", _etwProcessingTask.Status);
                // Use a timeout to avoid waiting forever if cancellation hangs
                var cancellationWaitTask = await Task.WhenAny(_etwProcessingTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                if (cancellationWaitTask != _etwProcessingTask)
                {
                     _logger.LogWarning("ETW processing task did not complete within the 2s cancellation timeout.");
                }
                else
                {
                    _logger.LogDebug("ETW processing task completed after cancellation signal. Status: {Status}", _etwProcessingTask.Status);
                }
            }

            // 3. Stop and dispose the current session resources explicitly
            // StopMonitoring handles disposal and nulling out fields.
            _logger.LogDebug("Stopping and disposing current ETW resources...");
            StopMonitoring();
            // Explicitly null out task/CTS after stopping
            _etwProcessingTask = null;
            _etwTaskCts?.Dispose(); // Dispose the CTS used by the task
            _etwTaskCts = null;

            _logger.LogInformation("ETW Monitoring stopped. Starting new session...");

            // 4. Start a new session 
            // StartEtwMonitoringAsync uses the _serviceStoppingToken field, which is already set
            _etwProcessingTask = StartEtwMonitoringAsync(); 

            _logger.LogInformation("--- New ETW session started successfully --- ");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "--- Failed to restart ETW session --- ");
            // Ensure cleanup is attempted even if restart fails
            StopMonitoring(); 
            _etwProcessingTask = null;
            _etwTaskCts?.Dispose();
            _etwTaskCts = null;
            // Consider implications: monitoring will be stopped until next restart attempt or service restart.
        }
    }

    private void ResetDailyTotalsCallback(object? state)
    {
        _logger.LogInformation("Executing daily reset callback...");

        // Reset in-memory dictionaries (existing logic)
        _logger.LogDebug("Resetting in-memory network/disk stat totals.");
        foreach (var stats in _processStats.Values)
        {
            stats.ResetTotalCounts();
        }
        foreach (var stats in _processDiskStats.Values)
        {
            stats.ResetTotalCounts();
        }
        _logger.LogInformation("In-memory totals reset.");

        // Purge old database records (new logic)
        _ = PurgeOldDatabaseRecordsAsync(); // Run async, don't wait for completion here

        // Reschedule the timer for the next day (existing logic)
        ScheduleDailyResetTimer();
        _logger.LogInformation("Daily reset timer rescheduled for next execution.");
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

            foreach (var stats in _processStats.Values)
            {
                stats.ResetTotalCounts();
            }
            _logger.LogInformation("Cleared totals for {Count} network stats.", _processStats.Count);

            foreach (var stats in _processDiskStats.Values)
            {
                stats.ResetTotalCounts();
            }
            _logger.LogInformation("Cleared totals for {Count} disk stats.", _processDiskStats.Count);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during reset/clear operation."); // Updated log message
        }
    }

    private void ScheduleDailyResetTimer()
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
            // The main stopping token (_serviceStoppingToken) can be used here if needed for registration
             if (_serviceStoppingToken.CanBeCanceled)
             {
                 _serviceStoppingToken.Register(() => _dailyResetTimer?.Dispose());
             }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to schedule the daily reset timer.");
             _dailyResetTimer?.Dispose(); // Ensure disposal on error
             _dailyResetTimer = null;
        }
    }

    // <<< New method to handle database purging asynchronously >>>
    private async Task PurgeOldDatabaseRecordsAsync()
    {
        _logger.LogInformation("Starting database purge task for records older than {RetentionDays} days...", _dataRetentionDays);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NetworkMonitorDbContext>();

            // Calculate cutoff date (UTC)
            var cutoffDate = DateTime.UtcNow.AddDays(-_dataRetentionDays);
            _logger.LogDebug("Calculated purge cutoff date (UTC): {CutoffDate}", cutoffDate);

            // Purge DiskActivityLogs
            _logger.LogDebug("Purging DiskActivityLogs older than {CutoffDate}...", cutoffDate);
            var deletedDiskLogs = await dbContext.DiskActivityLogs
                .Where(log => log.Timestamp < cutoffDate)
                .ExecuteDeleteAsync(); // EF Core bulk delete
            _logger.LogInformation("Purged {Count} old records from DiskActivityLogs.", deletedDiskLogs);

            // Purge NetworkUsageLogs
            _logger.LogDebug("Purging NetworkUsageLogs older than {CutoffDate}...", cutoffDate);
            var deletedNetworkLogs = await dbContext.NetworkUsageLogs
                .Where(log => log.Timestamp < cutoffDate)
                .ExecuteDeleteAsync(); // EF Core bulk delete
            _logger.LogInformation("Purged {Count} old records from NetworkUsageLogs.", deletedNetworkLogs);

            _logger.LogInformation("Database purge task completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during database purge task.");
        }
    }
}
