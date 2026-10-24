using Andromeld.Desktop.Services;

using Avalonia;
using Avalonia.Controls;

namespace Andromeld.Desktop.Views;

public partial class MirrorWindow
{
    private Size _normalWindowSize;
    private WindowState _windowedState;

    private void InitializeWindowSize(Window? owner)
    {
        if (_model.IsPhone)
        {
            return;
        }

        var display = Screens.ScreenFromWindow(owner ?? this) ?? Screens.Primary;
        if (_model.WindowSettings is { } saved)
        {
            Width = Math.Max(MinWidth, Math.Min(saved.Width, display?.WorkingArea.Width / display?.Scaling ?? saved.Width));
            Height = Math.Max(MinHeight, Math.Min(saved.Height, display?.WorkingArea.Height / display?.Scaling ?? saved.Height));
            WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
        else if (_model.App?.Game == true && display is not null)
        {
            Width = display.WorkingArea.Width / display.Scaling;
            Height = display.WorkingArea.Height / display.Scaling;
            Position = display.WorkingArea.Position;
            WindowState = WindowState.Maximized;
        }

        _normalWindowSize = new Size(Width, Height);
        _windowedState = WindowState;
    }

    private void RememberNormalWindowSize()
    {
        if (IsVisible && WindowState == WindowState.Normal && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            _normalWindowSize = ClientSize;
        }
    }

    private void SaveWindowSize()
    {
        RememberNormalWindowSize();
        if (!_model.IsPhone && _normalWindowSize.Width > 0 && _normalWindowSize.Height > 0)
        {
            _model.SaveWindowSettings(new MirrorWindowSettings(_normalWindowSize.Width, _normalWindowSize.Height,
                _windowedState == WindowState.Maximized));
        }
    }
}
