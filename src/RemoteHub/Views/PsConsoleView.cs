using System.ComponentModel;
using System.Management.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using RichTextBox = System.Windows.Controls.RichTextBox;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace RemoteHub.Views;

/// <summary>Tab content of a PowerShell remoting session: coloured output, an input line, and a status overlay.</summary>
public sealed class PsConsoleView : Border, IRecordableView
{
    public FrameworkElement RecordingArea { get; }

    private const int MaxInlines = 12000;
    private static readonly Brush ConsoleBackground = ColorTags.Frozen(Color.FromRgb(0x0C, 0x0F, 0x15));
    private static readonly Brush DefaultText = ColorTags.Frozen(Color.FromRgb(0xD6, 0xDB, 0xE4));
    private static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas, Courier New");

    private readonly PsSession _session;
    private readonly RichTextBox _output;
    private readonly Paragraph _para = new() { Margin = new Thickness(0) };
    private readonly TextBox _input;
    private readonly Border _headerStripe = new() { Width = 4, Height = 18, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
    private int _historyIndex;
    // ---- completion list state
    private CompletionSet? _completion;      // last result from the remote computer
    private string _cBase = "";              // input text the replacement applies to
    private int _cLength;                    // characters replaced at ReplacementIndex
    private bool _applying;                  // we are changing the text ourselves
    private System.Windows.Threading.DispatcherTimer? _autoTimer;
    private readonly TextBlock _highlight = new() { TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false };
    private readonly System.Windows.Controls.Primitives.Popup _popup = new()
    {
        Placement = System.Windows.Controls.Primitives.PlacementMode.Top, StaysOpen = false, AllowsTransparency = true,
    };
    private readonly ListBox _matches = new() { MaxHeight = 260, MinWidth = 280, BorderThickness = new Thickness(0) };

    /// <summary>Files dropped on the console or picked with the upload button: upload them to the current remote folder.</summary>
    public event Action<IReadOnlyList<string>>? UploadRequested;
    public event Action? UploadPickRequested;

    public PsConsoleView(PsSession session, Action reconnect, Action close)
    {
        _session = session;
        DataContext = session;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(2);
        Background = ConsoleBackground;
        AllowDrop = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Child = root;

        // ---- header
        var header = new Border { CornerRadius = new CornerRadius(6, 6, 0, 0), Padding = new Thickness(12, 4, 6, 4) };
        header.SetResourceReference(Border.BackgroundProperty, "SessionHeaderBrush");
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, nameof(PsSession.Title));
        var kind = new TextBlock { Text = "  ·  PowerShell  ·  " + session.Connection.Host, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_headerStripe);
        left.Children.Add(new SymbolIcon(SymbolRegular.WindowConsole20) { FontSize = 15, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(title);
        left.Children.Add(kind);
        left.Children.Add(RemoteToolChrome.TransportBadge());
        left.Children.Add(RemoteToolChrome.RecordingBadge());

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(HeaderButton(SymbolRegular.FolderSwap24, "File transfer", "Open a file transfer tab at the current remote folder (or drop files here to upload them)", () => UploadPickRequested?.Invoke()));
        tools.Children.Add(HeaderButton(SymbolRegular.Stop24, null, "Stop the running command (Ctrl+C)", session.Stop));
        tools.Children.Add(HeaderButton(SymbolRegular.Broom24, null, "Clear the screen (Ctrl+L)", Clear));
        var headerRow = new DockPanel();
        DockPanel.SetDock(tools, Dock.Right);
        headerRow.Children.Add(tools);
        headerRow.Children.Add(left);
        header.Child = headerRow;
        root.Children.Add(header);

        // ---- console
        var content = new Grid();
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        RecordingArea = content;

        var console = new Grid { Margin = new Thickness(10, 6, 4, 8) };
        console.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        console.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        console.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(console);

        _output = new RichTextBox
        {
            IsReadOnly = true, IsReadOnlyCaretVisible = false, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = DefaultText, FontFamily = Mono, FontSize = 13, Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = Mono, FontSize = 13 },
            ContextMenu = OutputMenu(),
        };
        _output.Document.Blocks.Add(_para);
        _output.Resources[typeof(Paragraph)] = new Style(typeof(Paragraph)) { Setters = { new Setter(Block.MarginProperty, new Thickness(0)) } };
        _output.PreviewTextInput += (_, e) => { FocusInput(); _input!.SelectedText = e.Text; _input.CaretIndex = _input.Text.Length; e.Handled = true; };
        _output.PreviewKeyDown += Output_PreviewKeyDown;
        console.Children.Add(_output);

        var progress = new DockPanel { Margin = new Thickness(0, 4, 8, 2) };
        progress.SetBinding(VisibilityProperty, new Binding(nameof(PsSession.ProgressText)) { Converter = new BoolToVisibility() });
        var bar = new ProgressBar { Width = 160, Height = 4, Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        bar.SetBinding(RangeBase_ValueProperty, nameof(PsSession.ProgressPercent));
        bar.SetBinding(ProgressBar.IsIndeterminateProperty, new Binding(nameof(PsSession.ProgressPercent)) { Converter = new NegativeToTrue() });
        var ptext = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.LightSkyBlue };
        ptext.SetBinding(TextBlock.TextProperty, nameof(PsSession.ProgressText));
        DockPanel.SetDock(bar, Dock.Left);
        progress.Children.Add(bar);
        progress.Children.Add(ptext);
        Grid.SetRow(progress, 1);
        console.Children.Add(progress);

        var inputRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var prompt = new TextBlock { FontFamily = Mono, FontSize = 13, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        prompt.SetBinding(TextBlock.TextProperty, new MultiBinding
        {
            Converter = new PromptConverter(),
            Bindings = { new Binding(nameof(PsSession.PromptText)), new Binding(nameof(PsSession.IsBusy)), new Binding(nameof(PsSession.IsReadingInput)) },
        });
        DockPanel.SetDock(prompt, Dock.Left);
        // The text box draws only caret and selection; the coloured copy of its text lies underneath (syntax colours).
        _input = new TextBox
        {
            FontFamily = Mono, FontSize = 13, Foreground = Brushes.Transparent, CaretBrush = Brushes.White, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), AcceptsTab = false, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap, SelectionBrush = Brushes.SteelBlue, SelectionOpacity = 0.45,
        };
        _input.Style = new Style(typeof(TextBox)); // plain console line, not the Fluent text box
        _highlight.FontFamily = Mono;
        _highlight.FontSize = 13;
        _highlight.Padding = new Thickness(2, 0, 2, 0); // the text box's inner margin
        _highlight.VerticalAlignment = VerticalAlignment.Center;
        _input.TextChanged += (_, _) => { Highlight(); if (!_applying) OnUserTyped(); };
        _input.PreviewTextInput += (_, e) => { if (e.Text is "-" or "." or "\\" or ":") ScheduleAutoComplete(); };
        BuildPopup();
        _input.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.None);
        _input.PreviewKeyDown += Input_PreviewKeyDown;
        _input.GotKeyboardFocus += (_, _) => session.HasKeyboardFocus = true;
        _input.LostKeyboardFocus += (_, _) => session.HasKeyboardFocus = false;
        inputRow.Children.Add(prompt);
        var inputCell = new Grid();
        inputCell.Children.Add(_highlight);
        inputCell.Children.Add(_input);
        inputRow.Children.Add(inputCell);
        _popup.PlacementTarget = inputCell;
        Grid.SetRow(inputRow, 2);
        console.Children.Add(inputRow);

        content.Children.Add(BuildOverlay(session, reconnect, close));

        session.Output += Append;
        session.ClearRequested += Clear;
        session.FocusRequested += FocusInput;
        session.PropertyChanged += OnSessionChanged;
        SizeChanged += (_, _) => UpdateWidth();
        DragOver += (_, e) => { e.Effects = Files(e).Length > 0 && session.IsConnected ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { var f = Files(e); if (f.Length > 0 && session.IsConnected) UploadRequested?.Invoke(f); e.Handled = true; };
        MouseLeftButtonUp += (_, _) => { if (_output.Selection.IsEmpty) FocusInput(); };
        UpdateFrame();
    }

    public PsSession Session => _session;

    private static readonly DependencyProperty RangeBase_ValueProperty = System.Windows.Controls.Primitives.RangeBase.ValueProperty;

    private static string[] Files(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] f ? f : [];

    private static Button HeaderButton(SymbolRegular icon, string? text, string tip, Action action)
    {
        var b = new Button { Icon = new SymbolIcon(icon), ToolTip = tip, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
        if (text is not null) b.Content = text;
        b.Click += (_, _) => action();
        return b;
    }

    private ContextMenu OutputMenu()
    {
        var copy = new System.Windows.Controls.MenuItem { Header = "Copy", InputGestureText = "Ctrl+C", Command = ApplicationCommands.Copy };
        var all = new System.Windows.Controls.MenuItem { Header = "Select all", Command = ApplicationCommands.SelectAll };
        var clear = new System.Windows.Controls.MenuItem { Header = "Clear screen", InputGestureText = "Ctrl+L" };
        clear.Click += (_, _) => Clear();
        return new ContextMenu { Items = { copy, all, new Separator(), clear } };
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PsSession.HasKeyboardFocus) or nameof(PsSession.AccentBrush) or nameof(PsSession.State)) UpdateFrame();
        if (e.PropertyName == nameof(PsSession.State) && _session.IsConnected) FocusInput();
    }

