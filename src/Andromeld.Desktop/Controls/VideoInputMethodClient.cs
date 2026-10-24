using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.TextInput;
using Avalonia.Media;

namespace Andromeld.Desktop.Controls;

/// <summary>UI-thread IME composition; only committed TextInput is forwarded to the remote display.</summary>
internal sealed class VideoInputMethodClient : TextInputMethodClient
{
    private readonly VideoView _view;
    private readonly TextBlock _preedit = new() { TextDecorations = TextDecorations.Underline };
    private Point _anchor;

    public VideoInputMethodClient(VideoView view)
    {
        _view = view;
        CompositionPopup = new Popup
        {
            PlacementTarget = view,
            Placement = PlacementMode.TopEdgeAlignedLeft,
            TakesFocusFromNativeControl = false,
            IsHitTestVisible = false,
            // A separate popup surface also keeps preedit visible above the phone's SDL child window.
            Child = new Border { Classes = { "mirrorImePreedit" }, Child = _preedit }
        };
    }

    public Popup CompositionPopup { get; }
    public bool IsComposing => !string.IsNullOrEmpty(_preedit.Text);
    public override Visual TextViewVisual => _view;
    public override bool SupportsPreedit => true;
    // The protocol provides no remote editor text or selection; do not invent local surrounding text.
    public override bool SupportsSurroundingText => false;
    public override string SurroundingText => string.Empty;
    public override TextSelection Selection { get; set; }
    public override Rect CursorRectangle => new(
        Math.Clamp(_anchor.X, 0, Math.Max(0, _view.Bounds.Width - 1)),
        Math.Clamp(_anchor.Y, 0, Math.Max(0, _view.Bounds.Height - _preedit.FontSize)), 1, _preedit.FontSize);

    public void SetAnchor(Point point)
    {
        _anchor = point;
        UpdateCursor();
    }

    public void UpdateCursor()
    {
        CompositionPopup.PlacementRect = CursorRectangle;
        RaiseCursorRectangleChanged();
    }

    public override void SetPreeditText(string? preeditText)
    {
        _preedit.Text = _view.IsFocused ? preeditText : null;
        UpdateCursor();
        CompositionPopup.IsOpen = IsComposing;
    }

    public void Reset()
    {
        SetPreeditText(null);
        RequestReset();
    }
}
