using NetworkMonitor.Shared.DTOs; // Updated using for DTOs
using System.Threading.Tasks;

namespace NetworkMonitorUI.Services // Create a Services sub-namespace
{
    /// <summary>
    /// Interface for interacting with the Network Monitor backend API.
    /// </summary>
    public interface IApiService
    {
        /// <summary>
        /// Checks if the API service is currently reachable.
        /// </summary>
        /// <returns>True if reachable, false otherwise.</returns>
        Task<bool> CheckServiceAvailabilityAsync();

        /// <summary>
        /// Fetches the latest network statistics for all processes.
        /// </summary>
        /// <returns>An AllProcessStatsDto containing the network stats, or null if an error occurs.</returns>
        Task<AllProcessStatsDto?> GetNetworkStatsAsync();

        /// <summary>
        /// Fetches the latest disk statistics for all processes.
        /// </summary>
        /// <returns>An AllProcessDiskStatsDto containing the disk stats, or null if an error occurs.</returns>
        Task<AllProcessDiskStatsDto?> GetDiskStatsAsync();

        /// <summary>
        /// Sends a request to reset the statistics totals on the server.
        /// </summary>
        /// <returns>True if the request was successful, false otherwise.</returns>
        Task<bool> ResetTotalsAsync();
    }
} 