using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ListView = System.Windows.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>Labels for script settings, as the Scripts view and its windows show them.</summary>
public static class ScriptLabels
{
    public static string Context(string? c) => c switch
    {
        ScriptStore.ContextSystem => "System",
        ScriptStore.ContextUser => "User",
        ScriptStore.ContextBoth => "Both",
        ScriptStore.ContextAsMe => "As me",
        _ => "–",
    };

    public static string Risk(string? r) => r switch
    {
        ScriptStore.RiskR0 => "R0 Read-only",
        ScriptStore.RiskR1 => "R1 Reversible change",
        ScriptStore.RiskR2 => "R2 Disruptive",
        _ => "–",
    };

    public static string Os(string? o) => o switch
    {
        ScriptStore.OsClient => "Client",
        ScriptStore.OsServer => "Server",
        ScriptStore.OsBoth => "Both",
        _ => "–",
    };

    public static string Timeout(int? sec) => sec is { } s ? (s % 60 == 0 ? $"{s / 60} min" : $"{s} s") : "–";

    public static string Language(string language) => language == ScriptStore.PowerShell ? "PowerShell" : ScriptLanguages.DisplayName(language);

    /// <summary>"Runs as: System → Both · Timeout: 10 min → 15 min · Description changed", or null when the settings are the same.</summary>
    public static string? SettingsChanges(ScriptContent older, ScriptContent newer)
    {
        var parts = new List<string>();
        if (older.RunContext != newer.RunContext) parts.Add($"Runs as: {Context(older.RunContext)} → {Context(newer.RunContext)}");
        if (older.Risk != newer.Risk) parts.Add($"Risk: {Risk(older.Risk)} → {Risk(newer.Risk)}");
        if (older.OsScope != newer.OsScope) parts.Add($"OS: {Os(older.OsScope)} → {Os(newer.OsScope)}");
        if (older.TimeoutSec != newer.TimeoutSec) parts.Add($"Timeout: {Timeout(older.TimeoutSec)} → {Timeout(newer.TimeoutSec)}");
        if (older.Description != newer.Description) parts.Add("Description changed");
        return parts.Count == 0 ? null : string.Join("  ·  ", parts);
    }
}

/// <summary>The frame the Scripts view's windows share: a FluentWindow with its title bar, as the other dialogs are built.</summary>
internal static class ScriptWindowFrame
{
    public static void Apply(FluentWindow w, string title, double width, double? height = null)
    {
        w.Title = title;
        w.Width = width;
        if (height is { } h) { w.Height = h; w.ResizeMode = ResizeMode.CanResize; w.MinWidth = 520; w.MinHeight = 360; }
        else { w.SizeToContent = SizeToContent.Height; w.ResizeMode = ResizeMode.NoResize; }
        w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.ExtendsContentIntoTitleBar = true;
        w.WindowBackdropType = WindowBackdropType.None;
        w.ShowInTaskbar = false;
        w.SetResourceReference(Window.BackgroundProperty, "ChromeBackgroundBrush");
        w.SetResourceReference(Window.ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    public static TextBlock Info(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    public static StackPanel Buttons(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].MinWidth = 90;
            if (i < buttons.Length - 1) buttons[i].Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(buttons[i]);
        }
        return row;
    }

    /// <summary>A resizable window: title bar on top, the body filling the rest.</summary>
    public static void Fill(FluentWindow w, UIElement body, bool resizable)
    {
        var root = new DockPanel();
        var bar = new TitleBar { Title = w.Title, ShowMaximize = resizable, ShowMinimize = false };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(body);
        w.Content = root;
    }
}

/// <summary>
/// Compares two versions of a script, or a version and the draft (docs/scripts-view-design.md §4.10): any two of them,
/// oldest against latest to start with, side by side or inline, with ↑ ↓ between the changes.
/// </summary>
public sealed class CompareWindow : FluentWindow
{
    private readonly IReadOnlyList<(string Label, ScriptContent Content)> _choices;
    private readonly string _language;
    private readonly UiState _ui;
    private readonly Action _saveUi;
    private readonly ComboBox _older = new() { MinWidth = 200 };
    private readonly ComboBox _newer = new() { MinWidth = 200 };
    private readonly ComboBox _layout = new() { MinWidth = 130 };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _settings = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly DiffView _diff = new();

