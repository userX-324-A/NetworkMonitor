using Microsoft.EntityFrameworkCore;

namespace NetworkMonitorService.Models // Ensure this namespace matches the directory structure
{
    public class NetworkMonitorDbContext : DbContext
    {
        public NetworkMonitorDbContext(DbContextOptions<NetworkMonitorDbContext> options)
            : base(options)
        {
        }

        public DbSet<NetworkUsageLog> NetworkUsageLogs { get; set; }
        public DbSet<DiskActivityLog> DiskActivityLogs { get; set; }

        // Optional: Add OnModelCreating overrides here if needed
        // protected override void OnModelCreating(ModelBuilder modelBuilder)
        // {
        //     base.OnModelCreating(modelBuilder);
        // }
    }
} 