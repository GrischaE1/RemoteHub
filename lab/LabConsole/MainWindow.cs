using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteHub.Views;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using TreeView = System.Windows.Controls.TreeView;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace RemoteHub.Lab;

/// <summary>
/// RemoteHub Lab: the tests (lab\tests, read each time) with each case's latest result, the VMs and an optional build,
/// then a run through Start-LabRun.ps1 followed live from the lab's console.log – progress, time left, each case as it
/// gets its result – and the run's failures.txt at the end.
/// </summary>
public sealed class MainWindow : FluentWindow
{
    private sealed class Node
    {
        public required TreeViewItem Item { get; init; }
        public required CheckBox Box { get; init; }
        public required Border Pill { get; init; }
        public required TextBlock PillText { get; init; }
        public required TextBlock Detail { get; init; }
        public TestCase? Case { get; init; }
        public TestFile? File { get; init; }
        public string? Group { get; init; }
        public CaseState History { get; set; }
        public CaseState? Live { get; set; }
        public CaseState State => Live ?? History;
    }

    /// <summary>The checkout the app was started from (the lab password and certificate come from there).</summary>
    private readonly LabPaths _home;
    /// <summary>What runs use: the lab's checkout of main (<see cref="MainCheckout"/>), brought up to date before each run.</summary>
    private LabPaths _paths;
    private readonly TitleBar _titleBar = new();
    private IReadOnlyList<TestFile> _catalog;
    private LabHistory _history = new();
    private readonly List<Node> _cases = [], _files = [], _groups = [];
    private readonly TreeView _tree = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search case ID, file or words" };
    private readonly ComboBox _show = new() { Width = 130, Margin = new Thickness(6, 0, 0, 0) };
    private readonly WrapPanel _summary = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _tagText = Text("", muted: true, size: 12, bottom: 0);
    private readonly WrapPanel _vms = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly CheckBox _build = new() { Content = "Build new MSIs first, version", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _version = new() { Width = 100, Margin = new Thickness(8, 0, 0, 0) };
    private readonly CheckBox _harness = new() { Content = "Rebuild the lab harness (after client or harness changes)", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _msi = Text("", muted: true, size: 12, bottom: 6);
    private readonly CheckBox _repeat = new()
    {
        Content = "Run again when it ends, until stopped", Margin = new Thickness(0, 6, 0, 0),
        ToolTip = "Before each new run lab\\tests is read again: with everything selected, new test cases are run too.",
    };
    private const string QuickTip = "Device files run on the W11 VMs only, except those marked 'LabQuick: every kind'. The Console files run as usual.";
    private const string FailFastTip = "A test block skips its other cases after its first failure, and no more files start once 4 in a row passed nothing.";
    private readonly CheckBox _quick = new() { Content = "Quick build check – device files on one VM kind", ToolTip = QuickTip };
    private readonly CheckBox _failFast = new() { Content = "Fail fast", Margin = new Thickness(0, 6, 0, 0), ToolTip = FailFastTip };
    private bool _stopRequested, _waitingNext;
    private int _series;
    private readonly Button _start = new() { Content = "Start run", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Play24), MinWidth = 120 };
    private readonly Button _stop = new() { Content = "Stop", Icon = new SymbolIcon(SymbolRegular.Stop24), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly Button _open = new() { Content = "Open results", Icon = new SymbolIcon(SymbolRegular.FolderOpen24), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _phase = Text("", bottom: 6, size: 14, weight: FontWeights.SemiBold);
    private readonly ProgressBar _progress = new() { Maximum = 1, Height = 6, Margin = new Thickness(0, 2, 0, 8) };
    private readonly TextBlock _progressText = Text("", muted: true, size: 12, bottom: 0);
    private readonly StackPanel _fileRuns = new();
    private readonly System.Windows.Controls.TextBox _failures = MonoBox();
    private readonly System.Windows.Controls.TextBox _output = MonoBox();
    private readonly TabControl _tabs = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _busy;

    // the run going on
    private Process? _process;
    private string? _runId;
    private DateTime _started;
    private List<(string File, string Kind)> _planned = [];
    private readonly HashSet<string> _doneRead = [];
    private int _parallel = 1;

    public MainWindow(LabPaths paths)
    {
        _home = paths;
        // main as last brought up to date until Loaded updates it
        _paths = LabPaths.TryFind(MainCheckout.Dir) ?? paths;
        _catalog = Catalog.Read(_paths.TestsDir);
        Title = "RemoteHub Lab";
        Width = 1320;
        Height = 860;
        MinWidth = 980;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        WindowCornerPreference = WindowCornerPreference.Round;
        AppIcon.ApplyTo(this);
        AppChrome.UseAccentBorder(this);

        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, "ChromeBackgroundBrush");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var icon = new ImageIcon();
        AppIcon.Apply(icon, 18);
        _titleBar.Title = "RemoteHub Lab";
        _titleBar.Icon = icon;
        root.Children.Add(_titleBar);
        var body = new Grid { Margin = new Thickness(10, 0, 10, 10) };
        Grid.SetRow(body, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 420 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star), MinWidth = 440 });
        var left = BuildTests();
        var right = BuildRun();
        Grid.SetColumn(right, 2);
        body.Children.Add(left);
        body.Children.Add(right);
        root.Children.Add(body);
        Content = root;

        _timer.Tick += (_, _) => Tick();
        Closing += OnClosing;
        Loaded += async (_, _) =>
        {
            _phase.Text = "Getting main and the newest MSIs…";
            _start.IsEnabled = false;
            await UpdateMain();
            _phase.Text = "Reading the lab results…";
            _history = await Task.Run(() => LabHistory.Read(_paths.Results));
            foreach (var n in _cases) n.History = _history.Latest.TryGetValue(n.Case!.Id, out var r) ? r.State : CaseState.NotRun;
            Refresh();
            var last = NewestRun();
            _phase.Text = (last is null ? "No lab run yet" : $"Ready – the newest run is {last}") + (_mainNote.Length > 0 ? $" · {_mainNote}" : "");
            ShowFailures(last);
        };
    }

