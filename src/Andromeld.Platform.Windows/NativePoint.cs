using System.Runtime.InteropServices;

namespace Andromeld.Platform.Windows;

[StructLayout(LayoutKind.Auto)]
public readonly record struct NativePoint(int X, int Y);