using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteHub.Agent;

/// <summary>
/// The sessions automatic sign-in created (docs/credential-provider-design.md §9): their end action when the connection
/// ends on purpose, the 2 minutes after a dropped connection – the helper and its privacy screen stay, then the session
/// is signed out – and crash safety: they are recorded in autosignin.json, and whatever is left there when the service
/// starts is dealt with. Every action checks first that the session still belongs to the account (by SID).
/// </summary>
public sealed class AutoSignInSessions(IDesktopSessions sessions, string? stateFile = null)
{
    /// <summary>How long a dropped connection's session stays covered for a resume (tests shorten it).</summary>
    internal static TimeSpan HoldAfterDrop = TimeSpan.FromMinutes(2);


    private sealed class Parked(AutoSignInRecord record, IDesktopHelper helper)
    {
        public AutoSignInRecord Entry => record;
        public IDesktopHelper Helper => helper;
        public readonly CancellationTokenSource Timer = new();
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, AutoSignInRecord> _records = new();
    private readonly Dictionary<int, Parked> _parked = new();

    /// <summary>The sign-in was confirmed: from now on the session has its end action.</summary>
    public void Signed(int session, string sid, string account, string endAction)
    {
        lock (_lock) _records[session] = new AutoSignInRecord(session, sid, account, endAction, false);
        Save();
    }

    /// <summary>
    /// The connection ended on purpose: the end action (lock or sign out). Returns what was done, for the audit, or null
    /// when the session isn't ours any more (signed out meanwhile, or another user).
    /// </summary>
    /// <param name="action">Overrides the end action (a dropped connection with nothing to cover the screen: sign out).</param>
    public string? EndOnPurpose(int session, AgentLog log, string why, string? action = null)
    {
        AutoSignInRecord? r;
        lock (_lock) _records.Remove(session, out r);
        Save();
        return r is null ? null : Act(r, action ?? r.EndAction, log, why);
    }

    /// <summary>
    /// The connection dropped: <paramref name="helper"/> keeps covering the screen (privacy screen, local input blocked)
    /// for <see cref="HoldAfterDrop"/> while RemoteHub may continue; then the session is signed out. If the helper ends
    /// sooner – a crash, Disconnect on the device's banner – the session is signed out at once, unless it was locked.
    /// </summary>
    public void Dropped(int session, IDesktopHelper helper, AgentLog log)
    {
        Parked parked;
        lock (_lock)
        {
            if (!_records.TryGetValue(session, out var r)) { helper.Dispose(); return; }
            _records[session] = r = r with { Dropped = true };
            parked = new Parked(r, helper);
            _parked[session] = parked;
        }
        Save();
        log.Write($"Automatic sign-in: the connection to session {session} dropped – the screen stays covered for {HoldAfterDrop.TotalSeconds:0} s, then the session is signed out");
        var helperEnded = Drain(helper);
        _ = Task.Run(async () =>
        {
            var expired = Task.Delay(HoldAfterDrop, parked.Timer.Token);
            var first = await Task.WhenAny(expired, helperEnded);
            if (parked.Timer.IsCancellationRequested) return; // continued in a new connection
            if (first == helperEnded && !await helperEnded && sessions.List().FirstOrDefault(s => s.Id == session)?.State == DesktopSessionState.Locked)
                await Task.WhenAny(expired); // locked at the device: safe – the 2 minutes run out, then it's signed out
            if (parked.Timer.IsCancellationRequested) return;
            if (!Unpark(session, parked)) return;
            var why = first == helperEnded ? "the screen cover ended after the connection dropped" : "connection lost and not continued in time";
            lock (_lock) _records.Remove(session);
            Save();
            Act(parked.Entry, AutoSignInEndActions.SignOut, log, why);
        });
    }

