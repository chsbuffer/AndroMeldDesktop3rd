using System.Text;

namespace Andromeld.Packages;

/// <summary>Reads bundletool's public commands.proto/targeting.proto without a runtime or generated SDK dependency.</summary>
internal sealed class BundleTable(byte[] bytes)
{
    private readonly Message _table = Message.Parse(bytes);
    private static readonly string[] AbiNames = ["", "armeabi", "armeabi-v7a", "arm64-v8a", "x86", "x86_64", "mips", "mips64", "riscv64"];

    public HashSet<string> Select(int sdk, IReadOnlyList<string> abis, int density)
    {
        if (_table.Has(3)) throw new NotSupportedException("该 APKS 包含 Play Asset Delivery 资源包，请提供已导出的 APK/OBB 安装包。");
        foreach (var variant in _table.Children(1).OrderByDescending(static v => v.Number(3)))
        {
            var target = variant.Child(1);
            if (!MatchesSdk(target.Child(1), sdk) || !MatchesAbi(target.Child(2), abis) || !MatchesMultiAbi(target.Child(4), abis) ||
                !MatchesDensity(target.Child(3), density)) continue;
            if (target.Has(5) || target.Child(6).Number(1) != 0) continue;
            HashSet<string> paths = new(StringComparer.Ordinal);
            foreach (var set in variant.Children(2))
            {
                var module = set.Child(1);
                if (module.Number(2) != 0 || module.Number(3) != 0 || module.Number(6) == 2) continue;
                var moduleTarget = module.Child(5);
                if (!MatchesSdk(moduleTarget.Child(1), sdk)) continue;
                if (moduleTarget.Has(2) || moduleTarget.Has(3) || moduleTarget.Has(5))
                    throw new NotSupportedException("该 APKS 包含按设备功能或地区分发的模块，请提供针对设备导出的 APKS。");
                foreach (var apk in set.Children(2))
                {
                    var apkTarget = apk.Child(1);
                    if (!MatchesAbi(apkTarget.Child(1), abis) || !MatchesSdk(apkTarget.Child(5), sdk) || !MatchesMultiAbi(apkTarget.Child(7), abis)) continue;
                    // Install all languages and densities in the selected variant. Android selects resources at runtime.
                    if (apkTarget.Has(6) || apkTarget.Has(8) || apkTarget.Has(9) || apkTarget.Has(10) || apkTarget.Has(11))
                        throw new NotSupportedException("该 APKS 使用额外设备定向，请提供针对设备导出的 APKS。");
                    if (apk.Has(3) || apk.Has(4)) paths.Add(apk.Text(2));
                }
            }
            if (paths.Count > 0) return paths;
        }
        throw new NotSupportedException("APKS 中没有与该设备匹配的安装变体。");
    }

    private static bool MatchesSdk(Message target, int sdk)
    {
        int[] values = target.Children(1).Select(static v => checked((int)v.Child(1).Number(1))).ToArray();
        int[] alternatives = target.Children(2).Select(static v => checked((int)v.Child(1).Number(1))).ToArray();
        int best = values.Concat(alternatives).Where(v => v <= sdk).DefaultIfEmpty(-1).Max();
        return values.Length == 0 ? best < 0 : values.Contains(best);
    }

    private static string Abi(Message value)
    {
        ulong alias = value.Number(1);
        return alias < (ulong)AbiNames.Length ? AbiNames[alias] : "unknown";
    }

    private static bool MatchesAbi(Message target, IReadOnlyList<string> abis)
    {
        string[] values = target.Children(1).Select(Abi).ToArray();
        string[] alternatives = target.Children(2).Select(Abi).ToArray();
        string? best = abis.FirstOrDefault(a => values.Contains(a, StringComparer.Ordinal) || alternatives.Contains(a, StringComparer.Ordinal));
        return values.Length == 0 ? best is null : best is not null && values.Contains(best, StringComparer.Ordinal);
    }

