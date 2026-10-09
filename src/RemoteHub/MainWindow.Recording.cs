using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.Views;
using Wpf.Ui.Controls;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace RemoteHub;

/// <summary>Recording tabs to video files (manual start/stop; every tab type).</summary>
public partial class MainWindow
{
    private readonly Dictionary<SessionBase, SessionRecorder> _recorders = new();
    private DispatcherTimer? _recordTimer;
    private bool _recordingShutdown;

    public static bool RecordingAllowed => !AppPolicy.RecordingDisabled;

    private void ToggleRecording(SessionBase s)
    {
        if (_recorders.TryGetValue(s, out var r)) r.Stop();
        else _ = StartRecording(s);
    }

    private void Record_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedSession is { } s) ToggleRecording(s); }

    private async Task StartRecording(SessionBase s)
    {
        if (!RecordingAllowed || _recorders.ContainsKey(s)) return;
        var ui = _vm.Data.Ui;
        var folder = Recordings.Folder(ui);
        if (!ui.RecordingNoticeAccepted)
        {
            var owner = (Window?)_detached.GetValueOrDefault(s) ?? this;
            if (!await Dialogs.Confirm(owner, "Record this tab",
                    "Recording saves everything shown in this tab as a video file (no sound) – including personal data and any " +
                    "password that appears as plain text. The file isn't encrypted. It is saved in:\n\n" + folder + "\n\n" +
                    "Only record where you are allowed to (your organisation's rules, consent of the people involved), and delete " +
                    "recordings you no longer need. While recording, the tab shows a red REC badge.",
                    "Start recording")) return;
            ui.RecordingNoticeAccepted = true;
            _vm.RequestSave();
        }
        if (_recorders.ContainsKey(s) || !_views.ContainsKey(s) || !_vm.Sessions.Contains(s)) return;

        string path;
        try
        {
            Directory.CreateDirectory(folder);
            path = Recordings.UniquePath(folder, Recordings.FileName(s.Connection.Name, s.KindLabel, DateTime.Now));
        }
        catch (Exception ex)
        {
            await Dialogs.Info(this, "Recording", $"The folder for recordings can't be used:\n{folder}\n\n{ex.Message}");
            return;
        }

        var recorder = new SessionRecorder(path, SourceFor(s), Recordings.Options(ui));
        _recorders[s] = recorder;
        s.IsRecording = true;
        s.RecordingText = "REC " + Recordings.Elapsed(TimeSpan.Zero);
        recorder.Start();
        DiagLog.Write($"Recording started ({recorder.Width}x{recorder.Height}, {recorder.FramesPerSecond} fps)");
        EnsureRecordTimer();
        UpdateRecordButton();

        string? error = null;
        try { await recorder.Completion; }
        catch (Exception ex) { error = ex.Message; }

        _recorders.Remove(s);
        s.IsRecording = false;
        s.RecordingText = "";
        UpdateRecordButton();
        DiagLog.Write(error is null
            ? $"Recording saved ({(int)recorder.Elapsed.TotalSeconds} s, {recorder.FramesWritten} of {(long)(recorder.Elapsed.TotalSeconds * recorder.FramesPerSecond)} frames)"
            : "Recording stopped with an error");
        if (_recordingShutdown) return;

        var dialogOwner = (Window?)_detached.GetValueOrDefault(s) ?? this;
        if (error is not null)
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0) TryDelete(path);
            await Dialogs.Info(dialogOwner, "Recording stopped", $"The recording of {s.Connection.Name} · {s.KindLabel} stopped: {error}");
            return;
        }
        var size = File.Exists(path) ? TransferItem.Size(new FileInfo(path).Length) : "Data unavailable";
        var choice = await Dialogs.Choose(dialogOwner, "Recording saved",
            $"{System.IO.Path.GetFileName(path)}\n{Recordings.Elapsed(recorder.Elapsed)} · {size}\n\n{System.IO.Path.GetDirectoryName(path)}",
            "Play", "Show in folder", "Close", extra: ("Delete recording…", SymbolRegular.Delete24));
        try
        {
            if (choice == MessageBoxResult.Primary) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (choice == MessageBoxResult.Secondary) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (choice == Dialogs.Extra) await DeleteRecording(dialogOwner, path);
        }
        catch (Exception ex) { DiagLog.Write("Opening the recording failed: " + ex.Message); }
    }

    /// <summary>Deletes a recording after asking (it goes to the Recycle Bin, so it can still be restored).</summary>
    private static async Task DeleteRecording(Window owner, string path)
    {
        if (!await Dialogs.Confirm(owner, "Delete recording", $"Delete {System.IO.Path.GetFileName(path)}?\n\nIt goes to the Recycle Bin.", "Delete")) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            DiagLog.Write("Recording deleted");
        }
        catch (Exception ex)
        {
            await Dialogs.Info(owner, "Delete recording", $"The recording could not be deleted: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* keep it */ }
    }

    /// <summary>Follows layout changes (size, detach, tab switches) and updates the REC timers.</summary>
    private void EnsureRecordTimer()
    {
        if (_recordTimer is not null) return;
        _recordTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _recordTimer.Tick += (_, _) =>
        {
            if (_recorders.Count == 0) { _recordTimer!.Stop(); _recordTimer = null; return; }
            foreach (var (s, r) in _recorders)
            {
                r.Update(SourceFor(s));
                s.RecordingText = "REC " + Recordings.Elapsed(r.Elapsed);
            }
            UpdateRecordButton();
        };
        _recordTimer.Start();
    }

    /// <summary>
    /// What to capture for a tab: its view inside the main or detached window, the monitor when the remote desktop is in
    /// full screen, or nothing when it isn't visible.
    /// </summary>
    private RecordingSource SourceFor(SessionBase s)
    {
        try
        {
            if (s is RdpSession { IsFullScreen: true } r && r.Control.IsHandleCreated)
            {
                var b = System.Windows.Forms.Screen.FromHandle(r.Control.Handle).Bounds;
                return new RecordingSource(IntPtr.Zero, b.X, b.Y, b.Width, b.Height, Screen: true);
            }
            if (!_views.TryGetValue(s, out var tab)) return RecordingSource.Hidden;
            // The content below the tab's header (the remote screen, console, lists) – or, with the setting, the whole tab
            // including the header with the connection's name, address and how it connects.
            var view = _vm.Data.Ui.RecordingShowDetails ? tab : (tab as IRecordableView)?.RecordingArea ?? tab;
            if (!view.IsVisible || view.ActualWidth < 1 || view.ActualHeight < 1) return RecordingSource.Hidden;
            if (Window.GetWindow(view) is not { WindowState: not WindowState.Minimized } window) return RecordingSource.Hidden;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !Gdi.GetWindowRect(hwnd, out var w)) return RecordingSource.Hidden;
            var topLeft = view.PointToScreen(new Point(0, 0));
            var bottomRight = view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight));
            return new RecordingSource(hwnd, (int)Math.Round(topLeft.X) - w.Left, (int)Math.Round(topLeft.Y) - w.Top,
                (int)Math.Round(bottomRight.X - topLeft.X), (int)Math.Round(bottomRight.Y - topLeft.Y), Screen: false);
        }
        catch (InvalidOperationException)
        {
            return RecordingSource.Hidden; // not connected to a window (being moved between windows)
        }
    }

    /// <summary>Toolbar button: Record, or a red "REC 01:23" that stops.</summary>
    private void UpdateRecordButton()
    {
        RecordButton.Visibility = RecordingAllowed ? Visibility.Visible : Visibility.Collapsed;
        var s = _vm.SelectedSession;
        var on = s is not null && _recorders.ContainsKey(s);
        RecordButton.Icon = new SymbolIcon(on ? SymbolRegular.RecordStop24 : SymbolRegular.Record24);
        RecordButton.Content = on ? s!.RecordingText : null;
        if (on) RecordButton.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D));
        else RecordButton.ClearValue(ForegroundProperty);
        RecordButton.ToolTip = on ? "Stop recording and save the video" : "Record this tab to a video file";
        System.Windows.Automation.AutomationProperties.SetName(RecordButton, (string)RecordButton.ToolTip);
    }

    /// <summary>Exit: finish all recordings so the files are playable.</summary>
    private void StopAllRecordings()
    {
        if (_recorders.Count == 0) return;
        _recordingShutdown = true;
        var pending = _recorders.Values.ToList();
        foreach (var r in pending) r.Stop();
        try { Task.WaitAll(pending.Select(r => r.Completion).ToArray(), TimeSpan.FromSeconds(10)); }
        catch { /* logged by the recorder */ }
    }
}
