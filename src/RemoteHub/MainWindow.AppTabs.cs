using System.Windows.Controls;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.Views;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace RemoteHub;

/// <summary>
/// App tabs (docs/app-tabs-design.md §8): a connection's apps in their own tabs through the device's RemoteHub Agent –
/// the "Open app" group of the connection's, the tab's and the "+" menus, and opening one.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// App tabs can be offered for <paramref name="c"/>: the agent's remote desktop is allowed on this PC and the agent is
    /// installed there. Whether the device allows them for this account it says when connected (AppTabsRefusal).
    /// </summary>
    private static bool AppTabsOffered(Connection c) => Features.IsAllowed(Feature.AgentDesktop) && c.Agent is { Status: AgentStatus.Installed };

    /// <summary>
    /// Fills an "Open app" group afresh: the connection's apps, then "More apps on …"; empty (so the group hides) where app
    /// tabs aren't offered. Without automatic sign-in on the connection, it says so and offers to turn it on – app tabs
    /// need it (§8.1).
    /// </summary>
    private void FillOpenApps(MenuItem group, Connection? c)
    {
        group.Items.Clear();
        if (c is null || !AppTabsOffered(c)) return;
        var options = Inheritance.ResolveOptions(c, _vm.Data);
        if (options.AgentAutoSignIn != true)
        {
            group.Items.Add(MenuEntry("App tabs need automatic sign-in", SymbolRegular.Info24, () => { }, enabled: false,
                tip: "App tabs run in your own session on the device; when nobody is signed in there, RemoteHub signs you in automatically."));
            if (!AppPolicy.AgentAutoSignInDisabled && Features.IsAllowed(Feature.ConnectionEditing))
                group.Items.Add(MenuEntry($"Turn on automatic sign-in for {c.Name}", SymbolRegular.Key24, () => TurnOnAutoSignIn(c)));
            return;
        }
        foreach (var app in options.Apps ?? [])
        {
            var entry = MenuEntry(app.Name, SymbolRegular.Apps24, () => OpenAgentApp(c, app), tip: app.Elevated ? "Runs as administrator" : null);
            if (app.Icon is not null) entry.Icon = AppRows.Icon(app.Icon, 16);
            group.Items.Add(entry);
        }
        if (group.Items.Count > 0) group.Items.Add(new Separator());
        group.Items.Add(MenuEntry($"More apps on {c.Name}…", SymbolRegular.AppsAddIn24, () => PickAndOpenApp(c)));
    }

    /// <summary>
    /// Settings → Apps → "Add from the device…": the Start menu of the connection being edited – for a folder or the
    /// defaults, of a connection with the agent the user picks first (design §8.2) – with apps ticked to add; null when
    /// cancelled.
    /// </summary>
    private IReadOnlyList<ConnectionApp>? PickAppsFromDevice(System.Windows.Window owner, NodeBase? node, IReadOnlyList<ConnectionApp> current)
    {
        var c = node as Connection;
        if (c is null)
        {
            var below = node is Folder f ? _vm.AllNodes().OfType<Connection>().Where(x => x.Ancestors().Contains(f)) : _vm.AllNodes().OfType<Connection>();
            var candidates = below.Where(AppTabsOffered).ToList();
            if (candidates.Count == 0)
            {
                _ = Dialogs.Info(owner, "Add from the device", "No connection here has the RemoteHub Agent installed – its Start menu is read through the agent.");
                return null;
            }
            var favourites = _vm.Data.Ui.FavoriteConnections;
            var targets = candidates.OrderBy(x => favourites.Contains(x.Id) ? favourites.IndexOf(x.Id) : int.MaxValue).ThenBy(NodePath, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => new NoteLinkTarget(x.Id.ToString(), x.Name, NodePath(x), false)).ToList();
            var pick = new ConnectionPickerWindow("Add from the device", "Whose Start menu? The connections with the RemoteHub Agent installed.", targets,
                favourites.Select(x => x.ToString()).ToHashSet()) { Owner = owner };
            if (pick.ShowDialog() != true || pick.Selected is not { } id || candidates.FirstOrDefault(x => x.Id.ToString() == id) is not { } chosen) return null;
            c = chosen;
        }
        else if (!AppTabsOffered(c))
        {
            _ = Dialogs.Info(owner, "Add from the device", $"{c.Name} has no RemoteHub Agent installed – its Start menu is read through the agent. Install it first (right-click the connection → Install RemoteHub Agent…).");
            return null;
        }
        if (AgentDesktopTarget(c, null) is not { } t) return null;
        var picker = new AppPickerWindow(c.Name, ct => AgentDesktopSession.ListAppsAsync(t.Target, ct), current, singleChoice: false,
            explain: ex => PsRemoting.Explain(ex, t.Target)) { Owner = owner };
        return picker.ShowDialog() == true ? picker.Chosen : null;
    }

    /// <summary>The connection's own "Sign in automatically when nobody is signed in" set to On, as in its settings.</summary>
    private void TurnOnAutoSignIn(Connection c)
    {
        if (Refused(Feature.ConnectionEditing)) return;
        c.Options.AgentAutoSignIn = true;
        _vm.RequestSave();
    }

    /// <summary>Opens <paramref name="app"/> of <paramref name="c"/> in a tab of its own – or shows the tab it already has.</summary>
    private void OpenAgentApp(Connection c, ConnectionApp app)
    {
        if (_vm.FindAgentApp(c, app) is { } existing)
        {
            _vm.SelectedSession = existing;
            if (existing.Phase is DesktopPhase.Ended or DesktopPhase.Idle) ConnectAgentApp(existing);
            return;
        }
        if (Refused(Feature.AgentDesktop)) return;
        if (AgentDesktopTarget(c, null) is not { } t) return;
        var s = _vm.OpenAgentApp(c, app);
        AddAgentDesktopView(c, s);
        UseConnectionSignIn(s);
        s.Connect(t.Target, t.Source, AccentFor(c));
    }

    private void ConnectAgentApp(AgentDesktopSession s)
    {
        UseConnectionSignIn(s);
        ConnectAgentDesktop(s);
    }

    /// <summary>The connection's automatic sign-in, read when the tab connects (the setting may have changed since).</summary>
    private void UseConnectionSignIn(AgentDesktopSession s)
    {
        var options = Inheritance.ResolveOptions(s.Connection, _vm.Data);
        s.AppAutoSignIn = options.AgentAutoSignIn == true;
        s.AppEndAction = options.AgentAutoSignInEndAction;
    }

    /// <summary>
    /// "More apps on …": the device's Start menu, one app picked and opened – kept in the connection's apps when asked
    /// (the default); "Not in the list?" opens the connection's settings on its Apps page.
    /// </summary>
    private void PickAndOpenApp(Connection c)
    {
        if (Refused(Feature.AgentDesktop) || AgentDesktopTarget(c, null) is not { } t) return;
        var current = Inheritance.ResolveOptions(c, _vm.Data).Apps ?? [];
        var picker = new AppPickerWindow(c.Name, ct => AgentDesktopSession.ListAppsAsync(t.Target, ct), current, singleChoice: true,
            canKeep: Features.IsAllowed(Feature.ConnectionEditing), explain: ex => PsRemoting.Explain(ex, t.Target)) { Owner = this };
        if (picker.ShowDialog() != true)
        {
            if (picker.AddAnother) EditNode(c, "PageApps");
            return;
        }
        var app = picker.Chosen[0];
        if (picker.Keep && Features.IsAllowed(Feature.ConnectionEditing) && !current.Any(a => a.SameApp(app)))
        {
            // A list of its own, starting from what it inherited (RdpOptions.Apps: an own list replaces the folder's).
            c.Options.Apps = [.. c.Options.Apps ?? current, app];
            _vm.RequestSave();
        }
        OpenAgentApp(c, app);
    }
}
