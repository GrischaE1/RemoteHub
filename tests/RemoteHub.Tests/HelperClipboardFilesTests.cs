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
                mklink.WaitForExit();
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
        t.Join();
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
            writer.Join();
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
        t.Join();
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
        t.Join();
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
        var owner = new DelayedFileList(Drop(true, file), renderAt: int.MaxValue);
        try
        {
            Skip.IfNot(owner.Announce(), "no clipboard in this session");
            Assert.True(SpinWait.SpinUntil(() => notes.Any(n => n.Contains("could not be read")), 5000), "no note for the unreadable list");
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
