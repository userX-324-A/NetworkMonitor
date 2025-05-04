using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NetworkMonitorService.Data; // For IStatsRepository
using NetworkMonitorService.Domain; // For ProcessNetworkStats, ProcessDiskStats
using NetworkMonitorService.Models; // For DTOs, LogEntryBase

namespace NetworkMonitorService.Services;

public class StatsAggregatorService : IStatsAggregatorService
{
    private readonly ILogger<StatsAggregatorService> _logger;
    private readonly IStatsRepository _statsRepository;
    private readonly IMonitorControlService _monitorControlService;
    private readonly IConfiguration _configuration; // Keep if needed for config values like cache duration

    private Timer? _loggingTimer;
    private Timer? _dailyResetTimer;
    private readonly TimeSpan _logInterval = TimeSpan.FromMinutes(1); // Log every minute
    private readonly int _dataRetentionDays;
    private const int MaxDegreeOfParallelismForStatsProcessing = 4; // Limit concurrency

    // Core state moved from Worker
    private readonly ConcurrentDictionary<int, ProcessNetworkStats> _processStats = new();
    private readonly ConcurrentDictionary<int, ProcessDiskStats> _processDiskStats = new();
    private readonly ConcurrentDictionary<int, (string Name, DateTime Timestamp)> _processNameCache = new();
    private readonly TimeSpan _processNameCacheDuration = TimeSpan.FromMinutes(5); // Example, read from config if needed

    private CancellationToken _serviceStoppingToken = CancellationToken.None; // Add field for token

    public StatsAggregatorService(
        ILogger<StatsAggregatorService> logger,
        IStatsRepository statsRepository,
        IMonitorControlService monitorControlService,
        IConfiguration configuration)
    {
        _logger = logger;
        _statsRepository = statsRepository;
        _monitorControlService = monitorControlService;
        _configuration = configuration;

        _dataRetentionDays = _configuration.GetValue<int?>("DataRetentionDays") ?? 30;
        _logger.LogInformation("Aggregator: Data retention period set to {RetentionDays} days.", _dataRetentionDays);

        // Read cache duration from config if desired
        // _processNameCacheDuration = TimeSpan.FromMinutes(_configuration.GetValue<int?>("ProcessNameCacheMinutes") ?? 5);

        // Register reset handler
        _monitorControlService.TriggerResetTotalCounts = PerformManualReset;
        _logger.LogInformation("Aggregator: Reset action registered with MonitorControlService.");

        // Start timers (requires stopping token, which we don't have at construction)
        // Timers will be started by the consuming service (Worker)
        _logger.LogInformation("StatsAggregatorService created.");
    }

    // Method to start timers, called by Worker after getting the stopping token
    public void InitializeTimers(CancellationToken stoppingToken)
    {
        _serviceStoppingToken = stoppingToken;
        _logger.LogDebug("Aggregator: Initializing timers...");

        _loggingTimer?.Dispose(); // Dispose if re-initializing
        _loggingTimer = new Timer(LogStatsCallback, null, _logInterval, _logInterval);
        _logger.LogDebug("Aggregator: Logging timer initialized.");

        ScheduleDailyResetTimer(); // Initial schedule
        _logger.LogDebug("Aggregator: Daily reset timer scheduled.");

        _logger.LogInformation("Aggregator: Timers initialized.");
    }

    // --- Data Aggregation Methods (called by Worker's event handlers) ---

    public void AggregateNetworkEvent(object? sender, NetworkEventArgs e)
    {
        // Get or create the stats object for this process
        var stats = _processStats.GetOrAdd(e.ProcessId, pid =>
        {
            var newStats = new ProcessNetworkStats(pid);
            TryResolveProcessName(newStats); // Attempt initial resolution
            return newStats;
        });

        stats.UpdateStats(e.Size, e.IsSend, e.RemoteAddress.ToString());
    }

    public void AggregateDiskEvent(object? sender, DiskEventArgs e)
    {
        if (e.ProcessId == 0) return;
        var stats = _processDiskStats.GetOrAdd(e.ProcessId, id => new ProcessDiskStats(id));
        if (e.IsRead)
        {
            stats.AddBytesRead(e.IoSize, e.FileName);
        }
        else
        {
            stats.AddBytesWritten(e.IoSize, e.FileName);
        }
        // Optionally resolve name here if needed immediately: TryResolveProcessName(stats);
    }

