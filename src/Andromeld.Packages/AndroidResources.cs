using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Runtime.InteropServices;

namespace Andromeld.Packages;

/// <summary>Bounded Android chunk reader for AXML and simple ARSC values; no Android runtime is needed.</summary>
internal sealed class AndroidResources(byte[] bytes)
{
    private readonly byte[] _bytes = bytes;
    private readonly Dictionary<uint, List<Value>> _values = [];
    internal readonly record struct Value(byte Type, uint Data, string? Text = null);
    internal sealed record Element(string Name, IReadOnlyDictionary<string, Value> Attributes);

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
    private int Int(int offset) => checked((int)U32(offset));
    private ReadOnlySpan<byte> Slice(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > _bytes.Length - count) throw new InvalidDataException("Android 资源被截断。");
        return _bytes.AsSpan(offset, count);
    }

    private IEnumerable<(int Offset, int Type, int Header, int End)> Chunks(int start, int end)
    {
        while (start < end)
        {
            int header = U16(start + 2), size = Int(start + 4);
            if (header < 8 || size < header || size > end - start) throw new InvalidDataException("无效的 Android 资源块。");
            yield return (start, U16(start), header, start + size);
            start += size;
        }
    }

    private string[] Strings(int start, int header, int end)
    {
        int count = Int(start + 8), data = checked(start + Int(start + 20));
        if (count > (end - start - header) / 4 || data > end) throw new InvalidDataException("无效的 Android 字符串表。");
        bool utf8 = (U32(start + 16) & 256) != 0;
        string[] result = new string[count];
        for (int i = 0; i < count; i++)
        {
            int pos = checked(data + Int(start + header + i * 4));
            int length = Length(ref pos, utf8);
            if (utf8) length = Length(ref pos, true);
            int size = checked(length * (utf8 ? 1 : 2));
            if (pos > end - size) throw new InvalidDataException("Android 字符串超出资源块。");
            result[i] = (utf8 ? Encoding.UTF8 : Encoding.Unicode).GetString(Slice(pos, size));
        }
        return result;
    }

    private int Length(ref int pos, bool utf8)
    {
        if (utf8)
        {
            int value = Slice(pos++, 1)[0];
            return (value & 128) == 0 ? value : ((value & 127) << 8) | Slice(pos++, 1)[0];
        }
        int first = U16(pos); pos += 2;
        if ((first & 32768) == 0) return first;
        int second = U16(pos); pos += 2;
        return ((first & 32767) << 16) | second;
    }

    public Element[] ReadXml()
    {
        if (U16(0) != 3 || Int(4) != _bytes.Length) throw new InvalidDataException("安装包的 AndroidManifest.xml 不是有效的二进制 XML。");
        string[] strings = [];
        List<Element> elements = [];
        foreach (var chunk in Chunks(U16(2), _bytes.Length))
        {
            if (chunk.Type == 1) strings = Strings(chunk.Offset, chunk.Header, chunk.End);
            if (chunk.Type == 0x103)
            {
                elements.Add(new("/" + GetString(strings, U32(chunk.Offset + chunk.Header + 4)), new Dictionary<string, Value>(StringComparer.Ordinal)));
                continue;
            }
            if (chunk.Type != 0x102) continue;
            int ext = chunk.Offset + chunk.Header;
            string name = GetString(strings, U32(ext + 4));
            int attrStart = U16(ext + 8), attrSize = U16(ext + 10), count = U16(ext + 12);
            if (attrSize < 20 || ext + attrStart > chunk.End - count * attrSize) throw new InvalidDataException("无效的 Android XML 属性。");
            Dictionary<string, Value> attributes = new(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int attr = ext + attrStart + i * attrSize;
                byte type = Slice(attr + 15, 1)[0];
                uint data = U32(attr + 16);
                uint raw = U32(attr + 8);
                string? text = raw != uint.MaxValue ? GetString(strings, raw) : null;
                if (type == 3) text = GetString(strings, data);
                attributes[GetString(strings, U32(attr + 4))] = new(type, data, text);
            }
            elements.Add(new(name, attributes));
        }
        return elements.ToArray();
    }

    private static string GetString(string[] strings, uint index) => index < strings.Length ? strings[index] : throw new InvalidDataException("Android 字符串索引无效。");

    public void ReadTable()
    {
        if (U16(0) != 2 || Int(4) != _bytes.Length) throw new InvalidDataException("无效的 Android 资源表。");
        string[] strings = [];
        foreach (var chunk in Chunks(U16(2), _bytes.Length))
        {
            if (chunk.Type == 1) strings = Strings(chunk.Offset, chunk.Header, chunk.End);
            if (chunk.Type != 0x200) continue;
            uint package = U32(chunk.Offset + 8);
            uint typeOffset = chunk.Header >= 288 ? U32(chunk.Offset + 284) : 0;
            foreach (var type in Chunks(chunk.Offset + chunk.Header, chunk.End).Where(static c => c.Type == 0x201))
            {
                int start = type.Offset;
                byte typeId = Slice(start + 8, 1)[0], flags = Slice(start + 9, 1)[0];
                int count = Int(start + 12), entries = checked(start + Int(start + 16));
                int width = (flags & 2) != 0 ? 2 : 4;
                if (count > (entries - start - type.Header) / width) throw new InvalidDataException("无效的 Android 资源偏移表。");
                for (int i = 0; i < count; i++)
                {
                    int at = start + type.Header + i * width;
                    uint index = (uint)i, offset;
                    if ((flags & 1) != 0) { index = U16(at); offset = (uint)U16(at + 2) * 4; }
                    else if (width == 2) { int shortOffset = U16(at); offset = shortOffset == ushort.MaxValue ? uint.MaxValue : (uint)shortOffset * 4; }
                    else offset = U32(at);
                    if (offset == uint.MaxValue) continue;
                    int entry = checked(entries + (int)offset);
                    if (entry > type.End - 8) throw new InvalidDataException("Android 资源条目越界。");
                    int entryFlags = U16(entry + 2);
                    if ((entryFlags & 1) != 0) continue; // Complex styles/bags are not app labels or icon paths.
                    Value value;
                    if ((entryFlags & 8) != 0) value = new((byte)(entryFlags >> 8), U32(entry + 4));
                    else
                    {
                        int valueAt = checked(entry + U16(entry));
                        if (valueAt > type.End - 8) throw new InvalidDataException("Android 资源值越界。");
                        value = new(Slice(valueAt + 3, 1)[0], U32(valueAt + 4));
                    }
                    if (value.Type == 3) value = value with { Text = GetString(strings, value.Data) };
                    uint id = (package << 24) | ((typeId + typeOffset) << 16) | index;
                    ref var values = ref CollectionsMarshal.GetValueRefOrAddDefault(_values, id, out _);
                    (values ??= []).Add(value);
                }
            }
        }
    }

    public void Merge(AndroidResources other)
    {
        foreach (var pair in other._values)
        {
            ref var values = ref CollectionsMarshal.GetValueRefOrAddDefault(_values, pair.Key, out _);
            (values ??= []).AddRange(pair.Value);
        }
    }

    public string? Resolve(Value value) => Resolve(value, [], 0);
    private string? Resolve(Value value, HashSet<uint> visited, int depth)
    {
        if (depth > 16) return null;
        if (value.Type == 1 && _values.TryGetValue(value.Data, out var values))
            return visited.Add(value.Data) ? values.Select(v => Resolve(v, visited, depth + 1)).FirstOrDefault(static s => !string.IsNullOrEmpty(s)) : null;
        if (value.Type == 4) return BitConverter.UInt32BitsToSingle(value.Data).ToString(CultureInfo.InvariantCulture);
        return value.Text ?? (value.Type is >= 16 and <= 31 ? value.Data.ToString(CultureInfo.InvariantCulture) : null);
    }

    public uint? ResolveColor(Value value) => ResolveColor(value, [], 0);
    private uint? ResolveColor(Value value, HashSet<uint> visited, int depth)
    {
        if (depth > 16) return null;
        if (value.Type == 1 && _values.TryGetValue(value.Data, out var values))
            return visited.Add(value.Data) ? values.Select(v => ResolveColor(v, visited, depth + 1)).FirstOrDefault(static v => v.HasValue) : null;
        return value.Type is >= 28 and <= 31 ? value.Data : null;
    }

    public IEnumerable<string> ResolveAll(Value value) => ResolveAll(value, [], 0);
    private IEnumerable<string> ResolveAll(Value value, HashSet<uint> visited, int depth)
    {
        if (depth > 16) yield break;
        if (value.Type == 1 && _values.TryGetValue(value.Data, out var values))
        {
            if (!visited.Add(value.Data)) yield break;
            foreach (string text in values.SelectMany(v => ResolveAll(v, visited, depth + 1))) yield return text;
        }
        else if (value.Text is { } text) yield return text;
    }
}
