using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Windows.Threading;
using RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// Virtual files on the real clipboard: the list is there at once, the contents are fetched only when a paste reads a
/// file – Explorer (open on a test machine) reads the list when the clipboard changes, which must fetch nothing.
/// </summary>
[Collection("Clipboard")]
public class VirtualFilesTests
{
    [SkippableFact]
    public void Contents_are_fetched_only_when_a_file_is_read()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-virtual-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "virtual content");
        var transfer = new DesktopTransfer(41, DesktopTransferKind.Files, [new DesktopTransferItem("report.txt", 15)], 15);
        var fetches = 0;
        nint offered = 0;
        Dispatcher? owner = null;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            owner = Dispatcher.CurrentDispatcher;
            offered = VirtualFiles.Set(transfer, () => { Interlocked.Increment(ref fetches); return [file]; });
            ready.Set();
            Dispatcher.Run(); // pumps: other programs (Explorer) talk to the data object through this thread
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            ready.Wait();
            Skip.If(offered == 0, "no clipboard in this session");
            Thread.Sleep(2000); // Explorer and others look at the new clipboard content
            Assert.Equal(0, Volatile.Read(ref fetches));

            string? names = null, content = null;
            var reader = new Thread(() =>
            {
                Assert.Equal(0, OleGetClipboard(out var data));
                var list = Read(data, "FileGroupDescriptorW", -1, TYMED.TYMED_HGLOBAL);
                names = System.Text.Encoding.Unicode.GetString(list, 4 + 72, 20).TrimEnd('\0');
                content = System.Text.Encoding.UTF8.GetString(Read(data, "FileContents", 0, TYMED.TYMED_ISTREAM));
            });
            reader.SetApartmentState(ApartmentState.STA);
            reader.Start();
            reader.Join();
            Assert.Equal("report.txt", names);
            Assert.Equal("virtual content", content);
            Assert.Equal(1, Volatile.Read(ref fetches));
        }
        finally
        {
            owner?.Invoke(() => { VirtualFiles.Release(offered); OleSetClipboard(0); });
            owner?.InvokeShutdown();
            File.Delete(file);
        }
    }

    [Fact]
    public void The_descriptor_lists_names_sizes_and_folders()
    {
        var t = new DesktopTransfer(1, DesktopTransferKind.Files,
            [new DesktopTransferItem("dir", 0, Folder: true), new DesktopTransferItem("dir/big.iso", 5000000000)], 5000000000);
        var d = VirtualFiles.Descriptor(t);
        Assert.Equal(2, BitConverter.ToInt32(d, 0));
        Assert.Equal(0x10u, BitConverter.ToUInt32(d, 4 + 36));                 // folder
        var second = 4 + 592;
        Assert.Equal(1u, BitConverter.ToUInt32(d, second + 64));              // size high
        Assert.Equal((uint)(5000000000 & 0xFFFFFFFF), BitConverter.ToUInt32(d, second + 68));
        Assert.Equal(@"dir\big.iso", System.Text.Encoding.Unicode.GetString(d, second + 72, 22));
    }

    internal static byte[] Read(IDataObject data, string format, int index, TYMED tymed)
    {
        var f = new FORMATETC { cfFormat = (short)RegisterClipboardFormatW(format), dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = tymed };
        data.GetData(ref f, out var medium);
        try
        {
            if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                var size = (int)GlobalSize(medium.unionmember);
                var bytes = new byte[size];
                Marshal.Copy(GlobalLock(medium.unionmember), bytes, 0, size);
                GlobalUnlock(medium.unionmember);
                return bytes;
            }
            var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            var result = new List<byte>();
            var buffer = new byte[4096];
            var read = Marshal.AllocHGlobal(4);
            try
            {
                while (true)
                {
                    stream.Read(buffer, buffer.Length, read);
                    var n = Marshal.ReadInt32(read);
                    if (n <= 0) break;
                    result.AddRange(buffer[..n]);
                }
            }
            finally { Marshal.FreeHGlobal(read); }
            return [.. result];
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    [DllImport("ole32.dll")] internal static extern int OleGetClipboard(out IDataObject data);
    [DllImport("ole32.dll")] private static extern int OleSetClipboard(nint data);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
}
