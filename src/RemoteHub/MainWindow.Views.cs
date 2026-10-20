using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.Views;
using Wpf.Ui.Controls;

namespace RemoteHub;

/// <summary>The view rail and switching between the top-level views (notes-view-design.md §3, scripts-view-design.md §3).</summary>
public partial class MainWindow
{
    public const string RemoteViewId = "Remote";
    public const string NotesViewId = "Notes";
    public const string ScriptsViewId = "Scripts";

    private FrameworkElement? _notesView;
    private FrameworkElement? _scriptsView;
    private bool _notesAllowed;
    private bool _scriptsAllowed;

    private string CurrentView => Rail.Selected ?? RemoteViewId;

    private void InitViews()
    {
        _notesAllowed = Features.IsAllowed(Feature.Notes);
        _scriptsAllowed = Features.IsAllowed(Feature.Scripts);
        Rail.SetViews(RailViews());
        Rail.ViewSelected += id => ShowView(id);
        Rail.SettingsClicked += () => OpenSettings(null);
        // The version always opens About; an update is offered by the bar at the top and in Settings → Updates & privacy.
        Rail.VersionClicked += () => new AboutWindow { Owner = this }.ShowDialog();
        Rail.FocusViewRequested += FocusCurrentView;
        // Policies are read when asked: after a gpupdate, Notes and Scripts come or go the next time the window is activated.
        Activated += (_, _) => ApplyViewPolicies();
        UpdateRailVersion();
        ShowView(_vm.Data.Ui.LastView, save: false);
    }

    internal static IEnumerable<RailView> RailViews(IPolicySource? source = null)
    {
        yield return new RailView(RemoteViewId, SymbolRegular.Desktop24, "Remote Control", "Ctrl+1");
        if (Features.IsAllowed(Feature.Notes, source)) yield return new RailView(NotesViewId, SymbolRegular.Notebook24, "Notes", "Ctrl+2");
        if (Features.IsAllowed(Feature.Scripts, source)) yield return new RailView(ScriptsViewId, SymbolRegular.Code24, "Scripts", "Ctrl+3");
    }

    /// <summary>
    /// The view a request for <paramref name="id"/> shows: Remote Control for an unknown value (from a newer version), and
    /// for Notes or Scripts while their policy blocks them (notes-view-design.md §8, scripts-view-design.md §9). A saved
    /// LastView of a blocked view is kept for when it comes back.
    /// </summary>
    internal static string ViewFor(string? id, IPolicySource? source = null) => id switch
    {
        NotesViewId when Features.IsAllowed(Feature.Notes, source) => NotesViewId,
        ScriptsViewId when Features.IsAllowed(Feature.Scripts, source) => ScriptsViewId,
        _ => RemoteViewId,
    };

    /// <summary>AllowNotes or AllowScripts changed since the rail was built: rebuild the rail, and leave a view that is blocked now.</summary>
    private void ApplyViewPolicies()
    {
        var notes = Features.IsAllowed(Feature.Notes);
        var scripts = Features.IsAllowed(Feature.Scripts);
        if (notes == _notesAllowed && scripts == _scriptsAllowed) return;
        if (notes != _notesAllowed) DiagLog.Write(notes ? "Notes: allowed by policy again" : "Notes: blocked by policy");
        if (scripts != _scriptsAllowed) DiagLog.Write(scripts ? "Scripts: allowed by policy again" : "Scripts: blocked by policy");
        _notesAllowed = notes;
        _scriptsAllowed = scripts;
        var current = CurrentView;
        Rail.SetViews(RailViews());
        if (current == NotesViewId && !notes)
        {
            _ = Notes?.Save(sync: true); // the open page, before the view goes
            ShowView(RemoteViewId, save: false);
        }
        else if (current == ScriptsViewId && !scripts)
        {
            Scripts?.Save(); // the open draft, before the view goes
            ShowView(RemoteViewId, save: false);
        }
        else Rail.Selected = current;
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
        var scripts = id == ScriptsViewId;
        if (notes) EnsureNotesView();
        if (scripts) EnsureScriptsView();
        RemoteView.Visibility = notes || scripts ? Visibility.Hidden : Visibility.Visible;
        NotesHost.Visibility = notes ? Visibility.Visible : Visibility.Hidden;
        ScriptsHostBorder.Visibility = scripts ? Visibility.Visible : Visibility.Hidden;
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
            NotesHost.Child = Unavailable("Notes can't be opened: the local database isn't available.");
            return;
        }
        NotesHost.Child = _notesView;
    }

    private void EnsureScriptsView()
    {
        if (_scriptsView is not null) return;
        try
        {
            _scriptsView = new ScriptsView(new ScriptsHost(this, _vm.Data.Ui, _vm.RequestSave, RunScriptFromView, ConnectionNameOf, OpenCompletionSession));
        }
        catch (Exception ex)
        {
            // As for Notes: without the database the view says so instead of failing.
            DiagLog.Write("Scripts: the view could not open: " + ex.GetType().Name);
            ScriptsHostBorder.Child = Unavailable("Scripts can't be opened: the local database isn't available.");
            return;
        }
        ScriptsHostBorder.Child = _scriptsView;
    }

    private static System.Windows.Controls.TextBlock Unavailable(string text)
    {
        var t = new System.Windows.Controls.TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    private NotesView? Notes => _notesView as NotesView;
    private ScriptsView? Scripts => _scriptsView as ScriptsView;

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
        else if (CurrentView == ScriptsViewId) _scriptsView?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
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

    /// <summary>Shortcuts for the views. True when the key was handled or belongs to the Notes or Scripts view.</summary>
    private bool HandleViewKeys(KeyEventArgs e, bool ctrl)
    {
        if (!ctrl || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return false;
        if (e.Key is Key.D1 or Key.NumPad1) { ShowView(RemoteViewId); FocusCurrentView(); e.Handled = true; return true; }
        // With Notes or Scripts blocked, Ctrl+2 or Ctrl+3 isn't taken and goes on to the session, as before there were views.
        if (e.Key is Key.D2 or Key.NumPad2 && ViewFor(NotesViewId) == NotesViewId) { ShowView(NotesViewId); FocusCurrentView(); e.Handled = true; return true; }
        if (e.Key is Key.D3 or Key.NumPad3 && ViewFor(ScriptsViewId) == ScriptsViewId) { ShowView(ScriptsViewId); FocusCurrentView(); e.Handled = true; return true; }
        // In Notes, Ctrl+B is bold, Ctrl+F searches the notes and Ctrl+N adds a page: the view and its editor handle them.
        if (CurrentView == NotesViewId) return e.Key is Key.B or Key.F or Key.N;
        // In Scripts, Ctrl+F searches, Ctrl+N adds a script, Ctrl+S saves the draft; the editor has its own keys (Ctrl+G, Ctrl+Space).
        if (CurrentView == ScriptsViewId) return e.Key is Key.F or Key.N or Key.S or Key.G or Key.Space or Key.D or Key.C;
        return false;
    }
}
