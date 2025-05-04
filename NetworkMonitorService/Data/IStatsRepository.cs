using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NetworkMonitorService.Models; // Assuming LogEntryBase is here
using System.Threading;

namespace NetworkMonitorService.Data;

public interface IStatsRepository
{
    /// <summary>
    /// Persists a batch of log entries (Network or Disk) to the database.
    /// </summary>
    /// <param name="logEntries">The list of log entries to save.</param>
    /// <param name="cancellationToken">Token to observe for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task LogEntriesAsync(List<LogEntryBase> logEntries, CancellationToken cancellationToken = default);

    /// <summary>
    /// Purges database records older than the specified retention period.
    /// </summary>
    /// <param name="retentionDays">The maximum age of records to keep, in days.</param>
    /// <param name="cancellationToken">Token to observe for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task PurgeOldRecordsAsync(int retentionDays, CancellationToken cancellationToken = default);
} 