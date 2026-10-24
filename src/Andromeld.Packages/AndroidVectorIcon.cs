using System.Globalization;
using System.IO.Compression;
using System.Numerics;

namespace Andromeld.Packages;

internal static class AndroidVectorIcon
{
    public static PackageVectorIcon? Read(ZipArchive zip, AndroidResources resources, AndroidResources.Value value, int depth = 0)
    {
        if (depth > 4) return null;
        foreach (string path in resources.ResolveAll(value).Distinct(StringComparer.Ordinal))
        {
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || zip.GetEntry(path) is not { } entry) continue;
            var elements = new AndroidResources(ApkReader.ReadEntry(entry)).ReadXml();
            var vector = Array.Find(elements, static e => string.Equals(e.Name, "vector", StringComparison.Ordinal));
            if (vector is not null) return ParseVector(elements, vector, resources);
            List<PackageIconPath> layers = [];
            foreach (var layer in elements.Where(static e => e.Name is "background" or "foreground"))
            {
                if (!layer.Attributes.TryGetValue("drawable", out var drawable)) continue;
                if (resources.ResolveColor(drawable) is { } color) layers.Add(new("M0 0H108V108H0Z", color, 1, Matrix3x2.Identity, false));
                else if (Read(zip, resources, drawable, depth + 1) is { } nested)
                    layers.AddRange(nested.Paths.Select(p => p with { Transform = p.Transform * Matrix3x2.CreateScale(108 / nested.Width, 108 / nested.Height) }));
            }
            if (layers.Count > 0) return new(108, 108, layers.ToArray());
        }
        return null;
    }

    private static PackageVectorIcon? ParseVector(AndroidResources.Element[] elements, AndroidResources.Element vector, AndroidResources resources)
    {
        float Number(AndroidResources.Element element, string name, float fallback = 0) => element.Attributes.TryGetValue(name, out var value) &&
            float.TryParse(resources.Resolve(value), NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number) ? number : fallback;
        float width = Number(vector, "viewportWidth"), height = Number(vector, "viewportHeight");
        if (width <= 0 || height <= 0) return null;
        List<PackageIconPath> paths = [];
        Stack<Matrix3x2> transforms = new();
        transforms.Push(Matrix3x2.Identity);
        foreach (var element in elements)
        {
            if (element.Name is "group")
            {
                float x = Number(element, "pivotX"), y = Number(element, "pivotY");
                var local = Matrix3x2.CreateTranslation(-x, -y) * Matrix3x2.CreateScale(Number(element, "scaleX", 1), Number(element, "scaleY", 1)) *
                    Matrix3x2.CreateRotation(Number(element, "rotation") * MathF.PI / 180) *
                    Matrix3x2.CreateTranslation(x + Number(element, "translateX"), y + Number(element, "translateY"));
                transforms.Push(local * transforms.Peek());
            }
            else if (element.Name is "/group" && transforms.Count > 1) transforms.Pop();
            else if (element.Name is "path" && element.Attributes.TryGetValue("pathData", out var data) && resources.Resolve(data) is { } path)
            {
                uint color = element.Attributes.TryGetValue("fillColor", out var fill) ? resources.ResolveColor(fill) ?? 0xff000000 : 0xff000000;
                paths.Add(new(path, color, Math.Clamp(Number(element, "fillAlpha", 1) * Number(vector, "alpha", 1), 0, 1), transforms.Peek(), element.Attributes.TryGetValue("fillType", out var fillType) && fillType.Data == 1));
            }
        }
        return paths.Count > 0 ? new(width, height, paths.ToArray()) : null;
    }
}