    private void UpdateFrame()
    {
        var accent = _session.AccentBrush;
        _headerStripe.Background = accent;
        if (_session.HasKeyboardFocus) BorderBrush = accent;
        else SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
    }

    private void UpdateWidth()
    {
        // Character cells that fit into the output area (monospace).
        var ft = new FormattedText("MMMMMMMMMM", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 13, Brushes.White, 1.0);
        var cell = ft.WidthIncludingTrailingWhitespace / 10;
        var avail = _output.ActualWidth - SystemParameters.VerticalScrollBarWidth - 8;
        if (cell > 0 && avail > 0) _session.Width = Math.Max(40, (int)(avail / cell));
    }

    public void FocusInput()
    {
        if (!IsVisible) return;
        Dispatcher.BeginInvoke(() => { _input.Focus(); Keyboard.Focus(_input); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Clear()
    {
        _para.Inlines.Clear();
    }

    // ---------------------------------------------------------------- output

    private void Append(IReadOnlyList<OutputChunk> chunks)
    {
        var atEnd = _output.VerticalOffset + _output.ViewportHeight >= _output.ExtentHeight - 4;
        foreach (var c in chunks)
        {
            if (c.IsCommand)
            {
                foreach (var (text, b) in PsSyntax.Colorize(c.Text.TrimEnd('\n')))
                {
                    var lines = text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Length > 0) _para.Inlines.Add(new Run(lines[i]) { Foreground = b });
                        if (i < lines.Length - 1) _para.Inlines.Add(new LineBreak());
                    }
                }
                _para.Inlines.Add(new LineBreak());
                continue;
            }
            var brush = c.Color is { } col ? BrushFor(col) : DefaultText;
            var parts = c.Text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0) _para.Inlines.Add(new Run(parts[i]) { Foreground = brush });
                if (i < parts.Length - 1) _para.Inlines.Add(new LineBreak());
            }
        }
        if (_para.Inlines.Count > MaxInlines)
        {
            var remove = _para.Inlines.Count - MaxInlines + 2000;
            for (int i = 0; i < remove && _para.Inlines.FirstInline is { } first; i++) _para.Inlines.Remove(first);
        }
        if (atEnd) _output.ScrollToEnd();
    }

    private static readonly Dictionary<ConsoleColor, Brush> Palette = new()
    {
        [ConsoleColor.Black] = ColorTags.Frozen(Color.FromRgb(0x6E, 0x76, 0x81)),
        [ConsoleColor.DarkBlue] = ColorTags.Frozen(Color.FromRgb(0x3B, 0x78, 0xFF)),
        [ConsoleColor.DarkGreen] = ColorTags.Frozen(Color.FromRgb(0x16, 0xC6, 0x0C)),
        [ConsoleColor.DarkCyan] = ColorTags.Frozen(Color.FromRgb(0x3A, 0x96, 0xDD)),
        [ConsoleColor.DarkRed] = ColorTags.Frozen(Color.FromRgb(0xE7, 0x48, 0x56)),
        [ConsoleColor.DarkMagenta] = ColorTags.Frozen(Color.FromRgb(0xB4, 0x00, 0x9E)),
        [ConsoleColor.DarkYellow] = ColorTags.Frozen(Color.FromRgb(0xC1, 0x9C, 0x00)),
        [ConsoleColor.Gray] = ColorTags.Frozen(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        [ConsoleColor.DarkGray] = ColorTags.Frozen(Color.FromRgb(0x8B, 0x94, 0x9E)),
        [ConsoleColor.Blue] = ColorTags.Frozen(Color.FromRgb(0x5C, 0x9D, 0xFF)),
        [ConsoleColor.Green] = ColorTags.Frozen(Color.FromRgb(0x3F, 0xB9, 0x50)),
        [ConsoleColor.Cyan] = ColorTags.Frozen(Color.FromRgb(0x61, 0xD6, 0xD6)),
        [ConsoleColor.Red] = ColorTags.Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B)),
        [ConsoleColor.Magenta] = ColorTags.Frozen(Color.FromRgb(0xD6, 0x70, 0xD6)),
        [ConsoleColor.Yellow] = ColorTags.Frozen(Color.FromRgb(0xF9, 0xE2, 0x7A)),
        [ConsoleColor.White] = ColorTags.Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2)),
    };

    private static Brush BrushFor(ConsoleColor c) => Palette.TryGetValue(c, out var b) ? b : DefaultText;

    // ---------------------------------------------------------------- input

    private void Output_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key is Key.C or Key.A or Key.Insert) return; // copy / select all in the output
        if (ctrl && e.Key == Key.L) { Clear(); e.Handled = true; return; }
        if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down) return;
        if (e.Key is Key.Enter or Key.Back or Key.Tab) { FocusInput(); e.Handled = e.Key != Key.Tab; }
    }

    private async void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_popup.IsOpen && PopupKey(e.Key, shift)) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Space when ctrl:
                e.Handled = true;
                await Complete(1, forceList: true);
                break;
            case Key.Enter:
                var line = _input.Text;
                _input.Clear();
                _historyIndex = _session.History.Count + 1;
                _session.Execute(line);
                _historyIndex = _session.History.Count;
                e.Handled = true;
                break;
            case Key.Up when !_session.IsReadingInput:
                Recall(-1); e.Handled = true; break;
            case Key.Down when !_session.IsReadingInput:
                Recall(1); e.Handled = true; break;
            case Key.Escape:
                _input.Clear(); e.Handled = true; break;
            case Key.C when ctrl && _input.SelectionLength == 0:
                if (!_output.Selection.IsEmpty) _output.Copy();
                else
                {
                    if (_session.IsBusy || _session.IsReadingInput) _session.Write("^C\n", ConsoleColor.DarkGray);
                    _session.Stop();
                    _input.Clear();
                }
                e.Handled = true;
                break;
            case Key.L when ctrl:
                Clear(); e.Handled = true; break;
            case Key.PageUp:
                _output.PageUp(); e.Handled = true; break;
            case Key.PageDown:
                _output.PageDown(); e.Handled = true; break;
            case Key.Tab:
                e.Handled = true;
                await Complete(shift ? -1 : 1);
                break;
        }
    }

    private void Recall(int delta)
    {
        var h = _session.History;
        if (h.Count == 0) return;
        _historyIndex = Math.Clamp(_historyIndex + delta, 0, h.Count);
        _input.Text = _historyIndex < h.Count ? h[_historyIndex] : "";
        _input.CaretIndex = _input.Text.Length;
    }

    /// <summary>
    /// Tab (or Ctrl+Space): complete against the remote session. One match is inserted directly; several open a list
    /// above the input line. Typing filters the list; Tab/↓ next, Shift+Tab/↑ previous, Enter accepts, Esc restores
    /// the typed text. <paramref name="auto"/>: opened by typing "-", ".", "\" or ":" – nothing is selected, so Enter still runs the line.
    /// </summary>
    private async Task Complete(int delta, bool forceList = false, bool auto = false)
    {
        var text = _input.Text;
        var caret = _input.CaretIndex;
        var result = await _session.CompleteAsync(text, caret);
        if (_input.Text != text || _input.CaretIndex != caret) return; // the user went on typing
        if (result is null) { if (!auto) ClosePopup(accept: true); return; }
        _completion = result;
        _cBase = text;
        _cLength = result.ReplacementLength;
        _popupOriginal = text;
        if (result.Matches.Count == 1 && !forceList)
        {
            Apply(result.Matches[0]);
            _popup.IsOpen = false;
            return;
        }
        _matches.ItemsSource = result.Matches;
        _popup.IsOpen = true;
        if (auto) _matches.SelectedIndex = -1;
        else Select(delta > 0 ? 0 : result.Matches.Count - 1);
    }

    private void ScheduleAutoComplete()
    {
        if (!_session.IsConnected || _session.IsBusy || _session.IsReadingInput) return;
        _autoTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(180), System.Windows.Threading.DispatcherPriority.Input,
            async (_, _) => { _autoTimer!.Stop(); if (!_popup.IsOpen) await Complete(1, forceList: true, auto: true); }, Dispatcher);
        _autoTimer.Stop();
        _autoTimer.Start();
    }

    /// <summary>While the list is open, typing narrows it to the matches that start with the word typed so far.</summary>
    private void OnUserTyped()
    {
        if (!_popup.IsOpen || _completion is not { } c) return;
        var text = _input.Text;
        var caret = _input.CaretIndex;
        var ri = c.ReplacementIndex;
        if (caret < ri || text.Length < ri || !text.StartsWith(_cBase[..Math.Min(ri, _cBase.Length)], StringComparison.Ordinal)) { ClosePopup(accept: true); return; }
        _cLength = Math.Max(0, _cLength + (text.Length - _cBase.Length));
        _cBase = text;
        var word = text[ri..caret].TrimStart('\'', '"');
        var hits = c.Matches.Where(m => m.ListItemText.StartsWith(word, StringComparison.OrdinalIgnoreCase) ||
                                        m.CompletionText.TrimStart('\'', '"', '.', '\\').StartsWith(word, StringComparison.OrdinalIgnoreCase) ||
                                        m.CompletionText.StartsWith(word, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) { ClosePopup(accept: true); return; }
        _matches.ItemsSource = hits;
        _matches.SelectedIndex = -1;
    }

    private void BuildPopup()
    {
        var item = new FrameworkElementFactory(typeof(DockPanel));
        var type = new FrameworkElementFactory(typeof(TextBlock));
        type.SetBinding(TextBlock.TextProperty, new Binding(nameof(CompletionMatch.ResultType)));
        type.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
        type.SetValue(TextBlock.FontSizeProperty, 11.0);
        type.SetValue(TextBlock.MarginProperty, new Thickness(16, 0, 0, 0));
        type.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        type.SetValue(DockPanel.DockProperty, Dock.Right);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(CompletionMatch.ListItemText)));
        text.SetValue(TextBlock.FontFamilyProperty, Mono);
        item.AppendChild(type);
        item.AppendChild(text);
        item.SetBinding(ToolTipProperty, new Binding(nameof(CompletionMatch.ToolTip)));
        _matches.ItemTemplate = new DataTemplate { VisualTree = item };
        _matches.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (MainWindow.FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is CompletionMatch m) { Apply(m); ClosePopup(accept: true); }
        };
        var card = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = _matches, Margin = new Thickness(0, 0, 0, 4) };
        card.SetResourceReference(Border.BackgroundProperty, "SessionHeaderBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
        _popup.Child = card;
        _popup.Closed += (_, _) => _completion = null;
    }

    /// <summary>Keys while the list is open. Returns true when the key was used.</summary>
    private bool PopupKey(Key key, bool shift)
    {
        var count = _matches.Items.Count;
        var sel = _matches.SelectedIndex;
        switch (key)
        {
            case Key.Tab: Select(sel < 0 ? (shift ? count - 1 : 0) : (sel + (shift ? -1 : 1) + count) % count); return true;
            case Key.Down: Select(Math.Min(count - 1, sel + 1)); return true;
            case Key.Up: Select(Math.Max(0, sel - 1)); return true;
            case Key.PageDown: Select(Math.Min(count - 1, Math.Max(0, sel) + 8)); return true;
            case Key.PageUp: Select(Math.Max(0, sel - 8)); return true;
            case Key.Escape: ClosePopup(accept: false); return true;
            case Key.Enter when sel >= 0: ClosePopup(accept: true); return true;
            case Key.Enter: ClosePopup(accept: true); return false; // nothing chosen (automatic list): run the line
            default: return false; // typing filters the list (see OnUserTyped)
        }
    }

    /// <summary>Selects a match and previews it in the input line.</summary>
    private void Select(int index)
    {
        if (index < 0 || index >= _matches.Items.Count) return;
        _matches.SelectedIndex = index;
        _matches.ScrollIntoView(_matches.SelectedItem);
        if (_matches.SelectedItem is CompletionMatch m) Apply(m);
    }

    private void ClosePopup(bool accept)
    {
        if (!accept && _popup.IsOpen)
        {
            _applying = true;
            _input.Text = _popupOriginal;
            _input.CaretIndex = _input.Text.Length;
            _applying = false;
        }
        _popup.IsOpen = false;
        _input.Focus();
    }

    private string _popupOriginal = "";

    /// <summary>Puts a match into the input line (replacing the word the completion was asked for).</summary>
    private void Apply(CompletionMatch m)
    {
        if (_completion is not { } c) return;
        var ri = Math.Min(c.ReplacementIndex, _cBase.Length);
        var end = Math.Min(_cBase.Length, ri + _cLength);
        _applying = true;
        _input.Text = _cBase[..ri] + m.CompletionText + _cBase[end..];
        _input.CaretIndex = ri + m.CompletionText.Length;
        _applying = false;
    }

    /// <summary>Colours the input line (the text box itself is transparent).</summary>
    private void Highlight()
    {
        _highlight.Inlines.Clear();
        foreach (var (text, brush) in PsSyntax.Colorize(_input.Text)) _highlight.Inlines.Add(new Run(text) { Foreground = brush });
    }

    // ---------------------------------------------------------------- overlay

    private static Border BuildOverlay(PsSession session, Action reconnect, Action close)
    {
        var status = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 560, Margin = new Thickness(0, 12, 0, 18) };
        status.SetBinding(TextBlock.TextProperty, nameof(PsSession.StatusText));
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var title = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, nameof(PsSession.Title));
        var sub = new TextBlock { FontSize = 13, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        sub.SetBinding(TextBlock.TextProperty, new Binding(nameof(SessionBase.Transport)) { StringFormat = "{0} · " + session.Connection.Host, TargetNullValue = session.Connection.Host });
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        var credInfo = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 560, Margin = new Thickness(0, -6, 0, 18) };
        credInfo.SetBinding(TextBlock.TextProperty, nameof(PsSession.CredentialInfo));
        credInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        var connect = new Button { Content = "Connect", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.PlugConnected24), Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0) };
        connect.Click += (_, _) => reconnect();
        var closeBtn = new Button { Content = "Close tab", Icon = new SymbolIcon(SymbolRegular.Dismiss24), Padding = new Thickness(18, 8, 18, 8) };
        closeBtn.Click += (_, _) => close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(connect);
        buttons.Children.Add(closeBtn);

        var icon = new SymbolIcon(SymbolRegular.WindowConsole20) { FontSize = 48, Margin = new Thickness(0, 0, 0, 12) };
        icon.SetBinding(Control.ForegroundProperty, nameof(PsSession.AccentBrush));

        var card = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        card.Children.Add(icon);
        card.Children.Add(title);
        card.Children.Add(sub);
        card.Children.Add(status);
        card.Children.Add(credInfo);
        card.Children.Add(buttons);

        var overlay = new Border { Child = card, CornerRadius = new CornerRadius(0, 0, 6, 6) };
        overlay.SetResourceReference(BackgroundProperty, "SessionAreaBrush");
        overlay.SetBinding(VisibilityProperty, new Binding(nameof(PsSession.ShowsOverlay)) { Converter = new BoolToVisibility() });
        return overlay;
    }

    private sealed class PromptConverter : IMultiValueConverter
    {
        public object Convert(object[] v, Type t, object p, System.Globalization.CultureInfo c) =>
            v.Length == 3 && v[2] is true ? "›" : v.Length == 3 && v[1] is true ? "…" : v[0] as string ?? "PS>";
        public object[] ConvertBack(object v, Type[] t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    private sealed class NegativeToTrue : IValueConverter
    {
        public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => v is int i && i < 0;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}
