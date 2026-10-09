using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>Remote files dragged out of a file transfer tab.</summary>
public sealed record RemoteDrag(FileBrowserSession Source, string[] Paths)
{
    // Drag & drop data crosses an OLE boundary: only plain text survives (.NET 10 has no BinaryFormatter for objects).
    public string Encode() => Source.DragId + "\n" + string.Join("\n", Paths);

    public static RemoteDrag? Decode(object? data)
    {
        if (data is not string s) return null;
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 1 && FileBrowserSession.FindByDragId(lines[0]) is { } src ? new RemoteDrag(src, lines[1..]) : null;
    }
}

/// <summary>
/// Tab content of a file transfer session: this PC on the left, the remote computer on the right.
/// Drag files between the sides (or from Explorer onto the remote side), or use the arrow buttons.
/// </summary>
public sealed class FileBrowserView : Border, IRecordableView
{
    public FrameworkElement RecordingArea { get; }

    public const string RemoteDragFormat = "RemoteHub.RemoteDrag";
    private readonly FileBrowserSession _session;
    private readonly Border _headerStripe = new() { Width = 4, Height = 18, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
    private bool _remoteLoaded;

    public FilePane Local { get; }
    public FilePane Remote { get; }

    /// <summary>Upload these local files/folders into the remote folder.</summary>
    public event Action<IReadOnlyList<string>, string>? UploadRequested;
    /// <summary>Download these remote files/folders into the local folder.</summary>
    public event Action<RemoteDrag, string>? DownloadRequested;

    public FileBrowserView(FileBrowserSession session, string localStart, Action reconnect, Action close)
    {
        _session = session;
        DataContext = session;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(2);
        SetResourceReference(BackgroundProperty, "SessionAreaBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Child = root;

        // ---- header
        root.Children.Add(RemoteToolChrome.Header(session, SymbolRegular.FolderSwap24, "File transfer", _headerStripe));

        // ---- panes
        var body = new Grid { Margin = new Thickness(10, 10, 10, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        RecordingArea = body;

        Local = new FilePane("This PC", "Desktop24", p => Task.Run(() => RemoteFs.ListLocal(p)),
            p => Task.Run(() => Directory.CreateDirectory(Environment.ExpandEnvironmentVariables(p)).FullName));
        Remote = new FilePane($"{session.Connection.Name}  ({session.Connection.Host})", "Server24", session.ListAsync, session.MkdirAsync);
        body.Children.Add(Local);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        var upload = new Button { Icon = new SymbolIcon(SymbolRegular.ArrowRight24), ToolTip = "Upload the selected files to the remote folder", Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(10, 8, 10, 8) };
        upload.Click += (_, _) => UploadSelection();
        var download = new Button { Icon = new SymbolIcon(SymbolRegular.ArrowLeft24), ToolTip = "Download the selected remote files to this PC", Padding = new Thickness(10, 8, 10, 8) };
        download.Click += (_, _) => DownloadSelection();
        System.Windows.Automation.AutomationProperties.SetName(upload, "Upload selected files");
        System.Windows.Automation.AutomationProperties.SetName(download, "Download selected files");
        mid.Children.Add(upload);
        mid.Children.Add(download);
        Grid.SetColumn(mid, 1);
        body.Children.Add(mid);

        var remoteCell = new Grid();
        remoteCell.Children.Add(Remote);
        remoteCell.Children.Add(RemoteToolChrome.Overlay(reconnect, close));
        Grid.SetColumn(remoteCell, 2);
        body.Children.Add(remoteCell);

        // ---- drag & drop
        Local.CanAccept = d => d.GetDataPresent(RemoteDragFormat) && _session.IsConnected;
        Local.Dropped += (d, folder) => { if (RemoteDrag.Decode(d.GetData(RemoteDragFormat)) is { } r) DownloadRequested?.Invoke(r, folder); };
        Local.DragStarting += items =>
        {
            var data = new DataObject(DataFormats.FileDrop, items.Select(i => i.FullPath).ToArray());
            data.SetData("RemoteHub.LocalPane", true);
            DragDrop.DoDragDrop(Local, data, DragDropEffects.Copy);
        };
        Remote.CanAccept = d => d.GetDataPresent(DataFormats.FileDrop) && _session.IsConnected;
        Remote.Dropped += (d, folder) => { if (d.GetData(DataFormats.FileDrop) is string[] files) UploadRequested?.Invoke(files, folder); };
        Remote.DragStarting += items =>
            DragDrop.DoDragDrop(Remote, new DataObject(RemoteDragFormat, new RemoteDrag(_session, items.Select(i => i.FullPath).ToArray()).Encode()), DragDropEffects.Copy);

        // ---- right-click menus, rename and delete
        Remote.MenuFor = RemoteMenu;
        Local.MenuFor = LocalMenu;
        Remote.RenameRequested += e => _ = RenameRemote(e);
        Remote.DeleteRequested += list => _ = DeleteRemote(list);

        session.Ready += () => _ = LoadRemote();
        session.FocusRequested += () => Remote.FocusList();
        session.PropertyChanged += OnSessionChanged;
        Loaded += (_, _) => { if (Local.Entries.Count == 0 && Local.CurrentPath.Length == 0) _ = Local.Navigate(localStart); };
        UpdateFrame();
    }

    private async Task LoadRemote()
    {
        var start = _remoteLoaded ? Remote.CurrentPath : _session.StartRemotePath;
        if (!await Remote.Navigate(string.IsNullOrWhiteSpace(start) ? @"C:\" : start) && !_remoteLoaded)
            await Remote.Navigate(@"C:\");
        _remoteLoaded = true;
    }

    private void UploadSelection()
    {
        var sel = Local.SelectedEntries.Where(e => e.Kind != EntryKind.Drive).Select(e => e.FullPath).ToList();
        if (sel.Count > 0 && Remote.CurrentPath.Length > 0 && _session.IsConnected) UploadRequested?.Invoke(sel, Remote.CurrentPath);
    }

    private void DownloadSelection()
    {
        var sel = Remote.SelectedEntries.Where(e => e.Kind != EntryKind.Drive).Select(e => e.FullPath).ToArray();
        if (sel.Length > 0 && Local.CurrentPath.Length > 0 && _session.IsConnected) DownloadRequested?.Invoke(new RemoteDrag(_session, sel), Local.CurrentPath);
    }

    private static System.Windows.Controls.MenuItem Item(string header, SymbolRegular icon, Action action, bool enabled = true, string? gesture = null) =>
        RemoteToolChrome.Item(header, icon, action, enabled, gesture);

    private ContextMenu RemoteMenu(List<FileEntry> sel)
    {
        var items = sel.Where(e => e.Kind != EntryKind.Drive).ToList();
        var ok = _session.IsConnected;
        var m = new ContextMenu();
        m.Items.Add(Item(Local.CurrentPath.Length > 0 ? $"Download to {Local.CurrentPath}" : "Download (open a local folder first)", SymbolRegular.ArrowDownload24,
            DownloadSelection, ok && items.Count > 0 && Local.CurrentPath.Length > 0));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Rename…", SymbolRegular.Rename24, () => _ = RenameRemote(items[0]), ok && items.Count == 1, "F2"));
        m.Items.Add(Item(items.Count > 1 ? $"Delete {items.Count} items…" : "Delete…", SymbolRegular.Delete24, () => _ = DeleteRemote(items), ok && items.Count > 0, "Del"));
        m.Items.Add(new Separator());
        m.Items.Add(Item("New folder…", SymbolRegular.FolderAdd24, () => _ = Remote.NewFolder(), ok && Remote.CurrentPath.Length > 0));
        m.Items.Add(Item("Copy path", SymbolRegular.Copy24, () => CopyPaths(sel, Remote.CurrentPath)));
        m.Items.Add(Item("Refresh", SymbolRegular.ArrowClockwise24, () => _ = Remote.Refresh(), ok, "F5"));
        return m;
    }

    private ContextMenu LocalMenu(List<FileEntry> sel)
    {
        var items = sel.Where(e => e.Kind != EntryKind.Drive).ToList();
        var m = new ContextMenu();
        m.Items.Add(Item(Remote.CurrentPath.Length > 0 ? $"Upload to {Remote.CurrentPath}" : "Upload (open a remote folder first)", SymbolRegular.ArrowUpload24,
            UploadSelection, _session.IsConnected && items.Count > 0 && Remote.CurrentPath.Length > 0));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Show in Explorer", SymbolRegular.Open24, () =>
        {
            var target = items.FirstOrDefault()?.FullPath ?? Local.CurrentPath;
            if (target.Length > 0) System.Diagnostics.Process.Start("explorer.exe", items.Count > 0 ? $"/select,\"{target}\"" : $"\"{target}\"");
        }, Local.CurrentPath.Length > 0));
        m.Items.Add(Item("New folder…", SymbolRegular.FolderAdd24, () => _ = Local.NewFolder(), Local.CurrentPath.Length > 0));
        m.Items.Add(Item("Copy path", SymbolRegular.Copy24, () => CopyPaths(sel, Local.CurrentPath)));
        m.Items.Add(Item("Refresh", SymbolRegular.ArrowClockwise24, () => _ = Local.Refresh(), true, "F5"));
        return m;
    }

    private static void CopyPaths(List<FileEntry> sel, string folder)
    {
        var text = sel.Count > 0 ? string.Join(Environment.NewLine, sel.Select(e => e.FullPath)) : folder;
        if (text.Length > 0) try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    private async Task RenameRemote(FileEntry e)
    {
        if (!_session.IsConnected || e.Kind == EntryKind.Drive) return;
        var dlg = new InputDialog("Rename", $"New name for \"{e.Name}\" on {_session.Connection.Name}", e.Name) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value.Trim();
        if (name == e.Name) return;
        if (!RemoteFs.IsValidName(name)) { Remote.ShowError($"\"{name}\" is not a valid name."); return; }
        try
        {
            await _session.RenameAsync(e.FullPath, name);
            await Remote.Refresh();
        }
        catch (Exception ex) { Remote.ShowError("Rename failed: " + ex.Message); }
    }

    private async Task DeleteRemote(List<FileEntry> items)
    {
        items = items.Where(i => i.Kind != EntryKind.Drive).ToList();
        if (!_session.IsConnected || items.Count == 0) return;
        var folders = items.Count(i => i.Kind == EntryKind.Folder);
        var list = string.Join("\n", items.Take(8).Select(i => (i.Kind == EntryKind.Folder ? "📁 " : "") + i.Name)) + (items.Count > 8 ? $"\n… and {items.Count - 8} more" : "");
        var text = $"Permanently delete {(items.Count == 1 ? "this item" : $"these {items.Count} items")} on {_session.Connection.Name}?\n\n{list}\n\n" +
                   (folders > 0 ? "Folders are deleted with everything in them. " : "") + "There is no recycle bin for remote deletes.";
        if (Window.GetWindow(this) is not { } owner || !await Dialogs.Confirm(owner, "Delete on remote computer", text, "Delete")) return;
        try
        {
            await _session.DeleteAsync(items.Select(i => i.FullPath));
            Services.DiagLog.Write($"Remote delete: {items.Count} item(s)");
        }
        catch (Exception ex) { Remote.ShowError("Delete failed: " + ex.Message); }
        await Remote.Refresh();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileBrowserSession.AccentBrush)) UpdateFrame();
    }

    private void UpdateFrame()
    {
        _headerStripe.Background = _session.AccentBrush;
        SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
    }
}
