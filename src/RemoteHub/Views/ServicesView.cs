using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;

namespace RemoteHub.Views;

/// <summary>One remote service in the list; updated in place on every refresh.</summary>
public sealed partial class ServiceRow : ObservableObject
{
    public ServiceRow(ServiceInfo s)
    {
        Name = s.Name;
        Update(s);
    }

    public string Name { get; }
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private ServiceStartup _startup;
    [ObservableProperty] private string _account = "";
    [ObservableProperty] private int _processId;
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _canStop;

    public bool IsRunning => State == "Running";
    public bool IsStopped => State == "Stopped";
    public string StateText => RemoteServices.StateName(State);
    public string StartupText => RemoteServices.StartupName(Startup);
    public string PidText => ProcessId > 0 ? ProcessId.ToString() : "";
    public Brush StateBrush => IsRunning ? SessionStateToBrush.GreenBrush : IsStopped ? SessionStateToBrush.GreyBrush : SessionStateToBrush.AmberBrush;

    partial void OnStateChanged(string value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
    }
    partial void OnStartupChanged(ServiceStartup value) => OnPropertyChanged(nameof(StartupText));
    partial void OnProcessIdChanged(int value) => OnPropertyChanged(nameof(PidText));

    public void Update(ServiceInfo s)
    {
        DisplayName = s.DisplayName;
        State = s.State;
        Startup = s.Startup;
        Account = s.Account;
        ProcessId = s.ProcessId;
        Path = s.Path;
        Description = s.Description;
        CanStop = s.CanStop;
    }
}

/// <summary>Services tab: start, stop, restart and startup type of the remote computer's services; refreshed regularly while visible.</summary>
public sealed class ServicesView : RemoteListView<ServiceRow>
{
    private readonly ServicesSession _session;
    private readonly Wpf.Ui.Controls.Button _start, _stop, _restart;

    private static readonly ServiceStartup[] Startups = [ServiceStartup.Automatic, ServiceStartup.AutomaticDelayed, ServiceStartup.Manual, ServiceStartup.Disabled];

    public ServicesView(ServicesSession session, Func<int> intervalSeconds, Action reconnect, Action close)
        : base(session, SymbolRegular.WrenchScrewdriver24, "Services", intervalSeconds, reconnect, close)
    {
        _session = session;
        List.SelectionMode = SelectionMode.Single;
        _start = RemoteToolChrome.Tool(SymbolRegular.Play24, "Start the service", () => { if (One is { } r) _ = Control(r, ServiceAction.Start); });
        _stop = RemoteToolChrome.Tool(SymbolRegular.Stop24, "Stop the service", () => { if (One is { } r) _ = Control(r, ServiceAction.Stop); });
        _restart = RemoteToolChrome.Tool(SymbolRegular.ArrowRepeatAll24, "Restart the service", () => { if (One is { } r) _ = Control(r, ServiceAction.Restart); });
        _start.Content = "Start";
        _stop.Content = "Stop";
        _restart.Content = "Restart";
        foreach (var b in new[] { _start, _stop, _restart }) AddTool(b);

        var name = AddColumn("Name", 280, "", nameof(ServiceRow.DisplayName), template: IconTextTemplate(nameof(ServiceRow.DisplayName), dotBrush: nameof(ServiceRow.StateBrush)));
        AddColumn("Status", 90, nameof(ServiceRow.StateText), nameof(ServiceRow.State));
        AddColumn("Startup type", 170, nameof(ServiceRow.StartupText), nameof(ServiceRow.Startup));
        AddColumn("Service name", 170, nameof(ServiceRow.Name), nameof(ServiceRow.Name));
        AddColumn("Log on as", 170, nameof(ServiceRow.Account), nameof(ServiceRow.Account));
        AddColumn("PID", 64, nameof(ServiceRow.PidText), nameof(ServiceRow.ProcessId), right: true);
        AddColumn("Description", 480, nameof(ServiceRow.Description), nameof(ServiceRow.Description));
        SortBy(name, toggle: false, ListSortDirection.Ascending);
        SelectionChanged();
    }

    private ServiceRow? One => Selected is [var r] ? r : null;

    protected override async Task<string> Load()
    {
        var list = await _session.ListAsync();
        Merge(list, s => s.Name.ToUpperInvariant(), r => r.Name.ToUpperInvariant(), s => new ServiceRow(s), (r, s) => r.Update(s));
        var running = Rows.Count(r => r.IsRunning);
        var autoStopped = Rows.Count(r => r.IsStopped && r.Startup == ServiceStartup.Automatic);
        return $"{Rows.Count} services  ·  {running} running" + (autoStopped > 0 ? $"  ·  {autoStopped} automatic but stopped" : "");
    }

