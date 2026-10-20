using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace RemoteHub.Views;

/// <summary>
/// The "Colour" entry of a context menu: the palette (<see cref="ColorTags.All"/>), None, and a custom #RRGGBB. Shared by
/// Notes sections, script folders and scripts, and folders and connections in the tree (docs/scripts-view-design.md §4.9).
/// </summary>
public static class ColorMenu
{
    /// <param name="current">The colour set now: a palette id, "#RRGGBB", or null (none, or inherited).</param>
    /// <param name="pick">Called with the new colour (null = none).</param>
    /// <param name="noneLabel">What "no colour" means here ("None", or "Inherit from the folder").</param>
    public static MenuItem Build(Window? owner, string? current, Action<string?> pick, string noneLabel = "None")
    {
        var menu = new MenuItem { Header = "Colour", Icon = new SymbolIcon(SymbolRegular.Color24) };
        Fill(menu, owner, current, pick, noneLabel);
        return menu;
    }

    /// <summary>Fills an existing "Colour" entry (one declared in XAML) with the choices.</summary>
    public static void Fill(MenuItem menu, Window? owner, string? current, Action<string?> pick, string noneLabel = "None")
    {
        menu.Items.Clear();
        foreach (var tag in ColorTags.All)
        {
            var id = tag.Id;
            var item = new MenuItem { Header = tag.Label, Icon = Swatch(ColorTags.Frozen(tag.Color)), IsCheckable = true, IsChecked = current == id };
            item.Click += (_, _) => pick(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var none = new MenuItem { Header = noneLabel, Icon = new SymbolIcon(SymbolRegular.Prohibited24), IsCheckable = true, IsChecked = current is null or ColorTags.None };
        none.Click += (_, _) => pick(null);
        menu.Items.Add(none);
        var isCustom = current is not null && ColorTags.All.All(t => t.Id != current) && ColorTags.TryParseHex(current, out _);
        var custom = new MenuItem { Header = isCustom ? $"Custom ({current})…" : "Custom…", IsCheckable = true, IsChecked = isCustom,
            Icon = isCustom ? Swatch(ColorTags.Brush(current)!) : new SymbolIcon(SymbolRegular.Color24) };
        custom.Click += (_, _) =>
        {
            var dlg = new InputDialog("Custom colour", "A colour as #RRGGBB, e.g. #1E90FF.", isCustom ? current! : "#") { Owner = owner };
            if (dlg.ShowDialog() == true && ColorTags.TryParseHex(dlg.Value, out _)) pick(dlg.Value.Trim().ToUpperInvariant());
        };
        menu.Items.Add(custom);
    }

    private static Border Swatch(System.Windows.Media.Brush brush) =>
        new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = brush };
}
