using System.Numerics;

namespace Andromeld.Packages;

public sealed record PackageIconPath(string Data, uint Color, float Alpha, Matrix3x2 Transform, bool EvenOdd);