    public CompareWindow(string scriptName, string language, IReadOnlyList<(string Label, ScriptContent Content)> choices, int older, int newer, UiState ui, Action saveUi)
    {
        _choices = choices;
        _language = language;
        _ui = ui;
        _saveUi = saveUi;
        ScriptWindowFrame.Apply(this, "Compare – " + scriptName, 1100, 720);

        foreach (var (label, _) in choices) { _older.Items.Add(label); _newer.Items.Add(label); }
        _older.SelectedIndex = Math.Clamp(older, 0, choices.Count - 1);
        _newer.SelectedIndex = Math.Clamp(newer, 0, choices.Count - 1);
        AutomationProperties(_older, "Older version");
        AutomationProperties(_newer, "Newer version");
        _layout.Items.Add("Side by side");
        _layout.Items.Add("Inline");
        _layout.SelectedIndex = ui.ScriptsCompareInline ? 1 : 0;
        _settings.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var swap = RemoteToolChrome.Tool(SymbolRegular.ArrowSwap24, "Swap the sides", () => { (_older.SelectedIndex, _newer.SelectedIndex) = (_newer.SelectedIndex, _older.SelectedIndex); });
        var prev = RemoteToolChrome.Tool(SymbolRegular.ArrowUp24, "Previous change (Shift+F7)", () => _diff.GoToChange(-1));
        var next = RemoteToolChrome.Tool(SymbolRegular.ArrowDown24, "Next change (F7)", () => _diff.GoToChange(1));
        var bar = new DockPanel { LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_older);
        left.Children.Add(swap);
        left.Children.Add(_newer);
        bar.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_layout);
        right.Children.Add(prev);
        right.Children.Add(next);
        right.Children.Add(_count);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);

        var body = new DockPanel { Margin = new Thickness(20, 4, 20, 20) };
        DockPanel.SetDock(bar, Dock.Top);
        body.Children.Add(bar);
        DockPanel.SetDock(_settings, Dock.Top);
        body.Children.Add(_settings);
        var copyOlder = new Button { Content = "Copy older", ToolTip = "Copies the older side's script" };
        copyOlder.Click += (_, _) => Copy(_older.SelectedIndex);
        var copyNewer = new Button { Content = "Copy newer", ToolTip = "Copies the newer side's script" };
        copyNewer.Click += (_, _) => Copy(_newer.SelectedIndex);
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = ScriptWindowFrame.Buttons(copyOlder, copyNewer, close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);
        var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = _diff };
        frame.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "NoteCodeBackgroundBrush");
        body.Children.Add(frame);
        ScriptWindowFrame.Fill(this, body, resizable: true);

        _older.SelectionChanged += (_, _) => _ = Refresh();
        _newer.SelectionChanged += (_, _) => _ = Refresh();
        _layout.SelectionChanged += (_, _) =>
        {
            _diff.Inline = _layout.SelectedIndex == 1;
            _ui.ScriptsCompareInline = _diff.Inline;
            _saveUi();
        };
        _diff.Shown += () => _count.Text = _diff.ChangeCount switch { 0 => "No changes", 1 => "1 change", var n => $"{n} changes" };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F7) { _diff.GoToChange(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        _diff.Inline = ui.ScriptsCompareInline;
        Loaded += (_, _) => _ = Refresh();
    }

    private static void AutomationProperties(FrameworkElement e, string name) => System.Windows.Automation.AutomationProperties.SetName(e, name);

    private async Task Refresh()
    {
        if (_older.SelectedIndex < 0 || _newer.SelectedIndex < 0) return;
        var a = _choices[_older.SelectedIndex].Content;
        var b = _choices[_newer.SelectedIndex].Content;
        _settings.Text = ScriptLabels.SettingsChanges(a, b) ?? "The settings are the same.";
        // Large scripts take a moment: say so after 200 ms rather than leaving the old count up.
        var shown = _diff.Show(a.Body, b.Body, _language);
        if (await Task.WhenAny(shown, Task.Delay(200)) != shown && !shown.IsCompleted) _count.Text = "Comparing…";
        await shown;
    }

    private void Copy(int index)
    {
        if (index >= 0) NoteEditor.SetClipboardText(_choices[index].Content.Body);
    }
}

