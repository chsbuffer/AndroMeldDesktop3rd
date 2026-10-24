using System.Diagnostics;

using Andromeld.Desktop.Services;
using Andromeld.Media;
using Andromeld.Protocol;

using Avalonia.Threading;

using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Disposables;
using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

/// <summary>Presentation and lifecycle are UI-thread owned; decoder frames cross through the latest-frame mailbox.</summary>
public sealed partial class MirrorViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IObservable<bool> _canControl, _canRelaunch, _canScreenshot;
    private readonly MultipleDisposable _commands = new();
    private readonly AndromeldClient _control;
    private readonly CancellationTokenSource _lifetime;
    private readonly DispatcherTimer _taskAwayTimer = new();
    private FfmpegVideoDecoder? _decoder;
    private Task? _fileUploadTask;
    private bool _hadActiveTask;
    private MirrorDisplayConfiguration? _lastDisplay;
    private DecodedFrame? _latest;
    private int _presentPending;
    private MirrorSession? _session;
    private Task? _startTask, _disposeTask;

    public MirrorViewModel(AndromeldClient control, PhoneApp? app, CancellationToken connection, MirrorShortcutsViewModel? shortcuts = null,
        byte[]? appIconPng = null,
        MirrorDisplayPreferences? displayPreferences = null, AndroidIntentUri? intent = null)
    {
        if (app is not null && intent is not null)
        {
            throw new ArgumentException("镜像不能同时指定应用和链接。", nameof(intent));
        }

        _control = control;
        App = app;
        Intent = intent;
        AppIconPng = appIconPng;
        Shortcuts = shortcuts ?? new MirrorShortcutsViewModel(new Settings());
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(connection);
        _control.WindowStateChanged += OnWindowState;
        _canControl = this.WhenAnyValue(x => x.CanControl);
        _canRelaunch = this.WhenAnyValue(x => x.CanRelaunch);
        _canScreenshot = this.WhenAnyValue(x => x.CanScreenshot);
        _canControlPhone = this.WhenAnyValue(x => x.CanControlPhone);
        _canEnterFusionMode = this.WhenAnyValue(x => x.CanEnterFusionMode);
        _commands.Add(ScreenshotCommand);
        InitializeDisplay(displayPreferences ?? new MirrorDisplayPreferences());
        _taskAwayTimer.Tick += ShowAwayTask;
        foreach (ReactiveCommand<RxVoid, RxVoid> command in new[] { BackCommand, HomeCommand, RecentsCommand, PowerCommand, RelaunchCommand,
                     EnterFusionModeCommand, ToggleDisplayPowerCommand })
        {
            command.ThrownExceptions.Subscribe(ex => Dispatcher.UIThread.Post(() => Fail(ex))).DisposeWith(_commands);
            _commands.Add(command);
        }
    }

    public PhoneApp? App { get; }
    public AndroidIntentUri? Intent { get; }
    // Immutable PNG snapshots, updated only on the UI thread as application icons arrive or refresh.
    [Reactive]
    public partial byte[]? AppIconPng { get; internal set; }
    public MirrorShortcutsViewModel Shortcuts { get; }
    public bool IsPhone => App is null && Intent is null;
    public string Title => IsPhone ? "手机屏幕 · AndroMeld" : $"{App?.DisplayName ?? "Android 链接"} · AndroMeld";
    public string Subtitle => IsPhone ? "手机屏幕" : App?.PackageName ?? Intent!.Value;
    public string? SessionId => _session?.SessionId;
    public bool IsDisposed { get; private set; }
    // Owned by the UI thread. The view supplies its measured viewport before StartAsync.
    public MirrorSize ViewportSize { get; set; } = new(480, 800, 1);
    public double TitleBarHeight { get; set; }
    public CancellationToken Lifetime => _lifetime.Token;

    [Reactive(nameof(HasFrame), nameof(Resolution), nameof(ShowVideo), nameof(CanScreenshot))]
    public partial DecodedFrame? Frame { get; private set; }
    public bool HasFrame => Frame is not null;
    public bool ShowVideo => HasFrame && !IsTaskAway;
    public string Resolution => Frame is { } frame ? $"{frame.Width} × {frame.Height}" : "";
    [Reactive(nameof(ShowVideo), nameof(CanRelaunch), nameof(CanScreenshot))]
    public partial bool IsTaskAway { get; private set; }
    public bool CanRelaunch => CanControl && !IsPhone && IsTaskAway;
    [Reactive]
    public partial string Status { get; private set; } = "正在启动镜像…";
    [Reactive(nameof(HasError))]
    public partial string Error { get; private set; } = "";
    public bool HasError => Error.Length > 0;
    [Reactive(nameof(CanRelaunch), nameof(CanChangeDisplay), nameof(CanZoomOut), nameof(CanZoomIn), nameof(CanUseScreenCamera),
        nameof(CanScreenshot), nameof(CanControlPhone), nameof(CanEnterFusionMode), nameof(FusionModeHint))]
    public partial bool CanControl { get; private set; }

    public ValueTask DisposeAsync()
    {
        if (IsDisposed)
        {
            return new ValueTask(_disposeTask ?? Task.CompletedTask);
        }

        IsDisposed = true;
        _taskAwayTimer.Stop();
        _taskAwayTimer.Tick -= ShowAwayTask;
        _disposeTask = DisposeCoreAsync();
        return new ValueTask(_disposeTask);
    }

    public event Action? FrameUpdated;
    public event Action? Closed;
    public event Action? CloseRequested;
    public event Action<DragOutTransfer>? DragOutStarted;

    public void ReportTransfer(Exception ex)
    {
        if (!IsDisposed)
        {
            Error = ex.Message;
        }
    }

    public Task UploadFilesAsync(IReadOnlyList<string> paths)
    {
        if (_fileUploadTask is { IsCompleted: false })
        {
            return _fileUploadTask;
        }

        _fileUploadTask = UploadFilesCoreAsync(paths);
        return _fileUploadTask;
    }

    private async Task UploadFilesCoreAsync(IReadOnlyList<string> paths)
    {
        if (!CanControl || SessionId is not { } sessionId)
        {
            return;
        }

        if (paths.Any(static p => !File.Exists(p)))
        {
            throw new IOException("请拖入文件；暂不支持整个文件夹。");
        }

        foreach (string path in paths)
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            UploadResult result = await _control.UploadAsync(Path.GetFileName(path), stream.Length, stream, null, sessionId, null, _lifetime.Token);
            if (!result.Imported)
            {
                throw new IOException($"{result.Name} 已保存到手机下载目录，当前应用未接收该文件。");
            }
        }
    }

    public Task StartAsync()
    {
        return _startTask ??= StartCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        try
        {
            MirrorDisplayConfiguration display = DisplayConfiguration;
            MirrorSize size = display.Size;
            _session = Intent is { } intent
                ? await _control.OpenIntentMirrorAsync(intent, _lifetime.Token, size.Width, size.Height, size.Dpr)
                : await _control.OpenMirrorAsync(App, _lifetime.Token, size.Width, size.Height, size.Dpr);
            _session.ConfigureVideo = ConfigureAsync;
            SubscribePhoneControls(_session);
            _session.ReceiveVideo = packet => _decoder?.WriteAsync(packet.Data, _lifetime.Token) ?? ValueTask.CompletedTask;
            _session.Disconnected += ex => Dispatcher.UIThread.Post(() => Fail(ex));
            _session.TaskStateChanged += away => Dispatcher.UIThread.Post(() => OnTaskState(away));
            _session.DragOutStarted += transfer => Dispatcher.UIThread.Post(() =>
            {
                if (!IsDisposed)
                {
                    DragOutStarted?.Invoke(transfer);
                }
            });
            _session.CameraRequestedChanged += requested => Dispatcher.UIThread.Post(() =>
            {
                if (!IsDisposed)
                {
                    CameraRequested = requested;
                }
            });
            _lifetime.Token.ThrowIfCancellationRequested();
            Status = "正在连接画面…";
            await _session.AttachAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!IsPhone)
            {
                await _session.ConfigureDisplayAsync(display, _lifetime.Token);
            }

            _lastDisplay = display;
            CanControl = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { await ReleaseMediaAsync(); }
        catch (Exception ex)
        {
            Fail(ex);
            await ReleaseMediaAsync();
        }
    }

    private async ValueTask ConfigureAsync(VideoConfiguration configuration)
    {
        if (_decoder is not null)
        {
            await _decoder.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Token.ThrowIfCancellationRequested();
        FfmpegVideoDecoder decoder = new();
        decoder.FrameDecoded += OnDecoded;
        decoder.Failed += ex => Dispatcher.UIThread.Post(() => Fail(ex));
        _decoder = decoder;
    }

    private void OnDecoded(DecodedFrame frame)
    {
        if (IsDisposed)
        {
            return;
        }

        Interlocked.Exchange(ref _latest, frame);
        if (Interlocked.Exchange(ref _presentPending, 1) == 0)
        {
            Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
        }
    }

    private void Present()
    {
        Interlocked.Exchange(ref _presentPending, 0);
        DecodedFrame? frame = Interlocked.Exchange(ref _latest, null);
        if (IsDisposed || frame is null)
        {
            return;
        }

        Frame = frame;
        Status = "已连接";
        FrameUpdated?.Invoke();
    }

    [ReactiveCommand(CanExecute = nameof(_canControl))]
    private Task BackAsync()
    {
        return SendKeyAsync(4);
    }

    [ReactiveCommand(CanExecute = nameof(_canControl))]
    private Task HomeAsync()
    {
        return SendKeyAsync(3);
    }

    [ReactiveCommand(CanExecute = nameof(_canControl))]
    private Task RecentsAsync()
    {
        return SendKeyAsync(187);
    }

    [ReactiveCommand(CanExecute = nameof(_canControl))]
    private Task PowerAsync()
    {
        return SendKeyAsync(26);
    }

    [ReactiveCommand(CanExecute = nameof(_canRelaunch))]
    private Task RelaunchAsync()
    {
        return SendInputAsync(s => s.RelaunchAsync(_lifetime.Token));
    }

    public Task SendPointerAsync(string action, int x, int y)
    {
        return SendInputAsync(s => s.PointerAsync(action, x, y, _lifetime.Token));
    }

    public Task SendScrollAsync(int x, int y, double dx, double dy)
    {
        return SendInputAsync(s => s.ScrollAsync(x, y, dx, dy, _lifetime.Token));
    }

    public Task SendTextAsync(string text)
    {
        return SendInputAsync(s => s.TextAsync(text, _lifetime.Token));
    }

    public Task SendKeyAsync(int code, int meta = 0)
    {
        return SendInputAsync(s => s.KeyAsync(code, meta, _lifetime.Token));
    }

    public async Task ResizeAsync()
    {
        if (IsPhone || !CanControl || _session is null)
        {
            return;
        }

        MirrorDisplayConfiguration display = DisplayConfiguration;
        if (display == _lastDisplay)
        {
            return;
        }

        MirrorSession session = _session;
        try
        {
            await session.ConfigureDisplayAsync(display, _lifetime.Token);
            _lastDisplay = display;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            /* Window closed during resize. */
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task SendInputAsync(Func<MirrorSession, Task> send)
    {
        if (!CanControl || _session is null)
        {
            return;
        }

        try { await send(_session); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            /* Window closed during input. */
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void OnWindowState(MirrorState state)
    {
        if (state.State is not ("closed" or "failed"))
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            if (IsDisposed || !string.Equals(state.SessionId, SessionId, StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(state.State, "closed", StringComparison.Ordinal))
            {
                await DisposeAsync();
            }
            else
            {
                Fail(new IOException(MirrorState.FailureMessage(state.Reason ?? state.State)));
            }
        });
    }

    private void OnTaskState(bool away)
    {
        if (IsDisposed || IsPhone)
        {
            return;
        }

        _taskAwayTimer.Stop();
        if (!away)
        {
            _hadActiveTask = true;
            IsTaskAway = false;
            return;
        }

        // Allow activity transitions to settle, and give a newly launched task more time to appear.
        _taskAwayTimer.Interval = TimeSpan.FromMilliseconds(_hadActiveTask ? 850 : 3000);
        _taskAwayTimer.Start();
    }

    private void ShowAwayTask(object? sender, EventArgs e)
    {
        _taskAwayTimer.Stop();
        IsTaskAway = true;
    }

    private void Fail(Exception ex)
    {
        if (IsDisposed)
        {
            return;
        }

        CanControl = false;
        CameraRequested = false;
        Status = "镜像已停止";
        Error = ex.Message;
    }

    private async Task ReleaseMediaAsync()
    {
        // Cancel decoder input first, so a blocked media reader can leave before it is awaited.
        if (_session is { } session)
        {
            UnsubscribePhoneControls(session);
            await session.DisposeAsync();
            _session = null;
        }

        if (_decoder is { } decoder)
        {
            await decoder.DisposeAsync();
            _decoder = null;
        }
    }

    private async Task DisposeCoreAsync()
    {
        IsDisposed = true;
        CanControl = false;
        CameraRequested = false;
        _control.WindowStateChanged -= OnWindowState;
        _displayPreferences.Changed -= OnDisplayPreferencesChanged;
        PropertyChanged -= SaveScreenCameraPreference;
        await _lifetime.CancelAsync();
        if (_fileUploadTask is not null)
        {
            try { await _fileUploadTask; }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }

        if (_startTask is not null)
        {
            await _startTask;
        }

        await ReleaseMediaAsync();
        Frame = null;
        Interlocked.Exchange(ref _latest, null);
        _commands.Dispose();
        _lifetime.Dispose();
        CloseRequested?.Invoke();
        Closed?.Invoke();
    }
}
