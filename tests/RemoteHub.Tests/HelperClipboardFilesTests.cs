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
            var t = new Thread(System.Windows.Forms.Clipboard.Clear);
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
        }
    }
}
