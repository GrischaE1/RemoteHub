using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using RemoteHub.Services;
using RemoteHub.Views;
using Button = Wpf.Ui.Controls.Button;

namespace RemoteHub.Tests;

/// <summary>The welcome tour and What's new windows: their pages, buttons and what they say.</summary>
[Collection("Updates")] // sets UpdateService.Current, like the update tests
public class FirstStartWindowTests
{
    private static List<Button> Buttons(DependencyObject root) => Sta.All<Button>(root).Where(b => b.IsVisible).ToList();
    private static Button ButtonNamed(DependencyObject root, string text) => Sta.Find<Button>(root, b => Equals(b.Content, text));
    private static List<string> Texts(DependencyObject root) => Sta.All<TextBlock>(root).Select(t => t.Text).Where(t => t.Length > 0).ToList();

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));

    [Fact]
    public void The_welcome_tour_goes_through_its_pages_with_Next_and_Back_and_ends_with_Get_started() => Sta.Run(owner =>
    {
        var tour = Sta.Theme(new WelcomeWindow { Owner = owner, Left = -4000, WindowStartupLocation = WindowStartupLocation.Manual });
        tour.Show();
        Sta.Pump();
        var dots = Sta.All<System.Windows.Shapes.Ellipse>(tour).Count();
        Assert.InRange(dots, 3, 5); // pages for features IT turns off are left out
        Assert.Contains("Welcome to RemoteHub", Texts(tour));
        Assert.False(ButtonNamed(tour, "Back").IsVisible); // nothing to go back to on the first page
        var next = ButtonNamed(tour, "Next");
        Assert.Contains("Page 1 of", System.Windows.Automation.AutomationProperties.GetHelpText(next));

        for (var page = 2; page <= dots; page++)
        {
            Click(next);
            Sta.Pump();
            Assert.Contains($"Page {page} of {dots}", System.Windows.Automation.AutomationProperties.GetHelpText(next));
            Assert.True(ButtonNamed(tour, "Back").IsVisible);
        }
        Assert.Equal("Get started", next.Content);
        Assert.Contains("Keep it safe", Texts(tour));
        Assert.Contains(Texts(tour), t => t.Contains("Help & about shows this tour again"));

        Click(ButtonNamed(tour, "Back"));
        Sta.Pump();
        Assert.Equal("Next", next.Content);

        Click(next);
        Click(next); // Get started closes the tour
        Sta.Pump();
        Assert.False(tour.IsVisible);
    });

    [Fact]
    public void Skip_closes_the_tour_on_any_page() => Sta.Run(owner =>
    {
        var tour = Sta.Theme(new WelcomeWindow { Owner = owner, Left = -4000, WindowStartupLocation = WindowStartupLocation.Manual });
        tour.Show();
        Sta.Pump();
        var skip = Sta.Find<Button>(tour, b => Equals(b.Content, "Skip"));
        Assert.True(skip.IsCancel); // Esc too
        Click(skip);
        Sta.Pump();
        Assert.False(tour.IsVisible);
    });

    [Fact]
    public void Whats_new_lists_the_highlights_of_each_version_since_the_last_one() => Sta.Run(owner =>
    {
        var old = UpdateService.Current;
        UpdateService.Current = AppVersion.Parse("0.22.1")!;
        try
        {
            WhatsNewEntry[] entries = [new("0.22.0", ["Second thing", "Third thing"]), new("0.21.0", ["First thing"])];
            var window = Sta.Theme(new WhatsNewWindow(entries, AppVersion.Parse("0.20.2")) { Owner = owner, Left = -4000, WindowStartupLocation = WindowStartupLocation.Manual });
            window.Show();
            Sta.Pump();
            var texts = Texts(window);
            Assert.Contains("What's new in RemoteHub 0.22.0", texts); // named after the newest entry, not the patch
            Assert.Contains("Updated from 0.20.2 to 0.22.1", texts);
            Assert.Contains("0.22.0", texts);
            Assert.Contains("0.21.0", texts); // a heading per version when there are several
            Assert.Equal(["Second thing", "Third thing", "First thing"], texts.Where(t => t.EndsWith(" thing")));
            var close = Sta.Find<Button>(window, b => Equals(b.Content, "Close"));
            Assert.True(close.IsDefault && close.IsCancel);
            Assert.NotNull(Sta.Find<Button>(window, b => Equals(b.Content, "All release notes")));
            Click(close);
            Sta.Pump();
            Assert.False(window.IsVisible);
        }
        finally { UpdateService.Current = old; }
    });

    [Fact]
    public void Whats_new_from_Help_and_about_shows_one_version_without_a_heading() => Sta.Run(owner =>
    {
        var window = Sta.Theme(new WhatsNewWindow([new("0.21.0", ["Only thing"])]) { Owner = owner, Left = -4000, WindowStartupLocation = WindowStartupLocation.Manual });
        window.Show();
        Sta.Pump();
        var texts = Texts(window);
        Assert.Contains("What's new in RemoteHub 0.21.0", texts);
        Assert.Contains(texts, t => t.StartsWith("Installed: "));
        Assert.DoesNotContain("0.21.0", texts);
        Assert.Contains("Only thing", texts);
        window.Close();
    });

    [Fact]
    public void Every_built_in_highlight_reads_as_British_English_without_double_punctuation()
    {
        foreach (var line in FirstStart.Entries.SelectMany(e => e.Highlights))
        {
            Assert.DoesNotContain("….", line);
            Assert.DoesNotContain("..", line);
            Assert.DoesNotContain(" - ", line); // asides use an en dash
            foreach (var us in new[] { "color", "organize", "favorite" }) Assert.DoesNotContain(us, line, StringComparison.OrdinalIgnoreCase);
        }
    }
}
