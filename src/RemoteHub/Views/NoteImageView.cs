using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;

namespace RemoteHub.Views;

/// <summary>An image in a note (docs/notes-view-design.md §4.5.4): natural size up to the page width, resizable by its corner.</summary>
public sealed class NoteImageView : Border, INoteImage
{
    private const double MinWidth_ = 48;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Thumb _grip = new() { Width = 12, Height = 12, Cursor = Cursors.SizeNWSE, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Hidden };
    private readonly BitmapSource? _bitmap;
    private readonly byte[]? _data;
    private double? _width;

    public string ImageId { get; }
    public string Alt { get; private set; }
    public double? DisplayWidth => _width;

    /// <summary>Size, alternative text or the image itself changed (the page needs saving).</summary>
    public event Action? Changed;
    public event Action<NoteImageView>? DeleteRequested;

    public NoteImageView(string imageId, string alt, double? width, byte[]? data)
    {
        ImageId = imageId;
        Alt = alt;
        _width = width;
        _data = data;
        _bitmap = data is null ? null : NoteImages.Load(data);
        BorderThickness = new Thickness(1);
        BorderBrush = Brushes.Transparent;
        Padding = new Thickness(1);
        Cursor = Cursors.Arrow;

        var grid = new Grid();
        if (_bitmap is not null)
        {
            _image.Source = _bitmap;
            grid.Children.Add(_image);
        }
        else
        {
            // Missing or unreadable: show the alternative text, keep the reference.
            var missing = new System.Windows.Controls.TextBlock { Text = alt.Length > 0 ? $"[{alt}]" : "[image]", Margin = new Thickness(4) };
            missing.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            grid.Children.Add(missing);
        }
        var gripFace = new FrameworkElementFactory(typeof(Border));
        gripFace.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        gripFace.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        _grip.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = gripFace };
        _grip.SetResourceReference(Control.BackgroundProperty, "BrandAccentBrush");
        grid.Children.Add(_grip);
        Child = grid;

        _grip.DragDelta += (_, e) =>
        {
            var w = Math.Max(MinWidth_, (_width ?? ActualWidth) + e.HorizontalChange);
            if (_bitmap is not null) w = Math.Min(w, _bitmap.PixelWidth * 4);
            SetWidth(w);
        };
        _grip.DragCompleted += (_, _) => Changed?.Invoke();
        MouseEnter += (_, _) => { if (_bitmap is not null) { _grip.Visibility = Visibility.Visible; SetResourceReference(BorderBrushProperty, "BrandAccentBrush"); } };
        MouseLeave += (_, _) => { if (!_grip.IsDragging) { _grip.Visibility = Visibility.Hidden; BorderBrush = Brushes.Transparent; } };
        ContextMenu = Menu();
        AutomationProperties.SetName(this, alt.Length > 0 ? alt : "Image");
        ApplyWidth();
    }

    // A Border has no automation peer of its own, so the alternative text set as its name reached no screen reader:
    // UI Automation saw only an unnamed image inside the page (lab run 20261022-092309, C-NOTE-06).
    protected override AutomationPeer OnCreateAutomationPeer() => new ImagePeer(this);

    private sealed class ImagePeer(NoteImageView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(NoteImageView);
    }

    /// <summary>The widest the image may be shown (the page's width); set by the editor.</summary>
    public double PageWidth
    {
        get => _pageWidth;
        set { _pageWidth = value; ApplyWidth(); }
    }
    private double _pageWidth = double.PositiveInfinity;

    private void SetWidth(double? w)
    {
        _width = w is null ? null : Math.Round(w.Value);
        ApplyWidth();
    }

    private void ApplyWidth()
    {
        if (_bitmap is null) return;
        // Natural size in device-independent pixels, at most the page width.
        var natural = _bitmap.PixelWidth * 96.0 / (_bitmap.DpiX > 0 ? _bitmap.DpiX : 96);
        var w = Math.Min(_width ?? natural, Math.Max(MinWidth_, _pageWidth - 8));
        _image.Width = w;
        _image.Height = w * _bitmap.PixelHeight / Math.Max(1, _bitmap.PixelWidth);
    }

    private ContextMenu Menu()
    {
        var m = new ContextMenu();
        m.Items.Add(RemoteToolChrome.Item("Copy", SymbolRegular.Copy24, () => { if (_bitmap is not null) try { Clipboard.SetImage(_bitmap); } catch (System.Runtime.InteropServices.ExternalException) { } }, _bitmap is not null));
        m.Items.Add(RemoteToolChrome.Item("Save as…", SymbolRegular.Save24, SaveAs, _data is not null));
        m.Items.Add(RemoteToolChrome.Item("Alternative text…", SymbolRegular.TextDescription24, EditAlt));
        m.Items.Add(RemoteToolChrome.Item("Original size", SymbolRegular.ArrowMaximize24, () => { SetWidth(null); Changed?.Invoke(); }));
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Delete", SymbolRegular.Delete24, () => DeleteRequested?.Invoke(this)));
        return m;
    }

    private void EditAlt()
    {
        var dlg = new InputDialog("Alternative text", "Describes the image for screen readers and when it can't be shown.", Alt) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        Alt = new string(dlg.Value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        AutomationProperties.SetName(this, Alt.Length > 0 ? Alt : "Image");
        Changed?.Invoke();
    }

    private void SaveAs()
    {
        if (_data is null) return;
        var png = _data.Length > 4 && _data[0] == 0x89 && _data[1] == (byte)'P';
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = (Alt.Length > 0 ? NoteFiles.SafeName(Alt) : "image") + (png ? ".png" : ".jpg"),
            Filter = png ? "PNG image (*.png)|*.png" : "JPEG image (*.jpg)|*.jpg",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllBytes(dlg.FileName, _data); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Window.GetWindow(this) is { } w) _ = Dialogs.Info(w, "Save failed", ex.Message);
        }
    }
}
