using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RemoteHub.Rdp;

namespace RemoteHub.Views;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public bool UseHidden { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool v = value switch { bool b => b, int i => i != 0, string s => !string.IsNullOrEmpty(s), null => false, _ => true };
        if (Invert) v = !v;
        return v ? Visibility.Visible : UseHidden ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>The same colour, see-through (parameter = opacity 0–1): tints the area of a folder in the tree.</summary>
public sealed class BrushTint : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not SolidColorBrush { Color: var col }) return Brushes.Transparent;
        var a = double.TryParse(p as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 0.08;
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), col.R, col.G, col.B));
        b.Freeze();
        return b;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Several collections shown as one list that follows their changes (a connection's shares, then what is reached through it).</summary>
public sealed class ConcatCollections : IMultiValueConverter
{
    public object Convert(object?[] values, Type t, object? p, CultureInfo c)
    {
        var all = new CompositeCollection();
        foreach (var v in values)
            if (v is System.Collections.IEnumerable items) all.Add(new CollectionContainer { Collection = items });
        return all;
    }

    public object[] ConvertBack(object? value, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class TransferStateToSymbol : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        Remoting.TransferState.Done => Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24,
        Remoting.TransferState.Failed => Wpf.Ui.Controls.SymbolRegular.ErrorCircle24,
        Remoting.TransferState.Cancelled or Remoting.TransferState.Skipped => Wpf.Ui.Controls.SymbolRegular.DismissCircle24,
        Remoting.TransferState.Running => Wpf.Ui.Controls.SymbolRegular.ArrowUpload24,
        _ => Wpf.Ui.Controls.SymbolRegular.Clock24,
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class TransferStateToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        Remoting.TransferState.Done => SessionStateToBrush.GreenBrush,
        Remoting.TransferState.Failed => SessionStateToBrush.RedBrush,
        Remoting.TransferState.Running => SessionStateToBrush.AmberBrush,
        _ => SessionStateToBrush.GreyBrush,
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolToChevron : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Wpf.Ui.Controls.SymbolRegular.ChevronUp24 : Wpf.Ui.Controls.SymbolRegular.ChevronDown24;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class SessionStateToBrush : IValueConverter
{
    private static readonly Brush Green = Frozen(0x3F, 0xB9, 0x50);
    private static readonly Brush Amber = Frozen(0xE3, 0xA0, 0x08);
    private static readonly Brush Red = Frozen(0xE5, 0x48, 0x4D);
    private static readonly Brush Grey = Frozen(0x8B, 0x8B, 0x8B);

    internal static Brush GreenBrush => Green;
    internal static Brush AmberBrush => Amber;
    internal static Brush RedBrush => Red;
    internal static Brush GreyBrush => Grey;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        SessionState.Connected => Green,
        SessionState.Connecting => Amber,
        SessionState.Failed => Red,
        _ => Grey,
    };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
