using Serilog;
using Serilog.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;

namespace NetworkMonitorService.Configuration;

public static class SerilogConfigurator
{
    public static void ConfigureSerilog(HostBuilderContext hostContext, LoggerConfiguration loggerConfiguration)
    {
        // Read base Serilog configuration (levels, console sink etc.) first
        loggerConfiguration.ReadFrom.Configuration(hostContext.Configuration)
            .Enrich.FromLogContext();

        // --- Programmatically configure File Sink with absolute path --- 
        try 
        { 
            // Get Base Directory
            var baseDirectory = AppContext.BaseDirectory;

            // Read relative path and other args from config (adjust index [1] if File is not the second sink)
            // Assuming 'File' sink is configured at Serilog:WriteTo:1
            var fileSinkConfigPath = "Serilog:WriteTo:1:Args"; 
            var relativePath = hostContext.Configuration[$"{fileSinkConfigPath}:path"] ?? "Logs\\networkmonitor.log";
            var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath)); // Ensure path is absolute and canonical

            // Create directory if it doesn't exist
            var logDirectory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(logDirectory) && !Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
                // Use bootstrap logger if available (or log staticly before full host built)
                Log.Information("Created log directory: {LogDirectory}", logDirectory); 
            }

            // Read other arguments (provide defaults)
            var rollInterval = Enum.TryParse<RollingInterval>(hostContext.Configuration[$"{fileSinkConfigPath}:rollingInterval"], true, out var interval) ? interval : RollingInterval.Day;
            var rollOnSize = bool.TryParse(hostContext.Configuration[$"{fileSinkConfigPath}:rollOnFileSizeLimit"], out var roll) ? roll : true;
            var fileSizeLimit = long.TryParse(hostContext.Configuration[$"{fileSinkConfigPath}:fileSizeLimitBytes"], out var limit) ? limit : (long?)null; // Allow null for default
            var retainCount = int.TryParse(hostContext.Configuration[$"{fileSinkConfigPath}:retainedFileCountLimit"], out var count) ? count : 31;
            var outputTemplate = hostContext.Configuration[$"{fileSinkConfigPath}:outputTemplate"] ?? "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
            
            // Read custom MB limit if needed (like before)
            if (!fileSizeLimit.HasValue && hostContext.Configuration.GetValue<int?>("Logging:FileSizeLimitMB") is int fileSizeLimitMB && fileSizeLimitMB > 0)
            {
                fileSizeLimit = fileSizeLimitMB * 1024L * 1024L; // Use long literal
            }

            // Log resolved config (use bootstrap logger if possible)
            Log.Information("Configuring Serilog File Sink. Path: {FullPath}, RollingInterval: {RollInterval}, RollOnSize: {RollOnSize}, SizeLimit: {SizeLimitBytes}, RetainCount: {RetainCount}", 
                fullPath, rollInterval, rollOnSize, fileSizeLimit, retainCount); 

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
    }
} 