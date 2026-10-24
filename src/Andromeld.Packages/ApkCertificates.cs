using System.Buffers.Binary;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Andromeld.Packages;

internal static class ApkCertificates
{
    public static PackageCertificate[] Read(Stream stream, ZipArchive zip)
    {
        List<PackageCertificate> certificates = [];
        byte[] tail = new byte[(int)Math.Min(stream.Length, ushort.MaxValue + 22)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (U32(tail.AsSpan(i)) != 0x06054b50 || i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) != tail.Length) continue;
            long directory = U32(tail.AsSpan(i + 16));
            if (directory < 24 || directory > stream.Length) break;
            stream.Position = directory - 24;
            byte[] footer = new byte[24]; stream.ReadExactly(footer);
            if (!footer.AsSpan(8).SequenceEqual("APK Sig Block 42"u8)) break;
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(footer);
            if (size < 24 || size > 16 * 1024 * 1024 || size + 8 > (ulong)directory) throw new InvalidDataException("APK 签名块长度无效。");
            byte[] block = new byte[checked((int)size + 8)];
            stream.Position = directory - block.Length; stream.ReadExactly(block);
            if (BinaryPrimitives.ReadUInt64LittleEndian(block) != size) throw new InvalidDataException("APK 签名块长度不匹配。");
            int at = 8;
            while (at < block.Length - 24)
            {
                ulong length = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(at)); at += 8;
                if (length < 4 || length > (ulong)(block.Length - 24 - at)) throw new InvalidDataException("APK 签名条目无效。");
                uint id = U32(block.AsSpan(at));
                if (id is 0x7109871a or 0xf05368c0 or 0x1b93ad61)
                {
                    ReadOnlyMemory<byte> data = block.AsMemory(at + 4, (int)length - 4);
                    var signers = Take(ref data);
                    while (!signers.IsEmpty)
                    {
                        var signer = Take(ref signers);
                        var signedData = Take(ref signer);
                        _ = Take(ref signedData); // Digests.
                        var chain = Take(ref signedData);
                        Add(certificates, Take(ref chain).Span);
                    }
                }
                at += (int)length;
            }
            break;
        }
        if (certificates.Count == 0)
        {
            foreach (var entry in zip.Entries.Where(static e => e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) &&
                         Path.GetExtension(e.FullName).ToUpperInvariant() is ".RSA" or ".DSA" or ".EC"))
            {
                var reader = new AsnReader(ApkReader.ReadEntry(entry), AsnEncodingRules.BER).ReadSequence();
                _ = reader.ReadObjectIdentifier();
                var signedData = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
                _ = signedData.ReadInteger(); _ = signedData.ReadSetOf(); _ = signedData.ReadSequence();
                if (!signedData.HasData || !signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) continue;
                var certs = signedData.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
                while (certs.HasData) Add(certificates, certs.ReadEncodedValue().Span);
            }
        }
        return certificates.DistinctBy(static c => c.Sha256, StringComparer.Ordinal).ToArray();
    }

    private static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    private static ReadOnlyMemory<byte> Take(ref ReadOnlyMemory<byte> data)
    {
        if (data.Length < 4) throw new InvalidDataException("APK 签名数据被截断。");
        uint size = U32(data.Span);
        if (size > data.Length - 4) throw new InvalidDataException("APK 签名字段被截断。");
        var value = data.Slice(4, (int)size);
        data = data.Slice(4 + (int)size);
        return value;
    }

    private static void Add(List<PackageCertificate> certificates, ReadOnlySpan<byte> bytes)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(bytes);
        certificates.Add(new(certificate.Subject, certificate.GetCertHashString(HashAlgorithmName.SHA256)));
    }
}
