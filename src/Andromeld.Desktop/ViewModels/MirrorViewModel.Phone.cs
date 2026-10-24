using Andromeld.Protocol;

using Avalonia.Threading;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class MirrorViewModel
{
    private readonly IObservable<bool> _canEnterFusionMode, _canControlPhone;

    [Reactive(nameof(CanEnterFusionMode), nameof(FusionModeHint))]
    public partial FocusedApplication? FocusedApplication { get; private set; }
    public bool CanEnterFusionMode => CanControlPhone && FocusedApplication is { PackageName.Length: > 0 };
    public bool CanControlPhone => IsPhone && CanControl;
    public string FusionModeHint => CanEnterFusionMode ? "融合模式：在独立窗口中打开当前应用" : "融合模式：请先在手机上打开一个 App";

    [Reactive(nameof(DisplayPowerLabel))]
    public partial bool IsScreenOff { get; private set; }
    public string DisplayPowerLabel => IsScreenOff ? "点亮屏幕" : "关屏使用";
    public event Func<FocusedApplication, Task>? FusionRequested;

    private void SubscribePhoneControls(MirrorSession session)
    {
        session.FocusedApplicationChanged += OnFocusedApplication;
        session.DisplayPowerChanged += OnDisplayPower;
        session.DisplayPowerStateChanged += OnDisplayPowerState;
    }

    private void UnsubscribePhoneControls(MirrorSession session)
    {
        session.FocusedApplicationChanged -= OnFocusedApplication;
        session.DisplayPowerChanged -= OnDisplayPower;
        session.DisplayPowerStateChanged -= OnDisplayPowerState;
    }

    private void OnFocusedApplication(FocusedApplication app)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsDisposed) FocusedApplication = app;
        });
    }

    private void OnDisplayPower(bool on)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsDisposed) IsScreenOff = !on;
        });
    }

    private void OnDisplayPowerState(DisplayPowerState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsDisposed && state is { State: 1, Reason: 4 }) IsScreenOff = false;
        });
    }

    [ReactiveCommand(CanExecute = nameof(_canEnterFusionMode))]
    private async Task EnterFusionModeAsync()
    {
        if (CanEnterFusionMode && FocusedApplication is { } app && FusionRequested is { } open)
        {
            try { await open(app); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                /* The source phone window closed while opening the application. */
            }
            catch (Exception ex) { ReportTransfer(ex); }
        }
    }

    [ReactiveCommand(CanExecute = nameof(_canControlPhone))]
    private async Task ToggleDisplayPowerAsync()
    {
        if (!CanControlPhone || _session is null) return;
        IsScreenOff = !IsScreenOff;
        await SendInputAsync(s => s.SetDisplayPowerAsync(!IsScreenOff, _lifetime.Token));
    }
}
