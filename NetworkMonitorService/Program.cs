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

// Initialize Serilog's static logger BEFORE building the host for bootstrap logging
// This allows logging issues during startup itself. It reads from appsettings.json.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information) // Default reasonable level
    .Enrich.FromLogContext()
    .WriteTo.Console() // Log essential startup messages to console
    .CreateBootstrapLogger(); // Use CreateBootstrapLogger for early logging

try
{
    Log.Information("Configuring Network Monitor Service host...");

    // Use WebApplication builder
    var builder = WebApplication.CreateBuilder(args);

    // Configure host to use Serilog, reading details from configuration
    builder.Host.UseSerilog((hostContext, services, loggerConfiguration) => {
        // Read the custom file size limit from the host's configuration
        long? fileSizeLimitBytes = null;
        if (hostContext.Configuration.GetValue<int?>("Logging:FileSizeLimitMB") is int fileSizeLimitMB && fileSizeLimitMB > 0)
        {
            fileSizeLimitBytes = fileSizeLimitMB * 1024 * 1024; // Convert MB to bytes
        }

        // Read base Serilog configuration (levels, console sink) from appsettings.json
        loggerConfiguration.ReadFrom.Configuration(hostContext.Configuration)
            .Enrich.FromLogContext(); // Enrich logs with context

        // Manually configure the File sink to apply the dynamic size limit
        // We read other file args from config for consistency
        loggerConfiguration.WriteTo.File(
            path: hostContext.Configuration["Serilog:WriteTo:1:Args:path"] ?? "Logs/networkmonitor-.log",
            rollingInterval: Enum.TryParse<RollingInterval>(hostContext.Configuration["Serilog:WriteTo:1:Args:rollingInterval"], true, out var interval) ? interval : RollingInterval.Day,
            rollOnFileSizeLimit: bool.TryParse(hostContext.Configuration["Serilog:WriteTo:1:Args:rollOnFileSizeLimit"], out var roll) ? roll : true,
            fileSizeLimitBytes: fileSizeLimitBytes, // Apply calculated size limit
            retainedFileCountLimit: int.TryParse(hostContext.Configuration["Serilog:WriteTo:1:Args:retainedFileCountLimit"], out var count) ? count : 31,
            outputTemplate: hostContext.Configuration["Serilog:WriteTo:1:Args:outputTemplate"] ?? "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
        );
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

    // Add controllers support (if using controllers)
    builder.Services.AddControllers();

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

    // Map controllers (if using controllers)
    app.MapControllers();

    // --- Define Minimal API Endpoints ---

    // Endpoint to get current stats
    app.MapGet("/stats", (Worker workerService) => workerService.GetAllStats());

    // Endpoint to get current disk stats
    app.MapGet("/stats/disk", (Worker workerService, ILogger<Program> logger) => // Inject ILogger
    {
        try
        {
            var stats = workerService.GetCurrentDiskStatsForDto();
            return Results.Ok(stats);
        }
        catch (Exception ex)
        {
            // Log the exception using Serilog
            logger.LogError(ex, "Error getting disk stats"); // Use injected logger
            return Results.Problem("An error occurred while fetching disk statistics.");
        }
    })
    .WithName("GetDiskStats")
    .WithTags("Stats") // Group with other stats endpoints
    .Produces<AllProcessDiskStatsDto>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status500InternalServerError);

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
