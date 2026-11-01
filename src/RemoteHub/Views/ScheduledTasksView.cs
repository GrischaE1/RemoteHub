using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TreeView = System.Windows.Controls.TreeView;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace RemoteHub.Views;

/// <summary>One scheduled task in the list; updated in place on every refresh.</summary>
public sealed partial class TaskRow : ObservableObject
{
    public TaskRow(TaskInfo t)
    {
        Path = t.Path;
        Update(t);
    }

    public string Path { get; }
    public TaskInfo Info { get; private set; } = null!;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private TaskState _state;
    [ObservableProperty] private DateTime? _lastRunUtc;
    [ObservableProperty] private DateTime? _nextRunUtc;
    [ObservableProperty] private int _lastResult;
    [ObservableProperty] private string _runAs = "";
    [ObservableProperty] private string _author = "";

    public bool IsRunning => State == TaskState.Running;
    public bool IsDisabled => State == TaskState.Disabled;
    public bool IsProtected => RemoteTasks.IsProtected(Path);
    public string StateText => RemoteTasks.StateName(State);
    public string LastRunText => Local(LastRunUtc);
    public string NextRunText => Local(NextRunUtc);
    public string LastResultText => LastRunUtc is null && LastResult is 0 or 0x41303 ? "" : RemoteTasks.ResultText(LastResult);
    public Brush StateBrush => IsRunning ? SessionStateToBrush.GreenBrush : IsDisabled ? SessionStateToBrush.AmberBrush : SessionStateToBrush.GreyBrush;

    private static string Local(DateTime? utc) => utc is { } t ? t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";

    partial void OnStateChanged(TaskState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDisabled));
    }
    partial void OnLastRunUtcChanged(DateTime? value) { OnPropertyChanged(nameof(LastRunText)); OnPropertyChanged(nameof(LastResultText)); }
    partial void OnNextRunUtcChanged(DateTime? value) => OnPropertyChanged(nameof(NextRunText));
    partial void OnLastResultChanged(int value) => OnPropertyChanged(nameof(LastResultText));

    public void Update(TaskInfo t)
    {
        Info = t;
        Name = t.Name;
        Folder = t.Folder;
        State = t.State;
        LastRunUtc = t.LastRunUtc;
        NextRunUtc = t.NextRunUtc;
        LastResult = t.LastResult;
        RunAs = t.RunAs;
        Author = t.Author;
    }
}

/// <summary>A folder of the Task Scheduler Library in the tree.</summary>
public sealed partial class TaskFolderNode(string name, string path) : ObservableObject
{
    public string Name { get; } = name;
    /// <summary>"\" for the library, else "\Microsoft\Windows\" with the trailing backslash the tasks' folders have.</summary>
    public string Path { get; } = path;
    public ObservableCollection<TaskFolderNode> Children { get; } = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Open while expanded, as the registry tab's keys.</summary>
    public SymbolRegular Symbol => IsExpanded ? SymbolRegular.FolderOpen24 : SymbolRegular.Folder24;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Symbol));
}

/// <summary>
/// Scheduled tasks tab: the Task Scheduler Library's folders on the left, the selected folder's tasks on the right; run,
/// end, enable and disable. RemoteHub's own tasks (script runs) can't be ended or disabled here.
/// </summary>
public sealed class ScheduledTasksView : RemoteListView<TaskRow>
{
    private readonly ScheduledTasksSession _session;
    private readonly Wpf.Ui.Controls.Button _run, _end, _enable, _disable;
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
    private readonly ObservableCollection<TaskFolderNode> _roots = [];
    private List<string> _folders = [];
    private string _folder = "\\";

    private const string ProtectedTip = "RemoteHub's own task (a script run) – it can't be ended or disabled here";

