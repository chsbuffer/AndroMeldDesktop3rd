using Andromeld.Protocol;

using Avalonia.Media.Imaging;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class AppTile(PhoneApp app, Action<AppTile>? togglePin = null) : ViewModelBase, IDisposable
{
    public PhoneApp App { get; } = app;

    public string Name => App.DisplayName;

    public string Package => App.PackageName;

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "A" : Name[..1];

    public string Profile => App.UserId == 0 ? string.Empty : $"用户 {App.UserId}";

    public string Detail => $"{Package}\n用户 {App.UserId}";

    [Reactive(nameof(PinActionLabel))]
    public partial bool IsPinned { get; internal set; }
    [Reactive]
    public partial bool IsRunning { get; internal set; }
    public string PinActionLabel => IsPinned ? "取消置顶" : "置顶";

    [Reactive(nameof(HasIcon))]
    public partial Bitmap? Icon { get; private set; }

    public bool HasIcon => Icon is not null;

    public void Dispose()
    {
        TogglePinCommand.Dispose();
        Icon?.Dispose();
    }

    [ReactiveCommand]
    private void TogglePin()
    {
        togglePin?.Invoke(this);
    }

    public void ReplaceIcon(Bitmap icon)
    {
        Bitmap? old = Icon;
        Icon = icon;
        old?.Dispose();
    }
}