    private static bool MatchesMultiAbi(Message target, IReadOnlyList<string> abis)
    {
        var values = target.Children(1).Select(static m => m.Children(1).Select(Abi).ToArray()).ToArray();
        var alternatives = target.Children(2).Select(static m => m.Children(1).Select(Abi).ToArray()).ToArray();
        var best = values.Concat(alternatives).Where(a => Array.TrueForAll(a, v => abis.Contains(v, StringComparer.Ordinal)))
            .OrderByDescending(static a => a.Length).ThenBy(a => AbiOrder(a, abis)).FirstOrDefault();
        return values.Length == 0 ? best is null : best is not null && Array.Exists(values, v => v.SequenceEqual(best));
    }

    private static int AbiOrder(string[] values, IReadOnlyList<string> abis)
    {
        for (int i = 0; i < abis.Count; i++) if (values.Contains(abis[i], StringComparer.Ordinal)) return i;
        return int.MaxValue;
    }

    private static bool MatchesDensity(Message target, int density)
    {
        static int Dpi(Message value) => value.Has(2) ? checked((int)value.Number(2)) : value.Number(1) switch { 1 => 0, 2 => 120, 3 => 160, 4 => 213, 5 => 240, 6 => 320, 7 => 480, 8 => 640, _ => 0 };
        int[] values = target.Children(1).Select(Dpi).ToArray();
        int[] alternatives = target.Children(2).Select(Dpi).ToArray();
        if (values.Length + alternatives.Length == 0) return true;
        if (density <= 0) throw new NotSupportedException("APKS 变体需要设备屏幕密度信息。");
        int best = values.Concat(alternatives).Aggregate((best, candidate) => BetterDensity(candidate, best, density) ? candidate : best);
        return values.Contains(best);
    }

    private static bool BetterDensity(int candidate, int current, int requested)
    {
        if (candidate == current) return false;
        int high = Math.Max(candidate, current), low = Math.Min(candidate, current);
        if (requested >= high) return candidate == high;
        if (requested <= low) return candidate == low;
        bool preferLow = (2L * low - requested) * high > (long)requested * requested;
        return candidate == (preferLow ? low : high);
    }

    private sealed class Message
    {
        private readonly List<(int Tag, ulong Number, ReadOnlyMemory<byte> Data)> _fields = [];
        public static Message Parse(ReadOnlyMemory<byte> bytes)
        {
            Message message = new();
            int at = 0;
            while (at < bytes.Length)
            {
                if (message._fields.Count >= 32768) throw new InvalidDataException("APKS 目录包含过多字段。");
                ulong key = Varint(bytes.Span, ref at);
                int tag = checked((int)(key >> 3)), wire = (int)(key & 7);
                if (tag == 0) throw new InvalidDataException("APKS 目录字段无效。");
                ulong number = 0;
                ReadOnlyMemory<byte> data = default;
                if (wire == 0) number = Varint(bytes.Span, ref at);
                else
                {
                    int length = wire switch { 1 => 8, 2 => checked((int)Varint(bytes.Span, ref at)), 5 => 4, _ => throw new InvalidDataException("APKS 目录编码无效。") };
                    if (length > bytes.Length - at) throw new InvalidDataException("APKS 目录被截断。");
                    data = bytes.Slice(at, length); at += length;
                }
                message._fields.Add((tag, number, data));
            }
            return message;
        }
        private static ulong Varint(ReadOnlySpan<byte> bytes, ref int at)
        {
            ulong number = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (at >= bytes.Length) throw new InvalidDataException("APKS 目录被截断。");
                byte value = bytes[at++];
                if (shift == 63 && value > 1) throw new InvalidDataException("APKS 目录整数溢出。");
                number |= (ulong)(value & 127) << shift;
                if ((value & 128) == 0) return number;
            }
            throw new InvalidDataException("APKS 目录整数无效。");
        }
        public IEnumerable<Message> Children(int tag) => _fields.Where(f => f.Tag == tag).Select(static f => Parse(f.Data));
        public Message Child(int tag) => Children(tag).FirstOrDefault() ?? new();
        public ulong Number(int tag) => _fields.Find(f => f.Tag == tag).Number;
        public string Text(int tag) => Encoding.UTF8.GetString(_fields.Find(f => f.Tag == tag).Data.Span);
        public bool Has(int tag) => _fields.Exists(f => f.Tag == tag);
    }
}
