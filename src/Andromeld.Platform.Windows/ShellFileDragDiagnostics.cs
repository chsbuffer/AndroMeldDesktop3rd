using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace Andromeld.Platform.Windows;

/// <summary>Native smoke check for the actual OLE formats and stream ownership; contains no phone data.</summary>
[SupportedOSPlatform("windows6.1")]
public static class ShellFileDragDiagnostics
{
    public static unsafe (string Name, long Size, byte[] Content) Read(IDataObject data, int index)
    {
        FORMATETC descriptor = new()
        {
            cfFormat = unchecked((short)PInvoke.RegisterClipboardFormat("FileGroupDescriptorW")),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };
        data.GetData(ref descriptor, out STGMEDIUM metadata);
        string name;
        long size;
        HGLOBAL memory = new((void*)metadata.unionmember);
        byte* pointer = (byte*)PInvoke.GlobalLock(memory);
        try
        {
            int count = *(int*)pointer;
            if (index < 0 || index >= count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            ReadOnlySpan<byte> item = new(pointer + 4 + (index * 592), 592);
            name = Encoding.Unicode.GetString(item[72..]).TrimEnd('\0');
            size = ((long)BitConverter.ToUInt32(item[64..]) << 32) | BitConverter.ToUInt32(item[68..]);
        }
        finally
        {
            _ = PInvoke.GlobalUnlock(memory);
            _ = PInvoke.GlobalFree(memory);
        }

        FORMATETC contents = new()
        {
            cfFormat = unchecked((short)PInvoke.RegisterClipboardFormat("FileContents")),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_ISTREAM
        };
        data.GetData(ref contents, out STGMEDIUM payload);
        IStream stream = (IStream)Marshal.GetObjectForIUnknown(payload.unionmember);
        try
        {
            using MemoryStream output = new();
            byte[] buffer = new byte[65536];
            int read;
            do
            {
                stream.Read(buffer, buffer.Length, (nint)(&read));
                output.Write(buffer, 0, read);
            } while (read > 0);

            return (name, size, output.ToArray());
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
            Marshal.Release(payload.unionmember);
        }
    }
}