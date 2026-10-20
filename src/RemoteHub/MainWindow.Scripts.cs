using System.Management.Automation.Runspaces;
using System.Windows;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.Views;

namespace RemoteHub;

/// <summary>
/// Running scripts on a connection (docs/scripts-view-design.md §6.5): "Run script…" in the tree's and the session tab's
/// Tools, "Run on…" and "Test draft on…" from the Scripts view, and completing against a device's session (phase 4).
/// </summary>
public partial class MainWindow
{
    private void CmRunScript_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNode is Connection c) RunScriptOn(c, KnownTarget(c));
    }

    private void CmScriptRuns_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNode is not Connection c) return;
        new ScriptRunHistoryWindow(null, c.Name, c.Id.ToString(), ConnectionNameOf, _vm.Data.Ui, _vm.RequestSave) { Owner = this }.ShowDialog();
    }

    /// <summary>Opens the run window for a connection: pick a script there.</summary>
    private void RunScriptOn(Connection c, PsTarget? known, ScriptEntry? script = null, bool draft = false)
    {
        if (Refused(Feature.ScriptRun) || Refused(RemoteTool.PowerShell, c)) return;
        if (draft && Refused(Feature.ScriptDraftRun)) return;
        if (ToolTarget(c, known) is not { } t) return;
        var w = new RunScriptWindow(new RunScriptContext(c.Id.ToString(), c.Name, t.Target, _vm.Data.Ui, _vm.RequestSave, script, draft)) { Owner = this };
        w.Show();
    }

    /// <summary>"Run on…" in the Scripts view: pick the connection, then the run window confirms.</summary>
    private void RunScriptFromView(ScriptEntry script, bool draft)
    {
        if (Refused(Feature.ScriptRun) || (draft && Refused(Feature.ScriptDraftRun))) return;
        if (PickConnection("Run on", "The connections where PowerShell remoting or the RemoteHub Agent is available. Test one in the connection's Tools menu if it isn't listed.")
            is not { } c) return;
        RunScriptOn(c, KnownTarget(c), script, draft);
    }

    /// <summary>A connection whose PowerShell (WinRM or agent) is available and allowed, picked by the user; null when cancelled.</summary>
    private Connection? PickConnection(string title, string info)
    {
        var candidates = _vm.AllNodes().OfType<Connection>()
            .Where(c => PsState(c) == PsRemotingState.Available && Features.IsAllowed(RemoteTool.PowerShell, UsesAgent(c)))
            .ToList();
        // Favourite connections first, then by path.
        var favourites = _vm.Data.Ui.FavoriteConnections;
        var targets = candidates
            .OrderBy(c => favourites.Contains(c.Id) ? favourites.IndexOf(c.Id) : int.MaxValue)
            .ThenBy(NodePath, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new NoteLinkTarget(c.Id.ToString(), c.Name, NodePath(c), false))
            .ToList();
        if (targets.Count == 0)
        {
            _ = Dialogs.Info(this, title, "No connection has PowerShell remoting or the RemoteHub Agent available yet. Test it in a connection's Tools menu first.");
            return null;
        }
        var picker = new ConnectionPickerWindow(title, info, targets, favourites.Select(f => f.ToString()).ToHashSet()) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected is not { } id) return null;
        return candidates.FirstOrDefault(c => c.Id.ToString() == id);
    }

    /// <summary>The name of a connection by id, for the run history (null: it was deleted).</summary>
    private string? ConnectionNameOf(string id) => _vm.AllNodes().OfType<Connection>().FirstOrDefault(c => c.Id.ToString() == id)?.Name;

    /// <summary>Phase 4: a device's session for the editor to complete against (its modules and commands).</summary>
    private async Task<(Runspace Runspace, string Name)?> OpenCompletionSession()
    {
        if (PickConnection("Complete from a device", "IntelliSense then offers the commands and modules of the device's PowerShell. The session stays open while the Scripts view is open.")
            is not { } c) return null;
        if (Refused(RemoteTool.PowerShell, c) || ToolTarget(c, KnownTarget(c)) is not { } t) return null;
        try
        {
            var rs = await Task.Run(() => PsRemoting.Open(t.Target));
            return (rs, c.Name);
        }
        catch (Exception ex)
        {
            await Dialogs.Info(this, "Complete from a device", PsRemoting.Explain(ex, t.Target));
            return null;
        }
    }
}

/// <summary>Picks one connection (the Scripts view's "Run on…"), searched by its path.</summary>
public sealed class ConnectionPickerWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Wpf.Ui.Controls.TextBox _search = new() { PlaceholderText = "Search connections", Margin = new Thickness(0, 0, 0, 8) };
    private readonly System.Windows.Controls.ListBox _items = new() { Height = 320, BorderThickness = new Thickness(0) };
    private readonly IReadOnlyList<NoteLinkTarget> _targets;
    private readonly IReadOnlySet<string> _favourites;

    public string? Selected { get; private set; }

    public ConnectionPickerWindow(string title, string info, IReadOnlyList<NoteLinkTarget> targets, IReadOnlySet<string> favourites)
    {
        _targets = targets;
        _favourites = favourites;
        ScriptWindowFrame.Apply(this, title, 500);
        var ok = new Wpf.Ui.Controls.Button { Content = "Next", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", IsCancel = true };
        var body = new System.Windows.Controls.StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(ScriptWindowFrame.Info(info));
        body.Children.Add(_search);
        body.Children.Add(_items);
        body.Children.Add(ScriptWindowFrame.Buttons(ok, cancel));
        ScriptWindowFrame.Fill(this, body, resizable: false);
        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Down && _items.Items.Count > 0) { _items.SelectedIndex = 0; _items.Focus(); e.Handled = true; } };
        _items.MouseDoubleClick += (_, _) => Accept();
        Loaded += (_, _) => _search.Focus();
        Fill();
    }

    private void Accept()
    {
        if (_items.SelectedItem is not System.Windows.Controls.ListBoxItem { Tag: string id }) return;
        Selected = id;
        DialogResult = true;
    }

    private void Fill()
    {
        _items.Items.Clear();
        var words = _search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in _targets.Where(t => words.All(w => t.Path.Contains(w, StringComparison.CurrentCultureIgnoreCase))).Take(300))
        {
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Children.Add(new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Desktop24) { FontSize = 14, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = t.Path, TextTrimming = TextTrimming.CharacterEllipsis });
            if (_favourites.Contains(t.Id))
            {
                // Marked as the Scripts lists mark their favourites.
                var star = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Star24) { FontSize = 12, Filled = true, Margin = new Thickness(6, 0, 0, 0), ToolTip = "In Favourites", VerticalAlignment = VerticalAlignment.Center };
                star.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextFillColorSecondaryBrush");
                row.Children.Add(star);
            }
            _items.Items.Add(new System.Windows.Controls.ListBoxItem { Content = row, Tag = t.Id });
        }
        if (_items.Items.Count > 0) _items.SelectedIndex = 0;
    }
}
