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

// Use WebApplication builder
var builder = WebApplication.CreateBuilder(args);

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
app.MapGet("/stats/disk", (Worker workerService) =>
{
    try
    {
        var stats = workerService.GetCurrentDiskStatsForDto();
        return Results.Ok(stats);
    }
    catch (Exception ex)
    {
        // Log the exception - Consider injecting ILogger<Program> if more detailed logging is needed
        Console.WriteLine($"Error getting disk stats: {ex.Message}"); // Basic console log
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
    Console.WriteLine("Attempting to apply database migrations..."); // Or use ILogger if available early
    using (var scope = host.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<NetworkMonitorDbContext>();
        dbContext.Database.Migrate(); // Apply pending migrations
    }
    Console.WriteLine("Database migrations applied successfully (or no pending migrations found).");
}
catch (Exception ex)
{
    // Log this critical error - the service might not function correctly if DB schema is wrong
    Console.WriteLine($"CRITICAL ERROR: Failed to apply database migrations: {ex.Message}"); 
    // Depending on policy, you might want to prevent the host from running:
    // return; // Or throw
}
*/
// --- End Optional Auto-migration ---

app.Run();
