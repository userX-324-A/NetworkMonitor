using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using NetworkMonitorService.Models; // Namespace for your DbContext
using System.IO;

namespace NetworkMonitorService.Data // Or choose another appropriate namespace
{
    // This factory is used by the EF Core tools (like dotnet ef migrations add)
    // It manually reads configuration to correctly set up the DbContextOptions
    public class NetworkMonitorDbContextFactory : IDesignTimeDbContextFactory<NetworkMonitorDbContext>
    {
        public NetworkMonitorDbContext CreateDbContext(string[] args)
        {
            // Build configuration manually, ensuring it reads appsettings.json
            // It searches upwards from the current directory until it finds the file.
            var basePath = Directory.GetCurrentDirectory(); 
            // Note: When running 'dotnet ef', the current directory is usually the project directory (NetworkMonitorService)

            var configuration = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true) // Load base settings
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true) // Load environment-specific settings (optional)
                 // Add user secrets if you use them for the connection string during development
                 // .AddUserSecrets<NetworkMonitorDbContextFactory>(optional: true) 
                .AddEnvironmentVariables() // Allow environment variables to override
                .Build();

            // Get the connection string from the manually built configuration
            var connectionString = configuration.GetConnectionString("DefaultConnection");

            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("Design-time factory could not find connection string 'DefaultConnection'. Check appsettings.json.");
            }

            // Create DbContextOptions using the connection string
            var optionsBuilder = new DbContextOptionsBuilder<NetworkMonitorDbContext>();
            optionsBuilder.UseSqlServer(connectionString,
                sqlServerOptions =>
                {
                    // Optional: Configure retry logic if needed for design-time operations
                    // sqlServerOptions.EnableRetryOnFailure();
                });

            // Return the new DbContext instance
            return new NetworkMonitorDbContext(optionsBuilder.Options);
        }
    }
} 