    protected override bool Matches(ServiceRow r, string f) =>
        r.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
        r.StateText.Equals(f, StringComparison.OrdinalIgnoreCase) || r.Account.Contains(f, StringComparison.OrdinalIgnoreCase) || r.PidText == f;

    protected override void SelectionChanged()
    {
        var r = One;
        var ok = Session.IsConnected && r is not null;
        _start.IsEnabled = ok && !r!.IsRunning && r.Startup != ServiceStartup.Disabled;
        _stop.IsEnabled = ok && !r!.IsStopped && r.CanStop;
        _restart.IsEnabled = ok && r!.IsRunning && r.CanStop;
    }

    protected override void Activate(ServiceRow row) => ShowDetails(row);

    protected override ContextMenu? Menu(List<ServiceRow> sel)
    {
        if (sel is not [var r]) return null;
        var ok = Session.IsConnected;
        var m = new ContextMenu();
        m.Items.Add(Item("Start", SymbolRegular.Play24, () => _ = Control(r, ServiceAction.Start), ok && !r.IsRunning && r.Startup != ServiceStartup.Disabled));
        m.Items.Add(Item("Stop…", SymbolRegular.Stop24, () => _ = Control(r, ServiceAction.Stop), ok && !r.IsStopped && r.CanStop));
        m.Items.Add(Item("Restart…", SymbolRegular.ArrowRepeatAll24, () => _ = Control(r, ServiceAction.Restart), ok && r.IsRunning && r.CanStop));
        var startup = new System.Windows.Controls.MenuItem { Header = "Startup type", Icon = new SymbolIcon(SymbolRegular.Timer24), IsEnabled = ok };
        foreach (var s in Startups)
        {
            var mi = Item(RemoteServices.StartupName(s), SymbolRegular.Timer24, () => _ = SetStartup(r, s));
            mi.Icon = null;
            mi.IsCheckable = false;
            mi.IsChecked = r.Startup == s;
            startup.Items.Add(mi);
        }
        m.Items.Add(startup);
        m.Items.Add(new Separator());
        m.Items.Add(Item("Details…", SymbolRegular.Info24, () => ShowDetails(r), true, "Enter"));
        m.Items.Add(Item("Copy service name", SymbolRegular.Copy24, () => CopyText(r.Name)));
        m.Items.Add(Item("Copy display name", SymbolRegular.Copy24, () => CopyText(r.DisplayName)));
        m.Items.Add(Item("Copy path", SymbolRegular.Copy24, () => CopyText(r.Path), r.Path.Length > 0));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Refresh", SymbolRegular.ArrowClockwise24, () => _ = Refresh(manual: true), ok, "F5"));
        return m;
    }

    private void ShowDetails(ServiceRow r)
    {
        if (Owner is not { } owner) return;
        var text = $"Service name:  {r.Name}\nStatus:  {r.StateText}\nStartup type:  {r.StartupText}\nLog on as:  {r.Account}\n" +
                   (r.ProcessId > 0 ? $"PID:  {r.ProcessId}\n" : "") + $"Path:  {r.Path}\n\n{r.Description}";
        _ = Dialogs.Info(owner, r.DisplayName, text);
    }

    private async Task Control(ServiceRow r, ServiceAction action)
    {
        if (!Session.IsConnected || Owner is not { } owner) return;
        if (action != ServiceAction.Start)
        {
            var verb = action == ServiceAction.Stop ? "Stop" : "Restart";
            var text = $"{verb} \"{r.DisplayName}\" ({r.Name}) on {Where}?\n\nRunning services that depend on it are stopped as well" +
                       (action == ServiceAction.Restart ? " (only this service is started again)." : ".");
            if (!await Dialogs.Confirm(owner, $"{verb} service", text, verb)) return;
        }
        await Do($"{action.ToString().ToLowerInvariant()} service", () => _session.ControlAsync(r.Name, action));
    }

    private async Task SetStartup(ServiceRow r, ServiceStartup s)
    {
        if (!Session.IsConnected || Owner is not { } owner || r.Startup == s) return;
        if (s == ServiceStartup.Disabled &&
            !await Dialogs.Confirm(owner, "Disable service", $"Disable \"{r.DisplayName}\" ({r.Name}) on {Where}?\n\nIt can't be started any more – also not by Windows or programs that need it – until the startup type is changed back.", "Disable"))
            return;
        await Do("change startup type", () => _session.SetStartupAsync(r.Name, s));
    }
}
