using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Remoting;

/// <summary>
/// The lab's RemoteHub enrollment secret (docs/agent-spec.md §5.5) – what RemoteHub keeps in its settings – and the claim
/// as RemoteHub makes it. The secret is kept next to the harness (LabRoot\harness\lab-enrollment.json, outside the
/// repository), so every command of a run uses the same one; --keys names another file.
/// </summary>
internal static class LabEnrollment
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The lab's enrollment secrets, made once (by the first harness that needs them, also when several start at once).</summary>
    public static AgentEnrollmentKeys Keys(Dictionary<string, string> o)
    {
        var path = o.GetValueOrDefault("keys") ?? Path.Combine(AppContext.BaseDirectory, "lab-enrollment.json");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) return JsonSerializer.Deserialize<AgentEnrollmentKeys>(File.ReadAllBytes(path), Json) ?? throw new InvalidDataException(path + " is empty");
                var keys = new AgentEnrollmentKeys();
                keys.EnsureCurrent(DateTime.UtcNow);
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); // another harness made it first: read that one
                JsonSerializer.Serialize(file, keys, Json);
                return keys;
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
            catch (JsonException) when (attempt < 20) { Thread.Sleep(100); } // being written by another harness
        }
    }

    /// <summary>A .rhagent file with the lab's current secret, as RemoteHub writes it (the install dialog's fields, or none for the settings' file).</summary>
    public static AgentEnrollment Create(Dictionary<string, string> o, string label, int port, IEnumerable<string>? groups, string? scope, bool? virtualDisplay = null)
    {
        var key = Keys(o).Current!;
        return AgentEnrollment.Create(key.Id.ToString(), key.Secret, label, port, groups, string.IsNullOrWhiteSpace(scope) ? null : scope, virtualDisplay: virtualDisplay);
    }

    /// <summary>
    /// claim --host .. [--port n] [--name <host name RemoteHub knows the device by>] [--revoke-current 1]: AgentInstaller.ClaimAsync,
    /// as RemoteHub claims an agent. --name checks the device against another name than the address (a DNS name that
    /// points elsewhere); --revoke-current judges the claim as RemoteHub would after "Replace and revoke".
    /// </summary>
    public static async Task<object> Claim(Dictionary<string, string> o)
    {
        var host = o["host"];
        var port = o.TryGetValue("port", out var p) ? int.Parse(p) : AgentProtocol.DefaultPort;
        var keys = Keys(o);
        if (o.GetValueOrDefault("revoke-current") == "1") keys.Replace(DateTime.UtcNow, revokeEarlier: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var r = await AgentInstaller.ClaimAsync(host, port, LabJump.Route(o), keys, cts.Token);
        // judged again for the name RemoteHub knows the device by, when that isn't the address the claim went to
        if (o.TryGetValue("name", out var name) && r.Claim is { } c && r.Outcome == AgentInstaller.ClaimOutcome.Claimed && !AgentInstaller.NameMatches(name, c))
            r = r with { Outcome = AgentInstaller.ClaimOutcome.OtherName };
        return new
        {
            outcome = r.Outcome.ToString(),
            pin = r.Claim?.Fingerprint,
            deviceId = r.Claim?.DeviceId,
            computerName = r.Claim?.ComputerName,
            dnsHostName = r.Claim?.DnsHostName,
            message = AgentInstaller.Explain(r, o.GetValueOrDefault("name") ?? host),
        };
    }

    /// <summary>Claims until the agent answers (an install still running doesn't yet): the first answer, or null after <paramref name="within"/>.</summary>
    public static async Task<AgentInstaller.ClaimResult?> ClaimWithin(string host, int port, Dictionary<string, string> o, TimeSpan within)
    {
        var keys = Keys(o);
        var until = DateTime.UtcNow + within;
        while (true)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                return await AgentInstaller.ClaimAsync(host, port, LabJump.Route(o), keys, cts.Token);
            }
            catch (Exception) when (DateTime.UtcNow < until) { await Task.Delay(3000); }
            catch (Exception) { return null; }
        }
    }
}
