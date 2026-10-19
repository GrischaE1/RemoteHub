using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private string CurrentView => Rail.Selected ?? RemoteViewId;

    private void InitViews()
    {
        Rail.SetViews(RailViews());
        Rail.ViewSelected += id => ShowView(id);
        Rail.SettingsClicked += () => OpenSettings(null);
        Rail.VersionClicked += () =>
        {
            if (_availableUpdate is not null) ShowUpdateWindow(_availableUpdate, null);
            else new AboutWindow { Owner = this }.ShowDialog();
        };
        Rail.FocusViewRequested += FocusCurrentView;
        UpdateRailVersion();
        ShowView(_vm.Data.Ui.LastView, save: false);
    }

    private static IEnumerable<RailView> RailViews()
    {
        yield return new RailView(RemoteViewId, SymbolRegular.Desktop24, "Remote Control", "Ctrl+1");
        yield return new RailView(NotesViewId, SymbolRegular.Notebook24, "Notes", "Ctrl+2");
    }

    /// <summary>
    /// Shows one view. The others are hidden, not collapsed: hidden keeps their layout, so embedded RDP and agent desktop
    /// controls keep their size and the sessions behind them are not resized or reconnected.
    /// </summary>
    private void ShowView(string? id, bool save = true)
    {
        if (id != NotesViewId) id = RemoteViewId; // also an unknown value from a newer version
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
        var placeholder = new System.Windows.Controls.TextBlock { Text = "Notes are coming.", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        placeholder.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _notesView = placeholder;
        NotesHost.Child = _notesView;
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
        if (e.Key is Key.D2 or Key.NumPad2) { ShowView(NotesViewId); FocusCurrentView(); e.Handled = true; return true; }
        if (CurrentView != NotesViewId) return false;
        // In Notes, Ctrl+B is bold, Ctrl+F searches the notes and Ctrl+N adds a page: the view handles them itself.
        return e.Key is Key.B or Key.F or Key.N;
    }
}
