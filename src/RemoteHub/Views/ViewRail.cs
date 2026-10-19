using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>One top-level view on the <see cref="ViewRail"/>.</summary>
public sealed record RailView(string Id, SymbolRegular Icon, string Label, string Shortcut);

/// <summary>
/// The slim bar of view icons on the far left of the main window (notes-view-design.md §3). Hovering or focusing it
/// shows an expanded copy with labels in a <see cref="Popup"/>: a popup is a window of its own, so it can draw over the
/// RDP and agent desktop controls (child windows that WPF content can't cover), and the views underneath keep their size.
/// </summary>
public sealed class ViewRail : Grid
{
    public const double CollapsedWidth = 48;
    public const double ExpandedWidth = 200;
    // Short enough to feel immediate, long enough that crossing the rail on the way to the sidebar doesn't open it.
    private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(150);
    private static readonly Duration Grow = new(TimeSpan.FromMilliseconds(100));

    private readonly StackPanel _top = new();
    private readonly StackPanel _bottom = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StackPanel _popupTop = new();
    private readonly StackPanel _popupBottom = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Border _popupCard = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(0, 8, 8, 0), BorderThickness = new Thickness(0, 1, 1, 1) };
    private readonly Popup _popup = new() { AllowsTransparency = true, Placement = PlacementMode.Relative, StaysOpen = true, Focusable = false };
    private readonly DispatcherTimer _openTimer = new() { Interval = OpenDelay };
    private readonly DispatcherTimer _closeTimer = new() { Interval = CloseDelay };
    private readonly List<Item> _items = [];
    private readonly Item _versionItem;
    private readonly Ellipse _updateDot = new() { Width = 7, Height = 7, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0) };
    private readonly TextBlock _versionShort = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _versionLong = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _versionSub = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
    private string? _selected;

    /// <summary>A view was picked (argument: its id).</summary>
    public event Action<string>? ViewSelected;
    public event Action? SettingsClicked;
    public event Action? VersionClicked;
    /// <summary>Esc on the rail: focus goes back to the view.</summary>
    public event Action? FocusViewRequested;

    public ViewRail()
    {
        Width = CollapsedWidth;
        Background = Brushes.Transparent; // hit-testable, so hovering the gaps counts
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _top.Margin = new Thickness(0, 2, 0, 0);
        _bottom.Margin = new Thickness(0, 0, 0, 10);
        Children.Add(_top);
        SetRow(_bottom, 1);
        Children.Add(_bottom);
        _updateDot.SetResourceReference(Shape.FillProperty, "BrandAccentBrush");
        _versionShort.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _versionSub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var popupGrid = new Grid();
        popupGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        popupGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _popupTop.Margin = _top.Margin;
        _popupBottom.Margin = _bottom.Margin;
        popupGrid.Children.Add(_popupTop);
        SetRow(_popupBottom, 1);
        popupGrid.Children.Add(_popupBottom);
        _popupCard.Child = popupGrid;
        _popupCard.SetResourceReference(Border.BackgroundProperty, "SidebarBackgroundBrush");
        _popupCard.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
        _popupCard.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.3 };
        _popup.Child = new Border { Child = _popupCard, Padding = new Thickness(0, 0, 18, 18), Background = Brushes.Transparent };
        _popup.PlacementTarget = this;
        _popup.MouseLeave += (_, _) => ScheduleClose();
        _popup.MouseEnter += (_, _) => _closeTimer.Stop();

        _openTimer.Tick += (_, _) => { _openTimer.Stop(); if (IsMouseOver) Open(); };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (!IsMouseOver && !_popupCard.IsMouseOver && !IsKeyboardFocusWithin) Close();
        };
        MouseEnter += (_, _) => { _closeTimer.Stop(); if (!_popup.IsOpen) _openTimer.Start(); };
        MouseLeave += (_, _) => { _openTimer.Stop(); ScheduleClose(); };
        IsKeyboardFocusWithinChanged += (_, e) => { if ((bool)e.NewValue) Open(); else ScheduleClose(); };
        PreviewKeyDown += OnKey;

        _versionItem = AddItem(_bottom, _popupBottom, VersionVisual(false), VersionVisual(true), "RemoteHub version", "", () => VersionClicked?.Invoke(), selectable: false);
        AddItem(_bottom, _popupBottom, Icon(SymbolRegular.Settings24), Labelled(SymbolRegular.Settings24, "Settings"), "Settings", "Ctrl+,", () => SettingsClicked?.Invoke(), selectable: false);

        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is not { } w) return;
            // The popup doesn't follow the window; close it when the window moves, resizes or loses focus.
            w.LocationChanged += (_, _) => Close();
            w.SizeChanged += (_, _) => Close();
            w.Deactivated += (_, _) => Close();
        };
    }

    /// <summary>Replaces the view items (the version and Settings at the bottom stay).</summary>
    public void SetViews(IEnumerable<RailView> views)
    {
        foreach (var it in _items.Where(i => i.ViewId is not null).ToList())
        {
            _top.Children.Remove(it.Rail);
            _popupTop.Children.Remove(it.Expanded);
            _items.Remove(it);
        }
        foreach (var v in views)
        {
            var it = AddItem(_top, _popupTop, Icon(v.Icon), Labelled(v.Icon, v.Label), v.Label, v.Shortcut, () => Select(v.Id, raise: true), selectable: true);
            it.ViewId = v.Id;
        }
        Select(_selected, raise: false);
    }

    /// <summary>The selected view (marker and accessibility state).</summary>
    public string? Selected
    {
        get => _selected;
        set => Select(value, raise: false);
    }

    /// <summary>The running version: short form on the rail, full line and a second line (channel or update) expanded.</summary>
    public void SetVersion(string shortText, string fullText, string? secondLine, bool updateAvailable)
    {
        _versionShort.Text = shortText;
        _versionLong.Text = fullText;
        _versionSub.Text = secondLine ?? "";
        _versionSub.Visibility = string.IsNullOrEmpty(secondLine) ? Visibility.Collapsed : Visibility.Visible;
        _updateDot.Visibility = updateAvailable ? Visibility.Visible : Visibility.Collapsed;
        var name = updateAvailable ? $"{fullText} – {secondLine}" : $"{fullText} – about this version";
        AutomationProperties.SetName(_versionItem.Button, name);
        _versionItem.Button.ToolTip = name;
    }

    /// <summary>Closes the expanded rail now (e.g. before a modal dialog or when the window locks).</summary>
    public void Close()
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        _popup.IsOpen = false;
    }

    private void ScheduleClose()
    {
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    private void Open()
    {
        if (_popup.IsOpen || !IsVisible) return;
        // Not over a modal dialog, and not while something is being dragged across the window.
        if (ComponentDispatcher.IsThreadModal || Mouse.LeftButton == MouseButtonState.Pressed) return;
        _popupCard.Height = ActualHeight;
        _popup.HorizontalOffset = 0;
        _popup.VerticalOffset = 0;
        if (SystemParameters.ClientAreaAnimation)
        {
            _popupCard.Width = CollapsedWidth;
            _popup.IsOpen = true;
            _popupCard.BeginAnimation(WidthProperty, new DoubleAnimation(CollapsedWidth, ExpandedWidth, Grow) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            _popupCard.BeginAnimation(WidthProperty, null);
            _popupCard.Width = ExpandedWidth;
            _popup.IsOpen = true;
        }
    }

    private void Select(string? id, bool raise)
    {
        _selected = id;
        foreach (var it in _items.Where(i => i.ViewId is not null)) it.SetSelected(it.ViewId == id);
        if (raise && id is not null)
        {
            Close();
            ViewSelected?.Invoke(id);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var buttons = _items.Select(i => i.Button).Where(b => b.IsVisible).ToList();
        var at = buttons.FindIndex(b => b.IsKeyboardFocused);
        if (e.Key == Key.Escape)
        {
            Close();
            FocusViewRequested?.Invoke();
            e.Handled = true;
        }
        else if ((e.Key is Key.Down or Key.Up) && at >= 0)
        {
            var next = (at + (e.Key == Key.Down ? 1 : -1) + buttons.Count) % buttons.Count;
            buttons[next].Focus();
            e.Handled = true;
        }
    }

    private Item AddItem(StackPanel railPanel, StackPanel popupPanel, UIElement railContent, UIElement expandedContent, string name, string shortcut, Action click, bool selectable)
    {
        var item = new Item(railContent, expandedContent, selectable);
        AutomationProperties.SetName(item.Button, name);
        if (shortcut.Length > 0) AutomationProperties.SetAcceleratorKey(item.Button, shortcut);
        item.Button.ToolTip = shortcut.Length > 0 ? $"{name} ({shortcut})" : name;
        item.Button.Click += (_, _) => click();
        item.ExpandedButton.Click += (_, _) => click();
        railPanel.Children.Add(item.Rail);
        popupPanel.Children.Add(item.Expanded);
        _items.Add(item);
        return item;
    }

    private static UIElement Icon(SymbolRegular symbol) => new SymbolIcon(symbol) { FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };

    private static UIElement Labelled(SymbolRegular symbol, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new SymbolIcon(symbol) { FontSize = 20, Width = 24, Margin = new Thickness(0, 0, 12, 0) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private UIElement VersionVisual(bool expanded)
    {
        if (!expanded)
        {
            var g = new Grid { Width = 40 };
            g.Children.Add(_versionShort);
            g.Children.Add(_updateDot);
            return g;
        }
        var lines = new StackPanel { Margin = new Thickness(36, 0, 0, 0) };
        lines.Children.Add(_versionLong);
        lines.Children.Add(_versionSub);
        return lines;
    }

    /// <summary>One entry: the icon button on the rail and its labelled twin in the expanded popup.</summary>
    private sealed class Item
    {
        public Button Button => _rail.Button;
        public Button ExpandedButton => _expanded.Button;
        public FrameworkElement Rail => _rail.Host;
        public FrameworkElement Expanded => _expanded.Host;
        public string? ViewId { get; set; }
        private readonly Look _rail;
        private readonly Look _expanded;
        private readonly bool _selectable;
        private bool _selected;

        public Item(UIElement railContent, UIElement expandedContent, bool selectable)
        {
            _selectable = selectable;
            _rail = new Look(railContent, CollapsedWidth - 8, HorizontalAlignment.Center, Refresh);
            _expanded = new Look(expandedContent, ExpandedWidth - 8, HorizontalAlignment.Left, Refresh);
            // The expanded copy is only for the pointer; keyboard focus stays on the rail and is mirrored on both.
            ExpandedButton.Focusable = false;
            ExpandedButton.IsTabStop = false;
            Button.IsKeyboardFocusedChanged += (_, _) => Refresh();
            Refresh();
        }

        public void SetSelected(bool selected)
        {
            _selected = selected && _selectable;
            AutomationProperties.SetItemStatus(Button, _selected ? "selected" : "");
            Refresh();
        }

        private void Refresh()
        {
            var focused = Button.IsKeyboardFocused;
            foreach (var l in new[] { _rail, _expanded })
            {
                l.Marker.Visibility = _selected ? Visibility.Visible : Visibility.Hidden;
                if (_selected) l.Back.SetResourceReference(Border.BackgroundProperty, "SessionHeaderBrush");
                else if (l.Button.IsMouseOver) l.Back.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                else l.Back.Background = Brushes.Transparent;
                if (focused) l.Back.SetResourceReference(Border.BorderBrushProperty, "BrandAccentBrush");
                else if (_selected) l.Back.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush"); // as the selected session tab
                else l.Back.BorderBrush = Brushes.Transparent;
            }
        }
    }

    /// <summary>The visual of one entry: a plain button with a rounded background and the selection marker.</summary>
    private sealed class Look
    {
        public Button Button { get; }
        public Border Back { get; }
        public Rectangle Marker { get; }
        public Grid Host { get; } = new();

        public Look(UIElement content, double width, HorizontalAlignment align, Action changed)
        {
            // Collapsed items are centred in 40 px: no side padding there, or the version text gets cut.
            Back = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = align == HorizontalAlignment.Center ? new Thickness(0) : new Thickness(8, 0, 8, 0) };
            Back.Child = new ContentPresenter { Content = content, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
            Marker = new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), IsHitTestVisible = false };
            Marker.SetResourceReference(Shape.FillProperty, "BrandAccentBrush");
            Button = new Button
            {
                Width = width, Height = 40, Margin = new Thickness(4, 0, 4, 4), Cursor = Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                FocusVisualStyle = null, Template = Plain(), Content = Back,
            };
            Button.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            Button.MouseEnter += (_, _) => changed();
            Button.MouseLeave += (_, _) => changed();
            Host.Children.Add(Button);
            Host.Children.Add(Marker);
        }

        private static ControlTemplate Plain() => new(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
    }
}
