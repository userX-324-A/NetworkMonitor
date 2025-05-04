using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetworkMonitorService.Models; // Assuming DbContext and models are here initially
using System.Threading;

namespace NetworkMonitorService.Data;

public class StatsRepository : IStatsRepository
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StatsRepository> _logger;

    // Inject IServiceScopeFactory to create DbContext scopes
    public StatsRepository(IServiceScopeFactory scopeFactory, ILogger<StatsRepository> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task LogEntriesAsync(List<LogEntryBase> logEntries, CancellationToken cancellationToken = default)
    {
        if (!logEntries.Any())
        {
            _logger.LogDebug("No log entries provided to LogEntriesAsync.");
            return;
        }

        // Create a scope to resolve DbContext
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NetworkMonitorDbContext>();

        // Separate entries by type for bulk operations
        var networkEntries = logEntries.OfType<NetworkUsageLog>().ToList();
        var diskEntries = logEntries.OfType<DiskActivityLog>().ToList();

        _logger.LogDebug("Logging {NetworkCount} network entries and {DiskCount} disk entries via repository.", networkEntries.Count, diskEntries.Count);

        // Get the execution strategy
        var strategy = dbContext.Database.CreateExecutionStrategy();

        // Execute using the strategy
        await strategy.ExecuteAsync(async () =>
        {
            // Use a transaction *within* the strategy's execution
            using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                // Perform bulk operations within the transaction
                if (networkEntries.Any())
                {
                    await dbContext.BulkInsertOrUpdateAsync(networkEntries, cancellationToken: cancellationToken);
                    _logger.LogDebug("Repository: Bulk inserted/updated {Count} network entries.", networkEntries.Count);
                }

                if (diskEntries.Any())
                {
                    await dbContext.BulkInsertOrUpdateAsync(diskEntries, cancellationToken: cancellationToken);
                    _logger.LogDebug("Repository: Bulk inserted/updated {Count} disk entries.", diskEntries.Count);
                }

                await transaction.CommitAsync(cancellationToken);
                _logger.LogInformation("Repository: Successfully committed {TotalCount} log entries to the database.", logEntries.Count);
            }
            catch (OperationCanceledException) // Catch cancellation specifically
            {
                _logger.LogWarning("Repository: Database bulk operation was cancelled. Rolling back transaction.");
                await transaction.RollbackAsync(CancellationToken.None); // Use None for rollback if original token is cancelled
                // Don't re-throw cancellation exceptions upwards typically
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Repository: Database bulk operation failed. Rolling back transaction.");
                await transaction.RollbackAsync(CancellationToken.None); // Use None for rollback
                // Re-throw the exception so the caller (aggregator service) knows the operation failed
                throw;
            }
        });
    }

    public async Task PurgeOldRecordsAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Repository: Starting database purge task for records older than {RetentionDays} days...", retentionDays);

        // Create a scope to resolve DbContext
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NetworkMonitorDbContext>();

        try
        {
            // Calculate cutoff date
            var cutoffDate = DateTime.Now.AddDays(-retentionDays);
            _logger.LogDebug("Repository: Calculated purge cutoff date (UTC): {CutoffDate}", cutoffDate);

            // Purge DiskActivityLogs
            _logger.LogDebug("Repository: Purging DiskActivityLogs older than {CutoffDate}...", cutoffDate);
            var deletedDiskLogs = await dbContext.DiskActivityLogs
                .Where(log => log.Timestamp < cutoffDate)
                .ExecuteDeleteAsync(cancellationToken); // EF Core bulk delete
            _logger.LogInformation("Repository: Purged {Count} old records from DiskActivityLogs.", deletedDiskLogs);

            // Purge NetworkUsageLogs
            _logger.LogDebug("Repository: Purging NetworkUsageLogs older than {CutoffDate}...", cutoffDate);
            var deletedNetworkLogs = await dbContext.NetworkUsageLogs
                .Where(log => log.Timestamp < cutoffDate)
                .ExecuteDeleteAsync(cancellationToken); // EF Core bulk delete
            _logger.LogInformation("Repository: Purged {Count} old records from NetworkUsageLogs.", deletedNetworkLogs);

            _logger.LogInformation("Repository: Database purge task completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Repository: Error occurred during database purge task.");
            // Optionally re-throw or handle further if needed
        }
    }
} 