extern alias agent;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;
using System.Threading;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// Files copied at the device: the helper reads the copied file list (CF_HDROP) as untrusted input and offers it to
/// RemoteHub – within the limits, without reparse points.
/// </summary>
[Collection("Clipboard")]
public class HelperClipboardFilesTests
{
    /// <summary>
    /// The lab's copy (run 20261018-112637): Windows PowerShell's Set-Clipboard -Path in a script that goes on running
    /// and then ends. Its OLE copy isn't rendered for other programs while the script runs, and once it ends only
    /// "DataObject" is left on the clipboard: the helper must get the files while the script still runs.
    /// </summary>
    [SkippableFact]
    public void Files_copied_by_a_script_that_keeps_running_reach_the_helper()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-script-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "from a script");
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        var notes = new ConcurrentQueue<string>();
        ui.ClipboardContent += got.Enqueue;
        ui.Noted = notes.Enqueue;
        try
        {
            using var script = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Set-Clipboard -Path '{file}'; Start-Sleep -Seconds 10; 'done'\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            // A cold Windows PowerShell can take several seconds to start on a busy CI runner (the 6 s wait failed there):
            // the script keeps running long enough for that, and the wait ends as soon as the files arrive.
            var ok = SpinWait.SpinUntil(() => !got.IsEmpty, 20_000);
            script.WaitForExit(20_000);
            Assert.True(ok, "no file list from a running script; notes: " + string.Join(" | ", notes));
            Assert.Contains(got, c => c.Files is { } f && f.SequenceEqual([file]));
        }
        finally
        {
            ClearClipboard();
            File.Delete(file);
        }
    }

    /// <summary>
    /// Copies <paramref name="file"/> as Explorer's Copy does: the shell's own data object for the item
    /// (IShellItem → BHID_DataObject), put on the clipboard with OleSetClipboard and never flushed – the copying
    /// thread keeps running a message loop and renders on request. Null when there is no clipboard here.
    /// </summary>
    private static (Thread Thread, Action Stop)? ExplorerCopy(string file)
    {
        var ready = new ManualResetEventSlim();
        var ok = false;
        uint threadId = 0;
        var t = new Thread(() =>
        {
            OleInitialize(0);
            var iidItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            var bhid = new Guid("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
            var iidData = new Guid("0000010e-0000-0000-C000-000000000046");
            if (SHCreateItemFromParsingName(file, 0, ref iidItem, out var item) == 0)
            {
                nint data = 0;
                unsafe
                {
                    // IShellItem::BindToHandler is its fourth vtable entry.
                    var bind = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)(*(*(nint**)item + 3));
                    var b = bhid; var i = iidData;
                    if (bind(item, 0, &b, &i, &data) != 0) data = 0;
                }
                System.Runtime.InteropServices.Marshal.Release(item);
                if (data != 0)
                {
                    ok = OleSetClipboard(data) == 0;
                    System.Runtime.InteropServices.Marshal.Release(data);
                }
            }
            threadId = GetCurrentThreadId();
            ready.Set();
            if (ok) System.Windows.Forms.Application.Run(); // the copier stays and answers, as Explorer does
            OleSetClipboard(0);
            OleUninitialize();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        ready.Wait(5000);
        if (!ok) { t.Join(2000); return null; }
        return (t, () => { PostThreadMessage(threadId, 0x0012 /* WM_QUIT */, 0, 0); t.Join(3000); });
    }

    [System.Runtime.InteropServices.DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [System.Runtime.InteropServices.DllImport("ole32.dll")] private static extern void OleUninitialize();
    [System.Runtime.InteropServices.DllImport("ole32.dll")] private static extern int OleSetClipboard(nint dataObject);
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, out nint item);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, nint w, nint l);

    [SkippableFact]
    public void Files_copied_as_Explorer_copies_them_reach_the_helper()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-explorer-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "copied in Explorer");
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        var notes = new ConcurrentQueue<string>();
        ui.ClipboardContent += got.Enqueue;
        ui.Noted = notes.Enqueue;
        var copy = ExplorerCopy(file);
        try
        {
            Skip.If(copy is null, "no clipboard (or no shell) in this session");
            Assert.True(SpinWait.SpinUntil(() => !got.IsEmpty, 5000), "no file list from Explorer's copy; notes: " + string.Join(" | ", notes));
            Assert.Contains(got, c => c.Files is { } f && f.SequenceEqual([file]));
        }
        finally
        {
            copy?.Stop();
            ClearClipboard();
            File.Delete(file);
        }
    }

    /// <summary>
    /// The helper's OLE read on its own: the file list of an unflushed OLE copy (Explorer's, and Windows Forms' with
    /// copy: false) comes from the copying program's data object, as the user's own programs get it.
    /// </summary>
    [SkippableFact]
    public void The_OLE_read_gets_the_files_of_an_unflushed_copy()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-ole-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "unflushed");
        var copy = ExplorerCopy(file);
        try
        {
            Skip.If(copy is null, "no clipboard (or no shell) in this session");
            IReadOnlyList<string>? files = null;
            var why = "";
            var reader = new Thread(() => files = A.Win32HelperUi.OleClipboardFiles(out why));
            reader.SetApartmentState(ApartmentState.STA);
            reader.Start();
            Assert.True(reader.Join(5000), "the OLE read didn't return");
            Assert.True(files is not null, why);
            Assert.Equal(new[] { file }, files);

            // Windows Forms without flushing (Clipboard.SetDataObject(data, copy: false)), the copier still running.
            copy.Value.Stop();
            copy = null;
            var ready = new ManualResetEventSlim();
            var winforms = new Thread(() =>
            {
                var data = new System.Windows.Forms.DataObject();
                data.SetFileDropList(new StringCollection { file });
                System.Windows.Forms.Clipboard.SetDataObject(data, false);
                ready.Set();
                var until = Stopwatch.StartNew();
                while (until.ElapsedMilliseconds < 3000) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
            });
            winforms.SetApartmentState(ApartmentState.STA);
            winforms.Start();
            ready.Wait(5000);
            files = null;
            reader = new Thread(() => files = A.Win32HelperUi.OleClipboardFiles(out why));
            reader.SetApartmentState(ApartmentState.STA);
            reader.Start();
            Assert.True(reader.Join(5000), "the OLE read didn't return");
            Assert.True(files is not null, why);
            Assert.Equal(new[] { file }, files);
            TestThread.Join(winforms, "the Windows Forms copier");
        }
        finally
        {
            copy?.Stop();
            ClearClipboard();
            File.Delete(file);
        }
    }
    /// <summary>
    /// Exactly the lab's copy: a short-lived powershell.exe started from another process runs Set-Clipboard -Path and
    /// ends at once. The helper must offer the file.
    /// </summary>
    [SkippableFact]
    public void Files_copied_by_a_short_lived_Set_Clipboard_reach_the_helper()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-shortlived-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "short-lived");
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        var notes = new ConcurrentQueue<string>();
        ui.ClipboardContent += got.Enqueue;
        ui.Noted = notes.Enqueue;
        try
        {
            using (var script = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"Set-Clipboard -Path '{file}'\"")
                   { UseShellExecute = false, CreateNoWindow = true })!)
                script.WaitForExit(15000);
            Assert.True(SpinWait.SpinUntil(() => got.Any(c => c.Files is not null), 6000), "no file list; notes: " + string.Join(" | ", notes));
            Assert.Contains(got, c => c.Files is { } f && f.SequenceEqual([file]));
        }
        finally
        {
            ClearClipboard();
            File.Delete(file);
        }
    }

    /// <summary>
    /// Puts only "DataObject" on the clipboard – what the LocalSystem helper was shown for a file copy on Windows 11
    /// 26H2 (lab runs 20261018-112637 to -133341) – from a thread that keeps answering until <paramref name="stop"/>.
    /// </summary>
    private static Thread DataObjectOnly(ManualResetEventSlim stop, Action announced)
    {
        var t = new Thread(() =>
        {
            var window = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, 0, 0);
            if (OpenClipboard(window))
            {
                EmptyClipboard();
                SetClipboardData(RegisterClipboardFormatW("DataObject"), Global(new byte[8]));
                CloseClipboard();
                announced();
            }
            while (!stop.IsSet) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); }
            DestroyWindow(window);
        }) { IsBackground = true };
        t.Start();
        return t;
    }

    [SkippableFact]
    public void A_copy_the_helper_is_not_shown_is_read_as_the_user()
    {
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        ui.ClipboardContent += got.Enqueue;
        var asked = 0;
        ui.UserFileReader = () =>
        {
            Interlocked.Increment(ref asked);
            return new A.ClipboardFilesReader.Result([@"C:\Users\someone\a.txt", @"D:\b.txt"], "");
        };
        using var stop = new ManualResetEventSlim();
        var announced = false;
        var copier = DataObjectOnly(stop, () => announced = true);
        try
        {
            Skip.IfNot(SpinWait.SpinUntil(() => announced, 3000), "no clipboard in this session");
            Assert.True(SpinWait.SpinUntil(() => !got.IsEmpty, 5000), "the user's reader wasn't asked");
            Assert.Equal(new[] { @"C:\Users\someone\a.txt", @"D:\b.txt" }, got.Single().Files);
            Assert.Equal(1, asked);
        }
        finally
        {
            stop.Set();
            copier.Join(3000);
            ClearClipboard();
        }
    }

    /// <summary>
    /// Two copies the helper isn't shown, the second while the reader still reads the first (lab run 20261018-152145,
    /// D-CLIP-16: neither arrived): the second is read next and offered; the first, out of date by then, is not.
    /// </summary>
    [SkippableFact]
    public void A_second_copy_while_the_reader_runs_is_read_next()
    {
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        ui.ClipboardContent += got.Enqueue;
        var calls = 0;
        ui.UserFileReader = () =>
        {
            var n = Interlocked.Increment(ref calls);
            Thread.Sleep(800); // starting a process as the user takes a moment
            return new A.ClipboardFilesReader.Result([n == 1 ? @"C:\first.txt" : @"C:\second.txt"], "");
        };
        using var stop1 = new ManualResetEventSlim();
        using var stop2 = new ManualResetEventSlim();
        var announced = 0;
        var first = DataObjectOnly(stop1, () => Interlocked.Increment(ref announced));
        try
        {
            Skip.IfNot(SpinWait.SpinUntil(() => Volatile.Read(ref announced) == 1, 3000), "no clipboard in this session");
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 3000), "the reader wasn't asked for the first copy");
            stop1.Set();
            first.Join(3000);
            var second = DataObjectOnly(stop2, () => Interlocked.Increment(ref announced));
            Assert.True(SpinWait.SpinUntil(() => got.Any(c => c.Files is { } f && f.Contains(@"C:\second.txt")), 6000), "the second copy wasn't read");
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.DoesNotContain(got, c => c.Files is { } f && f.Contains(@"C:\first.txt"));
            stop2.Set();
            second.Join(3000);
        }
        finally
        {
            stop1.Set();
            stop2.Set();
            ClearClipboard();
        }
    }

    [SkippableFact]
    public void A_reader_that_fails_or_hangs_leaves_a_note_and_never_holds_up_the_helper()
    {
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        var texts = new ConcurrentQueue<string>();
        var notes = new ConcurrentQueue<string>();
        ui.ClipboardContent += got.Enqueue;
        ui.ClipboardChanged += texts.Enqueue;
        ui.Noted = notes.Enqueue;
        using var stop = new ManualResetEventSlim();
        var announced = false;

        // Fails: one line with the reason, nothing sent.
        ui.UserFileReader = () => new A.ClipboardFilesReader.Result(null, "GetData 0x80040064");
        var copier = DataObjectOnly(stop, () => announced = true);
        try
        {
            Skip.IfNot(SpinWait.SpinUntil(() => announced, 3000), "no clipboard in this session");
            Assert.True(SpinWait.SpinUntil(() => notes.Any(n => n.Contains("read as the user: GetData 0x80040064")), 5000), string.Join(" | ", notes));
            Assert.Empty(got);
        }
        finally
        {
            stop.Set();
            copier.Join(3000);
        }

        // Hangs (until its time limit): a text copied meanwhile still arrives, and the late answer is dropped.
        using var release = new ManualResetEventSlim();
        ui.UserFileReader = () => { release.Wait(10000); return new A.ClipboardFilesReader.Result([@"C:\late.txt"], ""); };
        using var stop2 = new ManualResetEventSlim();
        announced = false;
        copier = DataObjectOnly(stop2, () => announced = true);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => announced, 3000));
            Thread.Sleep(500); // the helper has asked the reader by now
            stop2.Set();
            copier.Join(3000);
            var t = new Thread(() => System.Windows.Forms.Clipboard.SetText("meanwhile"));
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            TestThread.Join(t, "the copy of a text");
            Assert.True(SpinWait.SpinUntil(() => texts.Contains("meanwhile"), 5000), "the helper waited for the reader");
            release.Set();
            Thread.Sleep(500);
            Assert.DoesNotContain(got, c => c.Files is { } f && f.Contains(@"C:\late.txt"));
        }
        finally
        {
            release.Set();
            stop2.Set();
            ClearClipboard();
        }
    }

    [Fact]
    public void Only_a_well_formed_reader_answer_is_used()
    {
        var ok = A.ClipboardFilesReader.Parse(Encoding.UTF8.GetBytes("files\nC:\\a.txt\r\n\\\\.\\pipe\\x\nrelative.txt\n\\\\server\\share\\b.txt\n"));
        Assert.Equal(new[] { @"C:\a.txt", @"\\server\share\b.txt" }, ok.Files);
        Assert.Null(A.ClipboardFilesReader.Parse([0xFF, 0xFE, 0x00]).Files);                        // not UTF-8
        Assert.Null(A.ClipboardFilesReader.Parse(Encoding.UTF8.GetBytes("C:\\a.txt\n")).Files);     // no header
        Assert.Null(A.ClipboardFilesReader.Parse(new byte[A.ClipboardFilesReader.MaxOutputBytes + 1]).Files);
        Assert.Null(A.ClipboardFilesReader.Parse(Encoding.UTF8.GetBytes("files\n\\\\?\\C:\\x\n")).Files);
        var none = A.ClipboardFilesReader.Parse(Encoding.UTF8.GetBytes("none GetData 0x80040064\n"));
        Assert.Null(none.Files);
        Assert.Equal("GetData 0x80040064", none.Why);
        var many = "files\n" + string.Concat(Enumerable.Range(0, A.DesktopProtocol.MaxTransferItems + 5).Select(i => $"C:\\m\\f{i}.txt\n"));
        Assert.Equal(A.DesktopProtocol.MaxTransferItems, A.ClipboardFilesReader.Parse(Encoding.UTF8.GetBytes(many)).Files!.Count);
    }

    /// <summary>The reader verb itself, as the user runs it: a file copied by Set-Clipboard -Path comes out as its path.</summary>
    [SkippableFact]
    public void The_reader_verb_writes_the_copied_files()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-reader-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "read by the user");
        try
        {
            using (var script = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"Set-Clipboard -Path '{file}'\"")
                   { UseShellExecute = false, CreateNoWindow = true })!)
                script.WaitForExit(15000);
            var output = new StringWriter();
            var code = A.ClipboardFilesReader.Run(output);
            Skip.If(code != 0 && output.ToString().StartsWith("none OleGetClipboard"), "no clipboard in this session: " + output);
            Assert.True(code == 0 && output.ToString() == $"files\n{file}\n", $"exit {code}: {output}");
            Assert.Equal(new[] { file }, A.ClipboardFilesReader.Parse(Encoding.UTF8.GetBytes(output.ToString())).Files);
        }
        finally
        {
            ClearClipboard();
            File.Delete(file);
        }
    }

    private static nint Global(byte[] bytes)
    {
        var memory = GlobalAlloc(0x0002, bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, GlobalLock(memory), bytes.Length);
        GlobalUnlock(memory);
        return memory;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool OpenClipboard(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool CloseClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint data);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nint bytes);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    /// <summary>A DROPFILES block as programs put it on the clipboard: the header, then the paths and a closing \0.</summary>
    private static byte[] Drop(bool wide, params string[] paths)
    {
        var list = string.Concat(paths.Select(p => p + "\0")) + "\0";
        var bytes = wide ? Encoding.Unicode.GetBytes(list) : Encoding.ASCII.GetBytes(list);
        var drop = new byte[20 + bytes.Length];
        BitConverter.GetBytes(20u).CopyTo(drop, 0);
        BitConverter.GetBytes(wide ? 1 : 0).CopyTo(drop, 16);
        bytes.CopyTo(drop, 20);
        return drop;
    }

    [Fact]
    public void A_file_list_is_read_wide_and_ANSI()
    {
        Assert.Equal(new[] { @"C:\a\one.txt", @"\\server\share\two.txt" }, A.Win32HelperUi.DropFileList(Drop(true, @"C:\a\one.txt", @"\\server\share\two.txt"), 1000));
        Assert.Equal(new[] { @"C:\a\one.txt" }, A.Win32HelperUi.DropFileList(Drop(false, @"C:\a\one.txt"), 1000));
    }

    [Fact]
    public void A_malformed_file_list_is_refused_not_read_past_its_end()
    {
        Assert.Null(A.Win32HelperUi.DropFileList(new byte[19], 1000));
        var outside = Drop(true, @"C:\a.txt");
        BitConverter.GetBytes((uint)outside.Length).CopyTo(outside, 0); // the list would start behind the block
        Assert.Null(A.Win32HelperUi.DropFileList(outside, 1000));
        var inHeader = Drop(true, @"C:\a.txt");
        BitConverter.GetBytes(4u).CopyTo(inHeader, 0);
        Assert.Null(A.Win32HelperUi.DropFileList(inHeader, 1000));
        var unterminated = Encoding.Unicode.GetBytes(@"C:\a.txt");
        var drop = new byte[20 + unterminated.Length];
        BitConverter.GetBytes(20u).CopyTo(drop, 0);
        BitConverter.GetBytes(1).CopyTo(drop, 16);
        unterminated.CopyTo(drop, 20);
        Assert.Null(A.Win32HelperUi.DropFileList(drop, 1000));
        Assert.Null(A.Win32HelperUi.DropFileList(Drop(true), 1000)); // an empty list
    }

    [Fact]
    public void Device_paths_and_relative_paths_are_dropped()
    {
        var files = A.Win32HelperUi.DropFileList(Drop(true, @"\\.\pipe\agent", @"\\?\C:\x.txt", @"relative.txt", @"\rooted.txt", @"//?/C:/y.txt", @"C:\ok.txt"), 1000);
        Assert.Equal(new[] { @"C:\ok.txt" }, files);
        Assert.Null(A.Win32HelperUi.DropFileList(Drop(true, @"\\.\C:"), 1000));
    }

    [Fact]
    public void At_most_the_item_limit_is_read_from_a_long_list()
    {
        var paths = Enumerable.Range(1, A.DesktopProtocol.MaxTransferItems + 1).Select(i => $@"C:\many\f{i}.txt").ToArray();
        var files = A.Win32HelperUi.DropFileList(Drop(true, paths), A.DesktopProtocol.MaxTransferItems)!;
        Assert.Equal(A.DesktopProtocol.MaxTransferItems, files.Count);
        Assert.Equal(paths[0], files[0]);
    }

    [Fact]
    public void A_junction_in_a_copied_folder_is_skipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "rh-junction-" + Guid.NewGuid().ToString("N"));
        var copied = Path.Combine(root, "copied");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(Path.Combine(copied, "real"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(copied, "real", "a.txt"), "real file");
        File.WriteAllText(Path.Combine(outside, "hosts"), "not to be copied");
        try
        {
            // A junction needs no privilege (unlike a symbolic link).
            using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(copied, "junction")}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
                Assert.True(mklink.WaitForExit(30_000), "mklink didn't end");
            Assert.True(Directory.Exists(Path.Combine(copied, "junction")));
            var o = A.ClipboardTransfer.Files(1, [copied], out var error)!;
            Assert.Null(error);
            var names = o.Transfer.Items.Select(i => i.Path).ToList();
            Assert.Contains("copied/real/a.txt", names);
            Assert.DoesNotContain(names, n => n.Contains("junction") || n.Contains("hosts"));
        }
        finally
        {
            var junction = Path.Combine(copied, "junction");
            if (Directory.Exists(junction)) Directory.Delete(junction); // the link only, never its target
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void More_than_the_limits_copied_at_the_device_is_refused_with_a_reason()
    {
        var root = Path.Combine(Path.GetTempPath(), "rh-many-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i <= A.DesktopProtocol.MaxTransferItems; i++) File.WriteAllText(Path.Combine(root, $"f{i}.txt"), "x");
            Assert.Null(A.ClipboardTransfer.Files(1, [root], out var error));
            Assert.StartsWith("Too much to copy through the remote desktop", error);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Puts <paramref name="files"/> on the clipboard from another thread, as Windows Forms does it (OLE, rendered right after).</summary>
    private static void CopyFiles(string[] files, string? text = null)
    {
        Exception? failed = null;
        var t = new Thread(() =>
        {
            try
            {
                var data = new System.Windows.Forms.DataObject();
                var list = new StringCollection();
                list.AddRange(files);
                data.SetFileDropList(list);
                if (text is not null) data.SetText(text);
                System.Windows.Forms.Clipboard.SetDataObject(data, copy: true);
            }
            catch (Exception ex) { failed = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        TestThread.Join(t, "the copy of the files");
        Skip.If(failed is not null, $"no clipboard in this session ({failed?.Message})");
    }

    [SkippableFact]
    public void Files_copied_by_another_program_reach_the_helper_as_files()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-devfiles-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "from the device");
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        var texts = new ConcurrentQueue<string>();
        ui.ClipboardContent += got.Enqueue;
        ui.ClipboardChanged += texts.Enqueue;
        try
        {
            CopyFiles([file]);
            Assert.True(SpinWait.SpinUntil(() => !got.IsEmpty, 5000), "no file list from the clipboard");
            Assert.Contains(got, c => c.Files is { } f && f.SequenceEqual([file]));

            // The paths as text next to the list (some programs do that): still a file copy, not text.
            got.Clear();
            CopyFiles([file], text: file);
            Assert.True(SpinWait.SpinUntil(() => !got.IsEmpty, 5000), "no file list next to text");
            Assert.Contains(got, c => c.Files is { } f && f.SequenceEqual([file]));
            Assert.Empty(texts);
        }
        finally
        {
            File.Delete(file);
            ClearClipboard();
        }
    }

    /// <summary>
    /// Another program copies again and again (text and file lists, through OLE as Windows Forms and Set-Clipboard do)
    /// while the helper reads every change: none of its copies may fail because the helper holds the clipboard
    /// (lab run 20261017-160133: "Requested Clipboard operation did not succeed" in normaluser's session), and the
    /// helper ends up with the last copy.
    /// </summary>
    [SkippableFact]
    public void Copies_by_another_program_never_fail_while_the_helper_reads()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-busy-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "busy");
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        var texts = new ConcurrentQueue<string>();
        ui.ClipboardContent += got.Enqueue;
        ui.ClipboardChanged += texts.Enqueue;
        var failures = new ConcurrentQueue<string>();
        var copied = 0;
        var writer = new Thread(() =>
        {
            for (var i = 0; i < 60; i++)
            {
                try
                {
                    var data = new System.Windows.Forms.DataObject();
                    if (i % 2 == 0) data.SetText($"copy {i}");
                    else data.SetFileDropList(new StringCollection { file });
                    // Windows Forms' own patience (10 tries, 100 ms apart) – the lab's copies failed with exactly that.
                    System.Windows.Forms.Clipboard.SetDataObject(data, true);
                    copied++;
                }
                catch (Exception ex) { failures.Enqueue($"{i}: {ex.Message} (held by {Holder()})"); }
                // Bursts and pauses: some reads come between two copies, some during one. In a pause the writer pumps
                // messages like any program with windows; during a burst it doesn't answer (as when busy).
                var pause = Stopwatch.StartNew();
                while (pause.ElapsedMilliseconds < i % 3 * 120) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(5); }
            }
            try
            {
                var last = new System.Windows.Forms.DataObject();
                last.SetText("the last copy");
                System.Windows.Forms.Clipboard.SetDataObject(last, true);
            }
            catch (Exception ex) { failures.Enqueue($"last: {ex.Message}"); }
        });
        writer.SetApartmentState(ApartmentState.STA);
        try
        {
            writer.Start();
            TestThread.Join(writer, "the copying program", 60);
            Skip.If(copied == 0, $"no clipboard in this session ({failures.FirstOrDefault()})");
            Assert.True(failures.IsEmpty, "copies failed while the helper read: " + string.Join(" | ", failures));
            Assert.True(SpinWait.SpinUntil(() => texts.Contains("the last copy"), 5000), "the last copy didn't reach the helper");
            Assert.Contains(got, c => c.Files is { } f && f.SequenceEqual([file]));
        }
        finally
        {
            ClearClipboard();
            File.Delete(file);
        }
    }

    /// <summary>The program that has the clipboard open now, for the failure message.</summary>
    private static string Holder()
    {
        var window = GetOpenClipboardWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0) return "nobody";
        try { using var p = Process.GetProcessById((int)pid); return $"{p.ProcessName} ({pid}{(pid == Environment.ProcessId ? ", this test" : "")})"; }
        catch { return pid.ToString(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetOpenClipboardWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    private static void ClearClipboard()
    {
        var t = new Thread(System.Windows.Forms.Clipboard.Clear);
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        TestThread.Join(t, "clearing the clipboard");
    }

    [SkippableFact]
    public void An_image_copied_by_another_program_reaches_the_helper()
    {
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        ui.ClipboardContent += got.Enqueue;
        Exception? failed = null;
        var t = new Thread(() =>
        {
            try
            {
                using var b = new System.Drawing.Bitmap(32, 32);
                using (var g = System.Drawing.Graphics.FromImage(b)) g.Clear(System.Drawing.Color.Blue);
                System.Windows.Forms.Clipboard.SetImage(b); // a bitmap only, as in the lab (no PNG)
            }
            catch (Exception ex) { failed = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        TestThread.Join(t, "the copy of an image");
        try
        {
            Skip.If(failed is not null, $"no clipboard in this session ({failed?.Message})");
            Assert.True(SpinWait.SpinUntil(() => !got.IsEmpty, 5000), "no image from the clipboard");
            Assert.Contains(got, c => c.Image is [(byte)'B', (byte)'M', ..] && !c.Png && c.Files is null);
        }
        finally { ClearClipboard(); }
    }

    /// <summary>
    /// A program that announces a file list and doesn't render it when asked: the helper asks once, after the copy
    /// settled, notes it (without the paths) and doesn't keep asking – asking again and again is what kept other
    /// programs off the clipboard in lab run 20261017-160133.
    /// </summary>
    [SkippableFact]
    public void A_file_list_that_is_not_rendered_is_noted_and_not_asked_for_again()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-delayed-" + Guid.NewGuid().ToString("N") + ".txt");
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        var notes = new ConcurrentQueue<string>();
        ui.ClipboardContent += got.Enqueue;
        ui.Noted = notes.Enqueue;
        // The user's reader (a fake here) is asked next, and gets nothing either.
        ui.UserFileReader = () => new A.ClipboardFilesReader.Result(null, "GetData 0x80040064");
        var owner = new DelayedFileList(Drop(true, file), renderAt: int.MaxValue);
        try
        {
            Skip.IfNot(owner.Announce(), "no clipboard in this session");
            Assert.True(SpinWait.SpinUntil(() => notes.Any(n => n.Contains("its file list: no data") && n.Contains("read as the user: GetData 0x80040064")), 5000),
                "no note for the unreadable list: " + string.Join(" | ", notes));
            Thread.Sleep(1000);
            Assert.Equal(1, owner.Asked);
            Assert.Empty(got);
            Assert.DoesNotContain(notes, n => n.Contains(file));
        }
        finally
        {
            owner.Dispose();
            ClearClipboard();
        }
    }

    /// <summary>A delayed file list rendered when asked: read once the copy settled, without the copier being asked twice.</summary>
    [SkippableFact]
    public void A_delayed_file_list_is_read_once()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-delayed-" + Guid.NewGuid().ToString("N") + ".txt");
        using var ui = new A.Win32HelperUi();
        var got = new ConcurrentQueue<A.HelperClipboard>();
        ui.ClipboardContent += got.Enqueue;
        var owner = new DelayedFileList(Drop(true, file), renderAt: 1);
        try
        {
            Skip.IfNot(owner.Announce(), "no clipboard in this session");
            Assert.True(SpinWait.SpinUntil(() => !got.IsEmpty, 5000), "no file list");
            Assert.Contains(got, c => c.Files is { } f && f.SequenceEqual([file]));
        }
        finally
        {
            owner.Dispose();
            ClearClipboard();
        }
    }
    /// <summary>Owns the clipboard with a delayed CF_HDROP, rendered from the <c>renderAt</c>-th WM_RENDERFORMAT on.</summary>
    private sealed class DelayedFileList : System.Windows.Forms.NativeWindow, IDisposable
    {
        private readonly byte[] _drop;
        private readonly int _renderAt;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private bool _announced;
        public volatile int Asked;

        public DelayedFileList(byte[] drop, int renderAt)
        {
            (_drop, _renderAt) = (drop, renderAt);
            _thread = new Thread(() =>
            {
                CreateHandle(new System.Windows.Forms.CreateParams());
                if (OpenClipboard(Handle))
                {
                    EmptyClipboard();
                    SetClipboardData(15, 0); // CF_HDROP, rendered when asked
                    CloseClipboard();
                    _announced = true;
                }
                _ready.Set();
                System.Windows.Forms.Application.Run();
            });
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public bool Announce()
        {
            _thread.Start();
            _ready.Wait(5000);
            return _announced;
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            // Only this process's asks count: another clipboard reader on the machine (an installed agent's helper) may ask too.
            if (m.Msg == 0x0305 /* WM_RENDERFORMAT */ && FromThisProcess() && ++Asked >= _renderAt)
            {
                var memory = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, _drop.Length);
                System.Runtime.InteropServices.Marshal.Copy(_drop, 0, GlobalLock(memory), _drop.Length);
                GlobalUnlock(memory);
                SetClipboardData(15, memory);
                return;
            }
            if (m.Msg is 0x0305 or 0x0306) return; // WM_RENDERFORMAT (first time: nothing), WM_RENDERALLFORMATS
            base.WndProc(ref m);
        }

        private static bool FromThisProcess() =>
            GetWindowThreadProcessId(GetOpenClipboardWindow(), out var pid) != 0 && pid == (uint)Environment.ProcessId;

        public void Dispose()
        {
            if (_thread.IsAlive)
            {
                BeginInvokeExit();
                _thread.Join(3000);
            }
        }

        private void BeginInvokeExit() => PostMessage(Handle, 0x0012 /* WM_QUIT */, 0, 0);

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool OpenClipboard(nint hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetOpenClipboardWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool CloseClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint data);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nint bytes);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(nint hwnd, uint msg, nint w, nint l);
    }
}
