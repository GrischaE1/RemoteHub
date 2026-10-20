using System.Threading.Tasks;
using RemoteHub.Agent;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.Views;

namespace RemoteHub.Tests;

/// <summary>
/// The Scripts view and its windows as the user sees them (docs/scripts-view-design.md §4–§6): the folders and the
/// library, the script pane for own and library scripts, the run window's list and its confirmation, and the comparison.
/// Runs in windows off the screen; nothing is run on a device.
/// </summary>
[Collection("SettingsStore")]
public class ScriptsViewTests : IDisposable
{
    public ScriptsViewTests()
    {
        Assert.StartsWith(Path.GetTempPath(), LocalDb.Directory); // isolation guard
        StoreReset.Clean();
        LocalDb.Open();
    }

    public void Dispose() => StoreReset.Clean();

    internal static void OnSta(Action<Window> test, double width = 1300, double height = 800)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? w = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                // No Application: as in the other view tests, the views find no app styles and fall back to the defaults.
                w = new Window { Width = width, Height = height, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
                test(w);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                w?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the view test didn't finish");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    internal static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Every element below <paramref name="root"/>, through the visual and the logical tree: without the app's styles a
    /// FluentWindow has no template, so what it shows is only reachable as logical children.
    /// </summary>
    internal static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var children = new List<DependencyObject>();
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) children.Add(VisualTreeHelper.GetChild(node, i));
            children.AddRange(LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>());
            foreach (var child in children)
                if (seen.Add(child)) { yield return child; stack.Push(child); }
        }
    }

    internal static T Named<T>(DependencyObject root, string name) where T : DependencyObject =>
        Tree(root).OfType<T>().FirstOrDefault(x => AutomationProperties.GetName(x) == name)
        ?? throw new InvalidOperationException($"no {typeof(T).Name} named \"{name}\"");

    private static ScriptsView Show(Window w, UiState? ui = null)
    {
        var host = new ScriptsHost(w, ui ?? new UiState(), () => { }, (_, _) => { }, _ => null, null);
        var view = new ScriptsView(host);
        w.Content = view;
        w.Show();
        Pump();
        return view;
    }

    internal static ScriptContent Ps(string body, string risk = ScriptStore.RiskR0) => new(body, "", ScriptStore.ContextSystem, risk, ScriptStore.OsBoth, 600);

    [SkippableFact]
    public void The_view_shows_my_folders_and_the_library_and_a_library_script_is_read_only()
    {
        Skip.IfNot(Environment.UserInteractive, "needs a desktop to show a window");
        OnSta(w =>
        {
            Show(w);
            var tree = Named<TreeView>(w, "Script folders");
            var top = tree.Items.OfType<TreeViewItem>().Select(i => AutomationProperties.GetName(i)).ToList();
            Assert.Contains("My scripts", top);
            Assert.Contains("Library", top);
            Assert.Contains("Recently run", top);
            // A library area lists its scripts; one opens read-only, with Duplicate instead of the version bar.
            var library = tree.Items.OfType<TreeViewItem>().Single(i => AutomationProperties.GetName(i) == "Library");
            library.IsExpanded = true;
            ((TreeViewItem)library.Items.OfType<TreeViewItem>().Single(i => AutomationProperties.GetName(i) == "Inventory and reporting")).IsSelected = true;
            Pump();
            var list = Named<ListBox>(w, "Scripts");
            Assert.Equal(20, list.Items.Count);
            list.SelectedItem = list.Items.OfType<ListBoxItem>().First(i => AutomationProperties.GetName(i) == "Pending reboot detector");
            Pump();
            Assert.True(Named<TextBox>(w, "Script name").IsReadOnly);
            Assert.True(Tree(w).OfType<ICSharpCode.AvalonEdit.TextEditor>().First(e => AutomationProperties.GetName(e) == "Script").IsReadOnly);
            Assert.Contains(Tree(w).OfType<Wpf.Ui.Controls.Button>(), b => b.Content as string == "Duplicate to My scripts" && b.IsVisible);
            Assert.DoesNotContain(Tree(w).OfType<Wpf.Ui.Controls.Button>(), b => b.Content as string == "Ready to deploy" && b.IsVisible);
        });
    }

    [SkippableFact]
    public void An_own_script_saves_a_draft_and_offers_ready_to_deploy_until_it_is_deployed()
    {
        Skip.IfNot(Environment.UserInteractive, "needs a desktop to show a window");
        var folder = ScriptStore.CreateFolder(null, "Inventory", "Blue");
        var id = ScriptStore.CreateScript(folder, "Disk report", ScriptStore.PowerShell, Ps("Get-Volume"));
        OnSta(w =>
        {
            var view = Show(w, new UiState { LastScriptFolderId = folder, LastScriptId = id });
            var deploy = Tree(w).OfType<Wpf.Ui.Controls.Button>().First(b => b.Content as string == "Ready to deploy");
            Assert.True(deploy.IsEnabled);
            var run = Tree(w).OfType<Wpf.Ui.Controls.Button>().First(b => b.Content as string == "Run on…");
            Assert.False(run.IsEnabled); // nothing deployed yet: nothing to run
            var editor = Tree(w).OfType<ICSharpCode.AvalonEdit.TextEditor>().First(e => AutomationProperties.GetName(e) == "Script");
            editor.Document.Insert(editor.Document.TextLength, "\nGet-Disk");
            view.Save();
            Assert.Equal("Get-Volume\nGet-Disk", ScriptStore.Draft(id)!.Body);
            Assert.Null(ScriptStore.Deployed(id));
        });
    }

    [SkippableFact]
    public void The_run_window_lists_only_deployed_powershell_scripts_and_an_r2_script_needs_the_device_name()
    {
        Skip.IfNot(Environment.UserInteractive, "needs a desktop to show a window");
        var folder = ScriptStore.CreateFolder(null, "Ops", "Blue");
        var deployed = ScriptStore.CreateScript(folder, "Restart spooler", ScriptStore.PowerShell, Ps("Restart-Service Spooler", ScriptStore.RiskR2));
        ScriptStore.Deploy(deployed, "", "x");
        ScriptStore.CreateScript(folder, "Draft only", ScriptStore.PowerShell, Ps("Get-Date"));
        var sql = ScriptStore.CreateScript(folder, "Query", "tsql", ScriptContent.Empty("tsql"));
        ScriptStore.Deploy(sql, "", "x");
        OnSta(w =>
        {
            var ui = new UiState();
            // Port 9 on this PC: nothing answers, so the device question fails quickly and nothing is run.
            var target = new PsTarget("127.0.0.1", false, new CredentialSet(), Agent: null);
            var run = new RunScriptWindow(new RunScriptContext("node", "SRV01", target, ui, () => { })) { Left = -20000, Top = -20000, ShowActivated = false };
            run.Show();
            Pump();
            var list = Named<ListBox>(run, "Scripts");
            var names = list.Items.OfType<ListBoxItem>().Select(i => AutomationProperties.GetName(i)).ToList();
            Assert.Contains("Restart spooler", names);
            Assert.DoesNotContain("Draft only", names);
            Assert.DoesNotContain("Query", names);
            Assert.Contains("Pending reboot detector", names); // the library
            Assert.Equal("Restart spooler", names[0]); // own scripts before the library

            // Choosing the R2 script: Run waits for the device's name.
            list.SelectedIndex = 0;
            Tree(run).OfType<Wpf.Ui.Controls.Button>().First(b => b.Content as string == "Next").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump();
            var runButton = Tree(run).OfType<Wpf.Ui.Controls.Button>().First(b => b.Content as string == "Run");
            Assert.False(runButton.IsEnabled);
            var confirm = Named<Wpf.Ui.Controls.TextBox>(run, "Device name to confirm");
            confirm.Text = "srv01";
            Assert.True(runButton.IsEnabled);
            var context = Named<ComboBox>(run, "Runs as");
            Assert.Equal(4, context.Items.Count);
            run.Close();
        });
    }

    [SkippableFact]
    public async Task The_comparison_counts_changes_and_folds_unchanged_lines()
    {
        Skip.IfNot(Environment.UserInteractive, "needs a desktop to show a window");
        await Task.CompletedTask;
        OnSta(w =>
        {
            var diff = new DiffView();
            w.Content = diff;
            w.Show();
            var older = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"Write-Output {i}"));
            var newer = older.Replace("Write-Output 20", "Write-Output 'twenty'");
            var shown = diff.Show(older, newer, ScriptStore.PowerShell);
            while (!shown.IsCompleted) Pump();
            Pump();
            Assert.Equal(1, diff.ChangeCount);
            var left = Tree(w).OfType<ICSharpCode.AvalonEdit.TextEditor>().First(e => AutomationProperties.GetName(e) == "Older version");
            Assert.Contains("⋯ 16 unchanged lines", left.Text);
            Assert.Contains("⋯ 17 unchanged lines", left.Text);
            diff.Inline = true;
            Pump();
            Assert.Contains("Write-Output 20\nWrite-Output 'twenty'", left.Text.Replace("\r\n", "\n"));
        });
    }
}


