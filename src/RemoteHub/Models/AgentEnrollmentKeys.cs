using System.Text.Json.Serialization;
using RemoteHub.Agent;

namespace RemoteHub.Models;

/// <summary>
/// RemoteHub's enrollment secrets for the RemoteHub Agent (docs/agent-spec.md §5.1, §5.5). Every enrollment file carries
/// the current one; a device proves with it that it was enrolled from this RemoteHub's file, and RemoteHub then pins the
/// device's own certificate. Kept with the other settings (encrypted, shared with them in a shared folder), so every
/// RemoteHub using these settings can claim the devices. A replaced secret stays accepted until it is revoked; a revoked
/// one is refused for new claims – devices already claimed keep their pin.
/// </summary>
public sealed class AgentEnrollmentKeys
{
    public List<AgentEnrollmentKey> Keys { get; set; } = [];

    /// <summary>The newest secret that isn't revoked – the one new enrollment files carry (null: none yet).</summary>
    [JsonIgnore]
    public AgentEnrollmentKey? Current => Keys.Where(k => k.RevokedUtc is null).OrderByDescending(k => k.CreatedUtc).FirstOrDefault();

    /// <summary>Earlier secrets that are still accepted.</summary>
    [JsonIgnore]
    public List<AgentEnrollmentKey> Earlier => Keys.Where(k => k.RevokedUtc is null && k != Current).ToList();

    /// <summary>The current secret, made on first use.</summary>
    public AgentEnrollmentKey EnsureCurrent(DateTime now) => Current ?? Add(now);

    /// <summary>A new current secret. The earlier ones stay accepted unless <paramref name="revokeEarlier"/>.</summary>
    public AgentEnrollmentKey Replace(DateTime now, bool revokeEarlier)
    {
        if (revokeEarlier) RevokeAll(now);
        return Add(now);
    }

    /// <summary>Revokes every secret but the current one.</summary>
    public int RevokeEarlier(DateTime now)
    {
        var earlier = Earlier;
        foreach (var k in earlier) k.RevokedUtc = now;
        return earlier.Count;
    }

    /// <summary>The secret a claim names, whether it is accepted or not (null: not one of this RemoteHub's).</summary>
    public AgentEnrollmentKey? Find(string? keyId) =>
        Guid.TryParse(keyId, out var id) ? Keys.FirstOrDefault(k => k.Id == id) : null;

    private void RevokeAll(DateTime now)
    {
        foreach (var k in Keys.Where(k => k.RevokedUtc is null)) k.RevokedUtc = now;
    }

    private AgentEnrollmentKey Add(DateTime now)
    {
        // Strictly newer than any other, so Current is the one just made even within the same clock tick.
        var created = Keys.Count > 0 && Keys.Max(k => k.CreatedUtc) is var newest && newest >= now ? newest.AddTicks(1) : now;
        var key = new AgentEnrollmentKey { Id = Guid.NewGuid(), CreatedUtc = created, Secret = AgentEnrollment.NewSecret() };
        Keys.Add(key);
        return key;
    }
}

public sealed class AgentEnrollmentKey
{
    public Guid Id { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    /// <summary>32 random bytes, base64. Stored like a password (the database's secrets table).</summary>
    public string Secret { get; set; } = "";
}
