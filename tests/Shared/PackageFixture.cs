using System.IO.Compression;
using System.Text;
using System.Buffers.Binary;
using System.Formats.Asn1;

namespace Andromeld.Testing;

/// <summary>Small synthetic binary-XML APKs, independently encoded from Android's chunk layout.</summary>
public static class PackageFixture
{
    public static byte[] Apk(string? split = null, string package = "org.example.seaglass", int version = 241, int minSdk = 29, string? abi = null, bool includeIcon = true, string resourceLayout = "regular", bool vectorIcon = false)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var target = zip.CreateEntry("AndroidManifest.xml").Open()) target.Write(Manifest(split, package, version, minSdk));
            using (var target = zip.CreateEntry("resources.arsc").Open()) target.Write(Resources(resourceLayout, vectorIcon));
            if (includeIcon)
            {
                using var target = zip.CreateEntry("res/icon.png").Open();
                target.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAB1ElEQVR4nO2bQZLCIBBFCTWHGm+hGz2jbsZb6K2cVVupSEOA/rQ0vJVlAtX/0zQJVBaXye/175XbpiXPy2nJuX/Xzd8ummOPGdEbehW+JWaE5y5YEe9cXEvQAEviCU7ThwEWxRMhbT51gzW2Gj13wTJrrWwRHAXv3FijT5Dm4TPgRzuAEh7n4/v34Xav6mvpLf3X4okaE7qaAiHxsf/30JUBCKYB2gHkwM31mhqgsgrE5mxKzOF273MVKClUteL2AM+AmgpNbZFGQGtAjXhEPyFgBkgHjTIBMgVSwcZSOtb2cT6KTwfxIlhT4RH9pBCdApJBl2ZJLk0ehEpHrMUyKGYANyq1Irj2UlkAzQCpEez2OaAHRAyQ3qQIEepPYhrMDNAOQJtpgHYA2kwDtAPQRsQA1BKV6k9iqZ0ZgOx8qB0h1EsL6iWLaDIFSk1AjjwhaoDkJkarHSHIuQBqTzDVtgTYwQgifRH7ArAa0OJ1WILhd4TgR2MU/LBng8RaTKsKvweV4/HWImPMdwHtALSZBuR+Y2OJ5+W0zAzQDkAb71z+p2YWIM0zA+jHSFmw1uq5C1bZavyYApZNCGkL1gCLJnCa2CJoyYSYlvnxdG6n325Gbub+A/VCzFjl4ZhPAAAAAElFTkSuQmCC"));
            }
            if (vectorIcon)
            {
                using var target = zip.CreateEntry("res/icon.xml").Open(); target.Write(VectorIcon());
            }
            if (abi is not null)
            {
                using var target = zip.CreateEntry("lib/" + abi + "/libtest.so").Open(); target.Write([1, 2, 3]);
            }
        }
        return output.ToArray();
    }

    public static byte[] Archive(params (string Name, byte[] Data)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in files)
            {
                using var target = zip.CreateEntry(name).Open(); target.Write(data);
            }
        return output.ToArray();
    }

    public static byte[] WithSigningCertificate(byte[] apk, byte[] certificate, uint scheme)
    {
        static byte[] Prefix(byte[] data)
        {
            byte[] output = new byte[data.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(output, data.Length); data.CopyTo(output, 4); return output;
        }
        byte[] signedData = Prefix([]).Concat(Prefix(Prefix(certificate))).Concat(Prefix([])).ToArray();
        byte[] signer = Prefix(signedData).Concat(Prefix([])).Concat(Prefix([])).ToArray();
        byte[] value = Prefix(Prefix(signer));
        using var block = new MemoryStream();
        using var writer = new BinaryWriter(block, Encoding.UTF8, leaveOpen: true);
        ulong size = (ulong)(value.Length + 36);
        writer.Write(size); writer.Write((ulong)value.Length + 4); writer.Write(scheme); writer.Write(value); writer.Write(size); writer.Write("APK Sig Block 42"u8);
        int central = BinaryPrimitives.ReadInt32LittleEndian(apk.AsSpan(apk.Length - 6));
        byte[] result = apk.AsSpan(0, central).ToArray().Concat(block.ToArray()).Concat(apk.AsSpan(central).ToArray()).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(result.Length - 6), central + (int)block.Length);
        return result;
    }

    public static byte[] WithV1Certificate(byte[] apk, byte[] certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier("1.2.840.113549.1.7.2");
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);
                using (writer.PushSetOf()) { }
                using (writer.PushSequence()) writer.WriteObjectIdentifier("1.2.840.113549.1.7.1");
                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0))) writer.WriteEncodedValue(certificate);
                using (writer.PushSetOf()) { }
            }
        }
        using var output = new MemoryStream(); output.Write(apk);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
        using (var entry = zip.CreateEntry("META-INF/CERT.RSA").Open()) entry.Write(writer.Encode());
        return output.ToArray();
    }

    private static byte[] VectorIcon()
    {
        using var contents = new MemoryStream();
        using var writer = new BinaryWriter(contents, Encoding.UTF8, leaveOpen: true);
        StringPool(writer, ["vector", "viewportWidth", "viewportHeight", "path", "pathData", "M0 0H64V64H0Z", "fillColor"]);
        Element(writer, 0, [(1, 16, 64), (2, 16, 64)]);
        Element(writer, 3, [(4, 3, 5), (6, 28, unchecked((int)0xff38a9af))]);
        using var xml = new MemoryStream(); using var header = new BinaryWriter(xml);
        header.Write((ushort)3); header.Write((ushort)8); header.Write((int)contents.Length + 8); header.Write(contents.ToArray());
        return xml.ToArray();
    }

    public static byte[] BundleTable()
    {
        static byte[] Message(params byte[][] fields) => fields.SelectMany(static f => f).ToArray();
        static byte[] Number(int tag, int value) => [(byte)(tag << 3), (byte)value];
        static byte[] Field(int tag, byte[] bytes)
        {
            using var output = new MemoryStream(); output.WriteByte((byte)((tag << 3) | 2));
            int length = bytes.Length;
            while (length >= 128) { output.WriteByte((byte)((length & 127) | 128)); length >>= 7; }
            output.WriteByte((byte)length); output.Write(bytes); return output.ToArray();
        }
        static byte[] Sdk(int sdk, int alternative) => Message(Field(1, Field(1, Number(1, sdk))), Field(2, Field(1, Number(1, alternative))));
        static byte[] Apk(string path, int abi = 0)
        {
            byte[] target = abi == 0 ? [] : Field(1, Message(Field(1, Number(1, abi)), Field(2, Number(1, abi == 3 ? 4 : 3))));
            return Message(Field(1, target), Field(2, Encoding.UTF8.GetBytes(path)), Field(3, []));
        }
        static byte[] Variant(string directory, int sdk, int alternative, int number) => Message(
            Field(1, Field(1, Sdk(sdk, alternative))), Number(3, number),
            Field(2, Message(Field(1, Message(Field(1, Encoding.UTF8.GetBytes("base")), Number(6, 1))),
                Field(2, Apk(directory + "/base.apk")), Field(2, Apk(directory + "/arm64.apk", 3)), Field(2, Apk(directory + "/x86.apk", 4)))));
        return Message(Field(1, Variant("old", 29, 35, 0)), Field(1, Variant("new", 35, 29, 1)));
    }

    private static byte[] Manifest(string? split, string package, int version, int minSdk)
    {
        string[] strings = ["manifest", "package", package, "versionCode", "versionName", "2.4.1", "split", split ?? "",
            "uses-sdk", "minSdkVersion", "targetSdkVersion", "application", "label", "Seaglass", "uses-permission", "name",
            "android.permission.CAMERA", "android.permission.INTERNET", "android.permission.POST_NOTIFICATIONS", "icon"];
        using var contents = new MemoryStream();
        using var writer = new BinaryWriter(contents, Encoding.UTF8, leaveOpen: true);
        StringPool(writer, strings);
        var rootAttrs = new List<(int Name, byte Type, int Data)> { (1, 3, 2), (3, 16, version), (4, 3, 5) };
        if (split is not null) rootAttrs.Add((6, 3, 7));
        Element(writer, 0, rootAttrs.ToArray());
        Element(writer, 8, [(9, 16, minSdk), (10, 16, 36)]);
        Element(writer, 11, [(12, 1, 0x7f010000), (19, 1, 0x7f010001)]);
        foreach (int permission in new[] { 16, 17, 18 }) Element(writer, 14, [(15, 3, permission)]);
        using var xml = new MemoryStream();
        using var header = new BinaryWriter(xml);
        header.Write((ushort)3); header.Write((ushort)8); header.Write((int)contents.Length + 8); header.Write(contents.ToArray());
        return xml.ToArray();
    }

    private static void StringPool(BinaryWriter writer, string[] strings, bool utf16 = false)
    {
        using var stringData = new MemoryStream();
        List<int> offsets = [];
        foreach (string value in strings)
        {
            offsets.Add((int)stringData.Length);
            byte[] bytes = (utf16 ? Encoding.Unicode : Encoding.UTF8).GetBytes(value);
            stringData.WriteByte((byte)value.Length);
            stringData.WriteByte(utf16 ? (byte)0 : (byte)bytes.Length);
            stringData.Write(bytes); stringData.WriteByte(0);
            if (utf16) stringData.WriteByte(0);
        }
        while (stringData.Length % 4 != 0) stringData.WriteByte(0);
        writer.Write((ushort)1); writer.Write((ushort)28); writer.Write(28 + strings.Length * 4 + (int)stringData.Length);
        writer.Write(strings.Length); writer.Write(0); writer.Write(utf16 ? 0 : 256); writer.Write(28 + strings.Length * 4); writer.Write(0);
        foreach (int offset in offsets) writer.Write(offset);
        writer.Write(stringData.ToArray());
    }

    private static byte[] Resources(string layout, bool vectorIcon)
    {
        using var chunks = new MemoryStream();
        using var writer = new BinaryWriter(chunks, Encoding.UTF8, leaveOpen: true);
        StringPool(writer, ["Seaglass", vectorIcon ? "res/icon.xml" : "res/icon.png"], utf16: layout == "utf16");
        using var packageContents = new MemoryStream();
        using var packageWriter = new BinaryWriter(packageContents, Encoding.UTF8, leaveOpen: true);
        StringPool(packageWriter, ["string"]);
        int keys = (int)packageContents.Length;
        StringPool(packageWriter, ["app_name", "app_icon"]);
        byte flags = layout switch { "sparse" => 1, "offset16" => 2, _ => 0 };
        bool compact = layout == "compact";
        int offsetBytes = flags == 2 ? 4 : 8, entryBytes = compact ? 8 : 16;
        packageWriter.Write((ushort)0x201); packageWriter.Write((ushort)48); packageWriter.Write(48 + offsetBytes + entryBytes * 2);
        packageWriter.Write((byte)1); packageWriter.Write(flags); packageWriter.Write((ushort)0);
        packageWriter.Write(2); packageWriter.Write(48 + offsetBytes); packageWriter.Write(28); packageWriter.Write(new byte[24]);
        for (int i = 0; i < 2; i++)
        {
            if (flags == 1) { packageWriter.Write((ushort)i); packageWriter.Write((ushort)(i * entryBytes / 4)); }
            else if (flags == 2) packageWriter.Write((ushort)(i * entryBytes / 4));
            else packageWriter.Write(i * entryBytes);
        }
        for (int i = 0; i < 2; i++)
        {
            packageWriter.Write((ushort)(compact ? i : 8)); packageWriter.Write((ushort)(compact ? 0x308 : 0)); packageWriter.Write(i);
            if (!compact) { packageWriter.Write((ushort)8); packageWriter.Write((byte)0); packageWriter.Write(layout == "cycle" ? (byte)1 : (byte)3); packageWriter.Write(layout == "cycle" ? 0x7f010000 + i : i); }
        }
        writer.Write((ushort)0x200); writer.Write((ushort)288); writer.Write(288 + (int)packageContents.Length);
        writer.Write(0x7f);
        byte[] packageName = new byte[256]; Encoding.Unicode.GetBytes("org.example.seaglass").CopyTo(packageName, 0); writer.Write(packageName);
        writer.Write(288); writer.Write(1); writer.Write(288 + keys); writer.Write(2); writer.Write(0);
        writer.Write(packageContents.ToArray());
        using var table = new MemoryStream();
        using var header = new BinaryWriter(table);
        header.Write((ushort)2); header.Write((ushort)12); header.Write(12 + (int)chunks.Length); header.Write(1); header.Write(chunks.ToArray());
        return table.ToArray();
    }

    private static void Element(BinaryWriter writer, int name, (int Name, byte Type, int Data)[] attributes)
    {
        writer.Write((ushort)0x102); writer.Write((ushort)16); writer.Write(36 + attributes.Length * 20);
        writer.Write(1); writer.Write(-1); writer.Write(-1); writer.Write(name);
        writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)attributes.Length);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
        foreach (var attr in attributes)
        {
            writer.Write(-1); writer.Write(attr.Name); writer.Write(-1);
            writer.Write((ushort)8); writer.Write((byte)0); writer.Write(attr.Type); writer.Write(attr.Data);
        }
    }
}
