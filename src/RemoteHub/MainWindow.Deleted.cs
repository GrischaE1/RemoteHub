using System.Windows;
using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.Views;
using Wpf.Ui.Controls;

namespace RemoteHub;

/// <summary>
/// Deleting in the connection tree goes through "Recently deleted" with an undo bar, as in Notes and Scripts, and the
/// tree's colour menu (docs/scripts-view-design.md §4.9).
/// </summary>
public partial class MainWindow
{
    private UndoBar? _treeUndo;

    /// <summary>The undo bar at the bottom of the tree (made the first time it is needed).</summary>
    private UndoBar TreeUndo
    {
        get
        {
            if (_treeUndo is not null) return _treeUndo;
            _treeUndo = new UndoBar { Margin = new Thickness(0, 0, 0, 8) };
            TreeArea.Children.Add(_treeUndo);
            return _treeUndo;
        }
    }

    /// <summary>Deletes items into "Recently deleted" and offers to undo it.</summary>
    private void DeleteWithUndo(IReadOnlyList<NodeBase> nodes)
    {
        if (nodes.Count == 0) return;
        foreach (var n in nodes) _vm.Delete(n);
        var text = nodes.Count > 1 ? $"{nodes.Count} items deleted" : nodes[0] is Folder ? "Folder deleted" : nodes[0] is Connection ? "Connection deleted" : "Deleted";
        if (!Data.LocalDb.IsOpen) return;
        var ids = nodes.Select(n => n.Id.ToString()).ToList();
        TreeUndo.Show(text, () =>
        {
            foreach (var id in ids)
                if (_vm.RestoreDeleted(id) is { } back) Data.AuditLog.Write(Data.AuditEvents.NodeRestored, back is Connection ? id : null, back.Name, back is Folder ? "Folder" : "Connection");
        });
    }

    private void SetNodeColour(NodeBase n, string? colour)
    {
        if (Refused(Feature.ConnectionEditing)) return;
        n.Options.ColorTag = colour;
        _vm.RefreshTags();
        _vm.RequestSave();
    }

    private void CmRecentlyDeleted_Click(object sender, RoutedEventArgs e) => ShowTreeRecentlyDeleted();

    private void ShowTreeRecentlyDeleted()
    {
        if (Refused(Feature.ConnectionEditing) || !Data.LocalDb.IsOpen) return;
        var w = new RecentlyDeletedWindow(new TreeDeletedSource(_vm, this)) { Owner = this };
        w.ShowDialog();
    }

    /// <summary>"Recently deleted" of the connection tree: folders and connections, each with what was inside it.</summary>
    private sealed class TreeDeletedSource(ViewModels.MainViewModel vm, Window owner) : IRecentlyDeletedSource
    {
        public string Info => $"Deleted folders and connections are kept on this PC for {Data.DeletedNodeStore.KeepDays} days, with their saved credentials.";
        public string Empty => $"Nothing was deleted in the last {Data.DeletedNodeStore.KeepDays} days.";

        public IReadOnlyList<DeletedEntry> Entries() => Data.DeletedNodeStore.List().Select(d => new DeletedEntry(d.Kind, d.Id, d.Name,
            d.Kind switch
            {
                "folder" => d.Items == 1 ? "Folder, 1 item" : $"Folder, {d.Items} items",
                "connection" when d.Items > 0 => $"Connection, {d.Items} reached through it",
                "connection" => "Connection",
                _ => "Network share",
            },
            d.DeletedUtc,
            d.Kind switch { "folder" => SymbolRegular.Folder24, "connection" => SymbolRegular.Desktop24, _ => SymbolRegular.FolderLink24 })).ToList();

        public void Restore(DeletedEntry entry)
        {
            if (vm.RestoreDeleted(entry.Id) is { } back)
                Data.AuditLog.Write(Data.AuditEvents.NodeRestored, back is Connection ? entry.Id : null, back.Name, back is Folder ? "Folder" : "Connection");
            else _ = Dialogs.Info(owner, "Recently deleted", "This item can't be restored: it is in the list of connections already.");
        }

        public void DeleteNow(DeletedEntry entry)
        {
            Data.DeletedNodeStore.DeleteNow(entry.Id);
            Data.AuditLog.Write(Data.AuditEvents.NodePurged, entry.Kind == "connection" ? entry.Id : null, entry.Name, entry.Kind == "folder" ? "Folder" : "Connection");
        }
    }
}
