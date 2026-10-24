using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;

using Andromeld.Cache;
using Andromeld.Desktop.Services;
using Andromeld.Discovery;
using Andromeld.Protocol;

using Avalonia.Threading;
using Avalonia.Labs.Notifications;

using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Disposables;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

/// <summary>UI-thread owned, including commands, observable collections and lifecycle methods.</summary>
public sealed partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly List<AppTile> _allApps = [];

    private readonly IObservable<bool> _canConnect, _canRePair, _canUseApps, _canUseFiles;
    private readonly MultipleDisposable _commands = new();

    private readonly Settings _settings;

    private AndromeldClient? _client;

    private CancellationTokenSource _connection = new();

    private Task _disconnectTask = Task.CompletedTask;

    private bool _disposed;

    private bool _supportsApps, _supportsFiles;

    public MainViewModel() : this(Settings.Load()) { }

    public MainViewModel(Settings settings, ILanDiscovery? discovery = null, Func<IAudioPlayback>? audioPlaybackFactory = null,
        IUsbDiscovery? usbDiscovery = null, IWirelessAdbPairing? wirelessAdbPairing = null, INativeNotificationManager? notificationManager = null)
    {
        _settings = settings;
        _audioPlaybackFactory = audioPlaybackFactory;
        _notificationManager = notificationManager ?? NativeNotificationManager.Current;
        AudioForwardingEnabled = settings.AudioForwardingEnabled;
        ClipboardSyncEnabled = settings.ClipboardSyncEnabled;
        NotificationSyncEnabled = settings.NotificationSyncEnabled;
        PropertyChanged += SharingPreferenceChanged;
        MirrorShortcuts = new MirrorShortcutsViewModel(settings);
        MirrorDisplayPreferences = new MirrorDisplayPreferences(settings);
        _discovery = discovery ?? new LanDiscovery();
        _usbDiscovery = usbDiscovery ?? DesktopPlatform.CreateUsbDiscovery();
        _wirelessAdbPairing = wirelessAdbPairing ?? DesktopPlatform.CreateWirelessAdbPairing();
        _cache = new AppCache(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.StoragePath))!, "cache-v1.db"));
        Address = _settings.Address;
        _canConnect = this.WhenAnyValue(x => x.CanConnect);
        _canRePair = this.WhenAnyValue(x => x.CanRePair);
        _canUseApps = this.WhenAnyValue(x => x.CanRefreshApps);
        _canUseFiles = this.WhenAnyValue(x => x.CanUseFiles);
        _canOpenTerminal = this.WhenAnyValue(x => x.CanOpenTerminal);
        InitializeMirroring();
        InitializeDiscovery();
        InitializeSearch();
        InitializeWireless();
        InitializeWirelessPairing();
        this.WhenAnyValue(x => x.Search).Subscribe(_ => FilterApps()).DisposeWith(_commands);

        // Observe exceptions from generated commands so failures are presented in the UI.
        foreach (ReactiveCommand<RxVoid, RxVoid> command in new[]
                 {
                     ConnectCommand, RePairCommand, DisconnectCommand, RefreshAppsCommand,
                     ShowFilesCommand, CancelConnectCommand, ContinueUsbPrivacyCommand, SubmitWirelessPairingCommand,
                     DismissErrorCommand, OpenPhoneCommand, OpenSelectedAppCommand, OpenTerminalCommand, RunSearchActionCommand,
                     OpenSearchTerminalCommand, RefreshDiscoveryCommand, RefreshUsbDevicesCommand, ShowDeviceCommand, ShowApplicationsCommand,
                     ShowSettingsCommand, ShowQrConnectionCommand, ShowIpConnectionCommand, RegeneratePairingQrCommand
                 })
        {
            command.ThrownExceptions.Subscribe(ex =>
            {
                if (ex is not OperationCanceledException)
                {
                    Dispatcher.UIThread.Post(() => Report(ex));
                }
            }).DisposeWith(_commands);
            _commands.Add(command);
        }

        foreach (ReactiveCommand<NearbyDeviceViewModel, RxVoid> command in new[] { ConnectNearbyCommand, RePairNearbyCommand })
        {
            command.ThrownExceptions.Subscribe(ex =>
            {
                if (ex is not OperationCanceledException)
                {
                    Dispatcher.UIThread.Post(() => Report(ex));
                }
            }).DisposeWith(_commands);
            _commands.Add(command);
        }

        foreach (ReactiveCommand<UsbDeviceViewModel, RxVoid> command in new[] { ConnectUsbCommand, RePairUsbCommand })
        {
            command.ThrownExceptions.Subscribe(ex =>
            {
                if (ex is not OperationCanceledException)
                {
                    Dispatcher.UIThread.Post(() => Report(ex));
                }
            }).DisposeWith(_commands);
            _commands.Add(command);
        }
    }

    public bool NeedsDevelopmentNotice => !_settings.DevelopmentNoticeAcknowledged;

    public void AcknowledgeDevelopmentNotice()
    {
        _settings.DevelopmentNoticeAcknowledged = true;
        try
        {
            _settings.Save();
        }
        catch
        {
            _settings.DevelopmentNoticeAcknowledged = false;
            throw;
        }
    }

    public MirrorShortcutsViewModel MirrorShortcuts { get; }
    private MirrorDisplayPreferences MirrorDisplayPreferences { get; }

    public ObservableCollection<AppTile> Apps { get; } = [];
    internal FileBrowserViewModel FileBrowser { get; } = new();

    [Reactive(nameof(IsDevicePage), nameof(IsApplicationsPage), nameof(IsSettingsPage))]
    public partial WorkspacePage SelectedPage { get; set; }
    public bool IsDevicePage => SelectedPage == WorkspacePage.Devices;
    public bool IsApplicationsPage => SelectedPage == WorkspacePage.Applications;
    public bool IsSettingsPage => SelectedPage == WorkspacePage.Settings;

    [Reactive(nameof(CanConnect), nameof(CanRePair))]
    public partial string Address { get; set; }

    [Reactive]
    public partial string Code { get; set; } = "";

    [Reactive]
    public partial string Status { get; private set; } = "尚未连接";

    [Reactive(nameof(ConnectionLabel))]
    public partial string DeviceName { get; private set; } = "你的 Android 设备";

    public string ConnectionLabel
    {
        get
        {
            if (IsConnected)
            {
                return DeviceName;
            }

            return IsConnecting ? "正在连接…" : "设备连接";
        }
    }
    public bool ShowConnectAction => !IsConnected && !IsConnecting;

    [Reactive(nameof(HasError))]
    public partial string Error { get; private set; } = "";

    public bool HasError => Error.Length > 0;

    [Reactive(nameof(CanConnect), nameof(CanRePair), nameof(CanUseApps), nameof(CanRefreshApps), nameof(CanUseFiles),
        nameof(CanMirrorPhone), nameof(CanMirrorApp), nameof(CanOpenTerminal), nameof(CanSelectNearbyDevice), nameof(ConnectionLabel),
        nameof(ShowConnectAction), nameof(CanInstallPackages))]
    public partial bool IsConnected { get; private set; }

    [Reactive(nameof(CanConnect), nameof(CanRePair), nameof(CanSelectNearbyDevice), nameof(ConnectionLabel), nameof(ShowConnectAction))]
    public partial bool IsConnecting { get; private set; }

    [Reactive(nameof(CanConnect), nameof(CanRePair), nameof(CanSelectNearbyDevice))]
    public partial bool IsDisconnecting { get; private set; }

    public bool CanConnect => CanSelectNearbyDevice && !string.IsNullOrWhiteSpace(Address);

    public bool CanRePair => CanConnect;

    public bool CanUseApps => IsConnected && _supportsApps;

    public bool CanRefreshApps => CanUseApps && !AppsLoading;

    public bool CanUseFiles => IsConnected && _supportsFiles;

    [Reactive(nameof(CanRefreshApps), nameof(AppsEmpty))]
    public partial bool AppsLoading { get; private set; }

    [Reactive]
    public partial string AppsStatus { get; private set; } = "连接后读取手机上的应用";

    [Reactive]
    public partial string Search { get; set; } = "";

    public string AppCount => string.IsNullOrWhiteSpace(Search) ? $"{Apps.Count} 个应用" : $"{Apps.Count} / {_allApps.Count} 个应用";

    public bool AppsEmpty => !AppsLoading && Apps.Count == 0;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PropertyChanged -= SharingPreferenceChanged;
        await _connection.CancelAsync().ConfigureAwait(true);
        await DisposeWirelessPairingAsync().ConfigureAwait(true);
        await DisposeDiscoveryAsync().ConfigureAwait(true);
        await DisposeUsbDiscoveryAsync().ConfigureAwait(true);
        await DisconnectAsync();
        foreach (TerminalViewModel terminal in Terminals.ToArray())
        {
            await terminal.DisposeAsync();
        }

        await FileBrowser.DisposeAsync().ConfigureAwait(true);
        _connection.Dispose();
        _commands.Dispose();
    }

    [ReactiveCommand(CanExecute = nameof(_canConnect))]
    private async Task ConnectAsync()
    {
        try { await ConnectCoreAsync(false).ConfigureAwait(true); }
        catch (OperationCanceledException) when (_connection.IsCancellationRequested)
        {
            // Cancel is normal command completion. No delayed error may outlive the UI owner.
        }
    }

    [ReactiveCommand(CanExecute = nameof(_canRePair))]
    private async Task RePairAsync()
    {
        await DisconnectAsync();
        try { await ConnectCoreAsync(true).ConfigureAwait(true); }
        catch (OperationCanceledException) when (_connection.IsCancellationRequested)
        {
            // Cancel is normal command completion. No delayed error may outlive the UI owner.
        }
    }

    [ReactiveCommand(CanExecute = nameof(_canUseApps))]
    private Task RefreshAppsAsync()
    {
        return LoadAppsAsync(true);
    }

    public event Action? FilesRequested;

    [ReactiveCommand(CanExecute = nameof(_canUseFiles))]
    private void ShowFiles()
    {
        FilesRequested?.Invoke();
    }

    [ReactiveCommand]
    private async Task ShowDeviceAsync()
    {
        SelectedPage = WorkspacePage.Devices;
        await RefreshDeviceInformationAsync().ConfigureAwait(true);
    }

    [ReactiveCommand]
    private void ShowApplications()
    {
        SelectedPage = WorkspacePage.Applications;
    }

    [ReactiveCommand]
    private void ShowSettings()
    {
        SelectedPage = WorkspacePage.Settings;
    }

    [ReactiveCommand]
    private void CancelConnect()
    {
        _connection.Cancel();
        if (IsConnecting)
        {
            Status = "正在取消连接并清理…";
        }
    }

    [ReactiveCommand]
    private void DismissError()
    {
        Error = string.Empty;
    }

    public void Report(Exception ex)
    {
        Error = ex switch
        {
            UsbConnectionException usb => $"{UsbStageText(usb.Stage)}失败：{usb.Message}",
            TimeoutException => "设备响应超时，请检查手机上的 AndroMeld 服务是否仍在运行。",
            _ => ex.Message
        };
    }

    private async Task ConnectCoreAsync(bool forcePair, NearbyDeviceViewModel? nearby = null, UsbDeviceViewModel? usb = null, CancellationToken ct = default)
    {
        if (!CanSelectNearbyDevice || _disposed)
        {
            return;
        }

        if (nearby is not null && !NearbyDevices.Contains(nearby))
        {
            return;
        }

        if (usb is not null && ((!usb.Device.IsWireless && !UsbDevices.Contains(usb)) || !usb.IsAvailable))
        {
            return;
        }

        string? addressHost = null;
        int? addressPort = null;
        if (nearby is null && usb is null && !AndromeldEndpoint.TryParseAddress(Address, out addressHost, out addressPort))
        {
            throw new FormatException("请输入有效的 IP 地址，例如 192.168.1.7。");
        }

        _connection.Dispose();
        _connection = new CancellationTokenSource();
        await using CancellationTokenRegistration callerCancellation = ct.Register(_connection.Cancel);
        CancellationToken connectionToken = _connection.Token;
        IsConnecting = true;
        Error = string.Empty;
        UsbConnectionNoticeText = string.Empty;
        HasUsbConnectionNotice = false;
        IsUsbProgressIndeterminate = true;
        UsbProgressPercent = 0;
        if (nearby is not null)
        {
            nearby.IsConnecting = true;
        }

        if (usb is not null)
        {
            usb.IsConnecting = true;
        }

        AndromeldClient? client = null;
        try
        {
            Uri? endpoint;
            bool throughAdb = false;
            string transport = "局域网";
            if (usb is not null)
            {
                transport = usb.Device.IsWireless ? "Wi-Fi" : "USB";
                Status = $"正在准备 {usb.Name} 的 {transport} 连接…";
                _usbConnection = await PrepareAdbConnectionAsync(usb.Device).ConfigureAwait(true);
                connectionToken.ThrowIfCancellationRequested();
                endpoint = _usbConnection.Endpoint;
                throughAdb = usb.Device.IsWireless;
            }
            else if (nearby is not null)
            {
                IsResolvingDevice = true;
                Status = $"正在确认 {nearby.Name} 的连接地址…";
                Uri? preferred = AndromeldEndpoint.TryParse(Address, out Uri? savedEndpoint) && savedEndpoint.Scheme is "ws"
                    ? savedEndpoint
                    : null;
                endpoint = await WebEndpointResolver.FindAsync(nearby.Device, preferred, connectionToken).ConfigureAwait(true);
                connectionToken.ThrowIfCancellationRequested();
                IsResolvingDevice = false;
                if (endpoint is null)
                {
                    throw new IOException($"无法连接 {nearby.Name}，请在手机上开启 AndroMeld 连接服务后重试。");
                }
            }
            else
            {
                Status = "正在查找 AndroMeld 服务…";
                Uri? preferred = addressPort is { } port ? AndromeldEndpoint.CreateEndpoint(addressHost!, port) : null;
                endpoint = await WebEndpointResolver.FindAsync(addressHost!, preferred, connectionToken).ConfigureAwait(true);
                connectionToken.ThrowIfCancellationRequested();
                if (endpoint is null)
                {
                    Status = "未找到 AndroMeld 服务，正在尝试无线调试…";
                    IPAddress? wirelessAddress = await ResolveWirelessAddressAsync(addressHost!, connectionToken).ConfigureAwait(true);
                    if (wirelessAddress is null)
                    {
                        throw new IOException("未找到 AndroMeld 服务，也无法解析该地址以尝试无线调试。请检查 IP 地址。");
                    }

                    _usbConnection = await PrepareAdbConnectionAsync(null, wirelessAddress).ConfigureAwait(true);
                    connectionToken.ThrowIfCancellationRequested();
                    endpoint = _usbConnection.Endpoint;
                    throughAdb = true;
                    transport = "无线调试";
                }
            }

            string key;
            if (usb is not null)
            {
                key = usb.Device.IsWireless ? ConnectionKeys.WirelessSerial(usb.Device.Serial) : ConnectionKeys.Usb(usb.Device.Serial);
            }
            else if (throughAdb)
            {
                key = ConnectionKeys.Wireless(addressHost!);
            }
            else
            {
                key = endpoint.AbsoluteUri;
            }

            if (forcePair)
            {
                _settings.Tokens.Remove(key);
            }

            _settings.Tokens.TryGetValue(key, out string? token);
            Status = !string.IsNullOrEmpty(token) ? "正在验证连接…" : ConnectionAttemptStatus(throughAdb, usb is not null);
            if (_client is not null)
            {
                await UpdateSharingAsync().ConfigureAwait(true);
                await _client.DisposeAsync().ConfigureAwait(true);
                _client = null;
            }

            connectionToken.ThrowIfCancellationRequested();
            client = CreateConnectionClient();
            // Nearby cards and IP connections use resume/phone confirmation, never the separate code input.
            try
            {
                await AuthenticateAsync(client, endpoint, token, connectionToken).ConfigureAwait(true);
            }
            catch (AuthenticationDeniedException ex) when (ex.Reason is "UNAUTHORIZED" && !string.IsNullOrEmpty(token))
            {
                // The phone may have forgotten this pairing. Retire the rejected socket before requesting approval.
                _client = null;
                await client.DisposeAsync().ConfigureAwait(true);
                client = null;
                _settings.Tokens.Remove(key);
                try { _settings.Save(); }
                catch (Exception saveError) { Error = $"无效凭据清理失败：{saveError.Message}"; }

                connectionToken.ThrowIfCancellationRequested();
                Status = PairingRetryStatus(throughAdb, usb is not null);
                client = CreateConnectionClient();
                await AuthenticateAsync(client, endpoint, null, connectionToken).ConfigureAwait(true);
            }

            connectionToken.ThrowIfCancellationRequested();
            DeviceName = client.DeviceName;
            DeviceIdentifier = client.DeviceId ?? "未知设备 ID";
            _supportsApps = client.Capabilities.Contains("app-list");
            _supportsFiles = client.Capabilities.Contains("fs");
            _supportsMirrors = client.Capabilities.Contains("video") && client.Capabilities.Contains("multi-session");
            _supportsPhone = client.Capabilities.Contains("phone-screen");
            _supportsTerminal = client.Capabilities.Contains("terminal");
            ConnectionTransport = transport;
            // Authentication hands the connection to the workspace; leaving the QR page must no longer cancel it.
            await callerCancellation.DisposeAsync().ConfigureAwait(true);
            connectionToken.ThrowIfCancellationRequested();
            IsConnected = true;
            Status = $"已连接 · {ConnectionTransport}";
            _settings.Address = Address;
            if (client.Token is not null)
            {
                _settings.Tokens[key] = client.Token;
            }

            try
            {
                _settings.Save();
                Error = string.Empty;
            }
            catch (Exception ex) { Error = $"连接成功，但凭据保存失败：{ex.Message}"; }
        }
        catch
        {
            if (client is not null)
            {
                if (_client == client)
                {
                    _client = null;
                }

                await client.DisposeAsync().ConfigureAwait(true);
            }

            await ReleaseUsbConnectionAsync().ConfigureAwait(true);
            Status = connectionToken.IsCancellationRequested ? "已取消连接" : "连接失败";
            throw;
        }
        finally
        {
            IsConnecting = false;
            IsResolvingDevice = false;
            if (nearby is not null)
            {
                nearby.IsConnecting = false;
            }

            if (usb is not null)
            {
                usb.IsConnecting = false;
            }
        }

        await UpdateSharingAsync().ConfigureAwait(true);
        await Task.WhenAll(LoadAppsSafelyAsync(), RefreshDeviceInformationAsync()).ConfigureAwait(true);
        if (_supportsFiles)
        {
            await FileBrowser.AttachAsync(_client!, DeviceName, connectionToken).ConfigureAwait(true);
        }
        else
        {
            await FileBrowser.DetachAsync().ConfigureAwait(true);
        }

        Task AuthenticateAsync(AndromeldClient target, Uri address, string? savedToken, CancellationToken ct)
        {
            return _usbConnection is { } connection
                ? target.ConnectUsbAsync(address, _settings.ClientId, savedToken, connection.Bootstrap, ct)
                : target.ConnectAsync(address, _settings.ClientId, savedToken, null, ct);
        }
    }

    private static string ConnectionAttemptStatus(bool throughAdb, bool usb)
    {
        if (throughAdb)
        {
            return "正在建立无线调试连接…";
        }

        return usb ? "正在建立 USB 连接…" : "连接中，请在手机上确认配对…";
    }

    private static string PairingRetryStatus(bool throughAdb, bool usb)
    {
        if (throughAdb)
        {
            return "正在重新验证无线调试连接…";
        }

        return usb ? "正在重新验证 USB 连接…" : "需要重新配对，请在手机上确认…";
    }

    private static async Task<IPAddress?> ResolveWirelessAddressAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return address;
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return Array.Find(addresses, static candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                   ?? Array.Find(addresses, static candidate => candidate.AddressFamily == AddressFamily.InterNetworkV6);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    private AndromeldClient CreateConnectionClient()
    {
        AndromeldClient client = new();
        _client = client;
        client.PairingCode += code => Dispatcher.UIThread.Post(() =>
        {
            if (_client == client)
            {
                Status = $"配对码 {code} · 请在手机上核对并批准";
            }
        });
        client.Disconnected += ex => Dispatcher.UIThread.Post(async () =>
        {
            if (_client == client && IsConnected)
            {
                await DisconnectAsync().ConfigureAwait(true);
                Status = "连接已断开";
                Report(ex);
            }
        });
        return client;
    }

    private async Task LoadAppsSafelyAsync()
    {
        try
        {
            await LoadAppsAsync();
        }
        catch (OperationCanceledException)
        {
            /* Cancellation belongs to navigation or disconnect. */
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private void FilterApps()
    {
        AppTile? selected = SelectedApp;
        Apps.Clear();
        string query = Search.Trim();
        foreach (AppTile tile in _allApps
                     .Where(a => AppSearchMatcher.Matches(a.Name, query) || a.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(static a => a.IsPinned)
                     .ThenByDescending(a => _appUsage?.LastOpened.GetValueOrDefault(AppUsageKey(a.App)) ?? DateTimeOffset.MinValue)
                     .ThenBy(static a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(static a => a.Package, StringComparer.Ordinal).ThenBy(static a => a.App.UserId))
        {
            Apps.Add(tile);
        }

        SelectedApp = selected is not null && Apps.Contains(selected) ? selected : null;
        UpdateRunningApps();
        UpdateSearchActions();
        this.RaisePropertyChanged(nameof(AppCount));
        this.RaisePropertyChanged(nameof(AppsEmpty));
    }

    [ReactiveCommand]
    private Task DisconnectAsync()
    {
        if (_disconnectTask.IsCompleted)
        {
            _disconnectTask = DisconnectCoreAsync();
        }

        return _disconnectTask;
    }

    private async Task DisconnectCoreAsync()
    {
        IsDisconnecting = true;
        try
        {
            IsConnected = false;
            _notificationSharing?.Deactivate();
            ClearDeviceInformation();
            await FileBrowser.DetachAsync().ConfigureAwait(true);
            await UpdateSharingAsync();
            await CloseMirrorsAsync();
            await DisconnectTerminalsAsync();
            await _connection.CancelAsync();
            AndromeldClient? client = _client;
            _client = null;
            if (client is not null)
            {
                await client.DisposeAsync();
            }

            Status = "已断开连接";
            Apps.Clear();
            foreach (AppTile tile in _allApps)
            {
                tile.Dispose();
            }

            _allApps.Clear();
            AppsStatus = "连接后读取手机上的应用";
            UpdateSearchActions();
            this.RaisePropertyChanged(nameof(AppCount));
            this.RaisePropertyChanged(nameof(AppsEmpty));
        }
        finally
        {
            await ReleaseUsbConnectionAsync().ConfigureAwait(true);
            IsDisconnecting = false;
        }
    }
}
