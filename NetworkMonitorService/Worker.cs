using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkMonitorService.Models; // Keep for DTOs
using System.Threading; 
using System.Threading.Tasks; 
using NetworkMonitorService.Services; // Keep for IEtwMonitorService, IStatsAggregatorService
using System;
using NetworkMonitorService.Domain; // +++ Add for ProcessDiskStats using

namespace NetworkMonitorService;

public class Worker : BackgroundService // Primary role: Orchestrate other services
{
    private readonly ILogger<Worker> _logger;
    private readonly IEtwMonitorService _etwMonitorService;
    private readonly IStatsAggregatorService _statsAggregatorService;

    public Worker(ILogger<Worker> logger,
                  IEtwMonitorService etwMonitorService,
                  IStatsAggregatorService statsAggregatorService) // Inject new service
    {
        _logger = logger;
        _etwMonitorService = etwMonitorService;
        _statsAggregatorService = statsAggregatorService; // Assign injected service

        // Subscribe to ETW events (remains here)
        _etwMonitorService.NetworkPacketSent += OnNetworkPacketSent;
        _etwMonitorService.NetworkPacketReceived += OnNetworkPacketReceived;
        _etwMonitorService.FileReadOperation += OnFileReadOperation;
        _etwMonitorService.FileWriteOperation += OnFileWriteOperation;
        _logger.LogInformation("Worker: Subscribed to ETW service events.");

    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker starting orchestration...");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker executing orchestration...");

        try
        {
            _logger.LogInformation("Worker: Initializing aggregator timers...");
            _statsAggregatorService.InitializeTimers(stoppingToken); // Pass token to aggregator

            _logger.LogInformation("Worker: Requesting ETW monitor service to start...");
            await _etwMonitorService.StartMonitoringAsync(stoppingToken);
            _logger.LogInformation("Worker: ETW monitor service started.");

            _logger.LogInformation("Worker orchestration running. Waiting for stop signal...");
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker execution canceled gracefully (stoppingToken requested).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception during worker orchestration. The service might stop.");
        }
        finally
        {
            _logger.LogInformation("Worker ExecuteAsync finishing orchestration.");
        }
    }

    // --- Event Handlers for EtwMonitorService (updated) ---
    private void OnNetworkPacketSent(object? sender, NetworkEventArgs e)
    {
        _statsAggregatorService.AggregateNetworkEvent(sender, e);
    }

    private void OnNetworkPacketReceived(object? sender, NetworkEventArgs e)
    {
        _statsAggregatorService.AggregateNetworkEvent(sender, e);
    }

    private void OnFileReadOperation(object? sender, DiskEventArgs e)
    {
        _statsAggregatorService.AggregateDiskEvent(sender, e);
    }

    private void OnFileWriteOperation(object? sender, DiskEventArgs e)
    {
        _statsAggregatorService.AggregateDiskEvent(sender, e);
    }
    // --- End Event Handlers ---

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker stopping orchestration...");
        // Unsubscribe events
        _etwMonitorService.NetworkPacketSent -= OnNetworkPacketSent;
        _etwMonitorService.NetworkPacketReceived -= OnNetworkPacketReceived;
        _etwMonitorService.FileReadOperation -= OnFileReadOperation;
        _etwMonitorService.FileWriteOperation -= OnFileWriteOperation;
        _logger.LogInformation("Worker: Unsubscribed from ETW service events.");

        // Explicitly stop the ETW monitor service
        _logger.LogInformation("Worker: Requesting ETW monitor service to stop...");
        try
        {
            // Pass the cancellationToken provided to StopAsync, allowing graceful shutdown within the host's timeout
            await _etwMonitorService.StopMonitoringAsync(); // Assuming StopMonitoringAsync accepts a CancellationToken or handles it internally via the token passed to Start
            _logger.LogInformation("Worker: ETW monitor service stopped.");
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Worker: Error stopping ETW monitor service.");
        }

        // No need to stop other services explicitly, DI handles disposal
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _logger.LogInformation("Worker disposing orchestration...");

        // Unsubscribe events again in case StopAsync wasn't called gracefully
        _etwMonitorService.NetworkPacketSent -= OnNetworkPacketSent;
        _etwMonitorService.NetworkPacketReceived -= OnNetworkPacketReceived;
        _etwMonitorService.FileReadOperation -= OnFileReadOperation;
        _etwMonitorService.FileWriteOperation -= OnFileWriteOperation;

        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
