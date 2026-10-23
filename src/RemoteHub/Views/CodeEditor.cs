using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;

namespace RemoteHub.Views;

/// <summary>
/// AvalonEdit's editor, with focus through UI Automation working. The editor hands the keyboard focus on to its inner
/// text area, so WPF's SetFocus on the editor (Focus() must leave the element itself focused) always failed with "Operation
/// is not valid due to the current state of the object" – for screen readers and every other UI Automation client
/// (lab UI-NOTE-03, run 20261022-061254). Everything else is AvalonEdit's own automation: its peer does the work.
/// </summary>
public class CodeEditor : TextEditor
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this, base.OnCreateAutomationPeer());

    private sealed class Peer(CodeEditor owner, AutomationPeer inner) : FrameworkElementAutomationPeer(owner)
    {
        public override object GetPattern(PatternInterface patternInterface) => inner.GetPattern(patternInterface);
        protected override AutomationControlType GetAutomationControlTypeCore() => inner.GetAutomationControlType();
        protected override string GetClassNameCore() => inner.GetClassName();
        protected override List<AutomationPeer>? GetChildrenCore() => inner.GetChildren();
        // the focus is in the text area, which is the editor as far as a client can tell
        protected override bool HasKeyboardFocusCore() => owner.IsKeyboardFocusWithin;

        protected override void SetFocusCore()
        {
            var area = owner.TextArea;
            if (Keyboard.Focus(area) != area) base.SetFocusCore();   // still refused (disabled, hidden): WPF's own error
        }
    }
}
