using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using RemoteHub.Views;

namespace RemoteHub.Tests;

/// <summary>
/// The code editor (AvalonEdit) as UI Automation sees it: SetFocus lands in its text area instead of failing, and its
/// text is still the Value pattern's. Shown in a window off the screen.
/// </summary>
public class CodeEditorTests
{
    [Fact]
    public void Ui_Automation_focus_lands_in_the_text_area() => Sta.Run(w =>
    {
        var editor = new CodeEditor { Text = "Get-Date", Height = 100 };
        AutomationProperties.SetName(editor, "Script block");
        w.Content = editor;
        Sta.Pump();
        var peer = UIElementAutomationPeer.CreatePeerForElement(editor);

        peer.SetFocus(); // AvalonEdit's own peer threw "Operation is not valid due to the current state of the object"
        Sta.Pump();

        Assert.True(editor.TextArea.IsKeyboardFocused);
        Assert.True(peer.HasKeyboardFocus());
    });

    [Fact]
    public void Its_children_name_it_as_their_parent() => Sta.Run(w =>
    {
        var editor = new CodeEditor { Text = "Get-Date\nGet-Service", Height = 100 };
        w.Content = editor;
        Sta.Pump();
        var peer = UIElementAutomationPeer.CreatePeerForElement(editor);

        // a client walking the tree goes down through the children and up through their parent: the same peer both ways
        var children = peer.GetChildren() ?? [];
        Assert.NotEmpty(children);
        foreach (var child in children) Assert.Same(peer, child.GetParent());
    });

    [Fact]
    public void Its_text_is_still_read_through_the_value_pattern() => Sta.Run(w =>
    {
        var editor = new CodeEditor { Text = "Get-Service" };
        AutomationProperties.SetName(editor, "Markdown");
        w.Content = editor;
        Sta.Pump();
        var peer = UIElementAutomationPeer.CreatePeerForElement(editor);

        var value = Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value));
        Assert.Equal("Get-Service", value.Value);
        Assert.Equal("Markdown", peer.GetName());
    });
}
