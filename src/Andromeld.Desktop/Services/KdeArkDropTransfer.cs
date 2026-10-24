using System.Text;

using Andromeld.Platform.Linux;

using Avalonia.Input;

namespace Andromeld.Desktop.Services;

/// <summary>Adapts the platform-neutral KDE drop service to Avalonia's native-data transfer.</summary>
internal static class KdeArkDropTransfer
{
    private static readonly DataFormat<byte[]> ServiceFormat = DataFormat.CreateBytesPlatformFormat(KdeArkDnd.ServiceMimeType);
    private static readonly DataFormat<byte[]> PathFormat = DataFormat.CreateBytesPlatformFormat(KdeArkDnd.PathMimeType);

    internal static DataTransfer Create(KdeArkDropService service)
    {
        DataTransfer data = new();
        DataTransferItem item = new();
        item.Set(ServiceFormat, Encoding.UTF8.GetBytes(service.ServiceName));
        item.Set(PathFormat, Encoding.UTF8.GetBytes(service.ObjectPath));
        data.Add(item);
        return data;
    }
}
