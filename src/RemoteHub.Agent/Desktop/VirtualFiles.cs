using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace RemoteHub.Agent;

/// <summary>
/// Files copied on the other side, on this clipboard as virtual files (as RDP does it): the list – names, sizes, folders
/// (FileGroupDescriptorW) – is there at once, the contents of each file (FileContents) only when something pastes. Explorer
/// reads the list whenever the clipboard changes (to offer "Paste"); a list costs nothing, so nothing is transferred until
/// a paste asks for a file's contents – then <c>fetch</c> gets the whole copy once and the paste reads the files.
/// Used by the agent's helper (copied in RemoteHub) and by RemoteHub (copied on the device). Call on an STA thread.
/// </summary>
public static unsafe partial class VirtualFiles
{
    private const int S_OK = 0, E_NOTIMPL = unchecked((int)0x80004001), DV_E_FORMATETC = unchecked((int)0x80040064),
        OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003), DATA_S_SAMEFORMATETC = 0x00040130, E_FAIL = unchecked((int)0x80004005);
    private const uint TYMED_HGLOBAL = 1, TYMED_ISTREAM = 4, DVASPECT_CONTENT = 1;

    private static readonly ushort DescriptorFormat = (ushort)RegisterClipboardFormatW("FileGroupDescriptorW");
    private static readonly ushort ContentsFormat = (ushort)RegisterClipboardFormatW("FileContents");
    private static readonly ushort DropEffectFormat = (ushort)RegisterClipboardFormatW("Preferred DropEffect");
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid DataObjectIid = new("0000010e-0000-0000-C000-000000000046");

    /// <summary>
    /// Puts the files of <paramref name="transfer"/> on the clipboard without their data. <paramref name="fetch"/> runs on
    /// this thread when a paste first needs a file: it gets the whole copy and returns the local path of every item (in
    /// the transfer's order), or null if it didn't arrive. Returns the data object (for <see cref="IsCurrent"/>), or 0.
    /// </summary>
    public static nint Set(DesktopTransfer transfer, Func<IReadOnlyList<string>?> fetch)
    {
        OleInitialize(IntPtr.Zero);
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(new DataObject(transfer, fetch), CreateComInterfaceFlags.None);
        try
        {
            if ((LastError = Marshal.QueryInterface(unknown, in DataObjectIid, out var dataObject)) != S_OK) return 0;
            if ((LastError = OleSetClipboard(dataObject)) != S_OK) { Marshal.Release(dataObject); return 0; }
            return dataObject; // one reference kept by the caller for IsCurrent; the clipboard holds its own
        }
        finally { Marshal.Release(unknown); }
    }

    /// <summary>
    /// The agent's helper runs as LocalSystem; a paste comes from the signed-in user's programs (Explorer). Without this,
    /// COM refuses their calls into the helper's data object and the paste falls back to the plain clipboard – where the
    /// contents aren't. Allows local calls from interactive users and SYSTEM, from medium integrity up; nothing remote.
    /// The helper offers no other COM object. Once per process, on the helper's UI thread before anything is offered.
    /// </summary>
    public static int AllowInteractiveCallers()
    {
        OleInitialize(IntPtr.Zero);
        // COM_RIGHTS_EXECUTE | COM_RIGHTS_EXECUTE_LOCAL for SYSTEM and interactive users; label: nothing below medium.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("O:SYG:SYD:(A;;0x3;;;SY)(A;;0x3;;;IU)S:(ML;;NX;;;ME)", 1, out var relative, 0))
            return Marshal.GetLastPInvokeError();
        // COM wants the descriptor in absolute form (the conversion gives self-relative); the buffers live for the process.
        uint sdSize = 0, daclSize = 0, saclSize = 0, ownerSize = 0, groupSize = 0;
        MakeAbsoluteSD(relative, 0, ref sdSize, 0, ref daclSize, 0, ref saclSize, 0, ref ownerSize, 0, ref groupSize);
        nint Buffer(uint size) => size == 0 ? 0 : Marshal.AllocHGlobal((int)size);
        var (sd, dacl, sacl, owner, group) = (Buffer(sdSize), Buffer(daclSize), Buffer(saclSize), Buffer(ownerSize), Buffer(groupSize));
        if (!MakeAbsoluteSD(relative, sd, ref sdSize, dacl, ref daclSize, sacl, ref saclSize, owner, ref ownerSize, group, ref groupSize))
            return Marshal.GetLastPInvokeError();
        // Authentication: connect level; impersonation: identify – the helper never acts as the caller.
        return CoInitializeSecurity(sd, -1, 0, 0, 2 /* RPC_C_AUTHN_LEVEL_CONNECT */, 2 /* RPC_C_IMP_LEVEL_IDENTIFY */, 0, 0, 0);
    }

    /// <summary>For the log: what <see cref="AllowInteractiveCallers"/> returned in this process (0 = allowed).</summary>
    public static int SecurityResult { get; set; }

    /// <summary>For the log: the HRESULT of the last <see cref="Set"/> that failed (0 when it worked).</summary>
    public static int LastError { get; private set; }

    /// <summary>The clipboard still holds this data object (nothing else was copied since).</summary>
    public static bool IsCurrent(nint dataObject) => dataObject != 0 && OleIsCurrentClipboard(dataObject) == S_OK;

    /// <summary>Drops the reference <see cref="Set"/> returned.</summary>
    public static void Release(nint dataObject)
    {
        if (dataObject != 0) Marshal.Release(dataObject);
    }

    /// <summary>FILEGROUPDESCRIPTORW: the count, then one FILEDESCRIPTORW (592 bytes) per item – names relative, folders marked.</summary>
    internal static byte[] Descriptor(DesktopTransfer t)
    {
        const int size = 592;
        var bytes = new byte[4 + t.Items.Length * size];
        BitConverter.GetBytes(t.Items.Length).CopyTo(bytes, 0);
        for (var i = 0; i < t.Items.Length; i++)
        {
            var item = t.Items[i];
            var at = 4 + i * size;
            const uint attributes = 0x4, fileSize = 0x40, progressUi = 0x4000, unicode = 0x80000000;
            BitConverter.GetBytes(attributes | fileSize | progressUi | unicode).CopyTo(bytes, at);                 // dwFlags
            BitConverter.GetBytes(item.Folder ? 0x10u /* DIRECTORY */ : 0x80u /* NORMAL */).CopyTo(bytes, at + 36); // dwFileAttributes
            BitConverter.GetBytes((uint)(item.Size >> 32)).CopyTo(bytes, at + 64);                                 // nFileSizeHigh
            BitConverter.GetBytes((uint)(item.Size & 0xFFFFFFFF)).CopyTo(bytes, at + 68);                          // nFileSizeLow
            var name = (DesktopWire.SafeRelativePath(item.Path) ?? Path.GetFileName(item.Path)).Replace('/', '\\');
            var chars = name.Length > 259 ? name[..259] : name;
            System.Text.Encoding.Unicode.GetBytes(chars).CopyTo(bytes, at + 72);                                   // cFileName
        }
        return bytes;
    }

    [GeneratedComClass]
    internal sealed partial class DataObject(DesktopTransfer transfer, Func<IReadOnlyList<string>?> fetch) : IDataObject
    {
        private IReadOnlyList<string>? _items;
        private bool _fetched;

        private (ushort Format, uint Tymed, int Index)[] Formats =>
            [(DescriptorFormat, TYMED_HGLOBAL, -1), (ContentsFormat, TYMED_ISTREAM, -1), (DropEffectFormat, TYMED_HGLOBAL, -1)];

        public int GetData(FORMATETC* format, STGMEDIUM* medium)
        {
            *medium = default;
            if ((format->dwAspect & DVASPECT_CONTENT) == 0) return DV_E_FORMATETC;
            if (format->cfFormat == DescriptorFormat && (format->tymed & TYMED_HGLOBAL) != 0) return Global(Descriptor(transfer), medium);
            if (format->cfFormat == DropEffectFormat && (format->tymed & TYMED_HGLOBAL) != 0) return Global(BitConverter.GetBytes(1), medium); // DROPEFFECT_COPY
            if (format->cfFormat != ContentsFormat || (format->tymed & TYMED_ISTREAM) == 0) return DV_E_FORMATETC;
            var index = format->lindex;
            if (index < 0 || index >= transfer.Items.Length || transfer.Items[index].Folder) return DV_E_FORMATETC;
            // The paste wants a file's contents: now (once) the whole copy is fetched.
            if (!_fetched) { _fetched = true; _items = fetch(); }
            if (_items is not { } items || index >= items.Count) { _fetched = false; return E_FAIL; }
            if (SHCreateStreamOnFileEx(items[index], 0x00000040 /* STGM_READ | STGM_SHARE_DENY_NONE */, 0, false, 0, out var stream) != S_OK) return E_FAIL;
            medium->tymed = TYMED_ISTREAM;
            medium->unionmember = stream;
            return S_OK;
        }

        public int GetDataHere(FORMATETC* format, STGMEDIUM* medium) => E_NOTIMPL;

        public int QueryGetData(FORMATETC* format) =>
            (format->dwAspect & DVASPECT_CONTENT) != 0 && Formats.Any(f => f.Format == format->cfFormat && (f.Tymed & format->tymed) != 0) ? S_OK : DV_E_FORMATETC;

        public int GetCanonicalFormatEtc(FORMATETC* formatIn, FORMATETC* formatOut)
        {
            *formatOut = *formatIn;
            formatOut->ptd = 0;
            return DATA_S_SAMEFORMATETC;
        }

        public int SetData(FORMATETC* format, STGMEDIUM* medium, int release) => E_NOTIMPL;

        public int EnumFormatEtc(uint direction, nint* enumerator)
        {
            *enumerator = 0;
            if (direction != 1 /* DATADIR_GET */) return E_NOTIMPL;
            var list = Formats.Select(f => new FORMATETC { cfFormat = f.Format, dwAspect = DVASPECT_CONTENT, lindex = f.Index, tymed = f.Tymed }).ToArray();
            fixed (FORMATETC* p = list) return SHCreateStdEnumFmtEtc((uint)list.Length, p, enumerator);
        }

        public int DAdvise(FORMATETC* format, uint advf, nint sink, uint* connection) => OLE_E_ADVISENOTSUPPORTED;
        public int DUnadvise(uint connection) => OLE_E_ADVISENOTSUPPORTED;
        public int EnumDAdvise(nint* enumerator) => OLE_E_ADVISENOTSUPPORTED;

        private static int Global(byte[] bytes, STGMEDIUM* medium)
        {
            var memory = GlobalAlloc(0x0002, (nuint)Math.Max(1, bytes.Length)); // GMEM_MOVEABLE
            if (memory == 0) return E_FAIL;
            Marshal.Copy(bytes, 0, GlobalLock(memory), bytes.Length);
            GlobalUnlock(memory);
            medium->tymed = TYMED_HGLOBAL;
            medium->unionmember = memory;
            return S_OK;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FORMATETC { public ushort cfFormat; public nint ptd; public uint dwAspect; public int lindex; public uint tymed; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STGMEDIUM { public uint tymed; public nint unionmember; public nint pUnkForRelease; }

    [GeneratedComInterface, Guid("0000010e-0000-0000-C000-000000000046")]
    internal partial interface IDataObject
    {
        [PreserveSig] int GetData(FORMATETC* format, STGMEDIUM* medium);
        [PreserveSig] int GetDataHere(FORMATETC* format, STGMEDIUM* medium);
        [PreserveSig] int QueryGetData(FORMATETC* format);
        [PreserveSig] int GetCanonicalFormatEtc(FORMATETC* formatIn, FORMATETC* formatOut);
        [PreserveSig] int SetData(FORMATETC* format, STGMEDIUM* medium, int release);
        [PreserveSig] int EnumFormatEtc(uint direction, nint* enumerator);
        [PreserveSig] int DAdvise(FORMATETC* format, uint advf, nint sink, uint* connection);
        [PreserveSig] int DUnadvise(uint connection);
        [PreserveSig] int EnumDAdvise(nint* enumerator);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(nint dataObject);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeSecurity(nint securityDescriptor, int authServices, nint services, nint reserved1, uint authnLevel, uint impLevel, nint authList, uint capabilities, nint reserved3);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MakeAbsoluteSD(nint relative, nint absolute, ref uint absoluteSize, nint dacl, ref uint daclSize, nint sacl, ref uint saclSize,
        nint owner, ref uint ownerSize, nint group, ref uint groupSize);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint securityDescriptor, nint size);
    [DllImport("ole32.dll")] private static extern int OleIsCurrentClipboard(nint dataObject);
    [DllImport("shell32.dll")] private static extern int SHCreateStdEnumFmtEtc(uint count, FORMATETC* formats, nint* enumerator);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, nint template, out nint stream);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
}
