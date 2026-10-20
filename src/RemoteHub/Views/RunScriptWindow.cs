using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ListView = System.Windows.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>Where a run goes and how to reach it; the password was asked for (if at all) before the window opened.</summary>
public sealed record RunScriptContext(string ConnectionId, string ConnectionName, PsTarget Target, UiState Ui, Action SaveUi,
    ScriptEntry? Script = null, bool Draft = false);

/// <summary>
/// Runs a script on one device (docs/scripts-view-design.md §6.5): pick a deployed PowerShell script – searched,
/// filtered by favourites, recent runs, colour and folder – confirm how it runs, and follow the output, one tab per
/// result. Not modal: several runs on several devices can be open at once.
/// </summary>
public sealed class RunScriptWindow : FluentWindow
{
    private readonly RunScriptContext _ctx;
    private readonly ContentControl _page = new();
    private readonly CancellationTokenSource _cancel = new();
    private bool _running;

    // pick
    private readonly TextBox _search = new() { PlaceholderText = "Search scripts", ClearButtonEnabled = true, Icon = new SymbolIcon(SymbolRegular.Search24), Margin = new Thickness(0, 0, 0, 8) };
    private readonly System.Windows.Controls.Primitives.ToggleButton _favourites = new() { Content = "Favourites", Margin = new Thickness(0, 0, 6, 0) };
    private readonly System.Windows.Controls.Primitives.ToggleButton _recent = new() { Content = "Recently run", Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _colour = new() { MinWidth = 120, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _folder = new() { MinWidth = 180 };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<ScriptEntry> _all = [];

    public RunScriptWindow(RunScriptContext ctx)
    {
        _ctx = ctx;
        ScriptWindowFrame.Apply(this, $"Run script on {ctx.ConnectionName}", Math.Clamp(ctx.Ui.RunScriptWidth, 560, 1600), Math.Clamp(ctx.Ui.RunScriptHeight, 420, 1200));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = true;
        var body = new Border { Padding = new Thickness(20, 4, 20, 20), Child = _page };
        ScriptWindowFrame.Fill(this, body, resizable: true);
        SizeChanged += (_, _) => { if (WindowState == WindowState.Normal) { ctx.Ui.RunScriptWidth = ActualWidth; ctx.Ui.RunScriptHeight = ActualHeight; } };
        Closing += OnClosing;
        Closed += (_, _) => ctx.SaveUi();
        if (ctx.Script is { } s) _ = Confirm(s, ctx.Draft);
        else ShowPick();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_running) return;
        e.Cancel = true;
        if (!await Dialogs.Confirm(this, "Run script", "The script is still running. Cancel it and close?", "Cancel the run")) return;
        _cancel.Cancel();
        _running = false;
        Close();
    }

    // ================================================================== pick

    private void ShowPick()
    {
        try { _all = ScriptCatalog.All().Where(e => e.IsPowerShell && e.IsDeployed).ToList(); }
        catch (Exception ex)
        {
            DiagLog.Write("Scripts: listing for a run failed: " + ex.GetType().Name);
            _all = [];
        }
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        chips.Children.Add(_favourites);
        chips.Children.Add(_recent);
        chips.Children.Add(_colour);
        chips.Children.Add(_folder);
        AutomationProperties.SetName(_colour, "Filter by colour");
        AutomationProperties.SetName(_folder, "Filter by folder");
        _colour.Items.Add(new ComboBoxItem { Content = "All colours", Tag = "" });
        foreach (var tag in ColorTags.All.Where(t => _all.Any(e => e.Color == t.Id)))
            _colour.Items.Add(new ComboBoxItem { Content = ScriptsView.Swatched(tag.Label.Split(' ')[0], ColorTags.Frozen(tag.Color)), Tag = tag.Id });
        _colour.SelectedIndex = 0;
        _folder.Items.Add(new ComboBoxItem { Content = "All folders", Tag = "" });
        foreach (var path in _all.Select(e => e.IsLibrary ? e.FolderPath : "My scripts › " + e.FolderPath).Distinct().Order(StringComparer.CurrentCultureIgnoreCase))
            _folder.Items.Add(new ComboBoxItem { Content = path, Tag = path });
        _folder.SelectedIndex = 0;
        var info = ScriptWindowFrame.Info("Deployed PowerShell scripts. Enter or a double-click chooses one; how it runs is confirmed next.");
        var choose = new Button { Content = "Next", Appearance = ControlAppearance.Primary, IsDefault = true };
        choose.Click += (_, _) => Choose();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        var top = new StackPanel();
        top.Children.Add(info);
        top.Children.Add(_search);
        top.Children.Add(chips);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        var buttons = ScriptWindowFrame.Buttons(choose, cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        AutomationProperties.SetName(_list, "Scripts");
        dock.Children.Add(_list);
        _page.Content = dock;

        _search.TextChanged += (_, _) => FillList();
        _favourites.Click += (_, _) => FillList();
        _recent.Click += (_, _) => FillList();
        _colour.SelectionChanged += (_, _) => FillList();
        _folder.SelectionChanged += (_, _) => FillList();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = 0; _list.Focus(); e.Handled = true; } };
        _list.MouseDoubleClick += (_, _) => Choose();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Choose(); e.Handled = true; } };
        Loaded += (_, _) => _search.Focus();
        FillList();
    }

    private void FillList()
    {
        IEnumerable<ScriptEntry> list = _all;
        var text = _search.Text.Trim();
        if (text.Length > 0)
        {
            var hits = ScriptCatalog.Search(text).Select(e => e.Key).ToHashSet();
            list = list.Where(e => hits.Contains(e.Key));
        }
        if (_favourites.IsChecked == true) list = list.Where(e => _ctx.Ui.FavoriteScripts.Contains(e.Key));
        if (_recent.IsChecked == true)
        {
            var recent = ScriptStore.RecentlyRun().Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            list = list.Where(e => recent.ContainsKey(e.Key)).OrderBy(e => recent[e.Key]);
        }
        if ((_colour.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } colour) list = list.Where(e => e.Color == colour);
        if ((_folder.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } folder)
            list = list.Where(e => (e.IsLibrary ? e.FolderPath : "My scripts › " + e.FolderPath) == folder);
        if (_recent.IsChecked != true) list = list.OrderByDescending(e => _ctx.Ui.FavoriteScripts.Contains(e.Key)).ThenBy(e => e.IsLibrary).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
        _list.Items.Clear();
        foreach (var e in list.Take(500)) _list.Items.Add(PickRow(e));
        if (_list.Items.Count == 0)
        {
            var hint = ScriptWindowFrame.Info(_all.Count == 0 ? "There are no deployed PowerShell scripts yet: mark a script Ready to deploy in the Scripts view." : "No script matches.");
            _list.Items.Add(new ListBoxItem { Content = hint, IsEnabled = false });
        }
        else _list.SelectedIndex = 0;
    }

    private ListBoxItem PickRow(ScriptEntry e)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top, Background = ColorTags.Brush(e.Color) ?? Brushes.Transparent };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        var right = new TextBlock { Text = $"{ScriptLabels.Context(e.RunContext)} · {e.Risk}", Style = TryFindResource("Caption") as Style, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        if (_ctx.Ui.FavoriteScripts.Contains(e.Key))
        {
            var star = new SymbolIcon(SymbolRegular.Star24) { FontSize = 12, Filled = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 3, 0, 0), ToolTip = "In Favourites" };
            star.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            DockPanel.SetDock(star, Dock.Right);
            row.Children.Add(star);
        }
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = e.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = $"{(e.IsLibrary ? e.FolderPath : "My scripts › " + e.FolderPath)} · {e.VersionLabel}", Style = TryFindResource("Caption") as Style, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);
        var item = new ListBoxItem { Content = row, Tag = e };
        AutomationProperties.SetName(item, e.Name);
        var menu = new ContextMenu();
        var pinned = _ctx.Ui.FavoriteScripts.Contains(e.Key);
        menu.Items.Add(RemoteToolChrome.Item(pinned ? "Remove from favourites" : "Add to favourites", pinned ? SymbolRegular.StarOff24 : SymbolRegular.Star24, () =>
        {
            if (pinned) _ctx.Ui.FavoriteScripts.Remove(e.Key); else _ctx.Ui.FavoriteScripts.Add(e.Key);
            _ctx.SaveUi();
            FillList();
        }));
        item.ContextMenu = menu;
        return item;
    }

    private void Choose()
    {
        if (_list.SelectedItem is ListBoxItem { Tag: ScriptEntry e }) _ = Confirm(e, draft: false);
    }

    // ================================================================== confirm

    private async Task Confirm(ScriptEntry entry, bool draft)
    {
        var content = draft && entry.Own is { } own ? ScriptStore.Draft(own.Id) : entry.DeployedContent();
        if (content is null) { await Dialogs.Info(this, "Run script", "This script has no deployed version yet."); Close(); return; }
        var version = draft ? null : entry.DeployedVersion;
        var contexts = ScriptStore.Contexts.Where(Allowed).ToList();
        if (contexts.Count == 0) { await Dialogs.Info(this, "Run script", Features.WhyNot(Feature.ScriptRunSystem) ?? Features.TurnedOff); Close(); return; }

        var panel = new StackPanel();
        if (draft)
        {
            var warn = new TextBlock { Text = "This is a draft, not a deployed version. It runs once, on this device, and is marked as a draft in the run history and the audit log.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "ScriptWarningBrush");
            panel.Children.Add(warn);
        }
        else if (entry.HasDraftChanges)
            panel.Children.Add(ScriptWindowFrame.Info($"The draft has changes that aren't deployed – version {entry.DeployedVersion} runs."));
        panel.Children.Add(Field("Script", entry.Name + (entry.CatalogId is { } id ? $"  ({id})" : "")));
        panel.Children.Add(Field("Version", draft ? "Draft" : entry.VersionLabel));
        panel.Children.Add(Field("Device", $"{_ctx.ConnectionName} – {_ctx.Target.Transport}"));
        panel.Children.Add(Field("Risk", ScriptLabels.Risk(content.Risk) + " · for " + ScriptLabels.Os(content.OsScope).ToLowerInvariant() + " Windows"));
        panel.Children.Add(Field("Timeout", ScriptLabels.Timeout(content.TimeoutSec ?? ScriptStore.DefaultTimeoutSec)));

        var context = new ComboBox { MinWidth = 200 };
        foreach (var c in contexts) context.Items.Add(new ComboBoxItem { Content = ContextText(c), Tag = c });
        context.SelectedItem = context.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == content.RunContext) ?? context.Items[0];
        AutomationProperties.SetName(context, "Runs as");
        panel.Children.Add(Labelled("Runs as", context));
        var contextNote = ScriptWindowFrame.Info("");
        panel.Children.Add(contextNote);

        var users = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var usersStatus = ScriptWindowFrame.Info("Asking the device who is signed in…");
        users.Children.Add(usersStatus);
        panel.Children.Add(users);
        var deviceNote = ScriptWindowFrame.Info("");
        panel.Children.Add(deviceNote);

        // Phase 4: the script's parameters as a form; values go to the script by name.
        var parameters = PsCompletion.Parameters(content.Body);
        var inputs = new Dictionary<string, FrameworkElement>();
        if (parameters.Count > 0)
        {
            var form = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            form.Children.Add(new TextBlock { Text = "Parameters", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            foreach (var p in parameters) form.Children.Add(ParameterField(p, inputs));
            panel.Children.Add(form);
        }

        TextBox? typed = null;
        if (content.Risk == ScriptStore.RiskR2)
        {
            var warn = new TextBlock { Text = "This script can restart the device, stop services or delete data. Type the device's name to run it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "ScriptWarningBrush");
            panel.Children.Add(warn);
            typed = new TextBox { PlaceholderText = _ctx.ConnectionName };
            AutomationProperties.SetName(typed, "Device name to confirm");
            panel.Children.Add(typed);
        }

        var run = new Button { Content = "Run", Appearance = ControlAppearance.Primary, IsDefault = true, Icon = new SymbolIcon(SymbolRegular.Play24) };
        var back = new Button { Content = _ctx.Script is null ? "Back" : "Cancel", IsCancel = true };
        back.Click += (_, _) => { if (_ctx.Script is null) ShowPick(); else Close(); };
        var dock = new DockPanel();
        var buttons = ScriptWindowFrame.Buttons(run, back);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _page.Content = dock;

        DeviceInfo? info = null;
        var userChecks = new List<(CheckBox Box, DeviceUser User)>();
        void UpdateContext()
        {
            var c = (context.SelectedItem as ComboBoxItem)?.Tag as string;
            users.Visibility = c is ScriptStore.ContextUser or ScriptStore.ContextBoth ? Visibility.Visible : Visibility.Collapsed;
            contextNote.Text = c switch
            {
                ScriptStore.ContextSystem => "As NT AUTHORITY\\SYSTEM: machine-wide, no desktop. Needs an administrator account on the device.",
                ScriptStore.ContextUser => "In the signed-in user's session, as that user. Needs an administrator account on the device.",
                ScriptStore.ContextBoth => "As System first, then as the signed-in user – two results.",
                ScriptStore.ContextAsMe => "As the account RemoteHub signs in with. It can't reach other computers from there (second hop): run it on the server it works on.",
                _ => "",
            };
            if (info is { IsAdmin: false } && c is not ScriptStore.ContextAsMe)
                contextNote.Text += " The account isn't an administrator on this device.";
        }
        context.SelectionChanged += (_, _) => UpdateContext();
        UpdateContext();
        run.IsEnabled = typed is null;
        if (typed is not null) typed.TextChanged += (_, _) => run.IsEnabled = string.Equals(typed.Text.Trim(), _ctx.ConnectionName, StringComparison.OrdinalIgnoreCase);

        run.Click += async (_, _) =>
        {
            var c = (string)((ComboBoxItem)context.SelectedItem).Tag;
            var chosen = userChecks.Where(u => u.Box.IsChecked == true).Select(u => u.User.User).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (c is ScriptStore.ContextUser && chosen.Count == 0) { await Dialogs.Info(this, "Run script", "No user is signed in on the device, or none is ticked."); return; }
            if (draft && entry.IsPowerShell && PsCompletion.Errors(content.Body) is { Count: > 0 } errs
                && !await Dialogs.Confirm(this, "Run script", $"The draft has {(errs.Count == 1 ? "a syntax error" : $"{errs.Count} syntax errors")} (line {errs[0].Line}). Run anyway?", "Run anyway")) return;
            var values = inputs.ToDictionary(kv => kv.Key, kv => kv.Value switch
            {
                CheckBox b => (b.IsChecked == true).ToString(),
                ComboBox box => (box.SelectedItem as ComboBoxItem)?.Content as string ?? "",
                System.Windows.Controls.TextBox t => t.Text,
                _ => "",
            });
            var missing = parameters.FirstOrDefault(p => p.Mandatory && string.IsNullOrWhiteSpace(values.GetValueOrDefault(p.Name)));
            if (missing is not null) { await Dialogs.Info(this, "Run script", $"The parameter {missing.Name} needs a value."); return; }
            var args = PsCompletion.Arguments(parameters, values);
            var runs = new List<(string Context, string? User)>();
            if (c is ScriptStore.ContextSystem or ScriptStore.ContextBoth) runs.Add((ScriptStore.ContextSystem, null));
            if (c is ScriptStore.ContextUser or ScriptStore.ContextBoth)
            {
                if (chosen.Count == 0) runs.Add((ScriptStore.ContextUser, null)); // reported as "no user" by the device
                runs.AddRange(chosen.Select(u => (ScriptStore.ContextUser, (string?)u)));
            }
            if (c is ScriptStore.ContextAsMe) runs.Add((ScriptStore.ContextAsMe, null));
            await Run(entry, version, content, runs, args, draft);
        };

        // Who is signed in, and client or server: asked from the device while the summary shows.
        try { info = await Task.Run(() => ScriptRunner.Info(_ctx.Target)); }
        catch (Exception ex)
        {
            usersStatus.Text = "The device couldn't be asked who is signed in: " + PsRemoting.Explain(ex, _ctx.Target);
            return;
        }
        users.Children.Clear();
        if (info.Users.Count == 0) users.Children.Add(ScriptWindowFrame.Info("No user is signed in on the device."));
        else
        {
            users.Children.Add(new TextBlock { Text = "Run as these users", Margin = new Thickness(0, 0, 0, 4) });
            foreach (var u in info.Users.OrderByDescending(u => u.IsConsole).ThenBy(u => u.User, StringComparer.OrdinalIgnoreCase))
            {
                var box = new CheckBox { Content = u.User + (u.IsConsole ? "  (console)" : $"  (session {u.SessionId})"), IsChecked = u.IsConsole, Margin = new Thickness(0, 1, 0, 1) };
                userChecks.Add((box, u));
                users.Children.Add(box);
            }
        }
        if (info.IsServer is { } server && content.OsScope is { } scope && scope != ScriptStore.OsBoth && (scope == ScriptStore.OsServer) != server)
        {
            deviceNote.Text = $"This script is for {ScriptLabels.Os(scope).ToLowerInvariant()} Windows; the device runs {info.Caption}.";
            deviceNote.SetResourceReference(TextBlock.ForegroundProperty, "ScriptWarningBrush");
        }
        UpdateContext();
    }

    private static bool Allowed(string context) => context switch
    {
        ScriptStore.ContextSystem => Features.IsAllowed(Feature.ScriptRunSystem),
        ScriptStore.ContextUser => Features.IsAllowed(Feature.ScriptRunUser),
        ScriptStore.ContextBoth => Features.IsAllowed(Feature.ScriptRunSystem) && Features.IsAllowed(Feature.ScriptRunUser),
        ScriptStore.ContextAsMe => Features.IsAllowed(Feature.ScriptRunAsMe),
        _ => false,
    };

    private static string ContextText(string c) => c switch
    {
        ScriptStore.ContextSystem => "System",
        ScriptStore.ContextUser => "User (signed in on the device)",
        ScriptStore.ContextBoth => "Both (System, then User)",
        ScriptStore.ContextAsMe => "As me (the connection's account)",
        _ => c,
    };

    private FrameworkElement Field(string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var l = new TextBlock { Text = label, Width = 90 };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        DockPanel.SetDock(l, Dock.Left);
        row.Children.Add(l);
        row.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
        return row;
    }

    private static FrameworkElement Labelled(string label, FrameworkElement control)
    {
        var p = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) };
        caption.SetResourceReference(StyleProperty, "Caption");
        p.Children.Add(caption);
        p.Children.Add(control);
        return p;
    }

    private static FrameworkElement ParameterField(ScriptParameter p, Dictionary<string, FrameworkElement> inputs)
    {
        FrameworkElement input;
        if (p.Type is "switch" or "bool")
            input = new CheckBox { Content = p.Name, IsChecked = p.DefaultText is "true" or "$true" or "True" };
        else if (p.Choices.Count > 0)
        {
            var box = new ComboBox { MinWidth = 200 };
            foreach (var c in p.Choices) box.Items.Add(new ComboBoxItem { Content = c });
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Content, p.DefaultText, StringComparison.OrdinalIgnoreCase)) ?? (p.Mandatory ? null : box.Items[0]);
            input = box;
        }
        else
            input = new TextBox
            {
                Text = p.DefaultText is { } d && !d.StartsWith('$') && !d.StartsWith('@') && !d.StartsWith('(') ? d.Trim('\'', '"') : "",
                PlaceholderText = p.Type == "string[]" ? "One value per line, or separated by commas" : p.DefaultText ?? "",
                AcceptsReturn = p.Type == "string[]",
            };
        inputs[p.Name] = input;
        AutomationProperties.SetName(input, p.Name);
        if (input is CheckBox) return new Border { Child = input, Margin = new Thickness(0, 2, 0, 2), ToolTip = p.Help };
        var row = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
        row.Children.Add(new TextBlock { Text = p.Name + (p.Mandatory ? " (required)" : "") + (p.Type is "int" ? " – a number" : ""), Margin = new Thickness(0, 0, 0, 2), ToolTip = p.Help });
        row.Children.Add(input);
        return row;
    }

    // ================================================================== run

    private async Task Run(ScriptEntry entry, int? version, ScriptContent content, List<(string Context, string? User)> runs,
        IReadOnlyDictionary<string, object?> args, bool draft)
    {
        _running = true;
        var tabs = new TabControl();
        var cancel = new Button { Content = "Cancel", Appearance = ControlAppearance.Danger };
        var copy = new Button { Content = "Copy output", IsEnabled = false };
        var save = new Button { Content = "Save output…", IsEnabled = false };
        var again = new Button { Content = "Run again", IsEnabled = false };
        var close = new Button { Content = "Close", IsCancel = true };
        cancel.Click += (_, _) => _cancel.Cancel();
        close.Click += (_, _) => Close();
        var outputs = new Dictionary<TabItem, TextEditor>();
        copy.Click += (_, _) => { if (tabs.SelectedItem is TabItem t && outputs.TryGetValue(t, out var ed)) NoteEditor.SetClipboardText(ed.Text); };
        save.Click += (_, _) =>
        {
            if (tabs.SelectedItem is not TabItem t || !outputs.TryGetValue(t, out var ed)) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = ScriptTransfer.SafeName($"{entry.Name} on {_ctx.ConnectionName}") + ".txt", Filter = "Text (*.txt)|*.txt" };
            if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, ed.Text, new System.Text.UTF8Encoding(true));
        };
        again.Click += (_, _) => _ = Confirm(entry, draft);
        var head = ScriptWindowFrame.Info($"{entry.Name} · {(draft ? "Draft" : entry.VersionLabel)} on {_ctx.ConnectionName}");
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        var buttons = ScriptWindowFrame.Buttons(cancel, copy, save, again, close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(tabs);
        _page.Content = dock;

        var hash = content.Sha256();
        foreach (var (context, user) in runs)
        {
            var output = new TextEditor
            {
                IsReadOnly = true, FontFamily = NoteMarkdown.MonoFont, FontSize = 12.5, WordWrap = false, ShowLineNumbers = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent,
                Padding = new Thickness(6),
            };
            output.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            ICSharpCode.AvalonEdit.Search.SearchPanel.Install(output.TextArea);
            AutomationProperties.SetName(output, "Output");
            var state = new TextBlock { Text = "Starting…", Margin = new Thickness(0, 6, 0, 6) };
            var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = output };
            frame.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
            frame.SetResourceReference(Border.BackgroundProperty, "NoteCodeBackgroundBrush");
            var content2 = new DockPanel();
            DockPanel.SetDock(state, Dock.Top);
            content2.Children.Add(state);
            content2.Children.Add(frame);
            var tab = new TabItem { Header = context == ScriptStore.ContextUser ? user ?? "User" : ScriptLabels.Context(context), Content = content2 };
            outputs[tab] = output;
            tabs.Items.Add(tab);
            tabs.SelectedItem = tab;

            if (_cancel.IsCancellationRequested) { state.Text = "Cancelled."; continue; }
            var request = new ScriptRunRequest(_ctx.Target, _ctx.ConnectionId, entry.Key, version, content, context, user, args.Count > 0 ? args : null);
            var runner = new ScriptRunner(request);
            var started = DateTime.UtcNow;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => state.Text = $"Running… {Elapsed(DateTime.UtcNow - started)}";
            timer.Start();
            runner.OutputReceived += text => Dispatcher.BeginInvoke(() => { output.AppendText(text); output.ScrollToEnd(); });
            ScriptRunResult result;
            try { result = await runner.RunAsync(_cancel.Token); }
            catch (Exception ex)
            {
                DiagLog.Write("Scripts: the run failed: " + ex.GetType().Name);
                result = new ScriptRunResult(ScriptRunState.Failed, null, "The run failed: " + ex.GetType().Name, false, DateTime.UtcNow - started);
            }
            finally { timer.Stop(); }
            state.Text = $"{result.Message}  ·  {Elapsed(result.Duration)}" + (result.ExitCode is { } code && result.State is not (ScriptRunState.Succeeded or ScriptRunState.SucceededRestart) ? $"  ·  exit code {code}" : "");
            state.SetResourceReference(TextBlock.ForegroundProperty, result.State switch
            {
                ScriptRunState.Succeeded or ScriptRunState.SucceededRestart => "TextFillColorPrimaryBrush",
                ScriptRunState.Cancelled or ScriptRunState.TimedOut or ScriptRunState.ConnectionLost => "ScriptWarningBrush",
                _ => "ScriptErrorBrush",
            });
            tab.Header = $"{tab.Header} – {Short(result.State)}";
            Record(runner, entry, version, hash, context, user, result);
        }
        _running = false;
        cancel.Visibility = Visibility.Collapsed;
        copy.IsEnabled = save.IsEnabled = again.IsEnabled = true;
    }

    private void Record(ScriptRunner runner, ScriptEntry entry, int? version, byte[] hash, string context, string? user, ScriptRunResult result)
    {
        try
        {
            ScriptStore.AddRun(new ScriptRunRecord(runner.RunId, entry.Key, version, hash, _ctx.ConnectionId, context, user,
                _ctx.Target.Agent is null ? "WinRM" : "Agent", runner.StartedUtc == default ? DateTime.UtcNow : runner.StartedUtc,
                (long)result.Duration.TotalMilliseconds, result.State.ToString(), result.ExitCode, runner.Output), _ctx.Ui.KeepScriptRunOutput);
        }
        catch (Exception ex) { DiagLog.Write("Scripts: the run history couldn't be written: " + ex.GetType().Name); }
        // Ids, version and hash only – never the script's name, text or output (§10).
        AuditLog.Write(AuditEvents.ScriptRun, _ctx.ConnectionId, _ctx.ConnectionName,
            $"Script {entry.Key}, {(version is { } v ? $"version {v}" : "draft")}, SHA-256 {Convert.ToHexString(hash)}, {context}{(user is null ? "" : " (a signed-in user)")}, " +
            $"{(_ctx.Target.Agent is null ? "WinRM" : "agent")}: {result.State}{(result.ExitCode is { } code ? $", exit code {code}" : "")}");
        DiagLog.Write($"Scripts: run finished, {result.State}, {result.Duration.TotalSeconds:0.0} s");
    }

    private static string Short(ScriptRunState s) => s switch
    {
        ScriptRunState.Succeeded => "succeeded",
        ScriptRunState.SucceededRestart => "restart needed",
        ScriptRunState.Failed => "failed",
        ScriptRunState.TimedOut => "timed out",
        ScriptRunState.Cancelled => "cancelled",
        ScriptRunState.ConnectionLost => "connection lost",
        ScriptRunState.Refused => "refused",
        _ => "running",
    };

    private static string Elapsed(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}

