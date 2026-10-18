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
        lock (_lock) _records.TryGetValue(session, out r);
        if (r is null) return null;
        // The record goes only once the session is secured: a crash before, or a lock / sign-out Windows refused, leaves it
        // for Recover at the next start.
        action ??= r.EndAction;
        var (done, secured) = Act(r, action, log, why);
        if (!secured && action == AutoSignInEndActions.SignOut) (done, secured) = Act(r, AutoSignInEndActions.Lock, log, why + "; sign-out refused");
        if (secured) Forget(session, r);
        return done;
    }

    /// <summary>Drops the record (if it is still this one) and saves.</summary>
    private void Forget(int session, AutoSignInRecord r)
    {
        lock (_lock) if (_records.TryGetValue(session, out var now) && now.Session == r.Session && now.Sid == r.Sid) _records.Remove(session);
        Save();
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
            // The cover stays until Windows has ended the session (sign-out runs asynchronously) or, if it refused, locked it.
            var (_, secured) = Act(parked.Entry, AutoSignInEndActions.SignOut, log, why);
            if (secured) await Ended(parked.Entry);
            else
            {
                var (_, locked) = Act(parked.Entry, AutoSignInEndActions.Lock, log, why + "; sign-out refused");
                if (!locked)
                {
                    log.Write($"Automatic sign-in: session {session} could be neither signed out nor locked – the screen stays covered, the next agent start tries again");
                    return; // the helper keeps covering; the record stays for Recover
                }
            }
            try { parked.Helper.Dispose(); } catch { /* already gone */ }
            Forget(session, parked.Entry);
        });
    }

    /// <summary>How long the cover waits for a sign-out to finish before it ends anyway.</summary>
    internal static TimeSpan SignOutWait = TimeSpan.FromSeconds(60);

    /// <summary>Until the session no longer belongs to the account (signed out), at most <see cref="SignOutWait"/>.</summary>
    private async Task Ended(AutoSignInRecord r)
    {
        var until = DateTime.UtcNow + SignOutWait;
        while (DateTime.UtcNow < until && string.Equals(sessions.UserSid(r.Session), r.Sid, StringComparison.OrdinalIgnoreCase))
            await Task.Delay(250);
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
        // Kept for the next start when Windows refused (a sign-out falls back to a lock first).
        var failed = new List<AutoSignInRecord>();
        foreach (var r in left)
        {
            var (_, secured) = Act(r, r.Dropped ? AutoSignInEndActions.SignOut : r.EndAction, log, "the agent restarted while it was open");
            if (!secured && r.Dropped) (_, secured) = Act(r, AutoSignInEndActions.Lock, log, "the agent restarted while it was open; sign-out refused");
            if (!secured) failed.Add(r);
        }
        lock (_lock) foreach (var r in failed) _records[r.Session] = r;
        if (failed.Count == 0) { try { File.Delete(stateFile); } catch { /* gone */ } }
        else Save();
    }

    private bool Unpark(int session, Parked p)
    {
        lock (_lock)
        {
            if (!_parked.TryGetValue(session, out var now) || now != p) return false;
            _parked.Remove(session);
        }
        return true; // the caller ends the helper once the session is secured
    }

    /// <summary>
    /// Lock (disconnect – the lock screen on client Windows) or sign out – only while the session is still the account's.
    /// Done: what happened, for the audit (null when the session isn't the account's any more); Secured: nothing is left to
    /// do – done, or not ours any more.
    /// </summary>
    private (string? Done, bool Secured) Act(AutoSignInRecord r, string action, AgentLog log, string why)
    {
        if (!string.Equals(sessions.UserSid(r.Session), r.Sid, StringComparison.OrdinalIgnoreCase))
        {
            log.Write($"Automatic sign-in: session {r.Session} of {r.Account} is gone or has another user now – nothing to do ({why})");
            return (null, true);
        }
        var ok = action == AutoSignInEndActions.SignOut ? sessions.LogOff(r.Session) : sessions.Disconnect(r.Session);
        var done = action == AutoSignInEndActions.SignOut ? ok ? "signed out" : "couldn't be signed out" : ok ? "locked" : "couldn't be locked";
        log.Audit(AuditEvent.AutoSignInEnded, $"Automatic sign-in session {r.Session} of {r.Account} {done} ({why})");
        return (done, ok);
    }

    /// <summary>Reads and drops the helper's output until it ends (nobody watches); says whether it ended by the user's Disconnect.</summary>
    private static async Task<bool> Drain(IDesktopHelper helper)
    {
        try
        {
            while (await HelperOutput.ReadAsync(helper, CancellationToken.None) is { } m)
                if (m.Length > 0 && m[0] == (byte)HelperMessage.End)
                    return DesktopWire.ReadJson(m, HelperJson.Default.HelperEnd).ByUser;
        }
        catch { /* gone */ }
        return false;
    }

    private readonly object _saving = new();

    /// <summary>
    /// The records as they are now, replaced in one step (a temporary file moved over the old one): a crash while saving
    /// leaves the previous complete file, never a truncated one. One save at a time, so an older snapshot can't overwrite a
    /// newer one.
    /// </summary>
    private void Save()
    {
        if (stateFile is null) return;
        lock (_saving)
        {
            try
            {
                List<AutoSignInRecord> all;
                lock (_lock) all = [.. _records.Values];
                if (all.Count == 0) { File.Delete(stateFile); return; }
                // Flushed before the replace: a power loss just after it must still leave the records Recover needs.
                DurableFile.WriteAllText(stateFile, JsonSerializer.Serialize(all, AutoSignInJson.Default.ListAutoSignInRecord)); // no secrets: session, SID, account, end action
            }
            catch { /* best effort – the record is for crash safety only */ }
        }
    }
}

/// <summary>A session automatic sign-in created, as autosignin.json records it (no secrets).</summary>
internal sealed record AutoSignInRecord(int Session, string Sid, string Account, string EndAction, bool Dropped);

[JsonSerializable(typeof(List<AutoSignInRecord>))]
internal partial class AutoSignInJson : JsonSerializerContext;