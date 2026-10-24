using Microsoft.EntityFrameworkCore;

namespace Andromeld.Cache;

internal sealed class CacheDbContext(DbContextOptions<CacheDbContext> options) : DbContext(options)
{
    public DbSet<DeviceSnapshot> Devices => Set<DeviceSnapshot>();
    public DbSet<CachedApplication> Applications => Set<CachedApplication>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeviceSnapshot>().HasKey(static d => d.DeviceKey);
        modelBuilder.Entity<CachedApplication>().HasKey(static app => new { app.DeviceKey, app.PackageName, app.UserId });
        modelBuilder.Entity<CachedApplication>().HasOne<DeviceSnapshot>().WithMany()
            .HasForeignKey(static app => app.DeviceKey).OnDelete(DeleteBehavior.Cascade);
    }
}