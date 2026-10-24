using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;
using Windows.Win32.System.SystemServices;

using IStream = Windows.Win32.System.Com.IStream;

namespace Andromeld.Platform.Windows;

/// <summary>STA/window thread owns OLE. Providers may synchronously wait for independent network/file workers, never UI work.</summary>
[SupportedOSPlatform("windows6.1")]
public static class ShellFileDrag
{
    public static bool Run(nint window, Func<IReadOnlyList<PromisedFile>> metadata, Func<int, string> getFile, CancellationToken ct)
    {
        WindowThread.Verify(new HWND(window));
        Exception? failure = null;
        FileData data = new(() =>
        {
            try { return metadata(); }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
        }, index =>
        {
            try { return getFile(index); }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
        });
        HRESULT result = PInvoke.DoDragDrop(data, new Source(ct), DROPEFFECT.DROPEFFECT_COPY, out DROPEFFECT effect);
        GC.KeepAlive(data);
        if (failure is not null)
        {
            throw new IOException("文件拖出失败。", failure);
        }

        result.ThrowOnFailure();
        return result.Value == 0x40100 && effect == DROPEFFECT.DROPEFFECT_COPY;
    }

    public static IDataObject CreateDataObject(Func<IReadOnlyList<PromisedFile>> metadata, Func<int, string> getFile)
    {
        return new FileData(metadata, getFile);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Source(CancellationToken ct) : IDropSource
    {
        public HRESULT QueryContinueDrag(BOOL fEscapePressed, MODIFIERKEYS_FLAGS grfKeyState)
        {
            if (fEscapePressed || ct.IsCancellationRequested)
            {
                return new HRESULT(0x40101);
            }

            return new HRESULT((grfKeyState & MODIFIERKEYS_FLAGS.MK_LBUTTON) == (MODIFIERKEYS_FLAGS)0 ? 0x40100 : 0);
        }

        public HRESULT GiveFeedback(DROPEFFECT dwEffect)
        {
            return new HRESULT(0x40102);
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FileData(Func<IReadOnlyList<PromisedFile>> metadata, Func<int, string> getFile) : IDataObject
    {
        private readonly short _contents = unchecked((short)PInvoke.RegisterClipboardFormat("FileContents"));
        private readonly short _descriptor = unchecked((short)PInvoke.RegisterClipboardFormat("FileGroupDescriptorW"));
        private FORMATETC Descriptor =>
            new() { cfFormat = _descriptor, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        private FORMATETC Contents => new() { cfFormat = _contents, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = 0, tymed = TYMED.TYMED_ISTREAM };

        public int QueryGetData(ref FORMATETC format)
        {
            return format.dwAspect == DVASPECT.DVASPECT_CONTENT &&
                   ((format.cfFormat == _descriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != TYMED.TYMED_NULL) ||
                    (format.cfFormat == _contents && format.lindex >= 0 && (format.tymed & TYMED.TYMED_ISTREAM) != TYMED.TYMED_NULL))
                ? 0
                : unchecked((int)0x80040064);
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            Marshal.ThrowExceptionForHR(QueryGetData(ref format));
            if (format.cfFormat == _descriptor)
            {
                IReadOnlyList<PromisedFile> files = metadata();
                if (files.Count is < 1 or > 256)
                {
                    throw new IOException("拖动文件数量无效。");
                }

                byte[] bytes = new byte[4 + (592 * files.Count)];
                BitConverter.TryWriteBytes(bytes.AsSpan(), files.Count);
                for (int index = 0; index < files.Count; index++)
                {
                    PromisedFile file = files[index];
                    int offset = 4 + (index * 592);
                    string name = file.Name;
                    if (name.Length is 0 or > 259 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
                    {
                        throw new IOException("拖动文件名无效。");
                    }

                    BitConverter.TryWriteBytes(bytes.AsSpan(offset), 0x80004004u | (file.Size >= 0 ? 0x40u : 0));
                    BitConverter.TryWriteBytes(bytes.AsSpan(offset + 36), 0x80u);
                    if (file.Size >= 0)
                    {
                        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 64), (uint)(file.Size >> 32));
                        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 68), (uint)file.Size);
                    }

                    Encoding.Unicode.GetBytes(name.AsSpan(), bytes.AsSpan(offset + 72));
                }

                HGLOBAL memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)bytes.Length);
                if (memory.IsNull)
                {
                    throw new OutOfMemoryException();
                }

                unsafe
                {
                    void* pointer = PInvoke.GlobalLock(memory);
                    if (pointer is null)
                    {
                        _ = PInvoke.GlobalFree(memory);
                        throw new OutOfMemoryException();
                    }

                    try { Marshal.Copy(bytes, 0, (nint)pointer, bytes.Length); }
                    finally { _ = PInvoke.GlobalUnlock(memory); }

                    medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = (nint)memory.Value };
                }
            }
            else
            {
                IReadOnlyList<PromisedFile> files = metadata();
                if (format.lindex >= files.Count)
                {
                    throw new COMException("Invalid file index", unchecked((int)0x80040068));
                }

                string path = getFile(format.lindex);
                PInvoke.SHCreateStreamOnFileEx(path, 0x20, 0, false, null!, out IStream stream).ThrowOnFailure();
                try
                {
                    medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)) };
                }
                finally { Marshal.ReleaseComObject(stream); }
            }
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            return direction == DATADIR.DATADIR_GET
                ? new Formats([Descriptor, Contents])
                : throw new COMException("Unsupported direction", unchecked((int)0x80004001));
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
        {
            throw new NotSupportedException();
        }

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = formatIn;
            formatOut.ptd = 0;
            return 0x40130;
        }

        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
        {
            throw new NotSupportedException();
        }

        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        {
            connection = 0;
            return unchecked((int)0x80040003);
        }

        public void DUnadvise(int connection)
        {
            throw new NotSupportedException();
        }

        public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
        {
            enumAdvise = null!;
            return unchecked((int)0x80040003);
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Formats(FORMATETC[] formats) : IEnumFORMATETC
    {
        private int _index;

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            int copied = 0;
            while (copied < celt && _index < formats.Length)
            {
                rgelt[copied++] = formats[_index++];
            }

            if (pceltFetched is { Length: > 0 })
            {
                pceltFetched[0] = copied;
            }

            return copied == celt ? 0 : 1;
        }

        public int Skip(int celt)
        {
            int remaining = formats.Length - _index;
            _index += Math.Min(celt, remaining);
            return celt <= remaining ? 0 : 1;
        }

        public int Reset()
        {
            _index = 0;
            return 0;
        }

        public void Clone(out IEnumFORMATETC newEnum)
        {
            newEnum = new Formats(formats) { _index = _index };
        }
    }
}