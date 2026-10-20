using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RemoteHub.Tests;

/// <summary>Runs view tests on an STA thread with a window shown off screen, so layout, keys and header clicks work.</summary>
internal static class Sta
{
    public static void Run(Action<Window> test, int timeoutSeconds = 30)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var window = new Window
            {
                Width = 900, Height = 600, Left = -4000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None,
            };
            // The app's Fluent theme, so WPF-UI controls get their templates (rows in the visual tree, headers, …).
            window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            try
            {
                window.Show();
                test(window);
            }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(timeoutSeconds)), "the view test didn't finish");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>Lets the dispatcher run what is queued (layout, bindings, continuations).</summary>
    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>A key press on <paramref name="target"/> (KeyDown), as the keyboard would raise it.</summary>
    public static void Key(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("The element isn't shown.");
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    /// <summary>The first element of type <typeparamref name="T"/> below <paramref name="root"/> (visual tree) that matches.</summary>
    public static T Find<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject =>
        All<T>(root).FirstOrDefault(e => match?.Invoke(e) ?? true) ?? throw new InvalidOperationException($"No {typeof(T).Name} found.");

    public static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in All<T>(child)) yield return d;
        }
    }
}
