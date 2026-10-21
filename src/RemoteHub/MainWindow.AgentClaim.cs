using System.Threading;
using System.Windows;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.Views;

namespace RemoteHub;

/// <summary>
/// Claiming a device's RemoteHub Agent (docs/agent-spec.md §5.5, SEC-ENR-03): the device makes its own key when it is
/// enrolled, and RemoteHub pins its certificate the first time the agent proves it was enrolled with this RemoteHub's
/// enrollment secret. A pin is never replaced by an automatic claim – only by "Claim RemoteHub Agent again…", which
/// asks first.
/// </summary>
public partial class MainWindow
{
    /// <summary>The enrollment secret new enrollment files carry – made on first use and saved with the settings.</summary>
    private AgentEnrollmentKey EnrollmentKey()
    {
        var keys = _vm.Data.AgentEnrollment;
        if (keys.Current is { } current) return current;
        var made = keys.EnsureCurrent(DateTime.UtcNow);
        _vm.RequestSave();
        Data.AuditLog.Write(Data.AuditEvents.AgentEnrollment, null, "Enrollment secret created");
        return made;
    }

    /// <summary>
    /// Settings → Security &amp; storage → "Save enrollment file…": the file for Intune, software distribution or installing
    /// by hand, with this PC's install defaults (policy AgentInstall) and the device's computer name as its label.
    /// </summary>
    private async Task SaveEnrollmentFile(Window owner)
    {
        if (Refused(Feature.Agent)) return;
        var pick = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the RemoteHub Agent enrollment file",
            FileName = "RemoteHub" + AgentEnrollment.Extension,
            Filter = $"RemoteHub Agent enrollment (*{AgentEnrollment.Extension})|*{AgentEnrollment.Extension}",
        };
        if (pick.ShowDialog(owner) != true) return;
        var key = EnrollmentKey();
        var groups = AgentInstaller.ParseGroups(string.Join(",", AppPolicy.AgentInstall("AllowedGroups") as string[] ?? []));
        var scope = (AppPolicy.AgentInstall("FirewallScope") as string)?.Replace(';', ',');
        var display = AppPolicy.AgentInstall("VirtualDisplay") is int vd ? vd == 1 : (bool?)null;
        try
        {
            var file = AgentEnrollment.Create(key.Id.ToString(), key.Secret, "", AgentInstallDefaultPort(), groups, scope, virtualDisplay: display);
            await File.WriteAllBytesAsync(pick.FileName, file.Serialize());
        }
        catch (Exception ex)
        {
            await Dialogs.Info(owner, "Enrollment file", "The enrollment file couldn't be saved: " + ex.Message);
            return;
        }
        Data.AuditLog.Write(Data.AuditEvents.AgentEnrollment, null, "Enrollment file saved");
        await Dialogs.Info(owner, "Enrollment file",
            $"Saved {Path.GetFileName(pick.FileName)}. Put it in the same folder as {AgentInstaller.PackageName} (or pass ENROLLMENT=<path> to msiexec) " +
            "and deploy both. Each device makes its own certificate; RemoteHub claims an agent when it first meets it – at an RDP sign-in, or with " +
            "right-click → RemoteHub Agent → Claim RemoteHub Agent… on a connection or folder.\n\nKeep the file out of reach and delete copies you " +
            "no longer need: anyone with it can enroll a device of their own.");
    }

    /// <summary>"Replace enrollment secret…": a new secret for new files – the earlier one revoked or still accepted, as chosen.</summary>
    private async Task ReplaceEnrollmentSecret(Window owner)
    {
        if (Refused(Feature.Agent)) return;
        var choice = await Dialogs.Choose(owner, "Replace enrollment secret",
            "New enrollment files get a new secret. Agents RemoteHub has already claimed keep working either way.\n\n" +
            "Revoke the current secret at once (for a file that got out of hand: devices installed with it but not claimed yet can't be " +
            "claimed any more), or keep accepting it until you revoke it (while deployments with the current file are still running)?",
            "Replace and revoke", "Replace, keep accepting", "Cancel");
        if (choice is not (Wpf.Ui.Controls.MessageBoxResult.Primary or Wpf.Ui.Controls.MessageBoxResult.Secondary)) return;
        var revoke = choice == Wpf.Ui.Controls.MessageBoxResult.Primary;
        _vm.Data.AgentEnrollment.Replace(DateTime.UtcNow, revokeEarlier: revoke);
        _vm.RequestSave();
        Data.AuditLog.Write(Data.AuditEvents.AgentEnrollment, null, revoke ? "Enrollment secret replaced, earlier ones revoked" : "Enrollment secret replaced");
        await Dialogs.Info(owner, "Replace enrollment secret", "Done – save a new enrollment file for new installs.");
    }

    /// <summary>"Revoke earlier secrets…": only the current secret is accepted from now on.</summary>
    private async Task RevokeEnrollmentSecrets(Window owner)
    {
        if (Refused(Feature.Agent)) return;
        var keys = _vm.Data.AgentEnrollment;
        if (keys.Earlier.Count == 0) return;
        if (!await Dialogs.Confirm(owner, "Revoke earlier secrets",
                "Devices installed with an earlier enrollment file and not claimed yet can't be claimed any more – install them again with the " +
                "current file. Agents RemoteHub has already claimed keep working.", "Revoke"))
            return;
        var n = keys.RevokeEarlier(DateTime.UtcNow);
        _vm.RequestSave();
        Data.AuditLog.Write(Data.AuditEvents.AgentEnrollment, null, $"Earlier enrollment secrets revoked ({n})");
        await Dialogs.Info(owner, "Revoke earlier secrets", n == 1 ? "1 earlier secret is revoked." : $"{n} earlier secrets are revoked.");
    }

    /// <summary>An agent prepared for a connection and waiting for its claim: no device ID and no fingerprint yet.</summary>
    private static AgentDevice NewPendingAgent(string label, int port) =>
        new() { Label = label, Port = port, Status = AgentStatus.NotInstalled, CreatedUtc = DateTime.UtcNow };

    /// <summary>The agent waits for its claim: prepared here (or expected from software distribution), nothing pinned yet.</summary>
    internal static bool AwaitsClaim(AgentDevice? d) => d is { Status: AgentStatus.NotInstalled, Fingerprint.Length: 0 };

    /// <summary>
    /// One claim of <paramref name="c"/>'s agent at <paramref name="d"/>'s port – through its jump host, if any (only with
    /// a known password unless <paramref name="quiet"/> is false). Null when the agent couldn't be reached.
    /// </summary>
    private async Task<AgentInstaller.ClaimResult?> ClaimOnce(Connection c, AgentDevice d, bool quiet = true)
    {
        if (!TryRoute(c, quiet, out var via)) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(via is null ? 8 : 30));
            return await AgentInstaller.ClaimAsync(c.Host, d.Port, via, _vm.Data.AgentEnrollment, cts.Token);
        }
        catch (Exception) { return null; } // not installed yet, or not reachable from here
    }

    /// <summary>
    /// A claim that may be pinned for <paramref name="d"/>: a full match, and not the certificate this enrollment replaces –
    /// that is the old agent, still answering until the new one is installed.
    /// </summary>
    internal static bool IsNewClaim(AgentDevice d, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] AgentInstaller.ClaimResult? r) =>
        r is { Outcome: AgentInstaller.ClaimOutcome.Claimed, Claim: { } claim }
        && !string.Equals(claim.Fingerprint, d.ReplacesFingerprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Claims every 3 s until <paramref name="within"/> has passed: the first claim of a new certificate
    /// (<see cref="IsNewClaim"/>), else the last answer (null if none came).
    /// </summary>
    private async Task<AgentInstaller.ClaimResult?> ClaimWithin(Connection c, AgentDevice d, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        AgentInstaller.ClaimResult? last = null;
        while (true)
        {
            if (await ClaimOnce(c, d) is { } r)
            {
                if (IsNewClaim(d, r)) return r;
                last = r;
            }
            if (DateTime.UtcNow >= until) return last;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// Pins a claimed agent on <paramref name="c"/>: the device's certificate and ID, installed – and switches the
    /// connection to the agent when <paramref name="useAgent"/>.
    /// </summary>
    private void Pin(Connection c, AgentDevice d, AgentClaim claim, bool useAgent, string how)
    {
        d.Fingerprint = claim.Fingerprint;
        d.ReplacesFingerprint = null;
        d.DeviceId = claim.DeviceId;
        if (d.Label.Length == 0) d.Label = claim.ComputerName;
        d.Status = AgentStatus.Installed;
        d.InstalledUtc = DateTime.UtcNow;
        c.Agent = d;
        if (useAgent && !UsesAgent(c)) c.Options.UseAgent = true;
        c.PsRemoting = null; // test again the new way
        c.RefreshLabels();
        _vm.RequestSave();
        Data.AuditLog.Write(Data.AuditEvents.AgentInstalled, c, $"Claimed – {how}, certificate SHA-256 …{AgentInstaller.Tail(claim.Fingerprint, 16)}, device {claim.ComputerName}");
        DiagLog.Write("RemoteHub Agent: claimed – " + how);
    }

    /// <summary>
    /// After an install by hand, by clipboard or by software distribution: claims the agent every 10 s, for up to 30
    /// minutes. An answer that can't be pinned yet – no agent, an older agent without a secret, the old certificate of a
    /// device enrolled again – may be the device before its install, so the polling goes on. Until then the connection
    /// keeps working the way it did; later, opening it or "Claim RemoteHub Agent" tries again (<see cref="ClaimPending"/>).
    /// </summary>
    private async Task WaitForClaim(Connection c, AgentDevice d, bool useAgent)
    {
        var until = DateTime.UtcNow.AddMinutes(30);
        AgentInstaller.ClaimOutcome? logged = null;
        while (DateTime.UtcNow < until && c.Agent == d && AwaitsClaim(d))
        {
            var r = await ClaimOnce(c, d);
            if (c.Agent != d || !AwaitsClaim(d)) return; // replaced or claimed meanwhile
            if (IsNewClaim(d, r))
            {
                Pin(c, d, r!.Claim!, useAgent, "installed by hand");
                return;
            }
            if (r is not null && r.Outcome != logged) DiagLog.Write($"RemoteHub Agent: not claimed yet ({(logged = r.Outcome)})");
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>At start: one quiet claim for each connection whose agent still waits for it (installed while RemoteHub was closed).</summary>
    private void ClaimPending()
    {
        foreach (var c in _vm.AllNodes().OfType<Connection>().Where(c => AwaitsClaim(c.Agent)).ToList())
            _ = ClaimQuietly(c, c.Agent!, useAgent: AgentInstallDefaultUseAgent());
    }

    private async Task ClaimQuietly(Connection c, AgentDevice d, bool useAgent)
    {
        if (await ClaimOnce(c, d) is var r && IsNewClaim(d, r) && c.Agent == d && AwaitsClaim(d))
            Pin(c, d, r!.Claim!, useAgent, "claimed when it answered");
    }

    /// <summary>
    /// The first RDP sign-in of a connection without an agent: if a RemoteHub Agent enrolled with this RemoteHub's file
    /// (Intune, software distribution) answers on the device, it is claimed – quietly, and only on a full match.
    /// </summary>
    private void AutoClaim(Connection c)
    {
        if (c.Agent is not null || _vm.Data.AgentEnrollment.Keys.Count == 0 || !Features.IsAllowed(Feature.Agent)) return;
        if (!_autoClaimed.Add(c.Id)) return; // once per run and connection
        var d = NewPendingAgent("", AgentInstallDefaultPort());
        _ = Claim();

        async Task Claim()
        {
            if (await ClaimOnce(c, d) is { Outcome: AgentInstaller.ClaimOutcome.Claimed } r && c.Agent is null)
                Pin(c, d, r.Claim!, AgentInstallDefaultUseAgent(), "found at sign-in");
        }
    }

    private readonly HashSet<Guid> _autoClaimed = [];

    /// <summary>The port an agent installed with the enrollment file listens on: this PC's install default (policy), else the agent's default.</summary>
    private static int AgentInstallDefaultPort() => AppPolicy.AgentInstall("Port") as int? ?? AgentProtocol.DefaultPort;

    /// <summary>Whether a claimed agent is used for the connection afterwards: this PC's install default (policy), else yes – as in the install dialog.</summary>
    private static bool AgentInstallDefaultUseAgent() => AppPolicy.AgentInstall("UseAgentAfterwards") is not int u || u == 1;

    /// <summary>
    /// "Claim RemoteHub Agent…": for an agent installed with the enrollment file outside RemoteHub's install dialog, or
    /// one whose claim was refused. On an installed agent ("…again") it replaces the pin – after showing old and new.
    /// </summary>
    private async Task ClaimAgent(Connection c)
    {
        if (Refused(Feature.Agent)) return;
        var existing = c.Agent;
        var installed = existing is { Status: AgentStatus.Installed, Fingerprint.Length: > 0 };
        var d = installed || existing is null ? NewPendingAgent(existing?.Label ?? "", existing?.Port ?? AgentInstallDefaultPort()) : existing!;
        const string title = "Claim RemoteHub Agent";
        if (await ClaimOnce(c, d, quiet: false) is not { } r)
        {
            await Dialogs.Info(this, title, $"No RemoteHub Agent answers on {c.Name} (port {d.Port}). Check that it is installed with this RemoteHub's " +
                                            "enrollment file and that the device's firewall allows connections from this PC.");
            return;
        }
        if (r.Outcome == AgentInstaller.ClaimOutcome.OtherName
            && !await Dialogs.Confirm(this, title, $"The agent at {c.Name}'s address is the device {r.Claim!.ComputerName} ({r.Claim.DnsHostName}), not " +
                                                   $"{c.Host.Trim()}. It proved that it was enrolled with this RemoteHub's enrollment file.\n\nIf {c.Host.Trim()} is " +
                                                   $"another name for that device, claim it anyway?", "Claim anyway"))
            return;
        if (r.Outcome is not (AgentInstaller.ClaimOutcome.Claimed or AgentInstaller.ClaimOutcome.OtherName))
        {
            await Dialogs.Info(this, title, AgentInstaller.Explain(r, c.Name));
            return;
        }
        var claim = r.Claim!;
        if (!installed && !IsNewClaim(d, new(AgentInstaller.ClaimOutcome.Claimed, claim)))
        {
            await Dialogs.Info(this, title, $"{c.Name}'s agent still answers with the certificate the new enrollment replaces – the install " +
                                            "hasn't run on the device yet. RemoteHub claims the new agent once it answers.");
            return;
        }
        if (installed && string.Equals(claim.Fingerprint, existing!.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            await Dialogs.Info(this, title, $"The RemoteHub Agent of {c.Name} already answers with the pinned certificate (SHA-256 …{AgentInstaller.Tail(claim.Fingerprint, 16)}).");
            return;
        }
        if (installed && !await Dialogs.Confirm(this, title,
                $"{c.Name}'s agent now answers with another certificate:\n\n  pinned:  …{AgentInstaller.Tail(existing!.Fingerprint, 16)}\n  now:       …{AgentInstaller.Tail(claim.Fingerprint, 16)}\n\n" +
                "Claim it only if the agent was installed again on purpose. Otherwise another device may be answering at this address.", "Replace the pin"))
            return;
        d.VirtualDisplay = existing?.VirtualDisplay;
        d.CredentialProvider = existing?.CredentialProvider;
        Pin(c, d, claim, useAgent: AgentInstallDefaultUseAgent(), installed ? "claimed again" : "claimed from the menu");
        await Dialogs.Info(this, title, AgentInstaller.Explain(new(AgentInstaller.ClaimOutcome.Claimed, claim), c.Name));
    }

    /// <summary>
    /// "Claim RemoteHub Agents…" on a folder: every connection in it without a pinned agent is asked once (a few at a time);
    /// those whose agent proves it was enrolled with this RemoteHub's file are pinned. Afterwards a summary.
    /// </summary>
    private async Task ClaimFolder(Folder f)
    {
        if (Refused(Feature.Agent)) return;
        var targets = f.Descendants().OfType<Connection>().Where(c => c.Agent is null || AwaitsClaim(c.Agent)).ToList();
        const string title = "Claim RemoteHub Agents";
        if (targets.Count == 0)
        {
            await Dialogs.Info(this, title, $"Every connection in {f.Name} already has a RemoteHub Agent.");
            return;
        }
        var useAgent = AgentInstallDefaultUseAgent();
        var claimed = new List<string>();
        var refused = new List<string>();
        var silent = 0;
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(targets.Select(async c =>
        {
            await gate.WaitAsync();
            try
            {
                var d = c.Agent ?? NewPendingAgent("", AgentInstallDefaultPort());
                var r = await ClaimOnce(c, d);
                if (r is null) { silent++; return; }
                if (IsNewClaim(d, r) && (c.Agent is null || c.Agent == d))
                {
                    Pin(c, d, r.Claim!, useAgent, "claimed for the folder");
                    claimed.Add(c.Name);
                }
                else refused.Add(r.Outcome == AgentInstaller.ClaimOutcome.Claimed
                    ? $"{c.Name}'s agent still answers with the certificate the new enrollment replaces – the install hasn't run yet."
                    : AgentInstaller.Explain(r, c.Name));
            }
            finally { gate.Release(); }
        }));
        var text = $"{claimed.Count} of {targets.Count} claimed{(claimed.Count > 0 ? ": " + string.Join(", ", claimed.Order()) : "")}.";
        if (silent > 0) text += $"\n{silent} didn't answer (no agent, or not reachable from this PC).";
        if (refused.Count > 0) text += "\n\nNot claimed:\n" + string.Join("\n", refused.Order());
        await Dialogs.Info(this, title, text);
    }
}
