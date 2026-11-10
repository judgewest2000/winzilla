using System.Runtime.InteropServices;
using System.Text;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Winzilla.Services;

/// <summary>One file or folder to be pasted, with its path relative to the pasted root.</summary>
public sealed record RemoteNode(string RemotePath, string RelativePath, bool IsDirectory, long Size, DateTime? Modified);

/// <summary>An open remote file plus a callback to run once it has been closed.</summary>
public sealed record OpenedRemote(Stream Stream, Action Closed);

/// <summary>
/// Puts remote files on the Windows clipboard the way Explorer does for zip folders and similar: as virtual files.
/// Nothing is downloaded when the items are copied; Explorer pulls each file's bytes when the user pastes.
/// The data object lives on its own STA thread with a message pump, so a paste can block there
/// while the UI thread stays free.
/// </summary>
public sealed class RemoteClipboard
{
    private readonly RemoteDataObject _data;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly ManualResetEvent _stop = new(false);
    private readonly CancellationTokenSource _cts = new();
    private int _setResult;

    public RemoteClipboard(IReadOnlyList<FileEntry> items, IRemoteClient client, bool cut,
        Func<RemoteNode, CancellationToken, Task<OpenedRemote>> open, Action moved)
    {
        _data = new RemoteDataObject(items, client, cut, open, moved, _cts.Token);
        _thread = new Thread(Run) { IsBackground = true, Name = "Winzilla clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Raised on the clipboard thread once these items are no longer on the clipboard.</summary>
    public event Action? Ended;

    /// <summary>True while a Winzilla clipboard (from any instance) is on the clipboard.</summary>
    public static bool IsOnClipboard => Native.IsClipboardFormatAvailable(Native.OwnFormat);

    public void Start()
    {
        _thread.Start();
        _started.Wait();
        if (_setResult != 0) Marshal.ThrowExceptionForHR(_setResult);
    }

    public void Stop()
    {
        _cts.Cancel();
        _stop.Set();
    }

    private void Run()
    {
        Native.OleInitialize(IntPtr.Zero);
        try
        {
            _setResult = Native.OleSetClipboard(_data);
            _started.Set();
            if (_setResult != 0) return;

            var handles = new[] { _stop.SafeWaitHandle.DangerousGetHandle() };
            while (true)
            {
                if (Native.MsgWaitForMultipleObjectsEx(1, handles, 250, Native.QsAllInput, Native.MwmoInputAvailable) == 0) break;
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Native.PmRemove))
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                if (Native.OleIsCurrentClipboard(_data) != 0) break;
            }
        }
        finally
        {
            _started.Set();
            if (Native.OleIsCurrentClipboard(_data) == 0) Native.OleSetClipboard(null);
            _data.CloseStreams();
            Native.OleUninitialize();
            Ended?.Invoke();
        }
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class RemoteDataObject(IReadOnlyList<FileEntry> items, IRemoteClient client, bool cut,
    Func<RemoteNode, CancellationToken, Task<OpenedRemote>> open, Action moved, CancellationToken ct) : ComTypes.IDataObject
{
    private const int DvEFormatEtc = unchecked((int)0x80040064);
    private const int DvELindex = unchecked((int)0x80040068);
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int OleEAdviseNotSupported = unchecked((int)0x80040003);
    private const int DataSSameFormatEtc = 0x00040130;
    private const int DropEffectMove = 2;
    private const int ClipboardDescriptorSize = 592;

    private readonly object _lock = new();
    private readonly List<RemoteStream> _streams = [];
    private List<RemoteNode>? _nodes;
    private int _movedReported;

    private static readonly short Descriptor = (short)Native.RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly short Contents = (short)Native.RegisterClipboardFormat("FileContents");
    private static readonly short PreferredEffect = (short)Native.RegisterClipboardFormat("Preferred DropEffect");
    private static readonly short PerformedEffect = (short)Native.RegisterClipboardFormat("Performed DropEffect");
    private static readonly short PasteSucceeded = (short)Native.RegisterClipboardFormat("Paste Succeeded");
    private static readonly short Own = (short)Native.OwnFormat;

    public void GetData(ref ComTypes.FORMATETC format, out ComTypes.STGMEDIUM medium)
    {
        bool hglobal = (format.tymed & ComTypes.TYMED.TYMED_HGLOBAL) != 0;
        if (format.cfFormat == Descriptor && hglobal) medium = HGlobal(BuildDescriptor());
        else if (format.cfFormat == PreferredEffect && hglobal) medium = HGlobal(BitConverter.GetBytes(cut ? DropEffectMove : 1));
        else if (format.cfFormat == Own && hglobal) medium = HGlobal(new byte[4]);
        else if (format.cfFormat == Contents && (format.tymed & ComTypes.TYMED.TYMED_ISTREAM) != 0) medium = OpenContents(format.lindex);
        else throw new COMException("Unsupported format.", DvEFormatEtc);
    }

    public void GetDataHere(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium) =>
        throw new COMException("Not implemented.", ENotImpl);

    public int QueryGetData(ref ComTypes.FORMATETC format)
    {
        bool hglobal = (format.tymed & ComTypes.TYMED.TYMED_HGLOBAL) != 0;
        bool supported = hglobal && (format.cfFormat == Descriptor || format.cfFormat == PreferredEffect || format.cfFormat == Own)
            || format.cfFormat == Contents && (format.tymed & ComTypes.TYMED.TYMED_ISTREAM) != 0;
        return supported ? 0 : DvEFormatEtc;
    }

    public int GetCanonicalFormatEtc(ref ComTypes.FORMATETC formatIn, out ComTypes.FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DataSSameFormatEtc;
    }

    public void SetData(ref ComTypes.FORMATETC formatIn, ref ComTypes.STGMEDIUM medium, bool release)
    {
        // Explorer tells the source how a paste ended; only a completed move means the originals can go.
        if (cut && (formatIn.cfFormat == PerformedEffect || formatIn.cfFormat == PasteSucceeded)
            && medium.tymed == ComTypes.TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero)
        {
            int effect = ReadDword(medium.unionmember);
            if (effect == DropEffectMove && Interlocked.Exchange(ref _movedReported, 1) == 0) _ = Task.Run(moved);
        }
        if (release) Native.ReleaseStgMedium(ref medium);
    }

    public ComTypes.IEnumFORMATETC EnumFormatEtc(ComTypes.DATADIR direction)
    {
        if (direction != ComTypes.DATADIR.DATADIR_GET) throw new COMException("Not implemented.", ENotImpl);
        ComTypes.FORMATETC[] formats =
        [
            Format(Descriptor, ComTypes.TYMED.TYMED_HGLOBAL),
            Format(Contents, ComTypes.TYMED.TYMED_ISTREAM),
            Format(PreferredEffect, ComTypes.TYMED.TYMED_HGLOBAL),
            Format(Own, ComTypes.TYMED.TYMED_HGLOBAL),
        ];
        Marshal.ThrowExceptionForHR(Native.SHCreateStdEnumFmtEtc((uint)formats.Length, formats, out var enumerator));
        return enumerator;
    }

    public int DAdvise(ref ComTypes.FORMATETC pFormatetc, ComTypes.ADVF advf, ComTypes.IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OleEAdviseNotSupported;
    }

    public void DUnadvise(int connection) => throw new COMException("Advise not supported.", OleEAdviseNotSupported);

    public int EnumDAdvise(out ComTypes.IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return OleEAdviseNotSupported;
    }

    public void CloseStreams()
    {
        RemoteStream[] open;
        lock (_lock) open = [.. _streams];
        foreach (var s in open) s.Close();
    }

    private static ComTypes.FORMATETC Format(short format, ComTypes.TYMED tymed) => new()
    {
        cfFormat = format,
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = tymed,
    };

    private static int ReadDword(IntPtr hglobal)
    {
        IntPtr p = Native.GlobalLock(hglobal);
        if (p == IntPtr.Zero) return 0;
        try { return Marshal.ReadInt32(p); }
        finally { Native.GlobalUnlock(hglobal); }
    }

    private static ComTypes.STGMEDIUM HGlobal(byte[] bytes)
    {
        IntPtr handle = Native.GlobalAlloc(Native.GmemMoveable, (UIntPtr)bytes.Length);
        IntPtr p = Native.GlobalLock(handle);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Native.GlobalUnlock(handle);
        return new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = handle, pUnkForRelease = null! };
    }

    private ComTypes.STGMEDIUM OpenContents(int index)
    {
        var nodes = Nodes();
        if (index < 0 || index >= nodes.Count || nodes[index].IsDirectory) throw new COMException("Bad index.", DvELindex);

        var stream = new RemoteStream(nodes[index], open, ct);
        lock (_lock)
        {
            _streams.RemoveAll(s => s.IsClosed);
            _streams.Add(stream);
        }
        IntPtr unknown = Marshal.GetComInterfaceForObject<RemoteStream, ComTypes.IStream>(stream);
        return new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_ISTREAM, unionmember = unknown, pUnkForRelease = null! };
    }

