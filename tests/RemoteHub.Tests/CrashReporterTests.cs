using System.Collections.Generic;
using System.Text.Json;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>Crash reports: recorded locally while crashing, sent at the next start, nothing identifying inside.</summary>
[Collection("Updates")]
public class CrashReporterTests
{
    private static T InFolder<T>(Func<string, T> test)
    {
        var old = CrashReporter.Folder;
        var dir = Path.Combine(Path.GetTempPath(), "rh-crash-test-" + Guid.NewGuid().ToString("N"));
        CrashReporter.Folder = dir;
        try { return test(dir); }
        finally
        {
            CrashReporter.Folder = old;
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Messages_are_redacted()
    {
        var text = $@"Could not open \\fileserver01\share$\x.txt and C:\Users\{Environment.UserName}\AppData\a.json on {Environment.MachineName}; " +
                   "host sql01.prod.corp.local (10.1.2.3, fe80:0:0:0:1:2:3:4) for admin@contoso.com, value 'secret-name' " +
                   "and \"web01\" – System.Net.Http.HttpRequestException at RemoteHub.Remoting.PsRemoting.Open";
        var r = CrashReporter.Redact(text, 2000);
        foreach (var leak in new[] { "fileserver01", @"C:\Users", Environment.UserName, Environment.MachineName, "sql01", "10.1.2.3", "fe80", "contoso", "secret-name", "web01" })
            Assert.DoesNotContain(leak, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.Net.Http.HttpRequestException", r);     // code names stay readable
        Assert.Contains("RemoteHub.Remoting.PsRemoting.Open", r);
        Assert.Contains("[path]", r);
        Assert.Contains("[ip]", r);
        Assert.Contains("[host]", r);
        Assert.Equal(11, CrashReporter.Redact(new string('x', 50), 10).Length); // 10 + "…"
    }

    [Fact]
    public void Upper_case_hosts_short_ipv6_and_other_accounts_are_redacted()
    {
        var r = CrashReporter.Redact(@"Connect to SRV01.CONTOSO.COM, Web01.Contoso.local, fe80::1%12 and ::1 as FABRIKAM\jdoe failed " +
                                     "in System.Net.Sockets.Socket.Connect at 12:30:45", 2000);
        foreach (var leak in new[] { "SRV01", "Contoso", "fe80", "::1", "FABRIKAM", "jdoe" })
            Assert.DoesNotContain(leak, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.Net.Sockets.Socket.Connect", r); // code names stay readable
        Assert.Contains("12:30:45", r);                           // a time isn't an address
        Assert.Contains("[account]", r);
    }

    private static void Thrower() => throw new InvalidOperationException(@"Bad state in C:\Data\x", new IOException("disk"));

    [Fact]
    public void An_exception_becomes_a_report_with_type_and_stack()
    {
        Exception ex;
        try { Thrower(); throw null!; } catch (Exception e) { ex = e; }
        var r = CrashReporter.FromException(ex, "0.9.1", DateTime.UtcNow);
        Assert.Equal("System.InvalidOperationException", r.ErrorType);
        Assert.Equal("Bad state in [path]", r.Message);
        Assert.Contains(nameof(Thrower), r.Stack);
        Assert.Contains("--- inner System.IO.IOException ---", r.Stack);
        Assert.DoesNotContain("disk", r.Stack); // inner messages aren't included
        Assert.Equal("0.9.1", r.Version);
    }

    [Fact]
    public void Recorded_crashes_wait_for_the_next_start_and_are_limited() => InFolder(dir =>
    {
        Directory.CreateDirectory(dir);
        void Put(int daysAgo, string type)
        {
            var t = DateTime.UtcNow.AddDays(-daysAgo);
            File.WriteAllText(Path.Combine(dir, $"crash-{t.Ticks}-{Guid.NewGuid():N}.json"),
                JsonSerializer.Serialize(new CrashReport { Version = "0.9.0", ErrorType = type, Message = "m", Stack = "s", TimeUtc = t }));
        }
        Put(40, "TooOld");
        for (var i = 7; i >= 1; i--) Put(i, "E" + i);

        var pending = CrashReporter.TakePending();
        Assert.Equal(["E5", "E4", "E3", "E2", "E1"], pending.Select(p => p.ErrorType)); // newest 5, oldest first
        Assert.Equal(5, Directory.GetFiles(dir, "crash-*.json").Length);              // too old / beyond 5 deleted

        CrashReporter.Sent(pending[0]);
        CrashReporter.Discard(pending.Skip(1));
        Assert.Empty(Directory.GetFiles(dir, "crash-*.json"));
        return 0;
    });

    [Fact]
    public void An_unclean_end_of_the_last_session_is_reported() => InFolder(dir =>
    {
        Directory.CreateDirectory(dir);
        // The last session started and never cleared its marker (no event log entry for it can exist: it "started" now).
        File.WriteAllText(Path.Combine(dir, "session.json"),
            JsonSerializer.Serialize(new { StartedUtc = DateTime.UtcNow, Pid = 1, Version = "0.9.0" }));
        CrashReporter.Start();
        Assert.True(File.Exists(Path.Combine(dir, "session.json")));                    // this session is marked as running

        var report = Assert.Single(CrashReporter.TakePending());
        Assert.Equal("UnexpectedExit", report.ErrorType);
        Assert.Equal("0.9.0", report.Version);
        Assert.Equal(report.FilePath, Assert.Single(CrashReporter.TakePending()).FilePath); // reported once, kept until sent

        CrashReporter.Stop();                                                             // clean exit
        Assert.False(File.Exists(Path.Combine(dir, "session.json")));
        return 0;
    });

    [Fact]
    public void Crash_payload_adds_exactly_the_three_fields()
    {
        var extra = new Dictionary<string, object> { ["errorType"] = "NativeCrash", ["message"] = "Exception 0xc0000005 in mstscax.dll", ["stack"] = "mstscax.dll+0x1234" };
        using var doc = JsonDocument.Parse(Telemetry.Payload("app_crash", "0.9.0", "beta", Guid.NewGuid().ToString(), extra));
        Assert.Equal(["arch", "channel", "errorType", "event", "installId", "message", "os", "stack", "version"],
            doc.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("app_crash", doc.RootElement.GetProperty("event").GetString());
    }
}
