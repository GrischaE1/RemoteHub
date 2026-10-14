using System.Management.Automation.Runspaces;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using RemoteHub.Agent;

namespace RemoteHub.Remoting;

/// <summary>How the agent gets onto the device. SaveFiles: the installer and the enrollment file go to a folder, for installing by hand or with software distribution.</summary>
public enum AgentInstallMethod { Remoting, ThroughAgent, Clipboard, SaveFiles }

/// <summary>What the operator chose in the install dialog.</summary>
public sealed record AgentInstallOptions(
    AgentInstallMethod Method,
    bool NewIdentity,
    string Label,
    int Port,
    string FirewallScope,
    List<string> AllowedGroups,
    bool UseAgentAfterwards,
    bool VirtualDisplay = true);

/// <summary>
/// Installs or updates the RemoteHub Agent on a device:
/// <list type="bullet">
/// <item>over PowerShell remoting (WinRM): upload the MSI and the enrollment file to a temp folder, install silently,
/// verify the installed certificate, delete the folder;</item>
/// <item>through the running agent (update): the same, but msiexec is started outside the session (it restarts the agent);</item>
/// <item>or via the clipboard: both files as a file list, to paste into the RDP session.</item>
/// </list>
/// The enrollment file holds the device key: it only travels over these encrypted channels and is deleted after use.
/// </summary>
public static class AgentInstaller
{
    public const string PackageName = "RemoteHub-Agent-x64.msi";

