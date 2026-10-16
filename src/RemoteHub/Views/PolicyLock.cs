using System.Windows;
using System.Windows.Controls;
using RemoteHub.Services;

namespace RemoteHub.Views;

/// <summary>
/// A setting configured by policy stays visible, shows the policy's value and is greyed out, with a tooltip that says why
/// (docs/admx-design.md P2). Actions a policy blocks are hidden instead – that is up to whoever builds the menu.
/// </summary>
internal static class PolicyLock
{
    public const string ToolTipText = PolicyReport.ManagedText + " – see Settings → Policies.";

    public static void Apply(params FrameworkElement[] controls)
    {
        foreach (var c in controls)
        {
            c.IsEnabled = false;
            c.ToolTip = ToolTipText;
            ToolTipService.SetShowOnDisabled(c, true);
        }
    }
}
