using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Andromeld.Protocol;

namespace Andromeld.Testing;

/// <summary>Official metadata/APK boundary replaced by deterministic synthetic bytes; never accesses the network.</summary>
public sealed class FakeUsbDownloads : HttpMessageHandler
{
    public byte[] Apk { get; } = Encoding.UTF8.GetBytes(new string('x', 200000));
    public long Version { get; set; } = 100;
    public bool Unavailable { get; set; }
    public bool CorruptApk { get; set; }
    public string? Metadata { get; set; }
    public ConcurrentQueue<Uri> Requests { get; } = new();
    public Func<Uri, CancellationToken, Task>? BeforeResponse { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI");
        Requests.Enqueue(uri);
        if (BeforeResponse is { } before) await before(uri, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable) throw new HttpRequestException("synthetic offline");
        if (uri == UsbHelperMetadata.MetadataUri)
            return new(HttpStatusCode.OK) { Content = new StringContent(Metadata ?? JsonSerializer.Serialize(new
            {
                packageName = "com.catchingnow.andfiles.helper", versionCode = Version, apk = "AndroMeld-WebUSB.apk", sha256 = Convert.ToHexStringLower(SHA256.HashData(Apk))
            })) };
        if (uri == UsbHelperMetadata.ApkUri)
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(CorruptApk ? [1, 2, 3] : Apk) };
        throw new InvalidOperationException("Unexpected synthetic download: " + uri);
    }
}
