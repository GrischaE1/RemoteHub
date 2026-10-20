using System.Windows;
using RemoteHub.Views;

namespace RemoteHub.Lab;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // RemoteHub's chrome brushes (ChromeBackgroundBrush, SidebarBackgroundBrush, ChromeStrokeBrush, BrandAccentBrush)
        AppChrome.ApplyPalette(dark: true);
        var paths = FindCheckout();
        if (paths is null) { Shutdown(1); return; }
        MainWindow = new MainWindow(paths);
        MainWindow.Show();
    }

    /// <summary>
    /// The checkout to work with: the one the exe sits in (lab\RemoteHubLab.exe), else the one used last time (a copy or
    /// a shortcut elsewhere), else one the person picks. Remembered for the next start. null when none is chosen.
    /// </summary>
    private static LabPaths? FindCheckout()
    {
        var remembered = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteHubLab", "checkout.txt");
        var paths = LabPaths.TryFind(AppContext.BaseDirectory);
        if (paths is null && File.Exists(remembered)) paths = LabPaths.TryFind(File.ReadAllText(remembered).Trim());
        while (paths is null)
        {
            var pick = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the RemoteHub checkout (the folder with lab\\lab.json)" };
            if (pick.ShowDialog() != true) return null;
            paths = LabPaths.TryFind(pick.FolderName);
            if (paths is null)
                MessageBox.Show($"No lab\\lab.json in {pick.FolderName} or above it.", "RemoteHub Lab", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(remembered)!);
            File.WriteAllText(remembered, paths.Repo);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return paths;
    }
}
