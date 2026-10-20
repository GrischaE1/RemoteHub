using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteHub.Services;
using RemoteHub.Views;

namespace RemoteHub.Tests;

/// <summary>
/// Notes: the page editor as the user works with it – paste and typing inside a script block stay the script block's,
/// and View Markdown shows the page as Markdown text and brings edits back formatted. Runs in a window off the screen;
/// nothing touches the clipboard.
/// </summary>
public class NoteEditorTests
{
    private const string Page = "Some text.\n\n```powershell\nGet-Date\n```\n";

    /// <summary>Runs <paramref name="test"/> on an STA thread with the editor shown in a window (layout done).</summary>
    private static void WithEditor(string markdown, Action<NoteEditor, Window> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? w = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var editor = new NoteEditor();
                w = new Window { Content = editor, Width = 800, Height = 600, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
                w.Show();
                editor.Load(markdown, []);
                Pump();
                test(editor, w);
            }
            catch (Exception ex) { failure = ex; }
            finally { w?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the editor test didn't finish");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        var count = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Tree(child)) yield return d;
        }
    }

    private static T Find<T>(DependencyObject root, Func<T, bool>? where = null) where T : DependencyObject =>
        Tree(root).OfType<T>().First(x => where?.Invoke(x) ?? true);

    private static ICSharpCode.AvalonEdit.Editing.TextArea ScriptTextArea(NoteEditor editor) =>
        Find<ICSharpCode.AvalonEdit.Editing.TextArea>(Find<ScriptBlock>(editor));

    private static DataObjectPastingEventArgs Paste(UIElement on, string text)
    {
        var args = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, text), false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
        on.RaiseEvent(args);
        return args;
    }

    [SkippableFact]
    public void A_paste_into_a_script_block_is_left_to_the_script_block()
    {
        Skip.IfNot(Environment.UserInteractive, "needs a desktop to show a window");
        WithEditor(Page, (editor, _) =>
        {
            var area = ScriptTextArea(editor);
            Assert.True(editor.FromEmbedded(area));
            // The page's paste handler sees the event (it bubbles up) but must not cancel it.
            Assert.False(Paste(area, "Get-Service").CommandCancelled);
            // A paste into the page itself is the page's (reduced to what notes support).
            var box = Find<RichTextBox>(editor);
            Assert.False(editor.FromEmbedded(box));
            Assert.True(Paste(box, "plain").CommandCancelled);
        });
    }

    [SkippableFact]
    public void Markdown_typed_in_a_script_block_doesnt_format_the_page()
    {
        Skip.IfNot(Environment.UserInteractive, "needs a desktop to show a window");
        WithEditor("\\#\n\n```text\nx\n```\n", (editor, _) => // a paragraph with the text "#"
        {
            var box = Find<RichTextBox>(editor);
            var first = (Paragraph)box.Document.Blocks.FirstBlock;
            box.CaretPosition = first.ContentEnd;
            var area = ScriptTextArea(editor);
            var fromScript = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, area, " ")) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
            area.RaiseEvent(fromScript);
            Assert.False(fromScript.Handled);
            Assert.Null(first.Tag); // still a paragraph: "# " typed in the script block isn't a heading of the page
            // Typed in the page, "# " is a heading (the same handler, so the guard is what made the difference).
            var fromPage = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, box, " ")) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
            box.RaiseEvent(fromPage);
            Assert.True(fromPage.Handled);
            Assert.Equal(NoteMarkdown.H1, ((Paragraph)box.Document.Blocks.FirstBlock).Tag);
        });
    }

    [SkippableFact]
    public void View_Markdown_shows_the_page_as_Markdown_and_brings_edits_back_formatted()
    {
        Skip.IfNot(Environment.UserInteractive, "needs a desktop to show a window");
        WithEditor(Page, (editor, _) =>
        {
            var changed = 0;
            editor.Changed += () => changed++;
            var bold = Find<Wpf.Ui.Controls.Button>(editor, b => System.Windows.Automation.AutomationProperties.GetName(b) == "Bold (Ctrl+B)");
            editor.SetMarkdownView(true);
            Pump();
            Assert.True(editor.IsMarkdownView);
            var source = Find<ICSharpCode.AvalonEdit.TextEditor>(editor, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Markdown");
            Assert.Equal(Visibility.Visible, source.Visibility);
            Assert.Equal(Page, source.Text.Replace("\r\n", "\n"));
            Assert.False(bold.IsEnabled); // formatting buttons are off while the text shows
            Assert.Equal(0, changed);   // only looking changes nothing

            source.Text = Page + "\n## Added\n";
            Assert.True(changed > 0);
            Assert.Equal(Page + "\n## Added\n", editor.Markdown());

            editor.SetMarkdownView(false);
            Pump();
            Assert.False(editor.IsMarkdownView);
            Assert.True(bold.IsEnabled);
            var box = Find<RichTextBox>(editor);
            Assert.Contains(box.Document.Blocks.OfType<Paragraph>(), p => p.Tag as string == NoteMarkdown.H2 && new TextRange(p.ContentStart, p.ContentEnd).Text == "Added");
            Assert.Equal(Page + "\n## Added\n", editor.Markdown());
        });
    }

    [SkippableFact]
    public void View_Markdown_stays_on_for_the_next_page_and_starts_its_undo_history_empty()
    {
        Skip.IfNot(Environment.UserInteractive, "needs a desktop to show a window");
        WithEditor(Page, (editor, _) =>
        {
            editor.SetMarkdownView(true);
            editor.Load("# Other page\n", []);
            Pump();
            var source = Find<ICSharpCode.AvalonEdit.TextEditor>(editor, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Markdown");
            Assert.Equal("# Other page\n", source.Text.Replace("\r\n", "\n"));
            Assert.False(source.CanUndo);
            Assert.Equal("# Other page\n", editor.Markdown());
        });
    }

    [SkippableFact]
    public void Leaving_View_Markdown_without_an_edit_keeps_the_page_as_it_was()
    {
        Skip.IfNot(Environment.UserInteractive, "needs a desktop to show a window");
        WithEditor(Page, (editor, _) =>
        {
            var box = Find<RichTextBox>(editor);
            var before = box.Document;
            editor.SetMarkdownView(true);
            editor.SetMarkdownView(false);
            Assert.Same(before, box.Document); // not rebuilt: the formatted page's own undo history is kept
        });
    }
}
