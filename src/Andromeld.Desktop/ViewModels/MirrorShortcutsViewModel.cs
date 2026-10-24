using Andromeld.Desktop.Services;

using Avalonia.Input;
using Avalonia.Threading;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

/// <summary>UI-thread owned; shared by the main window and every mirror window.</summary>
public sealed partial class MirrorShortcutsViewModel : ViewModelBase
{
    private readonly Settings _settings;

    public MirrorShortcutsViewModel(Settings settings)
    {
        _settings = settings;
        SelectedModifier = Options.FirstOrDefault(option => option.Modifiers == settings.MirrorModifiers) ?? Options[0];
        _settings.MirrorModifiers = SelectedModifier.Modifiers;
        RightClickBackEnabled = settings.MirrorRightClickBackEnabled;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(SelectedModifier) or nameof(RightClickBackEnabled)))
            {
                return;
            }

            Dispatcher.UIThread.VerifyAccess();
            _settings.MirrorModifiers = SelectedModifier.Modifiers;
            _settings.MirrorRightClickBackEnabled = RightClickBackEnabled;
            try
            {
                _settings.Save();
                Error = "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = "镜像控制设置已生效，但未能保存。";
            }
        };
    }

    public IReadOnlyList<MirrorModifierOption> Options { get; } =
    [
        new(KeyModifiers.Control, "Ctrl"),
        new(KeyModifiers.Alt, "Alt"),
        new(KeyModifiers.Shift, "Shift"),
        new(KeyModifiers.Control | KeyModifiers.Alt, "Ctrl+Alt"),
        new(KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift"),
        new(KeyModifiers.Alt | KeyModifiers.Shift, "Alt+Shift"),
        new(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, "Ctrl+Alt+Shift")
    ];

    [Reactive(nameof(HomeGesture), nameof(RecentsGesture))]
    public partial MirrorModifierOption SelectedModifier { get; set; } = new(KeyModifiers.Control, "Ctrl");

    public string HomeGesture => $"{SelectedModifier.Label}+H";
    public string RecentsGesture => $"{SelectedModifier.Label}+R";

    [Reactive(nameof(BackGesture))]
    public partial bool RightClickBackEnabled { get; set; }

    public string BackGesture => RightClickBackEnabled ? "Esc / 鼠标右键" : "Esc";

    [Reactive(nameof(HasError))]
    public partial string Error { get; private set; } = "";
    public bool HasError => Error.Length > 0;
}
