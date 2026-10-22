using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>
/// A script block in a note (docs/notes-view-design.md §4.5.5): a language header, Copy and a menu over an AvalonEdit
/// editor with syntax colouring and line numbers. Documentation only – nothing here runs or sends the script.
/// </summary>
public sealed class ScriptBlock : Border, INoteScriptBlock
{
    private const int MaxAutoLines = 30;
    private readonly CodeEditor _editor = new();
    private readonly Button _language = new() { Padding = new Thickness(8, 2, 6, 2), MinWidth = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBlock _languageText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _copy;
    private readonly DispatcherTimer _copiedTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private double? _manualHeight;
    private string _languageId;

    /// <summary>Languages picked recently (most recent first), shared by all blocks and kept by the Notes view.</summary>
    public static List<string> RecentLanguages { get; } = [];
    public static event Action? RecentLanguagesChanged;

    /// <summary>The code or the language changed.</summary>
    public event Action? Changed;
    /// <summary>The caret wants to leave the block: -1 above it, +1 below it (with a new paragraph when <c>newParagraph</c>).</summary>
    public event Action<ScriptBlock, int, bool>? LeaveRequested;
    public event Action<ScriptBlock>? DeleteRequested;

    public string LanguageId => _languageId;
    public string Code => _editor.Text.Replace("\r\n", "\n");

    public ScriptBlock(string language, string code)
    {
        _languageId = ScriptLanguages.Normalize(language);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        Margin = new Thickness(0, 4, 0, 10);
        SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
        SetResourceReference(BackgroundProperty, "NoteCodeBackgroundBrush");
        Cursor = Cursors.Arrow;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Child = root;

        // ---- header: language ▾ … Copy ⋯
        var header = new DockPanel { Margin = new Thickness(4, 2, 4, 0), LastChildFill = false };
        var langRow = new StackPanel { Orientation = Orientation.Horizontal };
        langRow.Children.Add(_languageText);
        langRow.Children.Add(new SymbolIcon(SymbolRegular.ChevronDown24) { FontSize = 10, Margin = new Thickness(6, 1, 0, 0) });
        _language.Content = langRow;
        _language.Click += (_, _) => ShowLanguagePicker();
        _languageText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        header.Children.Add(_language);
        var more = Tool(SymbolRegular.MoreHorizontal24, "More actions for this script block");
        more.Click += (_, _) => { var m = Menu(); m.PlacementTarget = more; m.Placement = PlacementMode.Bottom; m.IsOpen = true; };
        _copy = Tool(SymbolRegular.Copy24, "Copy script block");
        _copy.Click += (_, _) => CopyCode();
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); _copy.Content = null; _copy.Icon = new SymbolIcon(SymbolRegular.Copy24); };
        DockPanel.SetDock(more, Dock.Right);
        DockPanel.SetDock(_copy, Dock.Right);
        header.Children.Add(more);
        header.Children.Add(_copy);
        root.Children.Add(header);

