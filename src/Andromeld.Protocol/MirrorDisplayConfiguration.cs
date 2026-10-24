namespace Andromeld.Protocol;

/// <summary>Web DISPLAY policy. Viewport pixels/DPR, menu zoom and Android density are distinct.</summary>
public sealed record MirrorDisplayConfiguration(MirrorSize Size, double Scale, int DecorationTop = 0)
{
    public static MirrorDisplayConfiguration FromViewport(MirrorSize viewport, int zoomPercent, bool compatible, double titleBarHeight = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(zoomPercent, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zoomPercent, 150);
        if (viewport.Width <= 0 || viewport.Height <= 0 || !double.IsFinite(viewport.Dpr) || viewport.Dpr <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewport));
        }

        if (!double.IsFinite(titleBarHeight) || titleBarHeight < 0 || titleBarHeight * viewport.Dpr > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(titleBarHeight));
        }

        int shortSide = Math.Min(viewport.Width, viewport.Height);
        double density = compatible ? 1 : Math.Max(.2, Round3(Math.Min(1, shortSide / viewport.Dpr / 360)));
        double scale = Math.Clamp(Round3(density * zoomPercent / 100 * .8), .2, 1.5);
        if (!compatible)
        {
            return Configuration(viewport);
        }

        double ratio = (double)Math.Max(viewport.Width, viewport.Height) / shortSide;
        MirrorSize size = viewport.Width <= viewport.Height
            ? MirrorSize.FromViewport(392, 392 * ratio, viewport.Dpr)
            : MirrorSize.FromViewport(392 * ratio, 392, viewport.Dpr);
        return Configuration(size);

        // Match Web decor-insets: DIP titlebar height times the effective display DPR,
        // including encoding caps and compatibility resolution. Density/zoom is separate.
        MirrorDisplayConfiguration Configuration(MirrorSize display)
        {
            return new MirrorDisplayConfiguration(display, scale, (int)Math.Floor((titleBarHeight * display.Dpr) + .5));
        }
    }

    private static double Round3(double value)
    {
        return Math.Floor((value * 1000) + .5) / 1000;
    }
}