namespace Andromeld.Protocol;

public sealed record VideoConfiguration(string Codec, int W, int H, int Fps = 60, double Dpr = 1);