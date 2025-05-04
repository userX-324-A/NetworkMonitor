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
        // Read base Serilog configuration (levels, console sink etc.) first
        loggerConfiguration.ReadFrom.Configuration(hostContext.Configuration)
            .Enrich.FromLogContext();

        // --- Programmatically configure File Sink with absolute path --- 
        try 
        { 
            // Get Base Directory
            var baseDirectory = AppContext.BaseDirectory;

            // Read relative path and other args from config (adjust index [1] if File is not the second sink)
            var relativePath = hostContext.Configuration["Serilog:WriteTo:1:Args:path"] ?? "Logs\\networkmonitor-.log";
            var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath)); // Ensure path is absolute and canonical

            // Create directory if it doesn't exist
            var logDirectory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
                Log.Information("Created log directory: {LogDirectory}", logDirectory); // Use bootstrap logger
            }

            // Read other arguments (provide defaults)
            var rollInterval = Enum.TryParse<RollingInterval>(hostContext.Configuration["Serilog:WriteTo:1:Args:rollingInterval"], true, out var interval) ? interval : RollingInterval.Day;
            var rollOnSize = bool.TryParse(hostContext.Configuration["Serilog:WriteTo:1:Args:rollOnFileSizeLimit"], out var roll) ? roll : true;
            var fileSizeLimit = long.TryParse(hostContext.Configuration["Serilog:WriteTo:1:Args:fileSizeLimitBytes"], out var limit) ? limit : (long?)null; // Allow null for default
            var retainCount = int.TryParse(hostContext.Configuration["Serilog:WriteTo:1:Args:retainedFileCountLimit"], out var count) ? count : 31;
            var outputTemplate = hostContext.Configuration["Serilog:WriteTo:1:Args:outputTemplate"] ?? "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
            
            // Read custom MB limit if needed (like before)
            if (!fileSizeLimit.HasValue && hostContext.Configuration.GetValue<int?>("Logging:FileSizeLimitMB") is int fileSizeLimitMB && fileSizeLimitMB > 0)
            {
                fileSizeLimit = fileSizeLimitMB * 1024L * 1024L; // Use long literal
            }

            Log.Information("Configuring Serilog File Sink. Path: {FullPath}, RollingInterval: {RollInterval}, RollOnSize: {RollOnSize}, SizeLimit: {SizeLimitBytes}, RetainCount: {RetainCount}", 
                fullPath, rollInterval, rollOnSize, fileSizeLimit, retainCount); // Log resolved config

            // Explicitly configure the File sink, overriding any potential misconfiguration from ReadFrom.Configuration
            loggerConfiguration.WriteTo.File(
                path: fullPath, 
                rollingInterval: rollInterval,
                rollOnFileSizeLimit: rollOnSize,
                fileSizeLimitBytes: fileSizeLimit,
                retainedFileCountLimit: retainCount,
                outputTemplate: outputTemplate
                // Add other args like buffered: true, shared: true if needed
            );
        }
        catch (Exception ex)
        { 
            Log.Error(ex, "Error occurred while programmatically configuring Serilog File Sink."); // Use bootstrap logger
            // Log configuration values if possible to help diagnose
            Log.Error("Relevant Config - Serilog:WriteTo:1:Args:path = {PathValue}", hostContext.Configuration["Serilog:WriteTo:1:Args:path"]);
            // Consider throwing or letting the app continue without file logging
        }
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
