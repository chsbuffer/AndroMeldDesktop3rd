using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Andromeld.Desktop.Controls;

/// <summary>UI-thread layout: derive columns from available width, keep sparse rows at tile width, preserve keyboard navigation.</summary>
public sealed class AdaptiveTilePanel : Panel, INavigableContainer
{
    public static readonly StyledProperty<double> MinimumItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveTilePanel, double>(nameof(MinimumItemWidth), 144,
            validate: static value => double.IsFinite(value) && value > 0);
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<AdaptiveTilePanel, double>(nameof(Spacing), 12, validate: static value => double.IsFinite(value) && value >= 0);
    private int _columns = 1;

    static AdaptiveTilePanel()
    {
        AffectsMeasure<AdaptiveTilePanel>(MinimumItemWidthProperty, SpacingProperty);
    }

    public double MinimumItemWidth { get => GetValue(MinimumItemWidthProperty); set => SetValue(MinimumItemWidthProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    IInputElement? INavigableContainer.GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        int index = from is Control child ? Children.IndexOf(child) : -1;
        index = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Children.Count - 1,
            NavigationDirection.Next or NavigationDirection.Right => index + 1,
            NavigationDirection.Previous or NavigationDirection.Left => index - 1,
            NavigationDirection.Up => index - _columns,
            NavigationDirection.Down => index + _columns,
            _ => -1
        };
        return index >= 0 && index < Children.Count ? Children[index] : null;
    }

    private int Columns(double width)
    {
        return double.IsFinite(width)
            ? Math.Max(1, (int)Math.Floor((width + Spacing) / (MinimumItemWidth + Spacing)))
            : 1;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _columns = Columns(availableSize.Width);
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : MinimumItemWidth;
        double itemWidth = Math.Max(0, (width - ((_columns - 1) * Spacing)) / _columns);
        foreach (Control child in Children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
        }

        return new Size(width, LayoutRows(itemWidth, false));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _columns = Columns(finalSize.Width);
        LayoutRows(Math.Max(0, (finalSize.Width - ((_columns - 1) * Spacing)) / _columns), true);
        return finalSize;
    }

    private double LayoutRows(double itemWidth, bool arrange)
    {
        double y = 0d;
        for (int first = 0; first < Children.Count; first += _columns)
        {
            int end = Math.Min(first + _columns, Children.Count);
            double height = 0d;
            for (int i = first; i < end; i++)
            {
                height = Math.Max(height, Children[i].DesiredSize.Height);
            }

            if (arrange)
            {
                for (int i = first; i < end; i++)
                {
                    Children[i].Arrange(new Rect((i - first) * (itemWidth + Spacing), y, itemWidth, height));
                }
            }

            y += height + (end < Children.Count ? Spacing : 0);
        }

        return y;
    }
}