    public ScheduledTasksView(ScheduledTasksSession session, Func<int> intervalSeconds, Action reconnect, Action close)
        : base(session, SymbolRegular.CalendarClock24, "Scheduled tasks", intervalSeconds, reconnect, close)
    {
        _session = session;
        List.SelectionMode = SelectionMode.Single;
        _run = RemoteToolChrome.Tool(SymbolRegular.Play24, "Run the task now", () => { if (One is { } r) _ = Act(r, TaskAction.Run); });
        _end = RemoteToolChrome.Tool(SymbolRegular.Stop24, "End the running task", () => { if (One is { } r) _ = Act(r, TaskAction.End); });
        _enable = RemoteToolChrome.Tool(SymbolRegular.CheckmarkCircle24, "Enable the task", () => { if (One is { } r) _ = Act(r, TaskAction.Enable); });
        _disable = RemoteToolChrome.Tool(SymbolRegular.DismissCircle24, "Disable the task", () => { if (One is { } r) _ = Act(r, TaskAction.Disable); });
        _run.Content = "Run";
        _end.Content = "End";
        _enable.Content = "Enable";
        _disable.Content = "Disable";
        foreach (var b in new[] { _run, _end, _enable, _disable })
        {
            ToolTipService.SetShowOnDisabled(b, true); // a protected task says why
            AddTool(b);
        }

        var name = AddColumn("Name", 260, "", nameof(TaskRow.Name), template: IconTextTemplate(nameof(TaskRow.Name), dotBrush: nameof(TaskRow.StateBrush)));
        AddColumn("Status", 90, nameof(TaskRow.StateText), nameof(TaskRow.State));
        AddColumn("Last run", 140, nameof(TaskRow.LastRunText), nameof(TaskRow.LastRunUtc));
        AddColumn("Next run", 140, nameof(TaskRow.NextRunText), nameof(TaskRow.NextRunUtc));
        AddColumn("Last result", 240, nameof(TaskRow.LastResultText), nameof(TaskRow.LastResult));
        AddColumn("Run as", 160, nameof(TaskRow.RunAs), nameof(TaskRow.RunAs));
        AddColumn("Author", 200, nameof(TaskRow.Author), nameof(TaskRow.Author));
        SortBy(name, toggle: false, ListSortDirection.Ascending);

        BuildTree();
        System.Windows.Automation.AutomationProperties.SetName(_tree, "Task folders");
        SidePane(_tree);
        SelectionChanged();
    }

    private TaskRow? One => Selected is [var r] ? r : null;

    // ================================================================== folders