/// <summary>The runs of a script, or of every script on a connection, with their output where it is kept (§6.6).</summary>
public sealed class ScriptRunHistoryWindow : FluentWindow
{
    private readonly ListView _list = new() { Height = 220, SelectionMode = SelectionMode.Single, BorderThickness = new Thickness(0) };
    private readonly TextEditor _output = new()
    {
        IsReadOnly = true, FontFamily = NoteMarkdown.MonoFont, FontSize = 12.5, WordWrap = false, Background = Brushes.Transparent,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6),
    };
    private readonly Button _compare = new() { Content = "Compare with latest", IsEnabled = false };

    public ScriptRunHistoryWindow(string? scriptKey, string title, string? nodeId, Func<string, string?> connectionName, UiState ui, Action saveUi)
    {
        ScriptWindowFrame.Apply(this, "Run history – " + title, 860, 600);
        _output.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        IReadOnlyList<ScriptRunRecord> runs;
        try { runs = ScriptStore.Runs(scriptKey, nodeId); }
        catch (Exception ex) { DiagLog.Write("Scripts: reading the run history failed: " + ex.GetType().Name); runs = []; }
        foreach (var r in runs)
        {
            var script = scriptKey is null ? ScriptCatalog.Find(r.ScriptId)?.Name ?? "A deleted script" : null;
            var device = connectionName(r.NodeId) ?? "A deleted connection";
            var row = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            row.Children.Add(new TextBlock { Text = $"{r.StartedUtc.ToLocalTime():g} · {(script ?? device)} · {ScriptLabels.Context(r.Context)}{(r.RunUser is { } u ? $" ({u})" : "")}" });
            var kept = r.Version is { } v && !ScriptLibrary.IsLibraryId(r.ScriptId) && ScriptStore.Version(r.ScriptId, v) is null ? " (no longer kept)" : "";
            row.Children.Add(new TextBlock
            {
                Text = $"{(r.Version is { } ver ? (ScriptLibrary.IsLibraryId(r.ScriptId) ? "Library" : $"Version {ver}{kept}") : "Draft")} · {r.Transport} · {r.State}" +
                       (r.ExitCode is { } code ? $" · exit code {code}" : "") + (r.DurationMs is { } ms ? $" · {TimeSpan.FromMilliseconds(ms).TotalSeconds:0.0} s" : "") +
                       (scriptKey is null ? "" : $" · {device}"),
                Style = TryFindResource("Caption") as Style,
            });
            _list.Items.Add(new ListViewItem { Content = row, Tag = r });
        }
        if (runs.Count == 0) _list.Items.Add(new ListViewItem { Content = ScriptWindowFrame.Info("No runs yet."), IsEnabled = false });
        _list.SelectionChanged += (_, _) =>
        {
            var r = (_list.SelectedItem as ListViewItem)?.Tag as ScriptRunRecord;
            _output.Text = r?.Output ?? (r is null ? "" : "The output of this run isn't kept (only for the last runs of a script, at most 30 days, and when Settings → Scripts keeps it).");
            _compare.IsEnabled = r is { Version: { } v } && !ScriptLibrary.IsLibraryId(r.ScriptId) && ScriptStore.Version(r.ScriptId, v) is not null
                                 && ScriptStore.Deployed(r.ScriptId) is { } latest && latest.Version != v;
        };
        _compare.Click += (_, _) =>
        {
            if ((_list.SelectedItem as ListViewItem)?.Tag is not ScriptRunRecord { Version: { } v } r) return;
            if (ScriptStore.Script(r.ScriptId) is not { } s || ScriptStore.Version(r.ScriptId, v) is not { } ran || ScriptStore.Deployed(r.ScriptId) is not { } latest) return;
            new CompareWindow(s.Name, s.Language, [($"Version {ran.Version} (this run)", ran.Content), ($"Version {latest.Version} (latest)", latest.Content)], 0, 1, ui, saveUi) { Owner = this }.ShowDialog();
        };
        var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = _output, Margin = new Thickness(0, 8, 0, 0) };
        frame.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "NoteCodeBackgroundBrush");
        var close = new Button { Content = "Close", IsCancel = true };
        var dock = new DockPanel { Margin = new Thickness(20, 4, 20, 20) };
        var top = new StackPanel();
        top.Children.Add(ScriptWindowFrame.Info("Runs from this PC, newest first. Output is kept for the last runs of each script, for at most 30 days."));
        top.Children.Add(_list);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        var buttons = ScriptWindowFrame.Buttons(_compare, close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(frame);
        ScriptWindowFrame.Fill(this, dock, resizable: true);
    }
}