/// <summary>The run window under this PC's policy (process-wide, so these run alone).</summary>
[Collection("Policies")]
public class ScriptRunPolicyViewTests : IDisposable
{
    private sealed class FakeSource(Dictionary<string, int> values) : IPolicySource
    {
        public object? Read(PolicyScope scope, string key, string name) =>
            scope == PolicyScope.Computer && key.Equals(PolicyCatalog.Root, StringComparison.OrdinalIgnoreCase) && values.TryGetValue(name, out var v) ? v : null;
    }

    public ScriptRunPolicyViewTests()
    {
        Assert.StartsWith(Path.GetTempPath(), LocalDb.Directory); // isolation guard
        StoreReset.Clean();
        LocalDb.Open();
    }

    public void Dispose()
    {
        AppPolicy.Source = RegistryPolicySource.Instance;
        StoreReset.Clean();
    }

    [SkippableFact]
    public void Blocked_contexts_arent_offered()
    {
        Skip.IfNot(Environment.UserInteractive, "needs a desktop to show a window");
        var folder = ScriptStore.CreateFolder(null, "Ops", null);
        var id = ScriptStore.CreateScript(folder, "Check", ScriptStore.PowerShell, ScriptsViewTests.Ps("Get-Date"));
        ScriptStore.Deploy(id, "", "x");
        AppPolicy.Source = new FakeSource(new Dictionary<string, int> { ["AllowScriptRunSystem"] = 0 });
        ScriptsViewTests.OnSta(w =>
        {
            var entry = ScriptCatalog.Find(id)!;
            var run = new RunScriptWindow(new RunScriptContext("node", "SRV01", new PsTarget("127.0.0.1", false, new CredentialSet()), new UiState(), () => { }, entry))
                { Left = -20000, Top = -20000, ShowActivated = false };
            run.Show();
            ScriptsViewTests.Pump();
            var contexts = ScriptsViewTests.Named<ComboBox>(run, "Runs as").Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag).ToList();
            Assert.Equal([ScriptStore.ContextUser, ScriptStore.ContextAsMe], contexts); // no System, so no Both
            run.Close();
        });
    }
}
