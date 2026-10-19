using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.Views;
using Wpf.Ui.Controls;

namespace RemoteHub;

/// <summary>The view rail and switching between the top-level views (notes-view-design.md §3).</summary>
public partial class MainWindow
{
    public const string RemoteViewId = "Remote";
    public const string NotesViewId = "Notes";

    private FrameworkElement? _notesView;
    private bool _notesAllowed;

    private string CurrentView => Rail.Selected ?? RemoteViewId;

    private void InitViews()
    {
        _notesAllowed = Features.IsAllowed(Feature.Notes);
        Rail.SetViews(RailViews());
        Rail.ViewSelected += id => ShowView(id);
        Rail.SettingsClicked += () => OpenSettings(null);
        // The version always opens About; an update is offered by the bar at the top and in Settings → Updates & privacy.
        Rail.VersionClicked += () => new AboutWindow { Owner = this }.ShowDialog();
        Rail.FocusViewRequested += FocusCurrentView;
        // Policies are read when asked: after a gpupdate, Notes come or go the next time the window is activated.
        Activated += (_, _) => ApplyNotesPolicy();
        UpdateRailVersion();
        ShowView(_vm.Data.Ui.LastView, save: false);
    }

    internal static IEnumerable<RailView> RailViews(IPolicySource? source = null)
    {
        yield return new RailView(RemoteViewId, SymbolRegular.Desktop24, "Remote Control", "Ctrl+1");
        if (Features.IsAllowed(Feature.Notes, source)) yield return new RailView(NotesViewId, SymbolRegular.Notebook24, "Notes", "Ctrl+2");
    }

    /// <summary>
    /// The view a request for <paramref name="id"/> shows: Remote Control for an unknown value (from a newer version), and
    /// for Notes while AllowNotes = 0 (notes-view-design.md §8). A saved LastView of Notes is kept for when it comes back.
    /// </summary>
    internal static string ViewFor(string? id, IPolicySource? source = null) =>
        id == NotesViewId && Features.IsAllowed(Feature.Notes, source) ? NotesViewId : RemoteViewId;

    /// <summary>AllowNotes changed since the rail was built: rebuild the rail, and leave Notes when they are blocked now.</summary>
    private void ApplyNotesPolicy()
    {
        var allowed = Features.IsAllowed(Feature.Notes);
        if (allowed == _notesAllowed) return;
        _notesAllowed = allowed;
        DiagLog.Write(allowed ? "Notes: allowed by policy again" : "Notes: blocked by policy");
        var current = CurrentView;
        Rail.SetViews(RailViews());
        if (current == NotesViewId && !allowed)
        {
            _ = Notes?.Save(sync: true); // the open page, before the view goes
            ShowView(RemoteViewId, save: false);
        }
        UpdateNotesMenu(_vm.SelectedNode);
    }

    /// <summary>
    /// Shows one view. The others are hidden, not collapsed: hidden keeps their layout, so embedded RDP and agent desktop
    /// controls keep their size and the sessions behind them are not resized or reconnected.
    /// </summary>
    private void ShowView(string? id, bool save = true)
    {
        id = ViewFor(id);
        var notes = id == NotesViewId;
        if (notes) EnsureNotesView();
        RemoteView.Visibility = notes ? Visibility.Hidden : Visibility.Visible;
        NotesHost.Visibility = notes ? Visibility.Visible : Visibility.Hidden;
        Rail.Selected = id;
        if (save && _vm.Data.Ui.LastView != id)
        {
            _vm.Data.Ui.LastView = id;
            _vm.RequestSave();
        }
    }

    private void EnsureNotesView()
    {
        if (_notesView is not null) return;
        try
        {
            _notesView = new NotesView(new NotesHost(this, _vm.Data.Ui, _vm.RequestSave, LinkTargets, SettingsStore.IsShared));
        }
        catch (Exception ex)
        {
            // The database isn't open (e.g. it couldn't be read at start): Notes say so instead of failing.
            DiagLog.Write("Notes: the view could not open: " + ex.GetType().Name);
            var text = new System.Windows.Controls.TextBlock { Text = "Notes can't be opened: the local database isn't available.", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            NotesHost.Child = text;
            return;
        }
        NotesHost.Child = _notesView;
    }

    private NotesView? Notes => _notesView as NotesView;

    /// <summary>Folders and connections a page can be linked to, with their path for the picker.</summary>
    private IReadOnlyList<NoteLinkTarget> LinkTargets() =>
        [.. _vm.AllNodes().Where(n => n is Folder or Connection).Select(n => new NoteLinkTarget(n.Id.ToString(), n.Name, NodePath(n), n is Folder))
            .OrderBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase)];

    private static string NodePath(NodeBase n)
    {
        var parts = new List<string>();
        for (var x = n; x is not null; x = x.Parent) parts.Insert(0, x.Name);
        return string.Join(" › ", parts);
    }

    private void UpdateNotesMenu(NodeBase? n)
    {
        var offered = n is Folder or Connection && Features.IsAllowed(Feature.Notes);
        var count = 0;
        if (offered && Data.LocalDb.IsOpen)
            try { count = Data.NoteStore.LinkCounts().GetValueOrDefault(n!.Id.ToString()); } catch (Exception ex) { DiagLog.Write("Notes: counting links failed: " + ex.GetType().Name); }
        CmNotes.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        CmNotes.Header = count > 0 ? $"Notes ({count})" : "Notes";
    }

    private void CmNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNode is { } n) ShowNotesFor(n);
    }

    /// <summary>The tree's "Notes" command: the pages linked to the selected folder or connection.</summary>
    private void ShowNotesFor(NodeBase node)
    {
        if (ViewFor(NotesViewId) != NotesViewId) return;
        ShowView(NotesViewId);
        Notes?.ShowLinkedTo([node.Id.ToString()], $"Linked to {node.Name}");
    }

    private void FocusCurrentView()
    {
        if (CurrentView == NotesViewId) _notesView?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        else if (_vm.SelectedSession is null) Tree.Focus();
        else SessionArea.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>Version above Settings on the rail; the update check result adds a dot and a line.</summary>
    private void UpdateRailVersion()
    {
        var v = AppVersion.Current;
        var update = _availableUpdate?.Info?.Version;
        var channel = UpdateService.EffectiveChannel(_vm.Data.Ui);
        var second = update is not null ? $"Update available: {update}"
            : channel == UpdateService.Beta ? "Beta channel" : null;
        Rail.SetVersion($"{v.Major}.{v.Minor}", $"RemoteHub {v}", second, update is not null);
    }

    /// <summary>Shortcuts for the views. True when the key was handled or belongs to the Notes view.</summary>
    private bool HandleViewKeys(KeyEventArgs e, bool ctrl)
    {
        if (!ctrl || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return false;
        if (e.Key is Key.D1 or Key.NumPad1) { ShowView(RemoteViewId); FocusCurrentView(); e.Handled = true; return true; }
        // With Notes blocked, Ctrl+2 isn't taken and goes on to the session, as before there was a Notes view.
        if (e.Key is Key.D2 or Key.NumPad2 && ViewFor(NotesViewId) == NotesViewId) { ShowView(NotesViewId); FocusCurrentView(); e.Handled = true; return true; }
        if (CurrentView != NotesViewId) return false;
        // In Notes, Ctrl+B is bold, Ctrl+F searches the notes and Ctrl+N adds a page: the view and its editor handle them.
        return e.Key is Key.B or Key.F or Key.N;
    }
}