    // --- Process Name Resolution (moved from Worker) ---
    private void TryResolveProcessName(object statsObj)
    {
        int processId = 0;
        Action<string> setName = (name) => {}; // Initialize to no-op instead of null
        string? currentName = null;

        if (statsObj is ProcessNetworkStats netStats)
        {
            processId = netStats.ProcessId;
            setName = (name) => netStats.ProcessName = name;
            currentName = netStats.ProcessName;
        }
        else if (statsObj is ProcessDiskStats diskStats)
        {
            processId = diskStats.ProcessId;
            setName = (name) => diskStats.ProcessName = name;
            currentName = diskStats.ProcessName;
        }
        else
        {
            _logger.LogWarning("Aggregator: TryResolveProcessName called with unexpected type: {Type}", statsObj?.GetType().Name);
            return;
        }

        if (processId == 0) return;

        if (_processNameCache.TryGetValue(processId, out var cacheEntry))
        {
            if ((DateTime.UtcNow - cacheEntry.Timestamp) < _processNameCacheDuration)
            {
                if (currentName == "Unknown" || currentName != cacheEntry.Name)
                {
                    setName?.Invoke(cacheEntry.Name);
                }
                return;
            }
            else
            {
                _logger.LogTrace("Aggregator: Cached name for PID {ProcessId} expired. Attempting re-resolution.", processId);
            }
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process != null && !string.IsNullOrEmpty(process.ProcessName))
            {
                string resolvedName = process.ProcessName ?? "Unknown";
                setName?.Invoke(resolvedName);
                var newCacheEntry = (Name: resolvedName, Timestamp: DateTime.UtcNow);
                _processNameCache.AddOrUpdate(processId, newCacheEntry, (key, oldEntry) => newCacheEntry);
            }
        }
        catch (ArgumentException)
        {
            _logger.LogDebug("Aggregator: Failed to resolve process name for PID {ProcessId} (Process likely exited).", processId);
            // Optional: Cache exit state?
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aggregator: Error resolving process name for PID {ProcessId}", processId);
            // Optional: Cache error state?
        }
    }

    // --- Logging Timer Logic (moved from Worker) ---
    private void LogStatsCallback(object? state)
    {
        _logger.LogInformation("Aggregator: Logging timer triggered.");
        _ = LogStatsAsync(_serviceStoppingToken); // Fire-and-forget
    }

    private async Task LogStatsAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Aggregator: --- Starting periodic log cycle --- ");
        var now = DateTime.Now; // Use UtcNow for consistency?
        var logEntries = new List<LogEntryBase>();

        _logger.LogInformation("Aggregator: Current Stats Counts - Network: {NetCount}, Disk: {DiskCount}", _processStats.Count, _processDiskStats.Count);
        LogMemoryUsage(); // Log GC info

        // Process Network Stats
        var networkStatsToLog = ProcessNetworkStatsForLogging(now);
        logEntries.AddRange(networkStatsToLog);
        _logger.LogDebug("Aggregator: Finished processing Network Stats. Found {ActiveCount} active processes.", networkStatsToLog.Count);

        // Process Disk Stats
        var diskStatsToLog = ProcessDiskStatsForLogging(now);
        logEntries.AddRange(diskStatsToLog);
        _logger.LogDebug("Aggregator: Finished processing Disk Stats. Found {ActiveCount} active processes.", diskStatsToLog.Count);

        // Database Persistence via Repository
        if (logEntries.Any())
        {
            _logger.LogInformation("Aggregator: Attempting to log {Count} entries via repository.", logEntries.Count);
            try
            {
                await _statsRepository.LogEntriesAsync(logEntries, stoppingToken);
                _logger.LogInformation("Aggregator: Successfully logged {Count} entries.", logEntries.Count);
                // Cleanup stale entries *after* successful logging
                CleanupStaleProcesses();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Aggregator: LogStatsAsync cancelled during shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aggregator: Failed to log stats batch via repository. Stats for this interval may be lost.");
                // Data loss occurred because interval stats were reset during processing.
            }
        }
        else
        {
            _logger.LogInformation("Aggregator: No activity detected in the log interval.");
             // Still run cleanup even if no activity logged, to remove exited processes
             CleanupStaleProcesses();
        }
        _logger.LogInformation("Aggregator: --- Finished periodic log cycle --- ");
    }

    private List<NetworkUsageLog> ProcessNetworkStatsForLogging(DateTime timestamp)
    {
        var logs = new List<NetworkUsageLog>();
        foreach (var kvp in _processStats)
        {
            var processId = kvp.Key;
            var stats = kvp.Value;
            if (stats.ProcessName == "Unknown") { TryResolveProcessName(stats); }

            var (intervalSent, intervalReceived, topIp) = stats.GetAndResetIntervalStats();

            if (intervalSent > 0 || intervalReceived > 0)
            {
                _logger.LogDebug("Aggregator: Logging Network activity for PID {ProcessId} ({ProcessName}): Sent={Sent}, Recv={Received}", processId, stats.ProcessName, intervalSent, intervalReceived);
                logs.Add(new NetworkUsageLog
                {
                    Timestamp = timestamp,
                    ProcessId = processId,
                    ProcessName = stats.ProcessName ?? "Unknown",
                    BytesSent = intervalSent,
                    BytesReceived = intervalReceived,
                    TopRemoteIpAddress = topIp
                });
            }
        }
        return logs;
    }

    private List<DiskActivityLog> ProcessDiskStatsForLogging(DateTime timestamp)
    {
        var logs = new List<DiskActivityLog>();
        foreach (var kvp in _processDiskStats)
        {
            var processId = kvp.Key;
            var stats = kvp.Value;
            if (stats.ProcessName == "Unknown") { TryResolveProcessName(stats); }

            var (intervalRead, intervalWritten, intervalReadOps, intervalWriteOps, topReadFile, topWriteFile) = stats.GetAndResetIntervalStats();

            if (intervalRead > 0 || intervalWritten > 0)
            {
                _logger.LogDebug("Aggregator: Logging Disk activity for PID {ProcessId} ({ProcessName}): Read={Read}, Written={Written}", processId, stats.ProcessName, intervalRead, intervalWritten);
                logs.Add(new DiskActivityLog
                {
                    Timestamp = timestamp,
                    ProcessId = processId,
                    ProcessName = stats.ProcessName ?? "Unknown",
                    BytesRead = intervalRead,
                    BytesWritten = intervalWritten,
                    ReadOperations = intervalReadOps,
                    WriteOperations = intervalWriteOps,
                    TopReadFile = topReadFile,
                    TopWriteFile = topWriteFile
                });
            }
        }
        return logs;
    }

    private void LogMemoryUsage()
    {
        try
        {
             var gcInfo = GC.GetGCMemoryInfo();
            _logger.LogDebug("Aggregator: GC Memory Info - HeapSize: {HeapSizeBytes}, Committed: {CommittedBytes}, MemoryLoad: {MemoryLoadBytes}, Available: {TotalAvailableBytes}, HighMemoryLoadThreshold: {HighMemoryLoadThresholdBytes}",
                gcInfo.HeapSizeBytes,
                gcInfo.TotalCommittedBytes,
                gcInfo.MemoryLoadBytes,
                gcInfo.TotalAvailableMemoryBytes,
                gcInfo.HighMemoryLoadThresholdBytes);
        }
         catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get GC memory info.");
        }
    }

    // --- Stale Process Cleanup (moved from Worker) ---
    private void CleanupStaleProcesses()
    {
        _logger.LogDebug("Aggregator: --- Starting Stale Process Cleanup --- ");
        int initialNetworkCount = _processStats.Count;
        int initialDiskCount = _processDiskStats.Count;
        int removedNetworkCount = 0;
        int removedDiskCount = 0;

        HashSet<int> allPids;
        try 
        { 
            allPids = Process.GetProcesses().Select(p => p.Id).ToHashSet(); 
        } 
        catch (Exception ex) 
        { 
            _logger.LogError(ex, "Aggregator: Failed to get current process list for cleanup."); 
            return; 
        }
        
        // Cleanup Network Stats
        var networkPidsToRemove = new List<int>();
        foreach (var kvp in _processStats)
        {
            int pid = kvp.Key;
            var stats = kvp.Value;

            // Condition 1: Process no longer exists
            bool processExists = allPids.Contains(pid);
            if (!processExists)
            {
                networkPidsToRemove.Add(pid);
                continue; // Move to the next process if this one is gone
            }

            // Condition 2: Network activity is zero (only if process still exists)
            if (stats.GetTotalBytesSent() == 0 && stats.GetTotalBytesReceived() == 0)
            {
                // Optionally log before removing based on zero activity
                // _logger.LogTrace("Marking network PID {pid} ({name}) for removal due to zero activity.", pid, stats.ProcessName);
                networkPidsToRemove.Add(pid);
            }
        }

        foreach (var pid in networkPidsToRemove)
        {
            if (_processStats.TryRemove(pid, out var removedStats))
            {
                _logger.LogDebug("Aggregator: Removed stale network process: PID {pid}, Name {Name}", pid, removedStats?.ProcessName ?? "N/A");
                removedNetworkCount++;
            }
        }

        // Cleanup Disk Stats
        var diskPidsToRemove = new List<int>();
        foreach (var kvp in _processDiskStats)
        {
            int pid = kvp.Key;
            var stats = kvp.Value;

            // Condition 1: Process no longer exists
            bool processExists = allPids.Contains(pid);
            if (!processExists)
            {
                 // _logger.LogTrace("Marking disk PID {pid} ({name}) for removal because process not found.", pid, stats.ProcessName);
                diskPidsToRemove.Add(pid);
                continue; // Move to the next process if this one is gone
            }

            // Condition 2: Disk activity is zero (only if process still exists)
            if (stats.GetTotalBytesRead() == 0 && stats.GetTotalBytesWritten() == 0)
            {
                 // _logger.LogTrace("Marking disk PID {pid} ({name}) for removal due to zero activity.", pid, stats.ProcessName);
                diskPidsToRemove.Add(pid);
            }
        }

        foreach (var pid in diskPidsToRemove)
        {
            if (_processDiskStats.TryRemove(pid, out var removedStats))
            {
                _logger.LogDebug("Aggregator: Removed stale disk process: PID {pid}, Name {Name}", pid, removedStats?.ProcessName ?? "N/A");
                removedDiskCount++;
            }
        }

        _logger.LogDebug("Finished stale process cleanup. Network: Initial={InitialNetwork}, Removed={RemovedNetwork}, Final={FinalNetwork}. Disk: Initial={InitialDisk}, Removed={RemovedDisk}, Final={FinalDisk}",
                         initialNetworkCount, removedNetworkCount, _processStats.Count,
                         initialDiskCount, removedDiskCount, _processDiskStats.Count);
    }

    // --- Daily Reset Logic (moved from Worker) ---
    private void ResetDailyTotalsCallback(object? state)
    {
        _logger.LogInformation("Aggregator: Executing daily reset callback...");

        // Reset totals in memory
        ResetAllTotalCounts();

        // Purge old database records via repository
        _ = PurgeOldDatabaseRecordsAsync(); // Fire-and-forget

        // Reschedule the timer for the next day
        ScheduleDailyResetTimer(); // Reschedule after execution
        _logger.LogInformation("Aggregator: Daily reset timer rescheduled.");
    }

    private async Task PurgeOldDatabaseRecordsAsync()
    {
        _logger.LogInformation("Aggregator: Triggering database purge task via repository...");
        try
        {
            await _statsRepository.PurgeOldRecordsAsync(_dataRetentionDays, _serviceStoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Aggregator: Database purge task was cancelled during shutdown.");
        }
        catch (Exception ex)
        {
            // Repository already logs errors
            _logger.LogError(ex, "Aggregator: Error occurred during repository purge task execution.");
        }
    }

    private void ScheduleDailyResetTimer()
    {
        if (_serviceStoppingToken.IsCancellationRequested) return; // Don't schedule if stopping

        try
        {
            var now = DateTime.Now;
            var midnight = now.Date.AddDays(1);
            var timeToMidnight = midnight - now;

            if (timeToMidnight <= TimeSpan.Zero)
            {
                timeToMidnight = TimeSpan.FromDays(1);
                _logger.LogWarning("Aggregator: Calculated time to midnight was negative/zero. Scheduling for 24 hours.");
            }

            _logger.LogInformation("Aggregator: Scheduling next daily reset in {TimeToMidnight}", timeToMidnight);

            _dailyResetTimer?.Dispose(); // Dispose previous before creating new
            _dailyResetTimer = new Timer(ResetDailyTotalsCallback, null, timeToMidnight, Timeout.InfiniteTimeSpan); // Fire once

            // Ensure timer is disposed on service stop
             _serviceStoppingToken.Register(() => 
             { 
                  _logger.LogInformation("Service stopping, disposing daily reset timer.");
                 _dailyResetTimer?.Dispose(); 
             }); 
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aggregator: Failed to schedule the daily reset timer.");
            _dailyResetTimer?.Dispose();
            _dailyResetTimer = null;
        }
    }

    // --- Manual Reset Logic (moved from Worker) ---
    public void PerformManualReset() // Interface method
    {
        _logger.LogInformation("Aggregator: --- Manual Reset Triggered --- ");
        ResetAllTotalCounts();
    }

    private void ResetAllTotalCounts()
    {
        _logger.LogInformation("Aggregator: Resetting total counters for all tracked processes...");
        try
        {
            int netCount = 0;
            foreach (var stats in _processStats.Values)
            {
                stats.ResetTotalCounts();
                netCount++;
            }
            _logger.LogInformation("Aggregator: Cleared totals for {Count} network stats.", netCount);

            int diskCount = 0;
            foreach (var stats in _processDiskStats.Values)
            {
                stats.ResetTotalCounts();
                diskCount++;
            }
            _logger.LogInformation("Aggregator: Cleared totals for {Count} disk stats.", diskCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Aggregator: Error occurred during total count reset operation.");
        }
    }

    // --- API Data Retrieval Methods (moved/adapted from Worker) ---
    public AllProcessStatsDto GetCurrentNetworkStatsDto()
    {
        var result = new AllProcessStatsDto();
        // Use ToList() for thread safety if ProcessName resolution modifies the collection concurrently (it shouldn't here)
        foreach (var kvp in _processStats.ToList()) 
        {
            var stats = kvp.Value;
            if (stats.ProcessName == "Unknown") { TryResolveProcessName(stats); }

            result.Stats.Add(new ProcessStatsDto
            {
                ProcessId = stats.ProcessId,
                ProcessName = stats.ProcessName,
                TotalBytesSent = stats.GetTotalBytesSent(),
                TotalBytesReceived = stats.GetTotalBytesReceived(),
            });
        }
        _logger.LogDebug("Aggregator: Returning DTO for {Count} network processes.", result.Stats.Count);
        return result;
    }

    public AllProcessDiskStatsDto GetCurrentDiskStatsDto()
    {
        var result = new AllProcessDiskStatsDto();
        // Use ToList() for thread safety
        foreach (var kvp in _processDiskStats.ToList())
        { 
            var stats = kvp.Value;
            if (stats.ProcessName == "Unknown") { TryResolveProcessName(stats); }

            result.Stats.Add(new ProcessDiskStatsDto
            {
                ProcessId = stats.ProcessId,
                ProcessName = stats.ProcessName,
                TotalBytesRead = stats.GetTotalBytesRead(),
                TotalBytesWritten = stats.GetTotalBytesWritten()
            });
        }
        _logger.LogDebug("Aggregator: Returning DTO for {Count} disk processes.", result.Stats.Count);
        return result;
    }

    // --- Disposal ---
    public void Dispose()
    {
        _logger.LogInformation("Disposing StatsAggregatorService...");
        _loggingTimer?.Dispose();
        _dailyResetTimer?.Dispose();
        // Unregister from control service? Not strictly necessary if singleton lifetime matches app.
        // if(_monitorControlService.TriggerResetTotalCounts == PerformManualReset)
        // { _monitorControlService.TriggerResetTotalCounts = null; }
        _logger.LogInformation("StatsAggregatorService disposed.");
        GC.SuppressFinalize(this);
    }
} 