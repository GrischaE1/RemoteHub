using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;

namespace RemoteHub.Views;

/// <summary>One remote process in the list; updated in place on every refresh.</summary>
public sealed partial class ProcessRow : ObservableObject
{
    public ProcessRow(ProcessSample s, int ownPid)
    {
        Id = s.Id;
        StartedUtc = s.StartedUtc;
        Name = s.Name;
        IsCritical = RemoteProcesses.IsCritical(s.Id, s.Name);
        IsOwn = s.Id == ownPid;
        Update(s, null);
    }

    public int Id { get; }
    public DateTime? StartedUtc { get; }
    public string Name { get; }
    /// <summary>Windows can't run without it – never ended from here.</summary>
    public bool IsCritical { get; }
    /// <summary>The remoting host of this tab (ending it would close the tab's connection).</summary>
    public bool IsOwn { get; }
    public (int, DateTime?) Key => (Id, StartedUtc);
    public long CpuTicks { get; private set; }

    /// <summary>CPU use since the previous refresh in % of all CPUs; -1 until there are two samples.</summary>
    [ObservableProperty] private double _cpu = -1;
    [ObservableProperty] private long _workingSet;
    [ObservableProperty] private long _privateBytes;
    [ObservableProperty] private int _threads;
    [ObservableProperty] private int _handles;
    [ObservableProperty] private int _sessionId;
    [ObservableProperty] private string _user = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _commandLine = "";

    public string CpuText => Cpu < 0 ? "" : Cpu < 0.05 ? "0" : Cpu.ToString("0.0");
    public string WorkingSetText => TransferItem.Size(WorkingSet);
    public string PrivateText => TransferItem.Size(PrivateBytes);
    public string StartedText => StartedUtc?.ToLocalTime().ToString("g") ?? "";
    public string IdText => Id.ToString();
    public string Symbol => IsCritical ? "ShieldLock24" : IsOwn ? "PlugConnected24" : "Apps24";
    public string Tooltip => CommandLine.Length > 0 ? CommandLine : Path.Length > 0 ? Path : Name;

    partial void OnCpuChanged(double value) => OnPropertyChanged(nameof(CpuText));
    partial void OnWorkingSetChanged(long value) => OnPropertyChanged(nameof(WorkingSetText));
    partial void OnPrivateBytesChanged(long value) => OnPropertyChanged(nameof(PrivateText));
    partial void OnCommandLineChanged(string value) => OnPropertyChanged(nameof(Tooltip));

    /// <summary>New values; <paramref name="previous"/> (the last snapshot) turns CPU time into a percentage.</summary>
    public void Update(ProcessSample s, (long Taken, long Now, int Cpus)? previous)
    {
        if (previous is { } p) Cpu = RemoteProcesses.CpuPercent(CpuTicks, s.CpuTicks, p.Taken, p.Now, p.Cpus);
        CpuTicks = s.CpuTicks;
        WorkingSet = s.WorkingSet;
        PrivateBytes = s.PrivateBytes;
        Threads = s.Threads;
        Handles = s.Handles;
        SessionId = s.SessionId;
        if (s.User.Length > 0) User = s.User;
        if (s.Path.Length > 0) Path = s.Path;
        if (s.CommandLine.Length > 0) CommandLine = s.CommandLine;
    }
}

/// <summary>Processes tab: the remote computer's processes, refreshed regularly while visible; end process / process tree.</summary>
public sealed class ProcessesView : RemoteListView<ProcessRow>
{
    private readonly ProcessesSession _session;
    private readonly Wpf.Ui.Controls.Button _end;
    private long _lastTaken;

    public ProcessesView(ProcessesSession session, Func<int> intervalSeconds, Action reconnect, Action close)
        : base(session, SymbolRegular.Pulse24, "Processes", intervalSeconds, reconnect, close)
    {
        _session = session;
        List.SelectionMode = SelectionMode.Extended;
        _end = RemoteToolChrome.Tool(SymbolRegular.DismissCircle24, "End process (Del)", () => _ = End(Selected, tree: false));
        _end.Content = "End process";
        AddTool(_end);

        var name = AddColumn("Name", 230, "", nameof(ProcessRow.Name), template: IconTextTemplate(nameof(ProcessRow.Name), nameof(ProcessRow.Symbol)));
        AddColumn("PID", 70, nameof(ProcessRow.IdText), nameof(ProcessRow.Id), right: true);
        AddColumn("CPU %", 70, nameof(ProcessRow.CpuText), nameof(ProcessRow.Cpu), right: true);
        AddColumn("Working set", 100, nameof(ProcessRow.WorkingSetText), nameof(ProcessRow.WorkingSet), right: true);
        AddColumn("Private bytes", 100, nameof(ProcessRow.PrivateText), nameof(ProcessRow.PrivateBytes), right: true);
        AddColumn("User", 170, nameof(ProcessRow.User), nameof(ProcessRow.User));
        AddColumn("Session", 64, nameof(ProcessRow.SessionId), nameof(ProcessRow.SessionId), right: true);
        AddColumn("Threads", 64, nameof(ProcessRow.Threads), nameof(ProcessRow.Threads), right: true);
        AddColumn("Handles", 70, nameof(ProcessRow.Handles), nameof(ProcessRow.Handles), right: true);
        AddColumn("Started", 130, nameof(ProcessRow.StartedText), nameof(ProcessRow.StartedUtc));
        AddColumn("Command line", 520, nameof(ProcessRow.CommandLine), nameof(ProcessRow.CommandLine));
        SortBy(name, toggle: false, ListSortDirection.Ascending);
        SelectionChanged();
    }

