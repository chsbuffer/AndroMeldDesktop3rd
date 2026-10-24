using System.Runtime.InteropServices;

namespace Andromeld.Platform.Windows;

/// <summary>Physical desktop coordinates, including negative coordinates on secondary monitors.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DesktopCaptureRegion(int X, int Y, int Width, int Height);