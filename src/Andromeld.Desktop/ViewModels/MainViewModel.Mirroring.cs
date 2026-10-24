using System.Collections.ObjectModel;

using Andromeld.Protocol;

using Avalonia.Media.Imaging;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IObservable<bool> _canMirrorPhone = null!, _canMirrorApp = null!;
    private bool _supportsMirrors, _supportsPhone;

    public ObservableCollection<MirrorViewModel> Mirrors { get; } = [];

    public bool CanMirrorPhone => IsConnected && _supportsMirrors && _supportsPhone;
    public bool CanMirrorApp => IsConnected && _supportsMirrors && SelectedApp is not null;

    [Reactive(nameof(CanMirrorApp))]
    public partial AppTile? SelectedApp { get; set; }
    public event Func<MirrorViewModel, Task>? MirrorRequested;

    private void InitializeMirroring()
    {
        _canMirrorPhone = this.WhenAnyValue(x => x.CanMirrorPhone);
        _canMirrorApp = this.WhenAnyValue(x => x.CanMirrorApp);
    }

    [ReactiveCommand(CanExecute = nameof(_canMirrorPhone))]
    private Task OpenPhoneAsync()
    {
        return OpenMirrorAsync(null);
    }

    [ReactiveCommand(CanExecute = nameof(_canMirrorApp))]
    private Task OpenSelectedAppAsync()
    {
        return SelectedApp is { } app ? OpenMirrorAsync(app.App) : Task.CompletedTask;
    }

    private async Task OpenMirrorAsync(PhoneApp? app, AndroidIntentUri? intent = null)
    {
        if (_client is null || !IsConnected)
        {
            return;
        }

        MirrorViewModel? existing = intent is null
            ? Mirrors.FirstOrDefault(m => !m.IsDisposed && m.Intent is null && string.Equals(m.App?.PackageName, app?.PackageName, StringComparison.Ordinal) && m.App?.UserId == app?.UserId)
            : null;
        if (existing is not null)
        {
            if (MirrorRequested is { } focus)
            {
                await focus(existing);
            }

            if (app is not null && existing.CanControl)
            {
                MarkAppUsed(app);
            }

            return;
        }

        byte[]? iconPng = null;
        if (app is not null && _allApps.Find(tile =>
                string.Equals(tile.App.PackageName, app.PackageName, StringComparison.Ordinal) && tile.App.UserId == app.UserId)?.Icon is { } icon)
        {
            // Snapshot on the UI thread: refreshing/disconnecting disposes the tile's bitmap.
            using MemoryStream stream = new();
            icon.Save(stream, PngBitmapEncoderOptions.Default);
            iconPng = stream.ToArray();
        }

        AndromeldClient client = _client;
        MirrorViewModel mirror = new(client, app, _connection.Token, MirrorShortcuts, iconPng, MirrorDisplayPreferences, intent);
        mirror.FusionRequested += focused => OpenFocusedApplicationAsync(mirror, client, focused);
        mirror.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(MirrorViewModel.CanControl), StringComparison.Ordinal))
            {
                UpdateRunningApps();
            }
        };
        mirror.PropertyChanged += MirrorSharingStateChanged;
        mirror.Closed += () =>
        {
            mirror.PropertyChanged -= MirrorSharingStateChanged;
            Mirrors.Remove(mirror);
            UpdateRunningApps();
            if (_activeMirror == mirror)
            {
                _activeMirror = null;
            }

            _ = UpdateSharingAsync();
        };
        Mirrors.Add(mirror);
        if (MirrorRequested is { } show)
        {
            await show(mirror);
        }

        await mirror.StartAsync();
        await UpdateSharingAsync();
        if (app is not null && mirror.CanControl)
        {
            MarkAppUsed(app);
        }
    }

    private async Task CloseMirrorsAsync()
    {
        foreach (MirrorViewModel mirror in Mirrors.ToArray())
        {
            await mirror.DisposeAsync();
        }
    }

    private async Task OpenFocusedApplicationAsync(MirrorViewModel source, AndromeldClient client, FocusedApplication focused)
    {
        if (source.IsDisposed || !ReferenceEquals(_client, client) || !IsConnected) return;
        PhoneApp? app = _allApps.Find(tile => tile.App.UserId == focused.UserId &&
            string.Equals(tile.App.PackageName, focused.PackageName, StringComparison.Ordinal))?.App;
        if (app is null) throw new InvalidOperationException("请先在手机上打开一个 App。");

        MirrorViewModel? existing = Mirrors.FirstOrDefault(m => !m.IsPhone && !m.IsDisposed && !m.HasError &&
            m.FocusedApplication is { } current && current.UserId == app.UserId &&
            string.Equals(current.PackageName, app.PackageName, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (MirrorRequested is { } focus) await focus(existing);
            if (existing.CanControl) MarkAppUsed(app);
            return;
        }

        await OpenMirrorAsync(app);
    }

    private void UpdateMirrorIcon(PhoneApp app, byte[] png)
    {
        foreach (MirrorViewModel mirror in Mirrors)
        {
            if (!mirror.IsDisposed && mirror.App?.UserId == app.UserId &&
                string.Equals(mirror.App?.PackageName, app.PackageName, StringComparison.Ordinal))
            {
                mirror.AppIconPng = png;
            }
        }
    }
}
