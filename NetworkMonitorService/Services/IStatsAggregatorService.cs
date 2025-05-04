using NetworkMonitorService.Models; // For DTOs
using System.Threading.Tasks; // For Task
using System.Threading; // For Timer/CancellationToken
using System;

namespace NetworkMonitorService.Services;

public interface IStatsAggregatorService : IDisposable
{
    // Methods to receive data from ETW service
    void AggregateNetworkEvent(object? sender, NetworkEventArgs e);
    void AggregateDiskEvent(object? sender, DiskEventArgs e);

    // Methods to provide data for APIs
    AllProcessStatsDto GetCurrentNetworkStatsDto();
    AllProcessDiskStatsDto GetCurrentDiskStatsDto();
    // Consider if GetAllStats() is still needed or if GetCurrent...Dtos cover it

    // Method for manual reset trigger
    void PerformManualReset();

    // Lifecycle methods (may not be needed if it's a singleton without complex startup)
    void InitializeTimers(CancellationToken stoppingToken);
    // Task StartAsync(CancellationToken cancellationToken);
    // Task StopAsync(CancellationToken cancellationToken);
} 