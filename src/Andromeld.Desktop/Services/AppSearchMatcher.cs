using System.Runtime.InteropServices;
using System.Text.Json;

using Avalonia.Platform;

namespace Andromeld.Desktop.Services;

/// <summary>Read-only character readings, matching the reference Web's full/initial/mixed pinyin search.</summary>
public static class AppSearchMatcher
{
    private static readonly Lazy<Dictionary<char, List<string>>> Readings = new(LoadReadings);

    public static bool Matches(string name, string query)
    {
        query = query.Trim().ToLowerInvariant();
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        query = string.Concat(query.Where(static c => !char.IsWhiteSpace(c)));
        if (query.Length == 0)
        {
            return false;
        }

        // Advance query offsets rather than recursing or expanding every polyphonic combination.
        HashSet<int> offsets = [];
        foreach (char character in name)
        {
            offsets.Add(0);
            HashSet<int> next = [];
            foreach (int offset in offsets)
            {
                if (Readings.Value.TryGetValue(character, out List<string>? readings))
                {
                    foreach (string reading in readings)
                    {
                        if (reading.AsSpan().StartsWith(query.AsSpan(offset), StringComparison.Ordinal))
                        {
                            return true;
                        }

                        if (query.AsSpan(offset).StartsWith(reading, StringComparison.Ordinal))
                        {
                            next.Add(offset + reading.Length);
                        }

                        if (query[offset] == reading[0])
                        {
                            next.Add(offset + 1);
                        }
                    }
                }
                else if (char.ToLowerInvariant(character) == query[offset])
                {
                    next.Add(offset + 1);
                }
            }

            if (next.Contains(query.Length))
            {
                return true;
            }

            offsets = next;
        }

        return false;
    }

    private static Dictionary<char, List<string>> LoadReadings()
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://Andromeld.Desktop/Assets/search-pinyin.json"));
        Dictionary<string, string> syllables = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        Dictionary<char, List<string>> readings = [];
        foreach ((string syllable, string characters) in syllables)
        foreach (char character in characters)
        {
            ref List<string>? values = ref CollectionsMarshal.GetValueRefOrAddDefault(readings, character, out _);
            values ??= [];
            values.Add(syllable);
        }

        return readings;
    }
}