    /// <summary>The copied items and everything inside copied folders, parents before children.</summary>
    private List<RemoteNode> Nodes()
    {
        lock (_lock)
        {
            if (_nodes is not null) return _nodes;

            var list = new List<RemoteNode>();
            void Add(FileEntry entry, string relative)
            {
                list.Add(new RemoteNode(entry.FullPath, relative, entry.IsDirectory, entry.Size, entry.Modified));
                if (!entry.IsDirectory) return;
                foreach (var child in client.ListAsync(entry.FullPath, ct).GetAwaiter().GetResult())
                    Add(child, relative + "\\" + child.Name);
            }
            foreach (var item in items) Add(item, item.Name);
            return _nodes = list;
        }
    }

    private byte[] BuildDescriptor()
    {
        const uint attributes = 0x4, writeTime = 0x20, fileSize = 0x40, progressUi = 0x4000;
        var nodes = Nodes();
        var buffer = new byte[4 + ClipboardDescriptorSize * nodes.Count];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), nodes.Count);

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var entry = buffer.AsSpan(4 + ClipboardDescriptorSize * i, ClipboardDescriptorSize);
            uint flags = attributes | progressUi;
            if (!node.IsDirectory) flags |= fileSize;
            if (node.Modified is not null) flags |= writeTime;

            BitConverter.TryWriteBytes(entry[0..], flags);
            BitConverter.TryWriteBytes(entry[36..], node.IsDirectory ? 0x10u : 0x80u);
            if (node.Modified is { } modified) BitConverter.TryWriteBytes(entry[56..], modified.ToFileTime());
            if (!node.IsDirectory)
            {
                BitConverter.TryWriteBytes(entry[64..], (uint)(node.Size >> 32));
                BitConverter.TryWriteBytes(entry[68..], (uint)node.Size);
            }
            string name = node.RelativePath.Length > 259 ? node.RelativePath[..259] : node.RelativePath;
            Encoding.Unicode.GetBytes(name, entry[72..]);
        }
        return buffer;
    }
}

