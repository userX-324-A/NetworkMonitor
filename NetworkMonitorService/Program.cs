using NetworkMonitorService;
// Add necessary using statements
using NetworkMonitorService.Models;
using NetworkMonitorService.Data;
using NetworkMonitorService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting; // Required for CreateApplicationBuilder, AddWindowsService etc.
using System; // For TimeSpan
using Microsoft.AspNetCore.Builder; // Added for WebApplication
using Microsoft.AspNetCore.Http; // Added for Results
using Serilog; // Add Serilog namespace
using Serilog.Events; // Required for LogEventLevel
using Serilog.Debugging; // <<< Add for SelfLog
using System.IO; // <<< Add for Path.Combine
using NetworkMonitorService.Configuration; // <<< Add using for SerilogConfigurator

// Initialize Serilog's static logger BEFORE building the host for bootstrap logging
// This allows logging issues during startup itself. It reads from appsettings.json.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information) // Default reasonable level
    .Enrich.FromLogContext()
    .WriteTo.Console() // Log essential startup messages to console
    .CreateBootstrapLogger(); // Use CreateBootstrapLogger for early logging

// <<< Enable Serilog SelfLog to output internal errors to Console.Error >>>
// Change Console.Error to a file path if needed: SelfLog.Enable(writer => File.AppendAllText("C:\Path\To\SelfLog.txt", writer));
SelfLog.Enable(Console.Error);

try
{
    Log.Information("Configuring Network Monitor Service host...");

    // Use WebApplication builder
    var builder = WebApplication.CreateBuilder(args);

    // Configure host to use Serilog
    builder.Host.UseSerilog((hostContext, services, loggerConfiguration) => {
        SerilogConfigurator.ConfigureSerilog(hostContext, loggerConfiguration); // <<< Use the new helper method
    });

    // Configure Windows Service options (if running as Windows Service)
    // Ensure the Microsoft.Extensions.Hosting.WindowsServices package is referenced in csproj
    builder.Services.AddWindowsService(options =>
    {
        // Set the Service Name (as it will appear in services.msc)
        options.ServiceName = "NetworkMonitorService";
    });

    // Get the connection string
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

    // Validate connection string
    if (string.IsNullOrEmpty(connectionString))
    {
        // Use Serilog's static logger for fatal errors before host runs
        Log.Fatal("Connection string 'DefaultConnection' not found or is empty in configuration.");
        throw new InvalidOperationException("Connection string 'DefaultConnection' not found or is empty in configuration.");
    }

    // Register DbContext with SQL Server provider
    builder.Services.AddDbContext<NetworkMonitorDbContext>(options =>
        options.UseSqlServer(connectionString, sqlServerOptions =>
        {
            sqlServerOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null);
        }));

    // Register the Worker service itself as a Singleton AND Hosted Service
    // This allows injection AND ensures ExecuteAsync runs.
    builder.Services.AddSingleton<Worker>();
    builder.Services.AddHostedService<Worker>(p => p.GetRequiredService<Worker>());

    // Register the MonitorControlService as a Singleton
    builder.Services.AddSingleton<IMonitorControlService, MonitorControlService>();

    // +++ Register the EtwMonitorService as a Singleton +++
    builder.Services.AddSingleton<IEtwMonitorService, EtwMonitorService>();

    // +++ Register the StatsRepository as Scoped (or Singleton if preferred, handles own scope) +++
    // Let's use Singleton here as it manages its own scope via IServiceScopeFactory
    builder.Services.AddSingleton<IStatsRepository, StatsRepository>();

    // +++ Register the StatsAggregatorService as a Singleton +++
    builder.Services.AddSingleton<IStatsAggregatorService, StatsAggregatorService>();

    // Add API explorer and Swagger services
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "NetworkMonitorService API", Version = "v1" });
    });

    // Build the WebApplication
    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "NetworkMonitorService API v1"));
    }

    // --- Define Minimal API Endpoints ---

    // Endpoint to get current network stats
    app.MapGet("/stats", (IStatsAggregatorService aggregator) => aggregator.GetCurrentNetworkStatsDto())
        .WithName("GetNetworkStats")
        .WithTags("Stats")
        .Produces<AllProcessStatsDto>(StatusCodes.Status200OK);

    // Endpoint to get current disk stats
    app.MapGet("/stats/disk", (IStatsAggregatorService aggregator, ILogger<Program> logger) =>
    {
        try
        {
            var stats = aggregator.GetCurrentDiskStatsDto();
            return Results.Ok(stats);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting disk stats via API");
            return Results.Problem("An error occurred while fetching disk statistics.");
        }
    })
    .WithName("GetDiskStats")
    .WithTags("Stats")
    .Produces<AllProcessDiskStatsDto>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status500InternalServerError);

    // Endpoint to manually reset total counters
    app.MapPost("/reset-totals", (IMonitorControlService controlService, ILogger<Program> logger) =>
    {
        logger.LogInformation("API request received to reset total counters.");
        bool triggered = controlService.ResetTotalCounters();
        if (triggered)
        {
            logger.LogInformation("ResetTotalCounters action successfully triggered via MonitorControlService.");
            return Results.Ok("Total counters reset triggered successfully.");
        }
        else
        {
            logger.LogWarning("Failed to trigger reset via MonitorControlService. Trigger action might not be registered.");
            return Results.NotFound("Reset action could not be triggered. Service might not be ready or action not registered.");
        }
    })
    .WithName("ResetTotalCounters")
    .WithTags("Control")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status404NotFound);

    // --- Optional: Auto-migration ---
    // Uncomment this block if you want the service to attempt to apply migrations on startup.
    // Use with caution, especially in production. Ensure the DB user has DDL permissions.
    /*
    try
    {
        Log.Information("Attempting to apply database migrations..."); // Use Serilog's static logger
        using (var scope = app.Services.CreateScope()) // Use app.Services after app is built
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NetworkMonitorDbContext>();
            dbContext.Database.Migrate(); // Apply pending migrations
        }
        Log.Information("Database migrations applied successfully (or no pending migrations found).");
    }
    catch (Exception ex)
    {
        // Log this critical error - the service might not function correctly if DB schema is wrong
        Log.Fatal(ex, "CRITICAL ERROR: Failed to apply database migrations"); // Use Serilog's static logger
        // Depending on policy, you might want to prevent the host from running:
        // return; // Or throw
    }
    */
    // --- End Optional Auto-migration ---

    Log.Information("Starting Network Monitor Service application..."); // Log before Run()
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Network Monitor Service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
