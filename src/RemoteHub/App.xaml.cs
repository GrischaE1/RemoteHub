using System.Windows;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.ViewModels;

namespace RemoteHub;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.ClaimOrForward(e.Args);
        if (_instance is null)
        {
            Shutdown(); // arguments were handed to the running instance
            return;
        }

        // Crashes are only written to a local file now; the next start sends them (if usage data is on).
        CrashReporter.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "RemoteHub – unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        SettingsStore.PasswordPrompt = message =>
        {
            var dlg = new Views.PasswordDialog(false, "Unlock shared settings",
                $"{message}\n\n{SettingsStore.FilePath}", "Unlock");
            return dlg.ShowDialog() == true ? dlg.Password : null;
        };
        // The password prompt may be the first window; don't let closing it end the app.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Startup protection: nothing is read before the password or Windows Hello unlocks the settings key.
        if (AppLock.IsEnabled)
        {
            var theme = SettingsStore.PeekTheme();
            var dark = theme == "System" ? Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme() != Wpf.Ui.Appearance.SystemTheme.Light : theme != "Light";
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.None, true);
            Views.AppChrome.ApplyPalette(dark);
            if (new Views.UnlockWindow().ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        AppData data;
        try
        {
            data = LoadSettings();
        }
        catch (OperationCanceledException) when (!LocalDb.IsOpen)
        {
            Shutdown(); // the unreadable database was neither restored nor replaced
            return;
        }
        catch (Exception ex) when (SettingsStore.IsShared && LocalDb.IsOpen)
        {
            // Shared file unavailable (not synced, offline, cancelled, wrong password …): keep the shared file
            // untouched and work with this PC's settings for this session.
            System.Windows.MessageBox.Show(
                $"The shared settings file could not be opened:\n{ex.Message}\n\n" +
                "RemoteHub starts with the settings stored on this PC. The shared file is not changed.\n" +
                "Restart the app to try again.",
                "RemoteHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            SettingsStore.UsingLocalFallback = true;
            data = LoadLocalOrEmpty();
        }
        catch (Exception ex)
        {
            var file = SettingsStore.UnreadableFile ?? SettingsStore.FilePath;
            var broken = file + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(file, broken, overwrite: true); } catch { /* best effort */ }
            System.Windows.MessageBox.Show(
                $"The settings file could not be read ({ex.Message}).\n\n" +
                $"A copy was saved to:\n{broken}\n\nYou can restore an exported backup via Menu → Import.",
                "RemoteHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            data = new AppData();
        }

        // In the background: the daily check and backup of the database, and the audit log's retention.
        _ = Task.Run(() =>
        {
            try
            {
                LocalDb.BackupIfDue();
                AuditLog.ApplyRetention(AuditLog.RetentionDays);
            }
            catch (Exception ex) { DiagLog.Write("Database maintenance failed: " + ex.Message); }
        });

        var vm = new MainViewModel(data);
        var window = new MainWindow(vm);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        HandleArgs(window, e.Args);
        _instance.Listen(args => Dispatcher.BeginInvoke(() =>
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            HandleArgs(window, args);
        }));
    }

    /// <summary>
    /// Loads the settings. If the database can't be opened, offers its latest automatic backup – or, without one (or
    /// if the backup doesn't open either), to start with an empty database; the unreadable one is kept beside it.
    /// Cancelling ends the app without changing anything.
    /// </summary>
    private static AppData LoadSettings()
    {
        try { return SettingsStore.Load(); }
        catch (DatabaseUnreadableException ex)
        {
            DiagLog.Write("Database unreadable: " + ex.Message + (ex.InnerException is { } i ? " – " + i.Message : ""));
            foreach (var (backup, time) in LocalDb.Backups())
            {
                var answer = MessageBox.Show(
                    $"RemoteHub's database can't be opened:\n{ex.Message}\n\nRestore the automatic backup of {time.ToLocalTime():g}?\n" +
                    "Changes made after it are lost. The current database is kept beside it.",
                    "RemoteHub", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel) throw new OperationCanceledException();
                if (answer == MessageBoxResult.No) break;
                try
                {
                    LocalDb.RestoreBackup(backup);
                    return SettingsStore.Load();
                }
                catch (DatabaseUnreadableException) { /* try the next older backup */ }
            }
            if (MessageBox.Show(
                    $"RemoteHub's database can't be opened:\n{ex.Message}\n\nStart with empty settings? The unreadable database is kept in\n" +
                    $"{LocalDb.Directory}\n\nYou can restore an exported backup via Settings → Data → Import backup.",
                    "RemoteHub", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                throw new OperationCanceledException();
            LocalDb.SetAsideAndCreateNew();
            return SettingsStore.Load();
        }
    }

    private static AppData LoadLocalOrEmpty()
    {
        try { return SettingsStore.Load(); }
        catch { return new AppData(); }
    }

    private static void HandleArgs(MainWindow window, string[] args)
    {
        var rdpFiles = args.Where(a => a.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase) && File.Exists(a)).ToArray();
        if (rdpFiles.Length > 0) window.ImportRdpFiles(rdpFiles, null, connectAfterImport: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AuditLog.Flush();
        LocalDb.Close();
        _instance?.Dispose();
        if (_instance is not null) CrashReporter.Stop();
        if (_instance is not null) Remoting.AgentInstaller.CleanTemp(TimeSpan.Zero); // staged agent files may hold a device key
        base.OnExit(e);
    }
}
