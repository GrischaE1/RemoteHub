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
        // RemoteHub.exe --decoder-check <file>: the decoder sandbox checked, the results written, nothing else started
        if (e.Args is ["--decoder-check", var checkFile])
        {
            Shutdown(Remoting.Decoder.DecoderCheck.Run(checkFile));
            return;
        }
        StartupTiming.Mark("app");
        Views.WindowPlacement.Install(); // popups open centred on the window they belong to, also when it is maximised

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
            UnexpectedErrors.Handle(args.Exception, message => System.Windows.MessageBox.Show(message, "RemoteHub – unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error));
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
            var theme = AppPolicy.Theme ?? SettingsStore.PeekTheme();
            var dark = theme == "System" ? Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme() != Wpf.Ui.Appearance.SystemTheme.Light : theme != "Light";
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.None, true);
            Views.AppChrome.ApplyPalette(dark);
            if (new Views.UnlockWindow().ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            StartupTiming.Mark("unlock (waits for the user)");
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
            // The database's recent changes may still be in its write-ahead log: keep that with the copy.
            foreach (var part in new[] { "-wal", "-shm" })
                try { if (File.Exists(file + part)) File.Copy(file + part, broken + part, overwrite: true); } catch { /* best effort */ }
            System.Windows.MessageBox.Show(
                $"The settings file could not be read ({ex.Message}).\n\n" +
                $"A copy was saved to:\n{broken}\n\nYou can restore an exported backup via Menu → Import.",
                "RemoteHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            data = new AppData();
        }

        // In the background: the daily check and backup of the database, and the audit log's retention.
        AuditLog.ConfiguredRetentionDays = data.Ui.AuditRetentionDays;
        _ = Task.Run(() =>
        {
            try
            {
                // Left over after a crash: staged agent files (may hold a device key) and old clipboard copies from devices.
                Remoting.AgentInstaller.CleanTemp(TimeSpan.Zero);
                Remoting.AgentDesktopSession.CleanReceived(TimeSpan.FromDays(1));
                LocalDb.BackupIfDue();
                DiagLog.ApplyRetention(DiagLog.RetentionDays); // first: it catches its own errors
                AuditLog.ApplyRetention(AuditLog.RetentionDays);
                if (LocalDb.IsOpen && NoteStore.PurgeDeleted() is > 0 and var purged)
                    DiagLog.Write($"Notes: removed {purged} pages deleted more than {NoteStore.KeepDeletedDays} days ago");
                if (LocalDb.IsOpen && ScriptStore.PurgeDeleted() is > 0 and var scripts)
                    DiagLog.Write($"Scripts: removed {scripts} scripts deleted more than {ScriptStore.KeepDeletedDays} days ago");
                if (LocalDb.IsOpen && DeletedNodeStore.Purge() is > 0 and var nodes)
                    DiagLog.Write($"Recently deleted: removed {nodes} items deleted more than {DeletedNodeStore.KeepDays} days ago");
                if (LocalDb.IsOpen) ScriptStore.PruneRunOutputs();
            }
            catch (Exception ex) { DiagLog.Write("Database maintenance failed: " + ex.Message); }
        });

        // Policy StartupProtection = Required (docs/admx-design.md §6.3): nothing opens until it is set up. Set up here, after the
        // settings are loaded, because turning it on re-protects them; closing the window without it ends RemoteHub.
        if (AppPolicy.StartupProtection == "Required" && !AppLock.IsEnabled)
        {
            DiagLog.Write("Startup protection: required by policy, not set up – asking for it");
            new Views.StartupProtectionWindow(() => data) { ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen }.ShowDialog();
            if (!AppLock.IsEnabled)
            {
                Shutdown();
                return;
            }
        }

        StartupTiming.Mark("settings");
        var vm = new MainViewModel(data);
        StartupTiming.Mark("view model");
        var window = new MainWindow(vm);
        StartupTiming.Mark("window");
        window.ContentRendered += (_, _) =>
        {
            var connections = vm.AllNodes().OfType<Connection>().ToList();
            var shares = connections.Sum(c => c.Shares.Count) + vm.AllNodes().OfType<NetworkShare>().Count();
            StartupTiming.Finish($"{connections.Count} connections, {shares} shares");
        };
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        StartupTiming.Mark("show");

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
                    $"{LocalDb.Directory}\n\nYou can restore an exported backup via Settings → Security & storage → Import backup…",
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
        // Started again by the update's restart helper: says why an update didn't install (UpdateService.StartInstall).
        var result = Array.IndexOf(args, UpdateService.UpdateResultArg);
        if (result >= 0 && result + 1 < args.Length && UpdateService.InstallResultMessage(args[result + 1]) is { } message)
        {
            DiagLog.Write("Update: the install after the restart didn't complete");
            _ = Views.Dialogs.Info(window, "RemoteHub updates", message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AuditLog.Flush();
        LocalDb.Close();
        _instance?.Dispose();
        if (_instance is not null) CrashReporter.Stop();
        if (_instance is not null) Telemetry.RunTime.Stop();
        if (_instance is not null) Remoting.AgentInstaller.CleanTemp(TimeSpan.Zero); // staged agent files may hold a device key
        if (_instance is not null) Remoting.AgentDesktopSession.CleanReceived(TimeSpan.FromDays(1));
        base.OnExit(e);
    }
}