    private void BuildTree()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(SymbolIcon));
        icon.SetBinding(SymbolIcon.SymbolProperty, new Binding(nameof(TaskFolderNode.Symbol)));
        icon.SetValue(SymbolIcon.FontSizeProperty, 15.0);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(TaskFolderNode.Name)));
        panel.AppendChild(icon);
        panel.AppendChild(text);
        _tree.ItemTemplate = new HierarchicalDataTemplate(typeof(TaskFolderNode)) { ItemsSource = new Binding(nameof(TaskFolderNode.Children)), VisualTree = panel };

        var style = new Style(typeof(TreeViewItem), (Style?)Application.Current?.TryFindResource(typeof(TreeViewItem)));
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(TaskFolderNode.IsExpanded)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(TaskFolderNode.IsSelected)) { Mode = BindingMode.TwoWay }));
        _tree.ItemContainerStyle = style;
        _tree.ItemsSource = _roots;
        _tree.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is not TaskFolderNode n || n.Path == _folder) return;
            _folder = n.Path;
            ScopeChanged();
            SetSummary(Summary());
        };
    }

    /// <summary>The folder tree from the listing, rebuilt only when the folders changed (expansion and selection stay).</summary>
    private void ShowFolders(List<string> folders)
    {
        var sorted = folders.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (sorted.SequenceEqual(_folders, StringComparer.OrdinalIgnoreCase)) return;
        var expanded = new HashSet<string>(All(_roots).Where(n => n.IsExpanded).Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
        _folders = sorted;
        _roots.Clear();
        var root = new TaskFolderNode("Task Scheduler Library", "\\") { IsExpanded = true };
        var byPath = new Dictionary<string, TaskFolderNode>(StringComparer.OrdinalIgnoreCase) { ["\\"] = root };
        foreach (var path in sorted.Where(p => p != "\\"))
        {
            var parentPath = path[..(path.TrimEnd('\\').LastIndexOf('\\') + 1)];
            var node = new TaskFolderNode(path.TrimEnd('\\')[(path.TrimEnd('\\').LastIndexOf('\\') + 1)..], path) { IsExpanded = expanded.Contains(path) };
            (byPath.TryGetValue(parentPath, out var parent) ? parent : root).Children.Add(node);
            byPath[path] = node;
        }
        _roots.Add(root);
        if (!byPath.ContainsKey(_folder)) { _folder = "\\"; ScopeChanged(); } // the open folder is gone: the library's tasks
        // Keep showing the folder that was open: select it and open the folders above it.
        var selected = byPath[_folder];
        for (var p = _folder; p != "\\"; p = p[..(p.TrimEnd('\\').LastIndexOf('\\') + 1)])
            if (byPath.TryGetValue(p, out var up) && up != selected) up.IsExpanded = true;
        selected.IsSelected = true;
    }

    private static IEnumerable<TaskFolderNode> All(IEnumerable<TaskFolderNode> nodes) => nodes.SelectMany(n => All(n.Children).Prepend(n));

    protected override bool InScope(TaskRow r) => string.Equals(r.Folder, _folder, StringComparison.OrdinalIgnoreCase);

    // ================================================================== list

    protected override async Task<string> Load()
    {
        var listing = await _session.ListAsync();
        Merge(listing.Tasks, t => t.Path.ToUpperInvariant(), r => r.Path.ToUpperInvariant(), t => new TaskRow(t), (r, t) => r.Update(t));
        // New rows are filtered as they are added; the view is only refreshed when the folder changes (selection and
        // scroll position stay, as in the other lists).
        ShowFolders(listing.Folders);
        return Summary();
    }

    private string Summary()
    {
        var running = Rows.Count(r => r.IsRunning);
        var disabled = Rows.Count(r => r.IsDisabled);
        var here = Rows.Count(InScope);
        return $"{Rows.Count} tasks  ·  {running} running  ·  {disabled} disabled  ·  {here} in {(_folder == "\\" ? "the library" : _folder.TrimEnd('\\'))}";
    }

    protected override bool Matches(TaskRow r, string f) =>
        r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(f, StringComparison.OrdinalIgnoreCase) ||
        r.StateText.Equals(f, StringComparison.OrdinalIgnoreCase) || r.RunAs.Contains(f, StringComparison.OrdinalIgnoreCase) ||
        r.Author.Contains(f, StringComparison.OrdinalIgnoreCase);

    protected override void SelectionChanged()
    {
        var r = One;
        var ok = Session.IsConnected && r is not null;
        _run.IsEnabled = ok && !r!.IsDisabled && !r.IsRunning;
        _end.IsEnabled = ok && r!.IsRunning && !r.IsProtected;
        _enable.IsEnabled = ok && r!.IsDisabled;
        _disable.IsEnabled = ok && !r!.IsDisabled && !r.IsProtected;
        _end.ToolTip = r?.IsProtected == true ? ProtectedTip : "End the running task";
        _disable.ToolTip = r?.IsProtected == true ? ProtectedTip : "Disable the task";
    }

    protected override void Activate(TaskRow row) => _ = ShowDetails(row);

    protected override ContextMenu? Menu(List<TaskRow> sel)
    {
        if (sel is not [var r]) return null;
        var ok = Session.IsConnected;
        var m = new ContextMenu();
        m.Items.Add(Item("Run", SymbolRegular.Play24, () => _ = Act(r, TaskAction.Run), ok && !r.IsDisabled && !r.IsRunning));
        m.Items.Add(Item("End…", SymbolRegular.Stop24, () => _ = Act(r, TaskAction.End), ok && r.IsRunning && !r.IsProtected));
        m.Items.Add(Item("Enable", SymbolRegular.CheckmarkCircle24, () => _ = Act(r, TaskAction.Enable), ok && r.IsDisabled));
        m.Items.Add(Item("Disable…", SymbolRegular.DismissCircle24, () => _ = Act(r, TaskAction.Disable), ok && !r.IsDisabled && !r.IsProtected));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Details…", SymbolRegular.Info24, () => _ = ShowDetails(r), ok, "Enter"));
        m.Items.Add(Item("Copy name", SymbolRegular.Copy24, () => CopyText(r.Name)));
        m.Items.Add(Item("Copy path", SymbolRegular.Copy24, () => CopyText(r.Path)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Refresh", SymbolRegular.ArrowClockwise24, () => _ = Refresh(manual: true), ok, "F5"));
        return m;
    }

    private async Task ShowDetails(TaskRow r)
    {
        if (!Session.IsConnected || Owner is not { } owner) return;
        string xml;
        Busy(true);
        try { xml = await _session.XmlAsync(r.Info); }
        catch (Exception ex) { SetError("Reading the task failed: " + ex.Message); return; }
        finally { Busy(false); }
        _ = Dialogs.Info(owner, r.Name, RemoteTasks.Describe(r.Info, xml));
    }

    private async Task Act(TaskRow r, TaskAction action)
    {
        if (!Session.IsConnected || Owner is not { } owner) return;
        if (action is TaskAction.End or TaskAction.Disable)
        {
            if (r.IsProtected) { SetError(ProtectedTip + "."); return; }
            var verb = action == TaskAction.End ? "End" : "Disable";
            var what = action == TaskAction.End
                ? "The running instance is stopped; what it was doing is left unfinished."
                : "It won't start on its triggers any more until it is enabled again.";
            var windows = RemoteTasks.IsWindows(r.Path) ? "\n\nThis is one of Windows' own tasks." : "";
            var folder = r.Folder == "\\" ? "Task Scheduler Library" : r.Folder;
            if (!await Dialogs.Confirm(owner, $"{verb} task", $"{verb} \"{r.Name}\" ({folder}) on {Where}?\n\n{what}{windows}", verb)) return;
        }
        var ok = await Do($"{action.ToString().ToLowerInvariant()} task", () => _session.DoAsync(r.Info, action));
        Data.AuditLog.Write(Data.AuditEvents.TaskAction, Session.Connection, $"{action} {r.Path}" + (ok ? "" : " – failed"));
    }
}