    /// <summary>
    /// A new connection continues the session: its timer stops, and the old helper is handed back to end once the new
    /// one covers the screen. Null when nothing was parked.
    /// </summary>
    public IDesktopHelper? Resumed(int session)
    {
        Parked? p;
        lock (_lock)
        {
            if (!_parked.Remove(session, out p)) return null;
            if (_records.TryGetValue(session, out var r)) _records[session] = r with { Dropped = false };
        }
        p.Timer.Cancel();
        Save();
        return p.Helper;
    }

    /// <summary>The session's end action, if automatic sign-in created it (a continued connection takes it over).</summary>
    public (string Sid, string EndAction)? Owned(int session)
    {
        lock (_lock) return _records.TryGetValue(session, out var r) ? (r.Sid, r.EndAction) : null;
    }

    /// <summary>
    /// At service start: sessions recorded by the last run have no connection any more. A dropped one is signed out, any
    /// other gets its end action (§9.3).
    /// </summary>
    public void Recover(AgentLog log)
    {
        if (stateFile is null || !File.Exists(stateFile)) return;
        List<AutoSignInRecord> left;
        try { left = JsonSerializer.Deserialize(File.ReadAllText(stateFile), AutoSignInJson.Default.ListAutoSignInRecord) ?? []; }
        catch (Exception ex) { log.Write($"Automatic sign-in: {stateFile} unreadable ({ex.Message}) – ignored"); left = []; }
        foreach (var r in left)
            Act(r, r.Dropped ? AutoSignInEndActions.SignOut : r.EndAction, log, "the agent restarted while it was open");
        try { File.Delete(stateFile); } catch { /* gone */ }
    }

    private bool Unpark(int session, Parked p)
    {
        lock (_lock)
        {
            if (!_parked.TryGetValue(session, out var now) || now != p) return false;
            _parked.Remove(session);
        }
        try { p.Helper.Dispose(); } catch { /* already gone */ }
        return true;
    }

    /// <summary>Lock (disconnect – the lock screen on client Windows) or sign out – only while the session is still the account's.</summary>
    private string? Act(AutoSignInRecord r, string action, AgentLog log, string why)
    {
        if (!string.Equals(sessions.UserSid(r.Session), r.Sid, StringComparison.OrdinalIgnoreCase))
        {
            log.Write($"Automatic sign-in: session {r.Session} of {r.Account} is gone or has another user now – nothing to do ({why})");
            return null;
        }
        var done = action == AutoSignInEndActions.SignOut
            ? sessions.LogOff(r.Session) ? "signed out" : "couldn't be signed out"
            : sessions.Disconnect(r.Session) ? "locked" : "couldn't be locked";
        log.Audit(AuditEvent.AutoSignInEnded, $"Automatic sign-in session {r.Session} of {r.Account} {done} ({why})");
        return done;
    }

    /// <summary>Reads and drops the helper's output until it ends (nobody watches); says whether it ended by the user's Disconnect.</summary>
    private static async Task<bool> Drain(IDesktopHelper helper)
    {
        try
        {
            while (await DesktopWire.ReadFramedAsync(helper.Output, CancellationToken.None) is { } m)
                if (m.Length > 0 && m[0] == (byte)HelperMessage.End)
                    return DesktopWire.ReadJson(m, HelperJson.Default.HelperEnd).ByUser;
        }
        catch { /* gone */ }
        return false;
    }

    private void Save()
    {
        if (stateFile is null) return;
        try
        {
            List<AutoSignInRecord> all;
            lock (_lock) all = [.. _records.Values];
            if (all.Count == 0) { File.Delete(stateFile); return; }
            File.WriteAllText(stateFile, JsonSerializer.Serialize(all, AutoSignInJson.Default.ListAutoSignInRecord)); // no secrets: session, SID, account, end action
        }
        catch { /* best effort – the record is for crash safety only */ }
    }
}

/// <summary>A session automatic sign-in created, as autosignin.json records it (no secrets).</summary>
internal sealed record AutoSignInRecord(int Session, string Sid, string Account, string EndAction, bool Dropped);

[JsonSerializable(typeof(List<AutoSignInRecord>))]
internal partial class AutoSignInJson : JsonSerializerContext;