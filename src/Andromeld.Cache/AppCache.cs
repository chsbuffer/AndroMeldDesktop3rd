using System.Security.Cryptography;
using System.Text;

using Andromeld.Protocol;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Andromeld.Cache;

/// <summary>
///     Concurrent operations use independent contexts and database transactions. Input collections must
///     remain unchanged until an operation completes; returned snapshots belong to the caller.
///     Schema initialization completes once before any operation executes.
/// </summary>
public sealed class AppCache
{
    private readonly DbContextOptions<CacheDbContext> _options;
    private readonly Task _ready;

    public AppCache(string path)
    {
        path = Path.GetFullPath(path);
        SqliteConnectionStringBuilder connection = new() { DataSource = path, Pooling = false, DefaultTimeout = 2 };
        _options = new DbContextOptionsBuilder<CacheDbContext>().UseSqlite(connection.ToString()).Options;
        _ready = Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using CacheDbContext db = new(_options);
            db.Database.EnsureCreated();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        });
    }

    // Called only after authentication. Never use an IP address or unauthenticated mDNS TXT as identity.
    public static string DeviceKey(string? deviceId, string? credential)
    {
        string identity;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            identity = "device:" + deviceId;
        }
        else if (!string.IsNullOrWhiteSpace(credential))
        {
            identity = "credential:" + credential;
        }
        else
        {
            throw new ArgumentException("Authenticated device identity is required.", nameof(deviceId));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public Task<AppSnapshot?> LoadAsync(string deviceKey, CancellationToken ct)
    {
        return RunAsync<AppSnapshot?>(db =>
        {
            using IDbContextTransaction transaction = db.Database.BeginTransaction();
            DeviceSnapshot? device = db.Devices.AsNoTracking().SingleOrDefault(d => d.DeviceKey == deviceKey);
            if (device is null)
            {
                return null;
            }

            CachedApplication[] apps = db.Applications.AsNoTracking().Where(app => app.DeviceKey == deviceKey).ToArray();
            return new AppSnapshot(ToDate(device.UpdatedUtcTicks), apps.Select(static app => new CachedApp(
                new PhoneApp(app.PackageName, app.DisplayName, app.UserId, app.IsSystemApp, app.AppCategory, app.Game), app.Png,
                app.IconUpdatedUtcTicks is { } ticks ? ToDate(ticks) : null)).ToArray());
        }, ct);
    }

    public Task SaveAppsAsync(string deviceKey, IReadOnlyList<PhoneApp> apps, DateTimeOffset now, CancellationToken ct)
    {
        return RunAsync(db =>
        {
            using IDbContextTransaction transaction = db.Database.BeginTransaction();
            DeviceSnapshot? device = db.Devices.Find(deviceKey);
            if (device is null)
            {
                db.Devices.Add(new DeviceSnapshot { DeviceKey = deviceKey, UpdatedUtcTicks = now.UtcTicks });
            }
            else
            {
                device.UpdatedUtcTicks = now.UtcTicks;
            }

            Dictionary<(string PackageName, int UserId), CachedApplication> existing = db.Applications.Where(app => app.DeviceKey == deviceKey)
                .ToDictionary(static app => (app.PackageName, app.UserId));
            foreach (PhoneApp app in apps.DistinctBy(static app => (app.PackageName, app.UserId)))
            {
                if (!existing.Remove((app.PackageName, app.UserId), out CachedApplication? row))
                {
                    row = new CachedApplication { DeviceKey = deviceKey, PackageName = app.PackageName, UserId = app.UserId };
                    db.Applications.Add(row);
                }

                row.DisplayName = app.DisplayName;
                row.IsSystemApp = app.IsSystemApp;
                row.AppCategory = app.AppCategory;
                row.Game = app.Game;
            }

            // The complete server list is authoritative, including an empty list and removed work profiles.
            db.Applications.RemoveRange(existing.Values);
            db.SaveChanges();
            transaction.Commit();
            return true;
        }, ct);
    }

    public Task SaveIconsAsync(string deviceKey, IReadOnlyList<IconData> icons, DateTimeOffset now, CancellationToken ct)
    {
        return RunAsync(db =>
        {
            using IDbContextTransaction transaction = db.Database.BeginTransaction();
            foreach (IconData icon in icons)
            {
                CachedApplication? row = db.Applications.Find(deviceKey, icon.PackageName, icon.UserId);
                if (row is null)
                {
                    continue;
                }

                row.Png = icon.Png;
                row.IconUpdatedUtcTicks = now.UtcTicks;
            }

            db.SaveChanges();
            transaction.Commit();
            return true;
        }, ct);
    }

    private async Task<T> RunAsync<T>(Func<CacheDbContext, T> operation, CancellationToken ct)
    {
        await _ready.WaitAsync(ct).ConfigureAwait(false);
        // SQLite's async methods execute synchronously. Each operation owns its DbContext and
        // transaction on a worker thread; no context or mutable entity is shared between operations.
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using CacheDbContext db = new(_options);
            return operation(db);
        }, ct).ConfigureAwait(false);
    }

    private static DateTimeOffset ToDate(long ticks)
    {
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}