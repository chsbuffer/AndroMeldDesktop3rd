using System.Globalization;
using System.Runtime.InteropServices;

using Andromeld.Cache;
using Andromeld.Desktop.Services;
using Andromeld.Protocol;

using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using ReactiveUI;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly AppCache _cache;
    private AppUsageSettings? _appUsage;

    private static string AppUsageKey(PhoneApp app)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{app.UserId}:{app.PackageName}");
    }

    private void ToggleAppPin(AppTile tile)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!IsConnected || _appUsage is null || !_allApps.Contains(tile))
        {
            return;
        }

        string key = AppUsageKey(tile.App);
        if (!_appUsage.Pinned.Remove(key))
        {
            _appUsage.Pinned.Add(key);
        }

        tile.IsPinned = _appUsage.Pinned.Contains(key);
        SaveAppUsage();
        FilterApps();
    }

    private void MarkAppUsed(PhoneApp app)
    {
        if (_appUsage is null)
        {
            return;
        }

        _appUsage.LastOpened[AppUsageKey(app)] = DateTimeOffset.UtcNow;
        SaveAppUsage();
        FilterApps();
    }

    private void SaveAppUsage()
    {
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"应用排序已更新，但保存失败：{ex.Message}";
        }
    }

    private async Task LoadAppsAsync(bool forceIcons = false)
    {
        if (!CanUseApps || AppsLoading)
        {
            return;
        }

        AndromeldClient client = _client!;
        CancellationToken ct = _connection.Token;
        string key = AppCache.DeviceKey(client.DeviceId, client.Token);
        ref AppUsageSettings? usage = ref CollectionsMarshal.GetValueRefOrAddDefault(_settings.AppUsage, key, out bool exists);
        if (!exists)
        {
            usage = new AppUsageSettings();
        }

        _appUsage = usage;

        AppsLoading = true;
        AppsStatus = "正在读取应用列表…";
        bool cacheAvailable = true;
        AppSnapshot? snapshot = null;
        try
        {
            try { snapshot = await _cache.LoadAsync(key, ct).ConfigureAwait(true); }
            catch (Exception ex) when (IsCacheFailure(ex)) { cacheAvailable = false; }

            ct.ThrowIfCancellationRequested();
            Dictionary<(string PackageName, int UserId), CachedApp> cached =
                snapshot?.Apps.ToDictionary(static a => (a.App.PackageName, a.App.UserId)) ?? [];
            if (snapshot is not null)
            {
                UpdateAppTiles(snapshot.Apps.Select(static a => a.App), cached);
                AppsStatus = $"已加载 {_allApps.Count} 个本地缓存应用，正在更新…";
                await Task.Yield();
            }

            PhoneApp[] apps = await client.GetAppsAsync(ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            UpdateAppTiles(apps, cached);
            cacheAvailable &= await TryCacheAsync(() => _cache.SaveAppsAsync(key, apps, DateTimeOffset.UtcNow, ct)).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            PhoneApp[] toFetch = apps.Where(app => forceIcons
                                                   || !cached.TryGetValue((app.PackageName, app.UserId), out CachedApp? entry)
                                                   || entry.IconUpdatedAt is not { } updated || updated < now - TimeSpan.FromHours(24) ||
                                                   updated > now + TimeSpan.FromMinutes(5)
                                                   || !_allApps.Exists(tile =>
                                                       string.Equals(tile.Package, app.PackageName, StringComparison.Ordinal) &&
                                                       tile.App.UserId == app.UserId && tile.HasIcon)).ToArray();
            int reused = apps.Length - toFetch.Length;
            Dictionary<(string Package, int UserId), AppTile> byKey = _allApps.ToDictionary(static a => (a.Package, a.App.UserId));
            int loaded = 0;
            foreach (PhoneApp[] batch in toFetch.Chunk(16))
            {
                AppIcon[] icons = await client.GetIconsAsync(batch, ct).ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();
                List<IconData> valid = [];
                foreach (AppIcon icon in icons)
                {
                    if (!byKey.TryGetValue((icon.PackageName, icon.UserId), out AppTile? tile))
                    {
                        continue;
                    }

                    try
                    {
                        byte[] bytes = Convert.FromBase64String(icon.PngBase64);
                        await using MemoryStream stream = new(bytes);
                        tile.ReplaceIcon(new Bitmap(stream));
                        UpdateMirrorIcon(tile.App, bytes);
                        valid.Add(new IconData(icon.PackageName, icon.UserId, bytes));
                    }
                    catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
                    {
                        /* Keep a placeholder or the last valid icon. */
                    }
                }

                if (valid.Count > 0)
                {
                    cacheAvailable &= await TryCacheAsync(() => _cache.SaveIconsAsync(key, valid, DateTimeOffset.UtcNow, ct)).ConfigureAwait(true);
                }

                ct.ThrowIfCancellationRequested();
                loaded += batch.Length;
                AppsStatus = $"正在加载图标 · {reused + loaded}/{apps.Length}";
            }

            AppsStatus = $"{apps.Length} 个应用 · {_allApps.Count(static a => a.HasIcon)} 个图标"
                         + (reused > 0 ? $" · 复用 {reused} 个缓存图标" : "")
                         + (cacheAvailable ? "" : " · 本地缓存不可用");
        }
        catch
        {
            AppsStatus = snapshot is not null ? "更新失败，保留上次加载的应用" : "应用加载失败";
            throw;
        }
        finally
        {
            AppsLoading = false;
            this.RaisePropertyChanged(nameof(AppsEmpty));
        }
    }

    private void UpdateAppTiles(IEnumerable<PhoneApp> apps, Dictionary<(string, int), CachedApp> cached)
    {
        PhoneApp? selected = SelectedApp?.App;
        Dictionary<(string Package, int UserId), AppTile> previous = _allApps.ToDictionary(static a => (a.Package, a.App.UserId));
        _allApps.Clear();
        foreach (PhoneApp app in apps.DistinctBy(static a => (a.PackageName, a.UserId))
                     .OrderBy(static a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            (string PackageName, int UserId) key = (app.PackageName, app.UserId);
            previous.Remove(key, out AppTile? tile);
            if (tile is null || tile.App != app)
            {
                tile?.Dispose();
                tile = new AppTile(app, ToggleAppPin);
            }

            tile.IsPinned = _appUsage?.Pinned.Contains(AppUsageKey(app)) == true;
            if (!tile.HasIcon && cached.TryGetValue(key, out CachedApp? entry) && entry.Png is { } png)
            {
                try
                {
                    using MemoryStream stream = new(png);
                    tile.ReplaceIcon(new Bitmap(stream));
                    UpdateMirrorIcon(app, png);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    /* Corrupt cached bytes are fetched again. */
                }
            }

            _allApps.Add(tile);
        }

        foreach (AppTile obsolete in previous.Values)
        {
            obsolete.Dispose();
        }

        FilterApps();
        SelectedApp = selected is null
            ? null
            : _allApps.Find(a => string.Equals(a.Package, selected.PackageName, StringComparison.Ordinal) && a.App.UserId == selected.UserId);
    }

    private static bool IsCacheFailure(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or SqliteException or DbUpdateException;
    }

    private static async Task<bool> TryCacheAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsCacheFailure(ex)) { return false; }
    }
}