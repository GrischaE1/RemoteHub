using System.Text;
using RemoteHub.Agent;

namespace RemoteHub.Services;

/// <summary>One configured policy value as Settings → Policies shows it.</summary>
public sealed record PolicyRow(string Group, string Title, string Value, string Registry, string Source, string? Problem, string? OffBecause = null,
    string? Conflict = null)
{
    public bool Valid => Problem is null;
}

/// <summary>
/// What Settings → Policies shows: every policy value set on this PC for RemoteHub, and – when the RemoteHub Agent is
/// installed here too – the agent's (docs/admx-design.md §4.4). Read-only: everything in it comes from the registry.
/// </summary>
public sealed record PolicyReport(IReadOnlyList<PolicyRow> Console, IReadOnlyList<PolicyRow>? Agent)
{
    public const string ManagedText = "Set by your organisation";
    /// <summary>A setting a policy fixes: shown greyed out with this text.</summary>
    public const string ManagedNote = ManagedText + " – see Settings → Policies.";

    public string Summary => Console.Count switch
    {
        0 => "No policies are set for RemoteHub on this PC.",
        1 => $"1 setting of RemoteHub on this PC is set by your organisation ({Scopes()}).",
        var n => $"{n} settings of RemoteHub on this PC are set by your organisation ({Scopes()}).",
    };

    private string Scopes()
    {
        var c = Console.Count(r => r.Source.StartsWith("Computer", StringComparison.Ordinal));
        var u = Console.Count - c;
        return u == 0 ? "for the computer" : c == 0 ? "for your user account" : $"{c} for the computer, {u} for your user account";
    }

    /// <summary>Reads the registry (or <paramref name="source"/> in tests). <paramref name="agentInstalled"/>: also list the agent's values.</summary>
    public static PolicyReport Build(bool agentInstalled, IPolicySource? source = null) => new(
        Rows(PolicyCatalog.ConsoleEntries, source),
        agentInstalled ? Rows(PolicyCatalog.AgentEntries, source) : null);

    private static List<PolicyRow> Rows(IEnumerable<PolicyEntry> entries, IPolicySource? source) =>
        PolicyCatalog.Configured(entries, source).Select(v =>
        {
            var hive = v.Scope == PolicyScope.Computer ? "HKLM" : "HKCU";
            var key = v.FromKey ?? v.Entry.FullKey;
            var value = v.Valid ? v.Entry.Describe(v.Value!) : Raw(v.Value);
            var where = v.Scope == PolicyScope.Computer ? "Computer" : "User";
            if (v.FromKey is not null) where += " (old RdpManager key)";
            var group = v.Entry.Key == PolicyCatalog.RightsKey ? "Access rights" : v.Entry.Group;
            // A feature below a main switch that is off: its own value doesn't matter (docs/admx-design.md §4.4, Effects).
            var offBecause = v.Entry.Product == PolicyProduct.Console && Features.OffBecause(v.Entry, source) is { } main ? $"Off because \"{main.Title}\" is off" : null;
            return new PolicyRow(group, v.Entry.Title, value, $@"{hive}\{key}\{v.Entry.Name}", where, v.Problem is null ? null : $"{v.Entry.Name} {v.Problem} – ignored", offBecause,
                v.Valid ? Conflict(v.Entry, v.Value!, source) : null);
        }).ToList();

    /// <summary>
    /// A connection setting that asks for something a feature switch blocks (docs/admx-design.md §7: the block wins), for
    /// example remote desktop through the agent with the agent desktop blocked.
    /// </summary>
    private static string? Conflict(PolicyEntry entry, object value, IPolicySource? source)
    {
        if (entry.Key != PolicyCatalog.ConnectionsKey) return null;
        var (needs, what) = entry.Name switch
        {
            nameof(Models.RdpOptions.RemoteDesktopVia) when Equals(value, "Agent") => (Feature.AgentDesktop, "there is no remote desktop"),
            nameof(Models.RdpOptions.RemoteDesktopVia) when Equals(value, "Rdp") => (Feature.Rdp, "there is no remote desktop"),
            nameof(Models.RdpOptions.UseAgent) when Equals(value, 1) => (Feature.Agent, "the tools go over PowerShell remoting"),
            nameof(Models.RdpOptions.AgentAutoSignIn) when Equals(value, 1) => (Feature.AgentAutoSignIn, "RemoteHub doesn't sign in automatically"),
            nameof(Models.RdpOptions.PsRemoting) or nameof(Models.RdpOptions.PsUseSsl) when Equals(value, 1) => (Feature.PowerShellRemoting, "it isn't used"),
            nameof(Models.RdpOptions.Reach) when Equals(value, "Broker") => (Feature.Broker, "devices in synced folders can't be reached"),
            _ => ((Feature?)null, ""),
        };
        return needs is { } f && Features.WhyNot(f, source) is { } why ? $"{why.Split(" – ")[0]}, so {what}." : null;
    }

    private static string Raw(object? value) => value switch
    {
        string[] l => string.Join("; ", l),
        byte[] b => $"{b.Length} bytes",
        null => "",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>For support: the same content as the page, as plain text.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("RemoteHub – policies on this PC");
        sb.AppendLine(Summary);
        Append("RemoteHub", Console);
        if (Agent is not null)
        {
            if (Agent.Count == 0) sb.AppendLine().AppendLine("RemoteHub Agent on this PC: no policies set.");
            else Append("RemoteHub Agent on this PC", Agent);
        }
        return sb.ToString();

        void Append(string title, IReadOnlyList<PolicyRow> rows)
        {
            if (rows.Count == 0) return;
            sb.AppendLine().AppendLine(title);
            foreach (var g in rows.GroupBy(r => r.Group))
            {
                sb.AppendLine("  " + g.Key);
                foreach (var r in g)
                {
                    sb.AppendLine($"    {r.Title}: {r.Value}  [{r.Source}: {r.Registry}]");
                    if (r.Problem is { } p) sb.AppendLine("      INVALID: " + p);
                    if (r.OffBecause is { } off) sb.AppendLine("      " + off);
                    if (r.Conflict is { } conflict) sb.AppendLine("      CONFLICT: " + conflict);
                }
            }
        }
    }
}