/// <summary>
/// "Ready to deploy" (§4.10): what changes against the latest version, an optional note, and Deploy. A PowerShell script
/// with syntax errors can't be deployed; a line that looks like a secret is pointed out again.
/// </summary>
public sealed class DeployWindow : FluentWindow
{
    private readonly TextBox _note = new() { PlaceholderText = "What changed and why (optional)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 2, MaxLength = ScriptStore.MaxNoteLength };

    public string Note => _note.Text.Trim();

    public DeployWindow(string scriptName, string language, ScriptVersion? latest, ScriptContent draft, IReadOnlyList<ScriptSyntaxError> errors, string? secretHint)
    {
        ScriptWindowFrame.Apply(this, "Ready to deploy – " + scriptName, 1000, 680);
        var info = ScriptWindowFrame.Info(latest is null
            ? "This becomes version 1: from now on it is what runs on devices. Typing goes on into a draft until you deploy again."
            : $"This becomes version {latest.Version + 1}: from now on it is what runs on devices instead of version {latest.Version}.");
        var settings = ScriptWindowFrame.Info(latest is null ? "" : ScriptLabels.SettingsChanges(latest.Content, draft) ?? "The settings are the same.");
        var problems = new StackPanel();
        if (errors.Count > 0)
        {
            var e = errors[0];
            var t = new TextBlock { Text = $"The script has {(errors.Count == 1 ? "a syntax error" : $"{errors.Count} syntax errors")} – line {e.Line}: {e.Message}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "ScriptErrorBrush");
            problems.Children.Add(t);
        }
        if (secretHint is not null)
        {
            var t = new TextBlock { Text = secretHint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "ScriptWarningBrush");
            problems.Children.Add(t);
        }
        var diff = new DiffView();
        var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = diff, MinHeight = 200 };
        frame.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "NoteCodeBackgroundBrush");

        var deploy = new Button { Content = "Deploy", Appearance = ControlAppearance.Primary, IsDefault = true, IsEnabled = errors.Count == 0 };
        deploy.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var top = new StackPanel();
        top.Children.Add(info);
        if (settings.Text.Length > 0) top.Children.Add(settings);
        top.Children.Add(problems);
        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        bottom.Children.Add(new TextBlock { Text = "Note", Margin = new Thickness(0, 0, 0, 4) });
        bottom.Children.Add(_note);
        bottom.Children.Add(ScriptWindowFrame.Buttons(deploy, cancel));
        var body = new DockPanel { Margin = new Thickness(20, 4, 20, 20) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(bottom);
        body.Children.Add(frame);
        ScriptWindowFrame.Fill(this, body, resizable: true);
        Loaded += async (_, _) => { _note.Focus(); await diff.Show(latest?.Content.Body ?? "", draft.Body, language); };
    }
}

/// <summary>The kept versions of a script, newest first, with the draft on top when it has changes (§4.10).</summary>
public sealed class VersionsWindow : FluentWindow
{
    private readonly ListView _list = new() { Height = 320, SelectionMode = SelectionMode.Single, BorderThickness = new Thickness(0) };
    private readonly Button _compare = new() { Content = "Compare with…", IsEnabled = false };
    private readonly Button _restore = new() { Content = "Restore to draft", IsEnabled = false, Appearance = ControlAppearance.Primary };
    private readonly Button _export = new() { Content = "Export…", IsEnabled = false };

    /// <summary>A version was restored into the draft (the view reloads it).</summary>
    public bool Restored { get; private set; }