        // ---- the editor
        _editor.Text = code.Replace("\r\n", "\n");
        _editor.ShowLineNumbers = true;
        _editor.FontFamily = NoteMarkdown.MonoFont;
        _editor.FontSize = 13;
        _editor.WordWrap = false;
        _editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.Background = Brushes.Transparent;
        _editor.Padding = new Thickness(4, 2, 8, 6);
        _editor.Options.ConvertTabsToSpaces = false;
        _editor.Options.EnableHyperlinks = false;      // documentation only: nothing in a script is clickable
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.HighlightCurrentLine = false;
        _editor.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "TextFillColorTertiaryBrush");
        _editor.TextArea.SelectionCornerRadius = 0;
        AutomationProperties.SetName(_editor, "Script block");
        Grid.SetRow(_editor, 1);
        root.Children.Add(_editor);
        _editor.TextChanged += (_, _) => { FitHeight(); Changed?.Invoke(); };
        _editor.PreviewKeyDown += Editor_PreviewKeyDown;

        // ---- drag the bottom edge to make it taller
        var grip = new Thumb { Height = 6, Cursor = Cursors.SizeNS, Opacity = 0 };
        grip.DragDelta += (_, e) =>
        {
            _manualHeight = Math.Max(LineHeight * 2 + 8, (_manualHeight ?? _editor.ActualHeight) + e.VerticalChange);
            _editor.Height = _manualHeight.Value;
        };
        Grid.SetRow(grip, 2);
        root.Children.Add(grip);

        ApplyLanguage();
        ScriptHighlighting.Changed += OnPaletteChanged;
        Unloaded += (_, _) => ScriptHighlighting.Changed -= OnPaletteChanged;
        Loaded += (_, _) => { ScriptHighlighting.Changed -= OnPaletteChanged; ScriptHighlighting.Changed += OnPaletteChanged; FitHeight(); };
    }

    /// <summary>Puts the caret into the script: at its start, or at its end.</summary>
    public void FocusCode(bool atEnd)
    {
        _editor.Focus();
        _editor.CaretOffset = atEnd ? _editor.Document.TextLength : 0;
        _editor.TextArea.Focus();
    }

    /// <summary>Selects the first occurrence of a word (search result highlight).</summary>
    public bool Highlight(string word)
    {
        var at = _editor.Text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0) return false;
        _editor.Select(at, word.Length);
        _editor.ScrollTo(_editor.Document.GetLineByOffset(at).LineNumber, 0);
        return true;
    }

    private double LineHeight => _editor.TextArea.TextView.DefaultLineHeight is > 0 and var h ? h : 18;

    private void FitHeight()
    {
        if (_manualHeight is not null) return;
        var lines = Math.Clamp(_editor.Document.LineCount, 1, MaxAutoLines);
        // Lines, padding and room for a horizontal scroll bar.
        _editor.Height = lines * LineHeight + 14 + (LongestLine() * 7.5 > ActualWidth - 60 ? SystemParameters.HorizontalScrollBarHeight : 0);
    }

    private int LongestLine() => _editor.Document.Lines.Select(l => l.Length).DefaultIfEmpty(0).Max();

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var caret = _editor.TextArea.Caret;
        var none = Keyboard.Modifiers == ModifierKeys.None;
        if (e.Key == Key.Up && none && caret.Line == 1) { LeaveRequested?.Invoke(this, -1, false); e.Handled = true; }
        else if (e.Key == Key.Down && none && caret.Line == _editor.Document.LineCount) { LeaveRequested?.Invoke(this, 1, false); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { LeaveRequested?.Invoke(this, 1, true); e.Handled = true; }
        else if (e.Key == Key.Escape) { LeaveRequested?.Invoke(this, 1, false); e.Handled = true; }
        else if (e.Key == Key.Back && _editor.Document.TextLength == 0) { DeleteRequested?.Invoke(this); e.Handled = true; }
    }

    private void OnPaletteChanged() => Dispatcher.BeginInvoke(() =>
    {
        _editor.SyntaxHighlighting = ScriptHighlighting.For(_languageId);
        _editor.TextArea.TextView.Redraw();
    });

    private void ApplyLanguage()
    {
        _languageText.Text = ScriptLanguages.DisplayName(_languageId) is { Length: > 0 } n ? n : "Plain text";
        _editor.SyntaxHighlighting = ScriptHighlighting.For(_languageId);
        AutomationProperties.SetName(_language, $"Language: {_languageText.Text}");
    }

    public void SetLanguage(string id)
    {
        id = ScriptLanguages.Normalize(id);
        if (id == _languageId) return;
        _languageId = id;
        ApplyLanguage();
        RecentLanguages.Remove(id);
        RecentLanguages.Insert(0, id);
        if (RecentLanguages.Count > 5) RecentLanguages.RemoveRange(5, RecentLanguages.Count - 5);
        RecentLanguagesChanged?.Invoke();
        Changed?.Invoke();
    }

    private void CopyCode()
    {
        try { Clipboard.SetText(Code.Replace("\n", Environment.NewLine)); }
        catch (System.Runtime.InteropServices.ExternalException) { return; } // clipboard busy; the user can try again
        _copy.Icon = new SymbolIcon(SymbolRegular.Checkmark24);
        _copy.Content = "Copied";
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private ContextMenu Menu()
    {
        var m = new ContextMenu();
        m.Items.Add(RemoteToolChrome.Item("Copy", SymbolRegular.Copy24, CopyCode));
        var lang = new MenuItem { Header = "Language", Icon = new SymbolIcon(SymbolRegular.Code24) };
        foreach (var group in ScriptLanguages.All.GroupBy(l => l.Group))
        {
            var g = new MenuItem { Header = group.Key };
            foreach (var l in group)
            {
                var id = l.Id;
                var item = new MenuItem { Header = l.Name, IsCheckable = true, IsChecked = id == _languageId };
                item.Click += (_, _) => SetLanguage(id);
                g.Items.Add(item);
            }
            lang.Items.Add(g);
        }
        m.Items.Add(lang);
        var wrap = new MenuItem { Header = "Wrap long lines", IsCheckable = true, IsChecked = _editor.WordWrap };
        wrap.Click += (_, _) => { _editor.WordWrap = !_editor.WordWrap; FitHeight(); };
        m.Items.Add(wrap);
        m.Items.Add(RemoteToolChrome.Item("Save as file…", SymbolRegular.Save24, SaveAs));
        // A PowerShell or SQL block can become a script in the Scripts view (scripts-view-design.md §4.7); the block stays text.
        var scriptLanguage = _languageId == "powershell" ? Data.ScriptStore.PowerShell : Data.ScriptStore.IsSql(_languageId) ? _languageId : null;
        if (scriptLanguage is not null && Features.IsAllowed(Feature.Scripts) && Data.LocalDb.IsOpen)
            m.Items.Add(RemoteToolChrome.Item("Save as script…", SymbolRegular.Code24, () => SaveAsScript(scriptLanguage)));
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Delete script block", SymbolRegular.Delete24, () => DeleteRequested?.Invoke(this)));
        return m;
    }

    /// <summary>Creates a draft script from the block's text in a folder the user picks; nothing is deployed or run.</summary>
    private void SaveAsScript(string language)
    {
        var owner = Window.GetWindow(this);
        if (Data.ScriptStore.Folders().Count == 0) Data.ScriptStore.CreateFolder(null, "Scripts", "Blue");
        var folders = Data.ScriptStore.Folders().ToDictionary(f => f.Id);
        var targets = folders.Values.Select(f => new NoteLinkTarget(f.Id, f.Name, ScriptCatalog.FolderPath(f.Id, folders), true))
            .OrderBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
        var picker = new FolderPickerWindow(targets) { Owner = owner, Title = "Save as script" };
        if (picker.ShowDialog() != true || picker.Selected is not { } folder) return;
        var name = new InputDialog("Save as script", "Name of the script", language == Data.ScriptStore.PowerShell ? "New script" : "New query") { Owner = owner };
        if (name.ShowDialog() != true) return;
        var content = ScriptHeader.Apply(Code, Data.ScriptContent.Empty(language), language == Data.ScriptStore.PowerShell);
        var id = Data.ScriptStore.CreateScript(folder, name.Value, language, content);
        Data.AuditLog.Write(Data.AuditEvents.ScriptAdded, null, id, "from a note");
    }

    private void SaveAs()
    {
        var ext = ScriptLanguages.Find(_languageId)?.Extension ?? ".txt";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = ext.StartsWith('.') ? "script" + ext : ext,
            Filter = ext.StartsWith('.') ? $"{ScriptLanguages.DisplayName(_languageId)} (*{ext})|*{ext}|All files (*.*)|*.*" : "All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(dlg.FileName, Code.Replace("\n", Environment.NewLine), new System.Text.UTF8Encoding(false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Window.GetWindow(this) is { } w) _ = Dialogs.Info(w, "Save failed", ex.Message);
        }
    }

    /// <summary>The language picker: a search box over the languages, recently used first.</summary>
    private void ShowLanguagePicker()
    {
        var search = new TextBox { PlaceholderText = "Search languages", Margin = new Thickness(6), ClearButtonEnabled = true };
        var list = new ListBox { MaxHeight = 320, Width = 280, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        var popup = new Popup { PlacementTarget = _language, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(2) };
        card.SetResourceReference(BackgroundProperty, "SidebarBackgroundBrush");
        card.SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
        var panel = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        panel.Children.Add(search);
        panel.Children.Add(list);
        card.Child = panel;
        popup.Child = card;

        void Fill()
        {
            list.Items.Clear();
            var q = search.Text.Trim();
            var recent = q.Length == 0 ? RecentLanguages.Select(ScriptLanguages.Find).OfType<ScriptLanguage>().ToList() : [];
            var matches = ScriptLanguages.All.Where(l => q.Length == 0 || l.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || l.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || l.Aliases.Any(a => a.Equals(q, StringComparison.OrdinalIgnoreCase)));
            if (recent.Count > 0) { list.Items.Add(GroupHeader("Recently used")); foreach (var l in recent) list.Items.Add(Entry(l)); }
            foreach (var g in matches.GroupBy(l => l.Group))
            {
                list.Items.Add(GroupHeader(g.Key));
                foreach (var l in g) list.Items.Add(Entry(l));
            }
        }
        ListBoxItem Entry(ScriptLanguage l) => new() { Content = l.Name, Tag = l.Id, FontWeight = l.Id == _languageId ? FontWeights.SemiBold : FontWeights.Normal };
        ListBoxItem GroupHeader(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            return new ListBoxItem { Content = t, IsEnabled = false, Focusable = false };
        }
        void Pick()
        {
            if (list.SelectedItem is ListBoxItem { Tag: string id }) SetLanguage(id);
            popup.IsOpen = false;
            _editor.TextArea.Focus();
        }
        search.TextChanged += (_, _) => Fill();
        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { list.SelectedIndex = list.Items.Cast<ListBoxItem>().ToList().FindIndex(i => i.Tag is string); list.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter) { list.SelectedItem ??= list.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag is string); Pick(); e.Handled = true; }
            else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
        };
        list.MouseUp += (_, _) => Pick();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Pick(); e.Handled = true; } };
        Fill();
        popup.Opened += (_, _) => search.Focus();
        popup.IsOpen = true;
    }

    private static Button Tool(SymbolRegular icon, string tip)
    {
        var b = new Button { Icon = new SymbolIcon(icon), ToolTip = tip, Padding = new Thickness(6, 2, 6, 2), MinWidth = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(2, 0, 0, 0) };
        AutomationProperties.SetName(b, tip);
        return b;
    }
}