    // ------------------------------------------------------------------ building

    private static TextBlock Text(string t, bool muted = false, double bottom = 8, double size = 14, FontWeight? weight = null)
    {
        var tb = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom), FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        if (muted) tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return tb;
    }

    private static TextBlock Section(string t) => Text(t, bottom: 8, size: 13, weight: FontWeights.SemiBold);

    private static System.Windows.Controls.TextBox MonoBox() => new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0),
    };

    private static Border Card(UIElement child, Thickness? margin = null)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(1), Child = child, Margin = margin ?? new Thickness(0) };
        b.SetResourceReference(Border.BackgroundProperty, "SidebarBackgroundBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
        return b;
    }

    private UIElement BuildTests()
    {
        var panel = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        foreach (var (label, act) in new (string, Action)[] { ("All", () => SetAll(true)), ("None", () => SetAll(false)), ("Failed in last run", SelectLastFailed), ("Changed since full run", SelectChanged) })
        {
            var b = new Button { Content = label, Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, _) => act();
            buttons.Children.Add(b);
        }
        head.Children.Add(buttons);
        head.Children.Add(Text("Tests", bottom: 0, size: 18, weight: FontWeights.SemiBold));
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);

        var filter = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var s in new[] { "All", "Failed", "Not passed", "Not run", "Selected" }) _show.Items.Add(s);
        _show.SelectedIndex = 0;
        _show.ToolTip = "Show cases by their latest lab result";
        DockPanel.SetDock(_show, Dock.Right);
        filter.Children.Add(_show);
        filter.Children.Add(_search);
        DockPanel.SetDock(filter, Dock.Top);
        panel.Children.Add(filter);

        var foot = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foot.Children.Add(_tagText);
        foot.Children.Add(_summary);
        DockPanel.SetDock(foot, Dock.Bottom);
        panel.Children.Add(foot);

        BuildTree();
        _tree.BorderThickness = new Thickness(0);
        _tree.Background = Brushes.Transparent;
        panel.Children.Add(_tree);
        _search.TextChanged += (_, _) => Filter();
        _show.SelectionChanged += (_, _) => Filter();
        return Card(panel);
    }

    /// <summary>The tree from <see cref="_catalog"/>: group › file › case.</summary>
    private void BuildTree()
    {
        _tree.Items.Clear();
        _cases.Clear();
        _files.Clear();
        _groups.Clear();
        foreach (var g in _catalog.GroupBy(f => f.Group).OrderBy(g => g.Key == "Smoke" ? "" : g.Key, StringComparer.Ordinal))
        {
            var gn = NewNode($"{g.Key}", null, null, g.Key, $"{g.Sum(f => f.Cases.Count)} cases", bold: true);
            foreach (var f in g)
            {
                var fn = NewNode(f.Name, null, f, null, $"{f.Cases.Count} cases{(f.Role.Length > 0 ? " · " + f.Role : "")}", tip: f.About);
                foreach (var c in f.Cases)
                {
                    var cn = NewNode(c.Id, c, null, null, c.Title, tip: c.Title);
                    cn.History = _history.Latest.TryGetValue(c.Id, out var r) ? r.State : CaseState.NotRun;
                    fn.Item.Items.Add(cn.Item);
                    _cases.Add(cn);
                }
                gn.Item.Items.Add(fn.Item);
                _files.Add(fn);
            }
            _tree.Items.Add(gn.Item);
            _groups.Add(gn);
        }
    }

    /// <summary>
    /// Reads lab\tests again. New cases (a new file, or a new It in a file) are added to the tree; with everything
    /// selected they are selected too, otherwise the selection stays as it was. Returns the new case IDs.
    /// </summary>
    private List<string> ReloadCatalog()
    {
        var fresh = Catalog.Read(_paths.TestsDir);
        var (newCases, changed) = Catalog.Compare(_catalog, fresh);
        var added = newCases.Select(c => c.Id).Distinct().ToList();
        if (!changed) return added;
        var all = _cases.Count > 0 && _cases.All(c => c.Box.IsChecked == true);
        var chosen = _cases.Where(c => c.Box.IsChecked == true).Select(c => Catalog.Key(c.Case!)).ToHashSet();
        _catalog = fresh;
        _busy = true;
        try
        {
            BuildTree();
            foreach (var c in _cases) c.Box.IsChecked = all || chosen.Contains(Catalog.Key(c.Case!));
            foreach (var p in _files.Concat(_groups)) p.Box.IsChecked = Tri(Kids(p));
        }
        finally { _busy = false; }
        Refresh();
        return added;
    }

    private Node NewNode(string name, TestCase? c, TestFile? f, string? group, string detail, string? tip = null, bool bold = false)
    {
        var pillText = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
        var pill = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(6, 0, 6, 1), Width = 74, Margin = new Thickness(0, 0, 8, 0), Child = pillText, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = name, FontWeight = bold || c is not null ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var det = new TextBlock { Text = detail, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        det.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(pill);
        row.Children.Add(title);
        row.Children.Add(det);
        var box = new CheckBox { Content = row, IsChecked = false, MinWidth = 0, Padding = new Thickness(6, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(box, c is null ? name : $"{c.Id} {c.Title}");
        if (tip is { Length: > 0 }) box.ToolTip = tip;
        var item = new TreeViewItem { Header = box };
        var node = new Node { Item = item, Box = box, Pill = pill, PillText = pillText, Detail = det, Case = c, File = f, Group = group };
        item.Tag = node;
        box.Checked += (_, _) => Toggle(node);
        box.Unchecked += (_, _) => Toggle(node);
        SetPill(node, CaseState.NotRun);
        return node;
    }

    private UIElement BuildRun()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());

        var run = new StackPanel();
        run.Children.Add(Section("VMs"));
        run.Children.Add(_vms);
        foreach (var (kind, lanes) in Kinds())
            _vms.Children.Add(new CheckBox { Content = lanes > 1 ? $"{kind}  ({lanes} lanes)" : kind, Tag = kind, IsChecked = true, Margin = new Thickness(0, 0, 16, 4) });
        if (_vms.Children.Count == 0) _vms.Children.Add(Text("No lab run yet to read the VMs from – the run uses all of them.", muted: true, size: 12, bottom: 0));
        run.Children.Add(Section("Build"));
        run.Children.Add(_msi);
        var b = new StackPanel { Orientation = Orientation.Horizontal };
        b.Children.Add(_build);
        b.Children.Add(_version);
        run.Children.Add(b);
        run.Children.Add(_harness);
        run.Children.Add(Section("Run"));
        run.Children.Add(_quick);
        run.Children.Add(_failFast);
        run.Children.Add(_repeat);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(_start);
        actions.Children.Add(_stop);
        actions.Children.Add(_open);
        run.Children.Add(actions);
        grid.Children.Add(Card(run));
        ShowMsi(null);

        var prog = new StackPanel();
        prog.Children.Add(_phase);
        prog.Children.Add(_progress);
        prog.Children.Add(_progressText);
        var progCard = Card(prog, new Thickness(0, 10, 0, 0));
        Grid.SetRow(progCard, 1);
        grid.Children.Add(progCard);

        _tabs.Items.Add(new TabItem { Header = "File runs", Content = new ScrollViewer { Content = _fileRuns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 8, 0, 0) } });
        _tabs.Items.Add(new TabItem { Header = "Failures", Content = _failures });
        _tabs.Items.Add(new TabItem { Header = "Output", Content = _output });
        var tabsCard = Card(_tabs, new Thickness(0, 10, 0, 0));
        Grid.SetRow(tabsCard, 2);
        grid.Children.Add(tabsCard);

        _start.Click += async (_, _) => await StartRun(again: false);
        _stop.Click += async (_, _) => await StopRun();
        _open.Click += (_, _) =>
        {
            var dir = _runId is not null && Directory.Exists(Path.Combine(_paths.Results, _runId)) ? Path.Combine(_paths.Results, _runId) : _paths.Results;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); } catch { /* no explorer */ }
        };
        return grid;
    }

    // ------------------------------------------------------------------ state shown

    private void SetPill(Node n, CaseState s)
    {
        var (text, brush) = s switch
        {
            CaseState.Passed => ("Passed", "SystemFillColorSuccessBrush"),
            CaseState.Failed => ("Failed", "SystemFillColorCriticalBrush"),
            CaseState.Skipped => ("Skipped", "SystemFillColorCautionBrush"),
            CaseState.Pending => ("Pending", "TextFillColorSecondaryBrush"),
            CaseState.Running => ("Running", "BrandAccentBrush"),
            _ => ("Not run", "TextFillColorTertiaryBrush"),
        };
        n.PillText.Text = text;
        n.PillText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        n.Pill.SetResourceReference(Border.BorderBrushProperty, brush);
    }

    /// <summary>Every case's pill and tooltip, the files' and groups' worst state and counts, the summary and the -Tag.</summary>
    private void Refresh()
    {
        foreach (var n in _cases)
        {
            SetPill(n, n.State);
            var r = _history.Latest.GetValueOrDefault(n.Case!.Id);
            n.Box.ToolTip = $"{n.Case.Id} {n.Case.Title}\n\n" + (n.Live is { } live ? $"In this run: {live}." :
                r is null ? "Not run in the lab yet." :
                r.State == CaseState.Failed ? $"Failed in run {r.Run} ({r.When:d MMM HH:mm}) on {r.FailedOn}." : $"{r.State} in run {r.Run} ({r.When:d MMM HH:mm}).");
        }
        foreach (var parent in _files.Concat(_groups))
        {
            var kids = Kids(parent).ToList();
            SetPill(parent, Worst(kids.Select(k => k.State)));
            var failed = kids.Count(k => k.State == CaseState.Failed);
            var notRun = kids.Count(k => k.State == CaseState.NotRun);
            var basis = parent.File is { } f ? $"{f.Cases.Count} cases{(f.Role.Length > 0 ? " · " + f.Role : "")}" : $"{kids.Count} cases";
            parent.Detail.Text = basis + (failed > 0 ? $" · {failed} failed" : "") + (notRun > 0 ? $" · {notRun} not run" : "");
        }
        UpdateSummary();
        Filter();
    }

    private IEnumerable<Node> Kids(Node parent) =>
        parent.File is not null ? _cases.Where(c => c.Case!.File == parent.File.Name)
        : _files.Where(f => f.File!.Group == parent.Group).SelectMany(Kids);

    private static CaseState Worst(IEnumerable<CaseState> states)
    {
        var s = states.ToList();
        if (s.Count == 0) return CaseState.NotRun;
        foreach (var w in new[] { CaseState.Running, CaseState.Failed, CaseState.Pending, CaseState.Skipped, CaseState.NotRun })
            if (s.Contains(w)) return w == CaseState.NotRun && s.Any(x => x == CaseState.Passed) ? CaseState.Skipped : w;
        return CaseState.Passed;
    }

    private void UpdateSummary()
    {
        _summary.Children.Clear();
        var chosen = _cases.Where(c => c.Box.IsChecked == true).ToList();
        var basis = chosen.Count > 0 ? chosen : _cases;
        Chip(chosen.Count > 0 ? $"{chosen.Count} selected" : $"{_cases.Count} cases", "TextFillColorPrimaryBrush");
        foreach (var s in new[] { CaseState.Running, CaseState.Pending, CaseState.Passed, CaseState.Failed, CaseState.Skipped, CaseState.NotRun })
        {
            var n = basis.Count(c => c.State == s);
            if (n == 0) continue;
            var (label, brush) = s switch
            {
                CaseState.Passed => ("passed", "SystemFillColorSuccessBrush"), CaseState.Failed => ("failed", "SystemFillColorCriticalBrush"),
                CaseState.Skipped => ("skipped", "SystemFillColorCautionBrush"), CaseState.Pending => ("pending", "TextFillColorSecondaryBrush"),
                CaseState.Running => ("running", "BrandAccentBrush"), _ => ("not run", "TextFillColorTertiaryBrush"),
            };
            Chip($"{n} {label}", brush);
        }
        var tags = SelectedTags();
        _tagText.Text = tags is null ? "Nothing selected." : tags.Count == 0 ? "Everything selected: a full run." : "-Tag " + Shorten(string.Join(",", tags), 300);
        _start.IsEnabled = tags is not null && _process is null && !_msi.Text.Contains("is missing", StringComparison.Ordinal);
    }

    private void Chip(string text, string brush)
    {
        var t = new TextBlock { Text = text, FontSize = 12 };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var b = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 0, 6, 4), Child = t };
        b.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
        _summary.Children.Add(b);
    }

    private static string Shorten(string s, int max) => s.Length > max ? s[..max] + " …" : s;

    private void Filter()
    {
        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var show = _show.SelectedIndex;   // 0 all, 1 failed, 2 not passed, 3 not run, 4 selected
        var open = words.Length > 0 || show > 0;
        foreach (var g in _groups)
        {
            var gAny = false;
            foreach (var f in _files.Where(f => f.File!.Group == g.Group))
            {
                var fAny = false;
                foreach (var c in _cases.Where(c => c.Case!.File == f.File!.Name))
                {
                    var hay = $"{c.Case!.Id} {c.Case.File} {c.Case.Title}";
                    var hit = words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase)) && show switch
                    {
                        1 => c.State == CaseState.Failed,
                        2 => c.State != CaseState.Passed,
                        3 => c.State == CaseState.NotRun,
                        4 => c.Box.IsChecked == true,
                        _ => true,
                    };
                    c.Item.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                    fAny |= hit;
                }
                f.Item.Visibility = fAny ? Visibility.Visible : Visibility.Collapsed;
                f.Item.IsExpanded = fAny && open;
                gAny |= fAny;
            }
            g.Item.Visibility = gAny ? Visibility.Visible : Visibility.Collapsed;
            g.Item.IsExpanded = gAny && open;
        }
    }

    // ------------------------------------------------------------------ selection

    private void Toggle(Node n)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var on = n.Box.IsChecked == true;
            foreach (var c in n.Case is not null ? [n] : Kids(n)) c.Box.IsChecked = on;
            foreach (var p in _files.Concat(_groups)) p.Box.IsChecked = Tri(Kids(p));
        }
        finally { _busy = false; }
        UpdateSummary();
    }

    private static bool? Tri(IEnumerable<Node> kids)
    {
        var k = kids.ToList();
        var on = k.Count(c => c.Box.IsChecked == true);
        return on == 0 ? false : on == k.Count ? true : null;
    }

    private void SetAll(bool on)
    {
        _busy = true;
        try { foreach (var n in _cases.Concat(_files).Concat(_groups)) n.Box.IsChecked = on; }
        finally { _busy = false; }
        UpdateSummary();
    }

    private void SelectLastFailed()
    {
        var last = NewestRun();
        var failed = last is null ? [] : LabHistory.ReadRun(Path.Combine(_paths.Results, last)).Where(r => r.State == CaseState.Failed).Select(r => r.Id).ToHashSet();
        if (failed.Count == 0) { _phase.Text = last is null ? "No lab run yet" : $"The newest run ({last}) has no failed cases."; return; }
        _busy = true;
        try
        {
            foreach (var c in _cases) c.Box.IsChecked = failed.Contains(c.Case!.Id);
            foreach (var p in _files.Concat(_groups)) p.Box.IsChecked = Tri(Kids(p));
        }
        finally { _busy = false; }
        _show.SelectedIndex = 4;
        UpdateSummary();
        Filter();
    }

    /// <summary>
    /// Selects the files that what changed since the last full run can affect (<see cref="Areas"/>): git's changes from
    /// the commit that run was made from to this checkout, committed or not.
    /// </summary>
    private async void SelectChanged()
    {
        var last = Areas.LastFullRun(_paths.Results);
        if (last is null) { _phase.Text = "No full run has recorded its commit yet – the next full run does."; return; }
        var (run, commit) = last.Value;
        List<string> changed;
        try
        {
            var diff = await MainCheckout.Git(_paths.Repo, "diff", "--name-only", commit);
            var untracked = await MainCheckout.Git(_paths.Repo, "ls-files", "--others", "--exclude-standard");
            if (diff is null || untracked is null) { _phase.Text = $"git doesn't know commit {commit[..10]} of run {run} – fetch it, or select by hand."; return; }
            changed = [.. diff, .. untracked];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _phase.Text = $"git didn't run: {ex.Message}";
            return;
        }
        var files = Areas.Affected(changed, _catalog);
        _busy = true;
        try
        {
            foreach (var c in _cases) c.Box.IsChecked = files is null || files.Contains(c.Case!.File);
            foreach (var p in _files.Concat(_groups)) p.Box.IsChecked = Tri(Kids(p));
        }
        finally { _busy = false; }
        _phase.Text = files is null
            ? $"{changed.Count} files changed since run {run}, some outside the known areas – everything is selected."
            : $"{changed.Count} files changed since run {run}: {files.Count} test files selected.";
        _show.SelectedIndex = 4;
        UpdateSummary();
        Filter();
    }

    /// <summary>
    /// The -Tag list for what is selected: null for nothing, empty for everything (a full run); a group's own tag where
    /// the whole group is selected and no file outside it carries that tag, else the selected cases' IDs.
    /// </summary>
    private List<string>? SelectedTags()
    {
        var chosen = _cases.Where(c => c.Box.IsChecked == true).ToList();
        if (chosen.Count == 0) return null;
        if (chosen.Count == _cases.Count) return [];
        var tags = new List<string>();
        var covered = new HashSet<string>();
        foreach (var g in _groups.Where(g => g.Box.IsChecked == true))
        {
            var mine = _catalog.Where(f => f.Group == g.Group).Select(f => f.Name).ToHashSet();
            if (_catalog.Where(f => f.Groups.Contains(g.Group!)).All(f => mine.Contains(f.Name))) { tags.Add(g.Group!); covered.UnionWith(mine); }
        }
        foreach (var c in chosen.Where(c => !covered.Contains(c.Case!.File)))
            if (!tags.Contains(c.Case!.Id)) tags.Add(c.Case.Id);
        return tags;
    }

    // ------------------------------------------------------------------ what there is

    /// <summary>The VM kinds and their lanes, from the result folders of the newest runs (no Hyper-V rights needed).</summary>
    private IEnumerable<(string Kind, int Lanes)> Kinds()
    {
        if (!Directory.Exists(_paths.Results)) return [];
        var names = Directory.GetDirectories(_paths.Results).Select(Path.GetFileName).OfType<string>()
            .Where(n => Regex.IsMatch(n, @"^\d{8}-\d{6}$")).OrderByDescending(n => n, StringComparer.Ordinal).Take(15)
            .SelectMany(r => Directory.GetDirectories(Path.Combine(_paths.Results, r)).Select(Path.GetFileName).OfType<string>())
            .Distinct().ToList();
        return names.GroupBy(RunLog.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Count()));
    }

    private string? NewestRun() => Directory.Exists(_paths.Results)
        ? Directory.GetDirectories(_paths.Results).Select(Path.GetFileName).OfType<string>()
            .Where(n => Regex.IsMatch(n, @"^\d{8}-\d{6}$") && File.Exists(Path.Combine(_paths.Results, n, "done.json")))
            .OrderByDescending(n => n, StringComparer.Ordinal).FirstOrDefault()
        : null;

    private Version? NewestMsi() => Directory.Exists(_paths.Artifacts)
        ? Directory.GetFiles(_paths.Artifacts, "RemoteHub-Agent-*-x64.msi").Select(Path.GetFileName)
            .Select(n => Regex.Match(n ?? "", @"^RemoteHub-Agent-(\d+\.\d+\.\d+)-x64\.msi$")).Where(m => m.Success)
            .Select(m => Version.Parse(m.Groups[1].Value)).OrderByDescending(v => v).FirstOrDefault()
        : null;

    // ------------------------------------------------------------------ running

    /// <param name="again">The next run of a repeat: the MSIs are built only before the first one.</param>
    private async Task StartRun(bool again)
    {
        if (_process is not null) return;
        // the tests and scripts of main as it is now, with the newest MSIs (a repeat did this before its countdown)
        if (!again)
        {
            _start.IsEnabled = false;
            _phase.Text = "Getting main and the newest MSIs…";
            await UpdateMain();
        }
        var tags = SelectedTags();
        if (tags is null) { _phase.Text = "Nothing selected."; return; }
        if (!again) { _series = 0; _stopRequested = false; }
        _series++;
        if (again) _build.IsChecked = false;
        var kinds = _vms.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        var allKinds = _vms.Children.OfType<CheckBox>().Select(c => (string)c.Tag).ToList();
        if (allKinds.Count > 0 && kinds.Count == 0) { _phase.Text = "Choose at least one VM."; return; }
        var version = _version.Text.Trim();
        if (_build.IsChecked == true && !Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) { _phase.Text = "The version must look like 0.20.3."; return; }

        // what will run: the files with a selected case, on the kinds their LabRole fits
        var files = _cases.Where(c => c.Box.IsChecked == true).Select(c => c.Case!.File).Distinct().ToHashSet();
        var on = kinds.Count > 0 ? kinds : allKinds;
        _planned = _catalog.Where(f => files.Contains(f.Name)).SelectMany(f => RunPlan.Kinds(f, on, _quick.IsChecked == true).Select(k => (f.Name, k))).ToList();
        _parallel = Math.Max(1, _vms.Children.OfType<CheckBox>().Where(c => c.IsChecked == true)
            .Sum(c => c.Content is string s && Regex.Match(s, @"\((\d+) lanes\)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 1));
        foreach (var c in _cases) c.Live = c.Box.IsChecked == true ? CaseState.Pending : null;
        _doneRead.Clear();
        _runId = null;
        _started = DateTime.Now;
        _output.Clear();
        _failures.Clear();
        _fileRuns.Children.Clear();
        _tabs.SelectedIndex = 0;
        _progress.IsIndeterminate = true;
        _stop.IsEnabled = true;
        foreach (var c in new UIElement[] { _build, _version, _harness, _vms, _quick, _failFast }) c.IsEnabled = false;
        Refresh();
        if (_series > 1) Output($"--- Run {_series} of the repeat ---");

        var q = (string s) => "'" + s.Replace("'", "''") + "'";
        var cmd = new StringBuilder("$ErrorActionPreference = 'Stop'; ");
        if (_build.IsChecked == true)
        {
            cmd.Append($"Write-Host 'Building RemoteHub {version} ...'; & {q(Path.Combine(_paths.Repo, "build", "build-installer.ps1"))} -Version '{version}' -LabSigning -CredentialProvider; ");
            cmd.Append("if ($LASTEXITCODE) { throw \"The build failed (exit $LASTEXITCODE).\" }; ");
        }
        cmd.Append($"& {q(Path.Combine(_paths.LabDir, "Start-LabRun.ps1"))}");
        if (tags.Count > 0) cmd.Append(" -Tag " + string.Join(",", tags.Select(q)));
        if (kinds.Count > 0 && kinds.Count < allKinds.Count) cmd.Append(" -Vm " + string.Join(",", kinds.Select(q)));
        if (_harness.IsChecked != true) cmd.Append(" -NoBuild");
        if (_quick.IsChecked == true) cmd.Append(" -Quick");
        if (_failFast.IsChecked == true) cmd.Append(" -FailFast");
        cmd.Append("; exit $LASTEXITCODE");
        _phase.Text = _build.IsChecked == true ? $"Building RemoteHub {version}" : "Starting";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = _paths.Repo,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd.ToString())));
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Dispatcher.BeginInvoke(() => Output(e.Data)); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Dispatcher.BeginInvoke(() => Output(e.Data)); };
        p.Exited += (_, _) => Dispatcher.BeginInvoke(async () => await Finished());
        _process = p;
        UpdateSummary();
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _timer.Start();
        await Task.CompletedTask;
    }

    private void Output(string line)
    {
        _output.AppendText(line + Environment.NewLine);
        _output.ScrollToEnd();
        if (_runId is null && Regex.Match(line, @"^Run (\d{8}-\d{6})") is { Success: true } m) { _runId = m.Groups[1].Value; Tick(); }
    }

    private void Tick()
    {
        var elapsed = DateTime.Now - _started;
        if (_runId is null) { _progressText.Text = $"{elapsed:h\\:mm\\:ss} so far"; return; }
        var dir = Path.Combine(_paths.Results, _runId);
        var logPath = Path.Combine(dir, "console.log");
        var log = File.Exists(logPath) ? LabHistory.ReadShared(logPath) : "";
        var entries = RunLog.Parse(log);
        if (RunLog.PlannedRuns(log) is { } total && total != _planned.Count && _planned.Count > 0 && total < _planned.Count)
            _planned = _planned.Take(total).ToList();   // the lab knows better what it runs
        if (log.Length == 0) { _phase.Text = $"Run {_runId} – queued, waiting for the lab"; _progressText.Text = $"{elapsed:h\\:mm\\:ss} so far"; return; }
        if (entries.Count == 0) { _phase.Text = $"Run {_runId} – preparing the VMs"; _progressText.Text = $"{elapsed:h\\:mm\\:ss} so far"; return; }

        // cases: running while their file runs anywhere, then the result as each file run ends
        foreach (var s in entries.Where(e => !e.Done))
            foreach (var c in _cases.Where(c => c.Case!.File == s.File && c.Live == CaseState.Pending)) c.Live = CaseState.Running;
        foreach (var d in entries.Where(e => e.Done))
        {
            var key = $"{d.File}|{d.Vm}";
            if (!_doneRead.Add(key)) continue;
            var results = LabHistory.ReadRun(Path.Combine(dir, RunLog.Kind(d.Vm)), d.File).ToDictionary(r => r.Id);
            foreach (var c in _cases.Where(c => c.Case!.File == d.File && c.Live is not null))
                if (results.TryGetValue(c.Case!.Id, out var r) && c.Live != CaseState.Failed) c.Live = r.State;
        }
        var est = Estimate.For(_planned, entries, _history, DateTime.Now.TimeOfDay, _parallel);
        _progress.IsIndeterminate = false;
        _progress.Value = est.Progress;
        var failedRuns = entries.Count(e => e.Done && e.Failed > 0);
        _phase.Text = $"Run {_runId} – {est.Done} of {est.Total} file runs done" + (_repeat.IsChecked == true ? $" · repeat {_series}" : "");
        _progressText.Text = $"{est.Progress:P0} · about {Minutes(est.Left)} left – ends around {DateTime.Now + est.Left:HH:mm}" +
                             (failedRuns > 0 ? $" · {failedRuns} with failures" : "") + $" · {elapsed:h\\:mm\\:ss} so far";
        ShowFileRuns(entries);
        Refresh();
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes < 1.5 ? "a minute" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Round(t.TotalMinutes)} min";

    private void ShowFileRuns(IReadOnlyList<RunLog.Entry> entries)
    {
        _fileRuns.Children.Clear();
        var rows = new List<(CaseState State, string Text, string Detail)>();
        foreach (var s in entries.Where(e => !e.Done))
        {
            var d = entries.FirstOrDefault(e => e.Done && e.File == s.File && e.Vm == s.Vm && e.At >= s.At);
            var expect = _history.Expected(s.File, RunLog.Kind(s.Vm));
            rows.Add(d is not null
                ? (d.Failed > 0 ? CaseState.Failed : CaseState.Passed, $"{s.File} on {s.Vm}", $"{d.Minutes:0} min{(d.Failed > 0 ? $" · {d.Failed} failed" : "")}")
                : (CaseState.Running, $"{s.File} on {s.Vm}", $"since {s.At:hh\\:mm}{(expect is { } x ? $" · usually {x:0} min" : "")}"));
        }
        var waiting = _planned.ToList();
        foreach (var s in entries.Where(e => !e.Done))
        {
            var i = waiting.FindIndex(p => p.File == s.File && p.Kind == RunLog.Kind(s.Vm));
            if (i >= 0) waiting.RemoveAt(i);
        }
        foreach (var w in waiting)
            rows.Add((CaseState.Pending, $"{w.File} on {w.Kind}", _history.Expected(w.File, w.Kind) is { } x ? $"usually {x:0} min" : "not timed yet"));
        foreach (var (state, text, detail) in rows.OrderBy(r => r.State == CaseState.Running ? 0 : r.State == CaseState.Pending ? 2 : 1))
        {
            var pillText = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
            var pill = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(6, 0, 6, 1), Width = 74, Margin = new Thickness(0, 0, 10, 0), Child = pillText };
            var n = new Node { Item = new TreeViewItem(), Box = new CheckBox(), Pill = pill, PillText = pillText, Detail = new TextBlock() };
            SetPill(n, state);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(pill);
            var det = Text(detail, muted: true, size: 12, bottom: 0);
            DockPanel.SetDock(det, Dock.Right);
            row.Children.Add(det);
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            _fileRuns.Children.Add(row);
        }
    }

    private async Task Finished()
    {
        _timer.Stop();
        var code = _process?.ExitCode ?? -1;
        _process = null;
        if (_runId is not null) Tick();
        _stop.IsEnabled = false;
        foreach (var c in new UIElement[] { _build, _version, _harness, _vms, _quick, _failFast }) c.IsEnabled = true;
        _progress.IsIndeterminate = false;
        var elapsed = DateTime.Now - _started;
        if (_runId is null)
        {
            _phase.Text = $"Ended before a run started (exit code {code}) – see Output";
            _progress.Value = 0;
            _tabs.SelectedIndex = 2;
            foreach (var c in _cases) c.Live = null;
        }
        else
        {
            var failed = _cases.Count(c => c.Live == CaseState.Failed);
            var notReached = _cases.Count(c => c.Live is CaseState.Pending or CaseState.Running);
            _phase.Text = $"Run {_runId} finished after {Minutes(elapsed)} – " +
                          (failed > 0 ? $"{failed} case(s) failed" : notReached > 0 ? $"{notReached} case(s) without a result" : "all passed");
            _progress.Value = 1;
            _progressText.Text = $"Ended at {DateTime.Now:HH:mm}";
            ShowFailures(_runId);
            _tabs.SelectedIndex = 1;
            _history = await Task.Run(() => LabHistory.Read(_paths.Results));
            foreach (var n in _cases)
            {
                n.History = _history.Latest.TryGetValue(n.Case!.Id, out var r) ? r.State : CaseState.NotRun;
                if (n.Live is CaseState.Pending or CaseState.Running) n.Live = null;   // what the run didn't reach keeps its earlier result
            }
        }
        Refresh();
        if (_repeat.IsChecked == true && !_stopRequested && _runId is not null) await RunAgain();
    }

    /// <summary>
    /// The next run of a repeat, a minute after the last one ended (Stop cancels it): lab\tests is read again first, so
    /// with everything selected a test case added since runs too (Start-LabRun.ps1 copies lab\ to the lab each time).
    /// </summary>
    private async Task RunAgain()
    {
        _phase.Text = "Getting main and the newest MSIs…";
        var added = await UpdateMain();
        var all = _cases.All(c => c.Box.IsChecked == true);
        var news = added.Count == 0 ? "no new test cases"
            : $"{added.Count} new test case(s){(all ? ", run from now on" : " – not selected, as not everything is")}: {Shorten(string.Join(", ", added), 200)}";
        _waitingNext = true;
        _stop.IsEnabled = true;
        try
        {
            for (var s = 60; s > 0 && !_stopRequested; s--)
            {
                _phase.Text = $"Next run in {s} s (repeat {_series + 1}) – {news}";
                await Task.Delay(1000);
            }
        }
        finally { _waitingNext = false; }
        if (_stopRequested) { _phase.Text = $"Repeat stopped after {_series} run(s)"; _stop.IsEnabled = false; return; }
        await StartRun(again: true);
    }

    private string _mainNote = "";

    /// <summary>
    /// Brings the lab's checkout of main up to date (<see cref="MainCheckout.Update"/>) and works from it: new test cases
    /// are added to the tree (<see cref="ReloadCatalog"/>, whose list of them this returns). When that fails, the app
    /// stays on the checkout it used so far and says why.
    /// </summary>
    private async Task<List<string>> UpdateMain()
    {
        string? from = null;
        try
        {
            var r = await MainCheckout.Update(_home);
            _paths = r.Paths;
            _mainNote = string.Join(" · ", r.Notes);
            from = r.Msi;
            _titleBar.Title = $"RemoteHub Lab – main {r.Commit}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _mainNote = $"main not updated: {ex.Message}";
            _titleBar.Title = $"RemoteHub Lab – {_paths.Repo}";
        }
        var added = ReloadCatalog();
        ShowMsi(from);
        // -Quick / -FailFast need a Start-LabRun.ps1 that has them
        var script = Path.Combine(_paths.LabDir, "Start-LabRun.ps1");
        var options = File.Exists(script) && File.ReadAllText(script).Contains("[switch]$FailFast", StringComparison.Ordinal);
        foreach (var (c, tip) in new[] { (_quick, QuickTip), (_failFast, FailFastTip) })
        {
            if (!options) c.IsChecked = false;
            c.IsEnabled = options && _process is null;
            c.ToolTip = options ? tip : "Not in main's lab scripts yet.";
        }
        return added;
    }

    /// <summary>The MSI under test (the newest in the lab checkout's artifacts\) and anything missing for a run.</summary>
    private void ShowMsi(string? from)
    {
        var newest = NewestMsi();
        if (newest is null) { _msi.Text = $"No agent MSI in {_paths.Artifacts} or any other worktree yet – build one."; _build.IsChecked = true; _version.Text = "0.1.0"; }
        else
        {
            _msi.Text = $"Under test: RemoteHub {newest}" + (from is not null ? $" – the newest build of any worktree ({from})" : " (the newest MSI in artifacts\\)");
            _version.Text = $"{newest.Major}.{newest.Minor}.{newest.Build + 1}";
        }
        var missing = new[] { "lab.local.json", "lab-signing.cer" }.Where(f => !File.Exists(Path.Combine(_paths.LabDir, f))).ToList();
        foreach (var f in missing) _msi.Text += $"\n{f} is missing in {_paths.LabDir} (git-ignored – it is copied from {_home.LabDir}, which has none).";
        _start.IsEnabled = missing.Count == 0 && _process is null;
    }

    private void ShowFailures(string? run)
    {
        if (run is null) { _failures.Text = "No lab run yet."; return; }
        var file = Path.Combine(_paths.Artifacts, "lab", run, "failures.txt");
        _failures.Text = File.Exists(file) ? LabHistory.ReadShared(file)
            : $"No failures.txt for run {run} in this checkout – lab\\Get-LabFailures.ps1 -Run {run} -Root {_paths.Results} makes one.";
    }

    private async Task StopRun()
    {
        if (_process is null)
        {
            // between two runs of a repeat: no next run
            if (_waitingNext) _stopRequested = true;
            return;
        }
        var answer = await new MessageBox
        {
            Title = "Stop this run?",
            Content = "The lab ends the scenario it is running; the VMs are reverted by the next run.",
            PrimaryButtonText = "Stop", CloseButtonText = "Keep running",
        }.ShowDialogAsync();
        if (answer != MessageBoxResult.Primary || _process is null) return;
        _stopRequested = true;   // a repeat ends with this run
        if (_runId is not null)
        {
            _phase.Text = $"Run {_runId} – stopping";
            try { Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -File \"{Path.Combine(_paths.LabDir, "Stop-LabRun.ps1")}\"") { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception ex) { Output($"Stop-LabRun.ps1 didn't start: {ex.Message}"); }
        }
        else
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already ended */ }
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_process is null) return;
        e.Cancel = true;
        var answer = await new MessageBox
        {
            Title = "A run is going on",
            Content = "It goes on without this window – Stop ends it. Close anyway?",
            PrimaryButtonText = "Close", CloseButtonText = "Cancel",
        }.ShowDialogAsync();
        if (answer != MessageBoxResult.Primary) return;
        Closing -= OnClosing;
        Close();
    }
}