    public VersionsWindow(ScriptInfo script, ScriptContent draft, UiState ui, Action saveUi, Action<ScriptVersion>? export)
    {
        ScriptWindowFrame.Apply(this, "Versions – " + script.Name, 680);
        var versions = ScriptStore.Versions(script.Id);
        var runs = ScriptStore.RunCounts(script.Id);
        var info = ScriptWindowFrame.Info($"The last {ScriptStore.KeptVersions} deployed versions are kept. Restoring one copies it into the draft; it runs on devices only once it is deployed again, as a new version.");
        if (script.HasDraftChanges || !script.IsDeployed) _list.Items.Add(Row("Draft", script.IsDeployed ? "Changes that aren't deployed" : "Not deployed yet", null, -1));
        foreach (var v in versions)
        {
            var ran = runs.GetValueOrDefault(v.Version);
            var detail = $"{v.DeployedUtc.ToLocalTime():g} · {v.DeployedBy}" + (ran > 0 ? $" · ran {ran}×" : "");
            _list.Items.Add(Row($"Version {v.Version}" + (v.Version == script.DeployedVersion ? " (runs on devices)" : ""), detail, v.Note.Length > 0 ? v.Note : null, v.Version));
        }
        _list.SelectionChanged += (_, _) =>
        {
            var v = SelectedVersion;
            _compare.IsEnabled = _list.SelectedItem is ListViewItem { Tag: int } && _list.Items.Count > 1;
            _restore.IsEnabled = v is not null && script.DeployedVersion is { } d && (v.Version != d || script.HasDraftChanges);
            _export.IsEnabled = v is not null && export is not null;
        };
        _compare.Click += (_, _) =>
        {
            // The selected entry against the latest version, or against the draft when the latest is selected.
            var choices = new List<(string, ScriptContent)>();
            foreach (var v in versions.OrderBy(v => v.Version)) choices.Add(($"Version {v.Version} · {v.DeployedUtc.ToLocalTime():d}", v.Content));
            if (script.HasDraftChanges || !script.IsDeployed) choices.Add(("Draft", draft));
            var selected = _list.SelectedItem is ListViewItem { Tag: int tag } ? tag : -1;
            var left = selected < 0 ? Math.Max(0, versions.Count - 1) : versions.OrderBy(v => v.Version).ToList().FindIndex(v => v.Version == selected);
            var right = choices.Count - 1;
            if (left == right) left = Math.Max(0, right - 1);
            if (selected < 0) (left, right) = (Math.Max(0, versions.Count - 1), choices.Count - 1);
            new CompareWindow(script.Name, script.Language, choices, left, right, ui, saveUi) { Owner = this }.ShowDialog();
        };
        _restore.Click += async (_, _) =>
        {
            if (SelectedVersion is not { } v) return;
            if (script.HasDraftChanges && !await Dialogs.Confirm(this, "Restore to draft", $"Replace the draft's changes with version {v.Version}?", "Restore")) return;
            ScriptStore.RestoreToDraft(script.Id, v.Version);
            Restored = true;
            Close();
        };
        _export.Click += (_, _) => { if (SelectedVersion is { } v) export?.Invoke(v); };
        var close = new Button { Content = "Close", IsCancel = true };
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(info);
        body.Children.Add(_list);
        body.Children.Add(ScriptWindowFrame.Buttons(_compare, _restore, _export, close));
        ScriptWindowFrame.Fill(this, body, resizable: false);
        _versions = versions;
    }

    private readonly IReadOnlyList<ScriptVersion> _versions;

    private ScriptVersion? SelectedVersion => _list.SelectedItem is ListViewItem { Tag: int n } && n > 0 ? _versions.FirstOrDefault(v => v.Version == n) : null;

    private ListViewItem Row(string title, string detail, string? note, int tag)
    {
        var text = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = detail, Style = TryFindResource("Caption") as Style });
        if (note is not null) text.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        return new ListViewItem { Content = text, Tag = tag };
    }
}

