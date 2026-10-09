using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Models;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using RemoteHub.Services;

namespace RemoteHub.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _saveTimer;

    public MainViewModel(AppData data)
    {
        Data = data;
        RefreshTags();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveNow();
        Sessions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSessions));
        // "today 15:42" becomes "yesterday 15:42": the navigation's last-connection labels are refreshed now and then.
        var labels = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        labels.Tick += (_, _) =>
        {
            foreach (var c in Items.SelectMany(n => n is Folder f ? f.Descendants() : [n]).OfType<Connection>()) c.RefreshLabels();
        };
        labels.Start();
        if (data.Ui.UsageTrackedSinceUtc is null)
        {
            data.Ui.UsageTrackedSinceUtc = DateTime.UtcNow;
            _saveTimer.Start();
        }
        Transfers.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (TransferItem t in e.NewItems) t.PropertyChanged += (_, _) => RefreshTransferSummary();
            RefreshTransferSummary();
        };
    }

    public AppData Data { get; private set; }
    public ObservableCollection<NodeBase> Items => Data.Items;
    public ObservableCollection<SessionBase> Sessions { get; } = new();
    /// <summary>Open tabs grouped by connection (top tab row); each group's tabs form the second row.</summary>
    public ObservableCollection<SessionGroup> Groups { get; } = new();
    public ObservableCollection<SearchHit> SearchResults { get; } = new();
    /// <summary>File transfers of this app run (newest first).</summary>
    public ObservableCollection<TransferItem> Transfers { get; } = new();

    [ObservableProperty] private SessionBase? _selectedSession;
    [ObservableProperty] private SessionGroup? _selectedGroup;
    [ObservableProperty] private bool _showTransfers;
    [ObservableProperty] private string _transferSummary = "";
    [ObservableProperty] private NodeBase? _selectedNode;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string? _saveError;
    [ObservableProperty] private string _storageInfo = SettingsStore.Describe();

    /// <summary>Raised when saving found that the file was changed on another device.</summary>
    public event Action? Conflict;

    public void RefreshStorageInfo() => StorageInfo = SettingsStore.Describe();
    public bool HasPendingSave => _saveTimer.IsEnabled;

    public bool HasSessions => Sessions.Count > 0;
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText) || IsFiltering;
    public bool IsFiltering => UnusedDays != 0;

    /// <summary>The "not used for" filter: days, 0 = off (all connections), <see cref="NeverConnected"/> = never connected.</summary>
    public const int NeverConnected = -1;
    [ObservableProperty] private int _unusedDays;
    /// <summary>"12 connections not used for 90 days", shown above the filtered list.</summary>
    [ObservableProperty] private string _unusedSummary = "";
    public string EmptySearchText => IsFiltering ? "No connection fits the filter." : "Nothing found. Press Enter to quick-connect to this computer name or IP address.";

    partial void OnUnusedDaysChanged(int value)
    {
        OnPropertyChanged(nameof(EmptySearchText));
        OnPropertyChanged(nameof(IsFiltering));
        OnSearchTextChanged(SearchText);
    }

    public static string DaysText(int days) => days switch { 30 => "30 days", 90 => "90 days", 180 => "6 months", 365 => "a year", _ => $"{days} days" };

    /// <summary>"not used for 90 days" / "never connected" – for the filter's texts.</summary>
    public static string FilterText(int days) => days == NeverConnected ? "never connected" : "not used for " + DaysText(days);
    public int ConnectionCount => AllNodes().OfType<Connection>().Count();

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        SearchResults.Clear();
        UnusedSummary = "";
        if (!IsSearching) return;
        if (UnusedDays == NeverConnected)
        {
            var since = Data.Ui.UsageTrackedSinceUtc ?? DateTime.UtcNow;
            foreach (var hit in NodeSearch.NeverConnected(AllNodes(), since, value)) SearchResults.Add(hit);
            UnusedSummary = (SearchResults.Count == 1 ? "1 connection" : $"{SearchResults.Count} connections") +
                $" never connected since use is recorded ({since.ToLocalTime():d MMM yyyy})";
            return;
        }
        if (UnusedDays > 0)
        {
            var since = Data.Ui.UsageTrackedSinceUtc ?? DateTime.UtcNow;
            foreach (var hit in NodeSearch.Unused(AllNodes(), UnusedDays, since, DateTime.UtcNow, value)) SearchResults.Add(hit);
            UnusedSummary = (SearchResults.Count == 1 ? "1 connection" : $"{SearchResults.Count} connections") + $" not used for {DaysText(UnusedDays)}" +
                (since > DateTime.UtcNow.AddDays(-UnusedDays) ? $" – use is recorded since {since.ToLocalTime():d MMM yyyy}, so connections without a recorded use show up from {since.AddDays(UnusedDays).ToLocalTime():d MMM yyyy}" : "");
            return;
        }
        foreach (var hit in NodeSearch.Find(AllNodes(), value)) SearchResults.Add(hit);
    }

    partial void OnSelectedSessionChanged(SessionBase? oldValue, SessionBase? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        var g = newValue is null ? null : GroupOf(newValue);
        if (g is not null) g.LastSelected = newValue;
        SelectedGroup = g;
        OnPropertyChanged(nameof(TabSelection));
        OnPropertyChanged(nameof(SelectedIsRdp));
        OnPropertyChanged(nameof(SelectedIsPowerShell));
    }

    partial void OnSelectedGroupChanged(SessionGroup? oldValue, SessionGroup? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            if (SelectedSession is null || GroupOf(SelectedSession) != newValue)
                SelectedSession = newValue.LastSelected is { } last && newValue.Sessions.Contains(last) ? last : newValue.Sessions.FirstOrDefault();
        }
        OnPropertyChanged(nameof(GroupSelection));
    }

    // The tab rows bind to these: a list box clears its selection while its items change, which must not close the view.
    public SessionBase? TabSelection { get => SelectedSession; set { if (value is not null) SelectedSession = value; } }
    public SessionGroup? GroupSelection { get => SelectedGroup; set { if (value is not null) SelectedGroup = value; } }

    public SessionGroup? GroupOf(SessionBase s) => Groups.FirstOrDefault(g => g.Connection == s.Connection);

    public bool SelectedIsRdp => SelectedSession is RdpSession;
    public bool SelectedIsPowerShell => SelectedSession is PsSession;

    public bool HasTransfers => Transfers.Count > 0;
    public int ActiveTransfers => Transfers.Count(t => t.IsActive);

    /// <summary>Overall speed: "11.2 MB/s now · avg 9.8 MB/s" while running, "Average 9.8 MB/s" afterwards.</summary>
    [ObservableProperty] private string _transferSpeed = "";

    private System.Windows.Threading.DispatcherTimer? _speedTimer;
    private long _speedBytes;
    private DateTime _speedAt;
    private double _speedNow;

    private void RefreshTransferSummary()
    {
        var active = Transfers.Where(t => t.IsActive).ToList();
        var failed = Transfers.Count(t => t.IsProblem);
        TransferSummary = active.Count > 0
            ? $"{active.Count} running · {TransferItem.Size(active.Sum(t => t.SentBytes))} of {TransferItem.Size(active.Sum(t => t.TotalBytes))}"
            : $"{Transfers.Count(t => t.IsDone)} done" + (failed > 0 ? $" · {failed} failed" : "");
        RefreshTransferSpeed(active.Count > 0);
        OnPropertyChanged(nameof(HasTransfers));
        OnPropertyChanged(nameof(ActiveTransfers));
    }

    /// <summary>
    /// Current speed from the total bytes moved per second (smoothed, so batched small files count too) and the average
    /// over the time something was actually moving – idle gaps between transfers don't lower it.
    /// </summary>
    private void RefreshTransferSpeed(bool running)
    {
        var now = DateTime.UtcNow;
        var total = Transfers.Sum(t => t.SentBytes);
        if (total < _speedBytes || _speedAt == default) { _speedBytes = total; _speedAt = now; }
        else if ((now - _speedAt).TotalSeconds >= 0.5)
        {
            var current = (total - _speedBytes) / (now - _speedAt).TotalSeconds;
            _speedNow = _speedNow == 0 ? current : _speedNow * 0.6 + current * 0.4;
            _speedBytes = total;
            _speedAt = now;
        }
        if (!running) _speedNow = 0;

        var average = AverageTransferRate(Transfers, now);
        TransferSpeed = running
            ? (_speedNow > 0 ? $"{TransferItem.Size((long)_speedNow)}/s now" : "Starting…") + (average > 0 ? $" · avg {TransferItem.Size((long)average)}/s" : "")
            : average > 0 ? $"Average {TransferItem.Size((long)average)}/s" : "";

        // While running, refresh once a second so "now" also drops when the transfer stalls.
        if (running && _speedTimer is null)
        {
            _speedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _speedTimer.Tick += (_, _) => RefreshTransferSummary();
            _speedTimer.Start();
        }
        else if (!running && _speedTimer is not null) { _speedTimer.Stop(); _speedTimer = null; }
    }

    /// <summary>Bytes moved divided by the union of the items' active time spans.</summary>
    internal static double AverageTransferRate(IEnumerable<TransferItem> items, DateTime now)
    {
        var spans = items.Where(t => t.StartedUtc is not null && t.SentBytes > 0)
            .Select(t => (Start: t.StartedUtc!.Value, End: t.FinishedUtc ?? now, Bytes: t.SentBytes)).OrderBy(s => s.Start).ToList();
        if (spans.Count == 0) return 0;
        double busy = 0;
        DateTime from = spans[0].Start, to = spans[0].End;
        foreach (var s in spans.Skip(1))
        {
            if (s.Start > to) { busy += (to - from).TotalSeconds; from = s.Start; to = s.End; }
            else if (s.End > to) to = s.End;
        }
        busy += (to - from).TotalSeconds;
        return busy > 0.05 ? spans.Sum(s => s.Bytes) / busy : 0;
    }

    /// <summary>Cancels every running and waiting transfer (finished ones stay; partial files are removed by the transfer).</summary>
    public void CancelAllTransfers()
    {
        foreach (var t in Transfers.Where(t => t.IsActive).ToList()) t.Cancel();
    }

    public void ClearFinishedTransfers()
    {
        foreach (var t in Transfers.Where(t => !t.IsActive).ToList()) Transfers.Remove(t);
        if (Transfers.Count == 0) ShowTransfers = false;
    }

    public IEnumerable<NodeBase> AllNodes()
    {
        foreach (var n in Items)
        {
            yield return n;
            if (n is Folder f) foreach (var d in f.Descendants()) yield return d;
        }
    }

    // ---------- persistence ----------

    /// <summary>Recomputes the inherited colour tag and the agent icon of every tree item.</summary>
    public void RefreshTags()
    {
        foreach (var n in AllNodes())
        {
            var options = Inheritance.ResolveOptions(n, Data);
            n.TagBrush = Views.ColorTags.Brush(options.ColorTag);
            if (n is Connection c)
                c.AgentMode = c.Agent is not { Status: AgentStatus.Installed } ? AgentMode.None
                            : options.UseAgent == true ? AgentMode.Active : AgentMode.Available;
        }
    }

    public void RequestSave()
    {
        RefreshTags();
        _saveTimer.Stop();
        _saveTimer.Start();
        OnPropertyChanged(nameof(ConnectionCount));
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            SettingsStore.Save(Data);
            SaveError = null;
        }
        catch (SettingsConflictException)
        {
            Conflict?.Invoke();
        }
        catch (Exception ex)
        {
            SaveError = "Settings could not be saved: " + ex.Message;
        }
    }

    public void ReplaceData(AppData data, bool save = true)
    {
        _saveTimer.Stop();
        Data = data;
        OnPropertyChanged(nameof(Data));
        OnPropertyChanged(nameof(Items));
        RefreshTags();
        if (save) SaveNow();
        OnPropertyChanged(nameof(ConnectionCount));
        RefreshStorageInfo();
    }

    /// <summary>Writes the current data even though the file changed elsewhere ("keep mine").</summary>
    public void ForceSave()
    {
        try
        {
            SettingsStore.Save(Data, force: true);
            SaveError = null;
        }
        catch (Exception ex) { SaveError = "Settings could not be saved: " + ex.Message; }
    }

    // ---------- tree editing ----------

    public ObservableCollection<NodeBase> ChildrenOf(Folder? f) => f?.Children ?? Items;

    /// <summary>Folder that new items should go into, based on the current selection.</summary>
    public Folder? TargetFolderForNew() => SelectedNode switch
    {
        Folder f => f,
        Connection c => c.Parent,
        _ => null,
    };

    public void Add(NodeBase node, Folder? parent, int index = -1)
    {
        node.Parent = parent;
        var list = ChildrenOf(parent);
        if (index < 0 || index > list.Count) list.Add(node); else list.Insert(index, node);
        if (parent is not null) parent.IsExpanded = true;
        RequestSave();
    }

    public void Remove(NodeBase node)
    {
        ChildrenOf(node.Parent).Remove(node);
        RequestSave();
    }

    public static bool CanMove(NodeBase node, Folder? target) =>
        target is null || (node != target && !(node is Folder f && target.Ancestors().Contains(f)));

    /// <summary>
    /// What changes for the credentials when <paramref name="node"/> moves into <paramref name="target"/> (null = top level):
    /// (old source, new source), or null when they stay the same (e.g. reordering within a folder).
    /// </summary>
    public (string From, string To)? CredentialChange(NodeBase node, Folder? target)
    {
        if (node.Parent == target || !node.InheritCredentials) return null;
        var (_, from) = Inheritance.ResolveCredentials(node, Data);
        var (_, to) = Inheritance.ResolveCredentialsIn(node, target, Data);
        return from == to ? null : (from, to);
    }

    /// <summary>Moves a node into <paramref name="target"/> at <paramref name="index"/> (−1 = end).</summary>
    public bool Move(NodeBase node, Folder? target, int index = -1)
    {
        if (!CanMove(node, target)) return false;
        var from = ChildrenOf(node.Parent);
        var to = ChildrenOf(target);
        var oldIndex = from.IndexOf(node);
        if (from == to && oldIndex >= 0 && index > oldIndex) index--;
        from.Remove(node);
        node.Parent = target;
        if (index < 0 || index > to.Count) to.Add(node); else to.Insert(index, node);
        if (target is not null) target.IsExpanded = true;
        RequestSave();
        return true;
    }

    public NodeBase Duplicate(NodeBase node)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<NodeBase>(node, SettingsStore.Json);
        var copy = System.Text.Json.JsonSerializer.Deserialize<NodeBase>(json, SettingsStore.Json)!;
        // A copy gets new IDs and no agent identity (a pinned agent certificate belongs to exactly one device).
        void NewIds(NodeBase n) { n.Id = Guid.NewGuid(); if (n is Connection k) k.Agent = null; if (n is Folder f) foreach (var c in f.Children) NewIds(c); }
        NewIds(copy);
        copy.Name += " (copy)";
        SettingsStore.RelinkParents([copy], node.Parent);
        var list = ChildrenOf(node.Parent);
        list.Insert(list.IndexOf(node) + 1, copy);
        RequestSave();
        return copy;
    }

    // ---------- sessions ----------

    public RdpSession? FindSession(Connection c) => Sessions.OfType<RdpSession>().FirstOrDefault(s => s.Connection == c);

    public AgentDesktopSession? FindAgentDesktop(Connection c) => Sessions.OfType<AgentDesktopSession>().FirstOrDefault(s => s.Connection == c);

    public AgentDesktopSession OpenAgentDesktop(Connection c)
    {
        var s = new AgentDesktopSession(c);
        Register(s);
        return s;
    }

    public RdpSession OpenSession(Connection c)
    {
        var s = new RdpSession(c);
        Register(s);
        return s;
    }

    public FileBrowserSession OpenFileBrowser(Connection c, string? remoteStart)
    {
        var s = new FileBrowserSession(c, remoteStart);
        Register(s);
        return s;
    }

    public RegistrySession OpenRegistry(Connection c, string? startPath)
    {
        var s = new RegistrySession(c, startPath);
        Register(s);
        return s;
    }

    public ProcessesSession OpenProcesses(Connection c)
    {
        var s = new ProcessesSession(c);
        Register(s);
        return s;
    }

    public ServicesSession OpenServices(Connection c)
    {
        var s = new ServicesSession(c);
        Register(s);
        return s;
    }

    public PsSession OpenPsSession(Connection c)
    {
        var s = new PsSession(c);
        Register(s);
        return s;
    }

    private void Register(SessionBase s)
    {
        var c = s.Connection;
        s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SessionBase.State)) return;
            UpdateLiveState(c);
            if (s.State == SessionState.Connected)
            {
                c.LastConnectedUtc = DateTime.UtcNow;
                RequestSave();
            }
        };
        s.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionBase.AccentBrush)) GroupOf(s)?.RefreshAccent(); };
        s.Closed += () =>
        {
            var group = GroupOf(s);
            var gi = group is null ? -1 : Groups.IndexOf(group);
            var si = group?.Sessions.IndexOf(s) ?? -1;
            Sessions.Remove(s);
            group?.Sessions.Remove(s);
            if (group is { Sessions.Count: 0 }) Groups.Remove(group);
            c.OpenSessions = Math.Max(0, c.OpenSessions - 1);
            UpdateLiveState(c);
            if (SelectedSession != s && SelectedSession is not null) return;
            // Next: a neighbouring tab of the same connection, else the neighbouring connection's last tab.
            if (group is { Sessions.Count: > 0 }) SelectedSession = group.Sessions[Math.Clamp(si - 1, 0, group.Sessions.Count - 1)];
            else if (Groups.Count > 0)
            {
                var next = Groups[Math.Clamp(gi - 1, 0, Groups.Count - 1)];
                SelectedSession = next.LastSelected is { } last && next.Sessions.Contains(last) ? last : next.Sessions[0];
            }
            else SelectedSession = null;
        };
        Sessions.Add(s);
        var g = GroupOf(s);
        if (g is null) Groups.Add(g = new SessionGroup(c));
        g.Sessions.Add(s);
        c.OpenSessions++;
        UpdateLiveState(c);
        SelectedSession = s;
    }

    /// <summary>Connected beats connecting beats failed beats disconnected/idle.</summary>
    private void UpdateLiveState(Connection c)
    {
        static int Rank(SessionState st) => st switch
        {
            SessionState.Connected => 4, SessionState.Connecting => 3, SessionState.Failed => 2, _ => 1,
        };
        var states = Sessions.Where(x => x.Connection == c).Select(x => x.State).ToList();
        c.LiveState = states.Count == 0 ? null : states.OrderByDescending(Rank).First();
    }

    public void StartSession(RdpSession s)
    {
        var c = s.Connection;
        var options = Inheritance.ResolveOptions(c, Data);
        var (cred, source) = Inheritance.ResolveCredentials(c, Data);
        s.Connect(options, cred.Clone(), source);
    }

    public void SelectNextSession(int delta)
    {
        // In the order shown: connection by connection, tab by tab.
        var all = Groups.SelectMany(g => g.Sessions).ToList();
        if (all.Count == 0) return;
        var i = SelectedSession is null ? 0 : all.IndexOf(SelectedSession);
        SelectedSession = all[((i + delta) % all.Count + all.Count) % all.Count];
    }
}
