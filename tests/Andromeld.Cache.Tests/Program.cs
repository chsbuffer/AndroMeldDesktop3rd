using Andromeld.Cache;
using Andromeld.Protocol;

namespace Andromeld.Cache.Tests;

internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
        Console.WriteLine($"PASS: {description}");
    }

    public static async Task Main()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "andromeld-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var path = Path.Combine(temporary, "cache.db");
            var cache = new AppCache(path);
            var key = AppCache.DeviceKey("phone-1", "credential-1");
            var other = AppCache.DeviceKey("phone-2", "credential-2");
            var now = DateTimeOffset.UtcNow;
            PhoneApp personal = new("test.app", "个人应用", 0, false, 1, false);
            var work = personal with { UserId = 10, DisplayName = "工作应用" };
            Check(await cache.LoadAsync(key, CancellationToken.None) is null, "unknown device has no cached snapshot");
            await cache.SaveAppsAsync(key, [personal, work], now, CancellationToken.None);
            await cache.SaveIconsAsync(key, [new(personal.PackageName, 0, [1, 2, 3]), new(work.PackageName, 10, [4, 5, 6])], now, CancellationToken.None);
            var reopened = new AppCache(path);
            var snapshot = (await reopened.LoadAsync(key, CancellationToken.None))!;
            Check(snapshot.Apps.Count == 2 && snapshot.Apps.Single(a => a.App.UserId == 0).Png!.SequenceEqual(new byte[] { 1, 2, 3 })
                && snapshot.Apps.Single(a => a.App.UserId == 10).Png!.SequenceEqual(new byte[] { 4, 5, 6 }), "SQLite persists distinct package/profile icons across cache instances");
            Check(await cache.LoadAsync(other, CancellationToken.None) is null
                && AppCache.DeviceKey("phone-1", "rotated-credential") == key
                && AppCache.DeviceKey(null, "credential-1") != AppCache.DeviceKey(null, "credential-2"), "authenticated device identity isolates caches and survives credential rotation");
            await cache.SaveAppsAsync(key, [personal with { DisplayName = "已改名" }], now.AddMinutes(1), CancellationToken.None);
            snapshot = (await cache.LoadAsync(key, CancellationToken.None))!;
            Check(snapshot.Apps is [{ App.DisplayName: "已改名", Png: not null }] && snapshot.Apps[0].IconUpdatedAt == now,
                "authoritative list updates metadata, preserves icon freshness and removes absent profile");
            await Task.WhenAll(
                cache.SaveAppsAsync(other, [work], now, CancellationToken.None),
                cache.SaveIconsAsync(key, [new(personal.PackageName, 0, [7, 8, 9])], now.AddHours(1), CancellationToken.None),
                cache.LoadAsync(key, CancellationToken.None));
            Check((await cache.LoadAsync(other, CancellationToken.None))!.Apps.Count == 1
                && (await cache.LoadAsync(key, CancellationToken.None))!.Apps[0].Png!.SequenceEqual(new byte[] { 7, 8, 9 }), "parallel operations use independent contexts and commit complete database transactions");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await cache.SaveAppsAsync(key, [], now, canceled.Token); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { Check((await cache.LoadAsync(key, CancellationToken.None))!.Apps.Count == 1, "canceled operation leaves prior snapshot intact"); }
            await cache.SaveAppsAsync(key, [], now.AddHours(2), CancellationToken.None);
            Check((await cache.LoadAsync(key, CancellationToken.None)) is { Apps.Count: 0 }
                && (await cache.LoadAsync(other, CancellationToken.None)) is { Apps.Count: 1 }, "empty authoritative list clears only its own device");
            Check(File.ReadAllBytes(path).AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8), "cache artifact is an actual SQLite database");
            Console.WriteLine($"{_checks} cache checks passed.");
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