    protected override async Task<string> Load()
    {
        var snap = await _session.ListAsync();
        (long, long, int)? previous = _lastTaken > 0 ? (_lastTaken, snap.TakenTicks, snap.Cpus) : null;
        var first = _lastTaken == 0;
        _lastTaken = snap.TakenTicks;
        Merge(snap.Processes, p => (p.Id, p.StartedUtc), r => r.Key, p => new ProcessRow(p, snap.OwnPid), (r, p) => r.Update(p, previous));
        // CPU % needs two samples: take the second one right away instead of after the full interval.
        if (first) RefreshSoon(TimeSpan.FromSeconds(2));

        var cpu = Rows.Where(r => r.Id != 0 && r.Cpu > 0).Sum(r => r.Cpu);
        var used = snap.TotalMemory - snap.FreeMemory;
        var mem = snap.TotalMemory > 0 ? $"  ·  memory {TransferItem.Size(used)} of {TransferItem.Size(snap.TotalMemory)} ({used * 100 / snap.TotalMemory}%)" : "";
        var users = Rows.Any(r => r.User.Length > 0) ? "" : "  ·  user names need an administrator account";
        return $"{Rows.Count} processes  ·  {snap.Cpus} logical CPUs" + (first ? "" : $"  ·  CPU {Math.Min(100, cpu):0}%") + mem + users;
    }

    protected override bool Matches(ProcessRow r, string f) =>
        r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.IdText == f || r.User.Contains(f, StringComparison.OrdinalIgnoreCase) ||
        r.CommandLine.Contains(f, StringComparison.OrdinalIgnoreCase);

    protected override void SelectionChanged()
    {
        var sel = Selected;
        _end.IsEnabled = Session.IsConnected && sel.Count > 0 && sel.All(r => !r.IsCritical && !r.IsOwn);
    }

    protected override void OnKey(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Selected is { Count: > 0 } sel) { _ = End(sel, tree: Keyboard.Modifiers == ModifierKeys.Shift); e.Handled = true; }
    }

    protected override ContextMenu? Menu(List<ProcessRow> sel)
    {
        if (sel.Count == 0) return null;
        var ok = Session.IsConnected && sel.All(r => !r.IsCritical && !r.IsOwn);
        var m = new ContextMenu();
        m.Items.Add(Item(sel.Count > 1 ? $"End {sel.Count} processes…" : "End process…", SymbolRegular.DismissCircle24, () => _ = End(sel, tree: false), ok, "Del"));
        m.Items.Add(Item("End process tree…", SymbolRegular.BranchFork24, () => _ = End(sel, tree: true), ok, "Shift+Del"));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Copy name", SymbolRegular.Copy24, () => CopyText(Join(sel.Select(r => r.Name)))));
        m.Items.Add(Item("Copy PID", SymbolRegular.Copy24, () => CopyText(Join(sel.Select(r => r.IdText)))));
        m.Items.Add(Item("Copy path", SymbolRegular.Copy24, () => CopyText(Join(sel.Select(r => r.Path).Where(p => p.Length > 0))), sel.Any(r => r.Path.Length > 0)));
        m.Items.Add(Item("Copy command line", SymbolRegular.Copy24, () => CopyText(Join(sel.Select(r => r.CommandLine).Where(p => p.Length > 0))), sel.Any(r => r.CommandLine.Length > 0)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Refresh", SymbolRegular.ArrowClockwise24, () => _ = Refresh(manual: true), Session.IsConnected, "F5"));
        return m;
    }

    private static string Join(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    private async Task End(List<ProcessRow> sel, bool tree)
    {
        if (sel.Count == 0 || Owner is not { } owner || !Session.IsConnected) return;
        if (sel.FirstOrDefault(r => r.IsCritical) is { } c) { SetError($"{c.Name} is a critical Windows process and can't be ended from here."); return; }
        if (sel.Any(r => r.IsOwn)) { SetError("That's the PowerShell remoting process of this tab – close the tab instead."); return; }
        var list = string.Join("\n", sel.Take(8).Select(r => $"{r.Name}  (PID {r.Id}{(r.User.Length > 0 ? ", " + r.User : "")})")) + (sel.Count > 8 ? $"\n… and {sel.Count - 8} more" : "");
        var what = tree ? (sel.Count == 1 ? "this process and every process it started" : $"these {sel.Count} processes and every process they started")
                        : sel.Count == 1 ? "this process" : $"these {sel.Count} processes";
        var text = $"End {what} on {Where}?\n\n{list}\n\nUnsaved data in them is lost. Ending system processes can make Windows unstable.";
        if (!await Dialogs.Confirm(owner, tree ? "End process tree" : "End process", text, "End process")) return;
        await Do($"end {sel.Count} process(es){(tree ? " (tree)" : "")}", () => _session.StopAsync(sel.Select(r => (r.Id, r.Name)).ToList(), tree));
    }
}