    /// <summary>The agent MSI shipped with RemoteHub (Agent\ next to the exe); in a dev build the newest one in artifacts\.</summary>
    public static string? FindPackage()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "Agent", PackageName);
        if (File.Exists(shipped)) return shipped;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var artifacts = Path.Combine(dir.FullName, "artifacts");
            if (!Directory.Exists(artifacts)) continue;
            var newest = new DirectoryInfo(artifacts).GetFiles("RemoteHub-Agent-*-x64.msi").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is not null) return newest.FullName;
        }
        return null;
    }

    /// <summary>%TEMP%\RemoteHub-Agent – local staging folders (one per install).</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "RemoteHub-Agent");

    /// <summary>A new staging folder with the MSI and (optionally) the enrollment file.</summary>
    public static (string Folder, string Msi, string? Enrollment) Prepare(string package, AgentEnrollment? enrollment)
    {
        CleanTemp(TimeSpan.FromHours(12));
        var folder = Directory.CreateDirectory(Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..12])).FullName;
        var msi = Path.Combine(folder, PackageName);
        File.Copy(package, msi);
        string? enrollmentPath = null;
        if (enrollment is not null)
        {
            enrollmentPath = Path.Combine(folder, AgentEnrollment.FileName(enrollment.DeviceLabel));
            File.WriteAllBytes(enrollmentPath, enrollment.Serialize());
        }
        return (folder, msi, enrollmentPath);
    }

    /// <summary>Removes staging folders older than <paramref name="age"/> (TimeSpan.Zero: all) – they may hold a device key.</summary>
    public static void CleanTemp(TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var dir in new DirectoryInfo(TempRoot).GetDirectories())
                if (DateTime.UtcNow - dir.CreationTimeUtc >= age)
                    try { dir.Delete(true); } catch { /* in use */ }
        }
        catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ remote scripts

    private const string MakeFolderScript = """
        param([string]$Name)
        $d = Join-Path ([IO.Path]::GetTempPath()) $Name
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        $d
        """;

    private const string CreateScript = """
        param([string]$Path)
        [IO.File]::WriteAllBytes($Path, [byte[]]@())
        """;

    private const string ChunkScript = """
        param([string]$Path, [byte[]]$Data)
        $s = [IO.File]::Open($Path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $s.Write($Data, 0, $Data.Length) } finally { $s.Dispose() }
        """;

    private const string HashScript = """
        param([string]$Path)
        $s = [IO.File]::OpenRead($Path)
        try { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s)).Replace('-', '') } finally { $s.Dispose() }
        """;

    private const string InstallScript = """
        param([string]$Msi, [string]$Enrollment, [string]$Log, [string]$VirtualDisplay)
        $a = @('/i', "`"$Msi`"", '/qn', '/norestart', '/l*v', "`"$Log`"", "VIRTUALDISPLAY=$VirtualDisplay")
        if ($Enrollment) { $a += "ENROLLMENT=`"$Enrollment`"" }
        $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $a -Wait -PassThru -WindowStyle Hidden
        $p.ExitCode
        """;

    private const string LogTailScript = """
        param([string]$Log)
        if (Test-Path -LiteralPath $Log) {
            Get-Content -LiteralPath $Log | Where-Object { $_ -match 'WixQuietExec|Error \d{4}|failed|Fehler|could not|No \.rhagent' } | Select-Object -Last 12
        }
        """;

    private const string VerifyScript = """
        $cfg = Get-Content -LiteralPath (Join-Path $env:ProgramData 'RemoteHub\Agent\agent.json') -Raw | ConvertFrom-Json
        $c = Get-Item -LiteralPath ('Cert:\LocalMachine\My\' + $cfg.certificateThumbprint)
        $fp = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($c.RawData)).Replace('-', '')
        $svc = Get-Service -Name RemoteHubAgent
        "$fp|$($cfg.listenPort)|$($cfg.deviceId)|$($svc.Status)"
        """;

    private const string RemoveFolderScript = """
        param([string]$Path)
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
        """;

    /// <summary>
    /// msiexec outside the agent session (via WMI), so restarting the agent doesn't kill it; then the folder is removed –
    /// only after msiexec has ended ("start /wait": msiexec is a GUI program, which cmd wouldn't wait for otherwise).
    /// </summary>
    private const string DetachedInstallScript = """
        param([string]$Msi, [string]$Enrollment, [string]$Log, [string]$Dir, [string]$VirtualDisplay)
        $m = "start `"`" /wait msiexec.exe /i `"$Msi`" /qn /norestart /l*v `"$Log`" VIRTUALDISPLAY=$VirtualDisplay"
        if ($Enrollment) { $m += " ENROLLMENT=`"$Enrollment`"" }
        $cmd = "cmd.exe /c `"$m & rd /s /q `"$Dir`"`""
        $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
        if ($r.ReturnValue -ne 0) { throw "Could not start the installer (WMI error $($r.ReturnValue))." }
        """;

    public sealed record Installed(string Fingerprint, int Port, string DeviceId, string ServiceStatus);

    /// <summary>Uploads the staging files to a new temp folder on the device (checksum-verified). Returns that folder.</summary>
    private static string Upload(Runspace rs, string msi, string? enrollment, Action<string> progress, out string remoteMsi, out string? remoteEnrollment)
    {
        var folder = RemoteFs.Invoke(rs, MakeFolderScript, ("Name", "RemoteHub-Agent-" + Guid.NewGuid().ToString("N")[..12])).Single().ToString();
        remoteMsi = Path.Combine(folder, PackageName);
        remoteEnrollment = enrollment is null ? null : Path.Combine(folder, Path.GetFileName(enrollment));
        foreach (var (local, remote) in new[] { (msi, remoteMsi), (enrollment, remoteEnrollment) })
        {
            if (local is null || remote is null) continue;
            progress($"Copying {Path.GetFileName(local)} ({TransferItem.Size(new FileInfo(local).Length)})…");
            RemoteFs.Invoke(rs, CreateScript, ("Path", remote));
            using (var file = File.OpenRead(local))
            {
                var buffer = new byte[FileUploader.ChunkSize];
                int read;
                while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                    RemoteFs.Invoke(rs, ChunkScript, ("Path", remote), ("Data", read == buffer.Length ? buffer : buffer[..read]));
            }
            var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(local)));
            var actual = RemoteFs.Invoke(rs, HashScript, ("Path", remote)).Single().ToString();
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new RemoteCommandException($"{Path.GetFileName(local)} was damaged while copying (checksum mismatch).");
        }
        return folder;
    }

    /// <summary>Silent install over a WinRM runspace. Throws <see cref="RemoteCommandException"/> with a readable reason.</summary>
    /// <param name="virtualDisplay">Install the virtual monitor for devices without one (MSI property VIRTUALDISPLAY).</param>
    public static Installed InstallOverRemoting(Runspace rs, string msi, string? enrollment, Action<string> progress, bool virtualDisplay = true)
    {
        string? folder = null;
        try
        {
            folder = Upload(rs, msi, enrollment, progress, out var remoteMsi, out var remoteEnrollment);
            var log = Path.Combine(folder, "install.log");
            progress("Installing (silent)…");
            var exit = Convert.ToInt32(RemoteFs.Invoke(rs, InstallScript, ("Msi", remoteMsi), ("Enrollment", remoteEnrollment ?? ""), ("Log", log), ("VirtualDisplay", virtualDisplay ? "1" : "0")).Last().BaseObject);
            if (exit is not (0 or 3010))
            {
                var tail = string.Join("\n", RemoteFs.Invoke(rs, LogTailScript, ("Log", log)).Select(o => o.ToString().Trim()).Where(l => l.Length > 0));
                throw new RemoteCommandException($"The installer failed (exit code {exit}).{(tail.Length > 0 ? "\n\n" + tail : "")}");
            }
            progress("Checking the installed agent…");
            var answer = RemoteFs.Invoke(rs, VerifyScript).Single().ToString();
            var parts = answer.Split('|');
            if (parts.Length != 4 || parts[0].Length != 64 || !int.TryParse(parts[1], out var port))
                throw new RemoteCommandException($"The installed agent's certificate or configuration couldn't be read (\"{answer}\").");
            return new Installed(parts[0], port, parts[2], parts[3]);
        }
        finally
        {
            if (folder is not null)
                try { RemoteFs.Invoke(rs, RemoveFolderScript, ("Path", folder)); } catch { /* the agent deletes the enrollment file itself */ }
        }
    }

    /// <summary>Update through the running agent: upload, then start msiexec detached (the session ends when the agent restarts).</summary>
    public static void StartUpdateThroughAgent(Runspace rs, string msi, string? enrollment, Action<string> progress, bool virtualDisplay = true)
    {
        var folder = Upload(rs, msi, enrollment, progress, out var remoteMsi, out var remoteEnrollment);
        var log = Path.Combine(Path.GetDirectoryName(folder)!, "RemoteHub-Agent-update.log");
        progress("Starting the installer on the device…");
        RemoteFs.Invoke(rs, DetachedInstallScript, ("Msi", remoteMsi), ("Enrollment", remoteEnrollment ?? ""), ("Log", log), ("Dir", folder), ("VirtualDisplay", virtualDisplay ? "1" : "0"));
    }

    // ------------------------------------------------------------------ health

    /// <summary>GET /v1/health over TLS pinned to <paramref name="fingerprint"/> (directly).</summary>
    public static Task<AgentHealth> HealthAsync(string host, int port, string fingerprint, CancellationToken ct) =>
        HealthAsync(host, new AgentEndpoint(port, fingerprint), ct);

    /// <summary>GET /v1/health over TLS pinned to the endpoint's fingerprint – through its jump host, if it has one.</summary>
    public static async Task<AgentHealth> HealthAsync(string host, AgentEndpoint endpoint, CancellationToken ct)
    {
        var expected = AgentProtocol.NormalizeFingerprint(endpoint.Fingerprint);
        var port = endpoint.Port;
        RemoteCertificateValidationCallback pinned = (_, cert, _, _) => cert is not null && AgentProtocol.Fingerprint(cert.GetRawCertData()) == expected;
        using var handler = endpoint.Via is { } via
            ? AgentClientTransport.ThroughJumpHost(via, pinned)
            : new SocketsHttpHandler { SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = pinned } };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(endpoint.Via is null ? 8 : 30) };
        var h = host.Trim();
        if (h.Contains(':') && !h.StartsWith('[')) h = $"[{h}]";
        var json = await http.GetStringAsync($"https://{h}:{port}{AgentProtocol.HealthPath}", ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AgentHealth>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidDataException("Empty answer from the agent.");
    }

    /// <summary>
    /// After an update through the agent: waits until it has gone away and answers again (with the expected certificate)
    /// with <paramref name="expectedVersion"/>. Returns the last answer – still the old version if the update didn't
    /// install (compare <see cref="AgentHealth.Version"/>) – or null if it never answered again.
    /// </summary>
    public static async Task<AgentHealth?> WaitForRestartAsync(string host, AgentEndpoint endpoint, string? expectedVersion,
        Action<string> progress, CancellationToken ct)
    {
        progress("Waiting for the agent to restart…");
        var downBy = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < downBy)
        {
            try { await HealthAsync(host, endpoint, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { break; } // it went down (or answers with the new certificate)
            await Task.Delay(1500, ct);
        }
        AgentHealth? last = null;
        var upBy = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < upBy)
        {
            try
            {
                last = await HealthAsync(host, endpoint, ct);
                if (expectedVersion is null || last.Version == expectedVersion) return last;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            await Task.Delay(2000, ct); // a short answer from the old agent before msiexec stops it, or not up yet
        }
        return last;
    }

    /// <summary>The ProductVersion of an MSI (e.g. "0.14.3" – the agent reports the same three numbers), or null.</summary>
    public static string? PackageVersion(string msi)
    {
        try
        {
            dynamic installer = Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer", throwOnError: true)!)!;
            var db = installer.OpenDatabase(msi, 0); // read-only
            var view = db.OpenView("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'");
            view.Execute();
            var record = view.Fetch();
            string? version = record?.StringData(1);
            view.Close();
            return Version.TryParse(version, out var v) ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : null;
        }
        catch { return null; }
    }

    /// <summary>The first or last <paramref name="n"/> characters for display (ids and fingerprints from older or edited data may be shorter).</summary>
    public static string Head(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[..n];
    public static string Tail(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[^n..];

    /// <summary>"Administrators" (any language) becomes its SID; other entries are passed on as typed (names or SIDs).</summary>
    public static List<string> ParseGroups(string text)
    {
        var list = text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => g.Equals("Administrators", StringComparison.OrdinalIgnoreCase) ? AgentProtocolGroups.Administrators : g)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? [AgentProtocolGroups.Administrators] : list;
    }
}

public static class AgentProtocolGroups
{
    public const string Administrators = "S-1-5-32-544";
}
