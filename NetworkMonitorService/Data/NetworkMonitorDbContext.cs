using Microsoft.EntityFrameworkCore;
using NetworkMonitorService.Domain;
using NetworkMonitorService.Models;

namespace NetworkMonitorService.Data
{
    public class NetworkMonitorDbContext : DbContext
    {
        public NetworkMonitorDbContext(DbContextOptions<NetworkMonitorDbContext> options)
            : base(options)
        {
        }

        public DbSet<NetworkUsageLog> NetworkUsageLogs { get; set; }
        public DbSet<DiskActivityLog> DiskActivityLogs { get; set; }

        // We need DbSets for the Domain models if EF needs to know about them directly
        // public DbSet<ProcessNetworkStats> ProcessNetworkStats { get; set; } // Example if needed
        // public DbSet<ProcessDiskStats> ProcessDiskStats { get; set; } // Example if needed

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {   
            // Configure primary keys for log tables if they don't have an explicit [Key] or Id property
            // modelBuilder.Entity<NetworkUsageLog>().HasKey(e => new { e.Timestamp, e.ProcessId }); // REMOVED: Let convention use Id PK
            // modelBuilder.Entity<DiskActivityLog>().HasKey(e => new { e.Timestamp, e.ProcessId });   // REMOVED: Let convention use Id PK

            // Configurations for ProcessNetworkStats/ProcessDiskStats if they were entities
            // modelBuilder.Entity<ProcessNetworkStats>().HasKey(e => e.ProcessId); // Example

            // Ensure ProcessName max length is specified if needed
            modelBuilder.Entity<NetworkUsageLog>().Property(e => e.ProcessName).HasMaxLength(256); // Example length
            modelBuilder.Entity<DiskActivityLog>().Property(e => e.ProcessName).HasMaxLength(256); // Example length

            // Add configuration for DiskActivityLog Top files if needed
            modelBuilder.Entity<DiskActivityLog>().Property(e => e.TopReadFile).HasMaxLength(1024); // Example length
            modelBuilder.Entity<DiskActivityLog>().Property(e => e.TopWriteFile).HasMaxLength(1024); // Example length

            base.OnModelCreating(modelBuilder);
        }
    }
} 