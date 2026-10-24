using Andromeld.Desktop.ViewModels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Andromeld.Desktop.Views;

public class MirrorDisplayMenu : MenuFlyout
{
    private MirrorWindow? _owner;

    public MirrorDisplayMenu()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void SetModel(MirrorViewModel model, MirrorWindow owner)
    {
        _owner = owner;
        foreach (MenuItem item in Items.OfType<MenuItem>())
        {
            item.DataContext = model;
        }
    }

    protected override bool ShowAtCore(Control placementTarget, bool showAtPointer = false)
    {
        // Drawn decorations are a sibling of Window under TopLevelHost. Popup needs the
        // actual Window as its owner; keep the anchor at the title button's client rectangle.
        if (_owner is { } owner && placementTarget != owner)
        {
            Popup.PlacementRect = new Rect(owner.PointToClient(placementTarget.PointToScreen(default)), placementTarget.Bounds.Size);
            return base.ShowAtCore(owner, showAtPointer: false);
        }

        return base.ShowAtCore(placementTarget, showAtPointer);
    }
}