/// <summary>
/// What an import brings in, before anything is written (§4.7): every script with a check box, where it goes, and for a
/// name that exists there already: keep both, replace (a new draft of the existing script) or skip.
/// </summary>
public sealed class ImportPreviewWindow : FluentWindow
{
    private readonly ScriptImportPlan _plan;
    private readonly Dictionary<string, CheckBox> _checks = [];
    private readonly Dictionary<string, ComboBox> _choices = [];
    private readonly CheckBox _deployed = new() { Content = "Import as deployed – the package's latest versions run on devices without being deployed here again", Margin = new Thickness(0, 8, 0, 0) };

    public bool KeepDeployed => _deployed.IsChecked == true;

    /// <summary>The scripts to bring in, and what to do where a name exists.</summary>
    public (ScriptsExport Data, Dictionary<string, ImportChoice> Choices) Result()
    {
        var data = new ScriptsExport
        {
            Folders = _plan.Data.Folders,
            Scripts = _plan.Data.Scripts.Where(s => _checks.TryGetValue(s.Id, out var c) && c.IsChecked == true).ToList(),
            Versions = _plan.Data.Versions,
        };
        var choices = _choices.ToDictionary(kv => kv.Key, kv => kv.Value.SelectedIndex switch { 1 => ImportChoice.Replace, 2 => ImportChoice.Skip, _ => ImportChoice.KeepBoth });
        return (data, choices);
    }