/// <summary>Streams one remote file to the paste target on demand, opening the connection only when the first byte is wanted.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class RemoteStream(RemoteNode node, Func<RemoteNode, CancellationToken, Task<OpenedRemote>> open, CancellationToken ct) : ComTypes.IStream
{
    private const int StgEReadFault = unchecked((int)0x8003001E);
    private const int StgEAccessDenied = unchecked((int)0x80030005);
    private const int ENotImpl = unchecked((int)0x80004001);

    private Stream? _stream;
    private Action? _closed;
    private long _position;
    private volatile bool _finished;

    public bool IsClosed => _stream is null;

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        int read = 0;
        if (!_finished)
        {
            try
            {
                EnsureOpen();
                read = _stream!.Read(pv, 0, cb);
                _position += read;
                if (read == 0 || _position >= node.Size) Finish();
            }
            catch (Exception ex)
            {
                Close();
                throw new COMException(ex.Message, StgEReadFault);
            }
        }
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        long target = dwOrigin switch { 0 => dlibMove, 1 => _position + dlibMove, _ => node.Size + dlibMove };
        if (target < 0) throw new COMException("Bad seek.", StgEReadFault);

        try
        {
            if (target >= node.Size)
            {
                Finish();
                _position = target;
            }
            else
            {
                if (target < _position || _finished)
                {
                    Close();
                    _position = 0;
                    _finished = false;
                }
                if (target > _position) Skip(target);
            }
        }
        catch (Exception ex) when (ex is not COMException)
        {
            Close();
            throw new COMException(ex.Message, StgEReadFault);
        }
        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, _position);
    }

    public void Stat(out ComTypes.STATSTG pstatstg, int grfStatFlag) =>
        pstatstg = new ComTypes.STATSTG { type = 2, cbSize = node.Size };

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("Read only.", StgEAccessDenied);
    public void SetSize(long libNewSize) => throw new COMException("Read only.", StgEAccessDenied);
    public void CopyTo(ComTypes.IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new COMException("Not implemented.", ENotImpl);
    public void Commit(int grfCommitFlags) { }
    public void Revert() { }
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Not implemented.", ENotImpl);
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Not implemented.", ENotImpl);
    public void Clone(out ComTypes.IStream ppstm) => throw new COMException("Not implemented.", ENotImpl);

    public void Close()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        var closed = Interlocked.Exchange(ref _closed, null);
        try { stream?.Dispose(); }
        finally { closed?.Invoke(); }
    }

    private void Finish()
    {
        _finished = true;
        Close();
    }

    private void EnsureOpen()
    {
        if (_stream is not null) return;
        var opened = open(node, ct).GetAwaiter().GetResult();
        _stream = opened.Stream;
        _closed = opened.Closed;
    }

    private void Skip(long target)
    {
        EnsureOpen();
        var buffer = new byte[81920];
        while (_position < target)
        {
            int n = _stream!.Read(buffer, 0, (int)Math.Min(buffer.Length, target - _position));
            if (n == 0) break;
            _position += n;
        }
    }
}

internal static class Native
{
    public const uint GmemMoveable = 0x2;
    public const uint QsAllInput = 0x4FF;
    public const uint MwmoInputAvailable = 0x4;
    public const uint PmRemove = 0x1;

    public static readonly uint OwnFormat = RegisterClipboardFormat("Winzilla.RemoteItems");

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("ole32.dll")] public static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] public static extern void OleUninitialize();
    [DllImport("ole32.dll")] public static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject? dataObject);
    [DllImport("ole32.dll")] public static extern int OleIsCurrentClipboard([MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject dataObject);
    [DllImport("ole32.dll")] public static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);

    [DllImport("shell32.dll")]
    public static extern int SHCreateStdEnumFmtEtc(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] ComTypes.FORMATETC[] formats,
        out ComTypes.IEnumFORMATETC enumerator);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] public static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GlobalUnlock(IntPtr handle);
}
