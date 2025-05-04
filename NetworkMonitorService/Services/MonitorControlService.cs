using System;

namespace NetworkMonitorService.Services
{
    /// <summary>
    /// Interface for controlling the monitoring service (e.g., resetting counters).
    /// </summary>
    public interface IMonitorControlService
    {
        /// <summary>
        /// Action to trigger the reset of total counters in the monitoring worker.
        /// This will be set by the Worker service instance.
        /// </summary>
        Action? TriggerResetTotalCounts { get; set; }

        /// <summary>
        /// Executes the registered action to reset total counters.
        /// </summary>
        /// <returns>True if the action was registered and invoked, false otherwise.</returns>
        bool ResetTotalCounters();
    }

    /// <summary>
    /// Singleton service implementation for controlling the monitoring service.
    /// </summary>
    public class MonitorControlService : IMonitorControlService
    {
        public Action? TriggerResetTotalCounts { get; set; }

        public bool ResetTotalCounters()
        {
            if (TriggerResetTotalCounts != null)
            {
                TriggerResetTotalCounts.Invoke();
                return true;
            }
            return false;
        }
    }
} 