    public ImportPreviewWindow(ScriptImportPlan plan, string? targetFolderId, string targetName)
    {
        _plan = plan;
        ScriptWindowFrame.Apply(this, "Import scripts", 720, 600);
        var folders = plan.Data.Folders.ToDictionary(f => f.Id);
        List<string> PathOf(string? folderId)
        {
            var parts = new List<string>();
            var seen = new HashSet<string>();
            for (var f = folderId; f is not null && folders.TryGetValue(f, out var info) && seen.Add(f); f = info.ParentId) parts.Insert(0, info.Name);
            return parts;
        }

        var list = new StackPanel();
        var conflicts = 0;
        foreach (var group in plan.Data.Scripts.GroupBy(s => string.Join(" › ", PathOf(s.FolderId))).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var head = new TextBlock { Text = group.Key.Length > 0 ? $"{targetName} › {group.Key}" : targetName, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
            list.Children.Add(head);
            foreach (var s in group.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var settings = s.Language == ScriptStore.PowerShell
                    ? $"PowerShell · {ScriptLabels.Context(s.Content.RunContext)} · {s.Content.Risk ?? "–"}" : ScriptLabels.Language(s.Language);
                var check = new CheckBox { IsChecked = true, Content = $"{s.Name}   ({settings})" };
                _checks[s.Id] = check;
                if (ScriptStore.FindExisting(targetFolderId, PathOf(s.FolderId), s.Name) is not null)
                {
                    conflicts++;
                    var choice = new ComboBox { Width = 130, Margin = new Thickness(8, 0, 0, 0) };
                    choice.Items.Add("Keep both");
                    choice.Items.Add("Replace");
                    choice.Items.Add("Skip");
                    choice.SelectedIndex = 0;
                    choice.ToolTip = "A script of this name is in that folder already. Replace makes the imported text a new draft of it – its versions stay.";
                    System.Windows.Automation.AutomationProperties.SetName(choice, "When the name exists: " + s.Name);
                    _choices[s.Id] = choice;
                    DockPanel.SetDock(choice, Dock.Right);
                    row.Children.Add(choice);
                }
                row.Children.Add(check);
                list.Children.Add(row);
            }
        }

        var summary = $"{plan.Data.Scripts.Count} script{(plan.Data.Scripts.Count == 1 ? "" : "s")} into {targetName}."
                      + (plan.Skipped > 0 ? $" {plan.Skipped} file{(plan.Skipped == 1 ? " was" : "s were")} left out (not .ps1 or .sql, or larger than 1 MB)." : "")
                      + " Imported scripts arrive as drafts: review them and mark them Ready to deploy before they can run on devices.";
        var top = new StackPanel();
        top.Children.Add(ScriptWindowFrame.Info(summary));
        if (conflicts > 0)
        {
            var all = new ComboBox { Width = 160 };
            all.Items.Add("Keep both");
            all.Items.Add("Replace");
            all.Items.Add("Skip");
            all.SelectedIndex = 0;
            all.SelectionChanged += (_, _) => { foreach (var c in _choices.Values) c.SelectedIndex = all.SelectedIndex; };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock { Text = $"{conflicts} name{(conflicts == 1 ? " exists" : "s exist")} already. For all of them:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(all);
            top.Children.Add(row);
        }
        var import = new Button { Content = "Import", Appearance = ControlAppearance.Primary, IsDefault = true };
        import.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var bottom = new StackPanel();
        if (plan.FromPackage && plan.Data.Versions.Count > 0) bottom.Children.Add(_deployed);
        bottom.Children.Add(ScriptWindowFrame.Buttons(import, cancel));
        var body = new DockPanel { Margin = new Thickness(20, 4, 20, 20) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(bottom);
        body.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        ScriptWindowFrame.Fill(this, body, resizable: true);
    }
}

/// <summary>How to export scripts (§4.7): as files or as a package, which text, and the package's password.</summary>
public sealed class ExportWindow : FluentWindow
{
    private readonly RadioButton _files = new() { Content = "Files – .ps1 and .sql in folders, the settings in a header line", IsChecked = true, GroupName = "format" };
    private readonly RadioButton _package = new() { Content = "Script package (.rhscripts) – folders, settings, colours and versions", GroupName = "format" };
    private readonly RadioButton _deployedText = new() { Content = "The deployed version (a script never deployed: its draft)", IsChecked = true, GroupName = "text" };
    private readonly RadioButton _draftText = new() { Content = "The draft", GroupName = "text" };
    private readonly CheckBox _protect = new() { Content = "Protect with a password", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };

    public bool AsPackage => _package.IsChecked == true;
    public bool UseDrafts => _draftText.IsChecked == true;
    public bool Protect => _protect.IsChecked == true;

    public ExportWindow(int count, bool anyDrafts)
    {
        ScriptWindowFrame.Apply(this, "Export scripts", 560);
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(ScriptWindowFrame.Info($"Export {count} script{(count == 1 ? "" : "s")}."));
        body.Children.Add(new TextBlock { Text = "Format", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        body.Children.Add(_files);
        body.Children.Add(_package);
        var text = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        text.Children.Add(new TextBlock { Text = "Text", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        text.Children.Add(_deployedText);
        text.Children.Add(_draftText);
        text.Visibility = anyDrafts ? Visibility.Visible : Visibility.Collapsed;
        body.Children.Add(text);
        var protect = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        protect.Children.Add(_protect);
        body.Children.Add(protect);
        var warning = ScriptWindowFrame.Info("Files are plain text: they aren't encrypted, wherever they are saved.");
        warning.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(warning);
        void Update()
        {
            _protect.Visibility = AsPackage ? Visibility.Visible : Visibility.Collapsed;
            text.Visibility = anyDrafts && !AsPackage ? Visibility.Visible : Visibility.Collapsed;
            warning.Text = AsPackage
                ? Protect ? "The package is encrypted with the password (AES-256)." : "Without a password the package isn't encrypted: anyone with the file can read the scripts."
                : "Files are plain text: they aren't encrypted, wherever they are saved.";
        }
        _files.Checked += (_, _) => Update();
        _package.Checked += (_, _) => Update();
        _protect.Checked += (_, _) => Update();
        _protect.Unchecked += (_, _) => Update();
        Update();
        var ok = new Button { Content = "Export…", Appearance = ControlAppearance.Primary, IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        body.Children.Add(ScriptWindowFrame.Buttons(ok, cancel));
        ScriptWindowFrame.Fill(this, body, resizable: false);
    }
}
