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
    public const string ToolTipText = PolicyReport.ManagedNote;

    public static void Apply(params FrameworkElement[] controls) => Lock(ToolTipText, controls);

    /// <summary>An option that belongs to a feature the policy blocks: greyed out the same way, with "Turned off by your organisation".</summary>
    public static void TurnedOff(params FrameworkElement[] controls) => Lock(Features.TurnedOff, controls);

    private static void Lock(string tip, FrameworkElement[] controls)
    {
        foreach (var c in controls)
        {
            c.IsEnabled = false;
            c.ToolTip = tip;
            ToolTipService.SetShowOnDisabled(c, true);
        }
    }
}
