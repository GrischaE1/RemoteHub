using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using RemoteHub.Models;
using RemoteHub.Views;

namespace RemoteHub.Tests;

/// <summary>What the install dialog and the settings say about enrollment now that the device makes its own key (SEC-ENR-03).</summary>
public class AgentEnrollmentViewTests
{
    private static List<string> Texts(DependencyObject root) =>
        Sta.All<TextBlock>(root).Select(t => t.Text).Concat(Sta.All<CheckBox>(root).Select(c => c.Content as string ?? "")).Where(t => t.Length > 0).ToList();

    private static AgentInstallDialog Dialog(Window owner, Connection c) =>
        Sta.Theme(new AgentInstallDialog(c, remotingAvailable: true, agentReachable: false, localAddress: null)
            { Owner = owner, Left = -4000, WindowStartupLocation = WindowStartupLocation.Manual });

    [Fact]
    public void The_install_dialog_says_the_device_makes_its_own_certificate() => Sta.Run(owner =>
    {
        var dialog = Dialog(owner, new Connection { Name = "WKS-07", Host = "wks-07.contoso.test" });
        dialog.Show();
        Sta.Pump();
        var texts = Texts(dialog);
        Assert.Contains(texts, t => t.Contains("The device makes its own certificate when it is installed"));
        Assert.DoesNotContain(texts, t => t.Contains("device certificate created here"));
        Assert.DoesNotContain(texts, t => t.Contains("device key"));
        dialog.Close();
    });

    [Fact]
    public void A_connection_waiting_for_its_claim_shows_no_certificate() => Sta.Run(owner =>
    {
        var c = new Connection { Name = "WKS-07", Host = "wks-07", Agent = new AgentDevice { Port = 58800, Status = AgentStatus.NotInstalled } };
        var dialog = Dialog(owner, c);
        dialog.Show();
        Sta.Pump();
        var texts = Texts(dialog);
        Assert.Contains("Prepared, waiting for the device to answer: port 58800.", texts);
        Assert.DoesNotContain(texts, t => t.Contains("certificate …"));
        Assert.Contains("Enroll again – the device makes a new certificate (the current one stops working)", texts);
        dialog.Close();
    });

    [Fact]
    public void The_settings_say_which_enrollment_secrets_are_accepted()
    {
        var keys = new AgentEnrollmentKeys();
        Assert.StartsWith("No enrollment secret yet", EditorWindow.EnrollmentStatus(keys));
        keys.EnsureCurrent(DateTime.UtcNow);
        Assert.DoesNotContain("earlier", EditorWindow.EnrollmentStatus(keys));
        keys.Replace(DateTime.UtcNow, revokeEarlier: false);
        Assert.EndsWith("1 earlier secret is still accepted.", EditorWindow.EnrollmentStatus(keys));
        keys.Replace(DateTime.UtcNow, revokeEarlier: false);
        Assert.EndsWith("2 earlier secrets are still accepted.", EditorWindow.EnrollmentStatus(keys));
        keys.RevokeEarlier(DateTime.UtcNow);
        Assert.DoesNotContain("earlier", EditorWindow.EnrollmentStatus(keys));
    }
}
