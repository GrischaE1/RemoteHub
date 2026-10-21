using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net.WebSockets;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;
using RemoteHub.Remoting;
using D = System.Collections.Generic.Dictionary<string, object?>;

/// <summary>
/// A scripted remote desktop viewer: RemoteHub's own client code (AgentDesktopClient, DesktopWire, ClipboardTransfer,
/// TransferReceiver, H264Decoder) driven by a steps file instead of a person.
///   desktop --host .. --port n --pin .. --user .. --password .. --steps <steps.json> [--out <folder>] [--timeout s (600)] [--decode 0]
/// The steps file is a JSON array run in order, each {"do": "<step>", ...}; the fields of each step are in Step().
/// Every video frame is acknowledged on arrival (like Program.cs), so the agent keeps sending whatever the steps do;
/// frames are decoded on their own thread unless --decode 0. Answers the steps (ok, seconds, detail) and everything the
/// agent sent, with ms since the start; the answer is also written to <out>\result.json. Never a top-level "error" -
/// the lab reads that as a failed harness call.
/// </summary>
internal static class DesktopHarness
{
    public static Task<object> Run(Dictionary<string, string> o) => new Script(o).RunAsync();

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    /// <summary>What RemoteHub tells the agent it supports – mirrors AgentDesktopSession.ViewerFeatures (private).</summary>
    private static readonly string[] ViewerFeatures = [DesktopFeatures.TransferOnPaste];

    // AgentDesktopClient has no abort and doesn't expose the close status: its socket, for drop / close and the close status.
    private static readonly FieldInfo? SocketField = typeof(AgentDesktopClient).GetField("_ws", BindingFlags.NonPublic | BindingFlags.Instance);

    private static ClientWebSocket? SocketOf(AgentDesktopClient c) => SocketField?.GetValue(c) as ClientWebSocket;

    /// <summary>One connection to the agent (connect / reconnect).</summary>
    private sealed class Conn(int n, AgentDesktopClient client)
    {
        public int N => n;
        public AgentDesktopClient Client => client;
        public volatile bool Closed;
        /// <summary>"drop" / "close" when the harness ended it.</summary>
        public volatile string? EndedBy;
        public volatile Script.Decoder? Decoder;
        // automatic sign-in as asked by the last start step (mirrors AgentDesktopSession.SendSignIn: once per start)
        public volatile bool AutoSignIn, SendSignIn, SignInSent;
        public volatile string? SignInPassword;
    }

    private sealed record StatusRecord(long Ms, int Connection, string State, string Message, int PinAttemptsLeft, bool Resume);

    private sealed class StepRecord
    {
        public int I { get; init; }
        public string Do { get; init; } = "";
        public bool Ok { get; set; }
        public long AtMs { get; init; }
        public double Seconds { get; set; }
        public D? Detail { get; set; }
    }

    private sealed class Script(Dictionary<string, string> o)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly string _out = Path.GetFullPath(o.GetValueOrDefault("out") ?? Path.Combine(Path.GetTempPath(), "RemoteHubDesktopHarness", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        private readonly bool _decode = o.GetValueOrDefault("decode") != "0";

        private long Ms => _clock.ElapsedMilliseconds;

        private Conn? _conn;
        private int _connections;
        private DesktopHello? _hello;
        private D? _connectError;

        private readonly List<StepRecord> _steps = [];
        private readonly List<StatusRecord> _statuses = [];
        private int _statusMark; // a wait looks at statuses from here on (set by start and by a wait that found one)
        private string? _lastResume;
        private readonly List<object> _layouts = [], _stats = [], _clipboard = [], _transfers = [], _privacy = [], _closes = [], _images = [];
        private readonly List<(long Ms, string Hex)> _pongs = [];
        private readonly List<string> _errors = [];
        private readonly Dictionary<string, int> _other = [];
        private int _cursorShapes, _cursorPositions;
        private long _frames, _keyFrames, _videoBytes, _lastFrameMs = -1;

        // copies to the device (mirrors AgentDesktopSession.OfferOrSend / SendTransfer / OnTransferReceipt)
        private int _transferIds;
        private ClipboardTransfer.Outgoing? _offered;
        private CancellationTokenSource? _sending;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<long>> _requests = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<D>> _sent = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<DesktopTransferEnd>> _receipts = new();

        // copies from the device (mirrors AgentDesktopSession.OnDeviceOffer / StartReceiving / ReceiveData / FinishReceiving)
        private readonly object _receiveGate = new();
        private TransferReceiver? _receiving;
        private string? _receivingFolder;
        private readonly List<DesktopTransfer> _deviceOffers = [];
        private readonly HashSet<int> _requestedOffers = [];
        private readonly ConcurrentDictionary<int, TaskCompletionSource<D>> _received = new();

        public async Task<object> RunAsync()
        {
            Directory.CreateDirectory(_out);
            var timeoutSeconds = int.TryParse(o.GetValueOrDefault("timeout"), out var t) ? t : 600;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var ct = timeout.Token;
            using var doc = JsonDocument.Parse(File.ReadAllText(o["steps"]), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The steps file must be a JSON array.");

            string? stopped = null;
            var i = 0;
            foreach (var step in doc.RootElement.EnumerateArray())
            {
                var record = new StepRecord { I = i++, Do = Str(step, "do") ?? "", AtMs = Ms };
                var sw = Stopwatch.StartNew();
                if (stopped is not null) record.Detail = new D { ["skipped"] = stopped };
                else
                {
                    try
                    {
                        (record.Ok, record.Detail) = await Step(record.Do, step, ct);
                        if (_connectError is not null) stopped = "the connection failed";
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        record.Detail = new D { ["failed"] = $"harness timeout ({timeoutSeconds} s)" };
                        stopped = "harness timeout";
                        AddError($"step {record.I} ({record.Do}): harness timeout after {timeoutSeconds} s");
                    }
                    catch (Exception ex)
                    {
                        record.Detail = new D { ["failed"] = ex.GetType().Name + ": " + ex.Message };
                        AddError($"step {record.I} ({record.Do}): {ex.GetType().Name}: {ex.Message}");
                    }
                }
                record.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                lock (_gate) _steps.Add(record);
            }

            // the end, like closing the tab: a normal close of what is still open
            if (_conn is { } open) { if (!open.Closed) open.EndedBy = "close"; open.Client.Dispose(); open.Decoder?.Dispose(); }
            lock (_receiveGate) { _receiving?.Dispose(); _receiving = null; }

            object result;
            lock (_gate)
            {
                result = new
                {
                    hello = _hello,
                    connectError = _connectError,
                    steps = _steps.ToList(),
                    statuses = _statuses.ToList(),
                    layouts = _layouts.ToList(),
                    stats = _stats.ToList(),
                    clipboard = _clipboard.ToList(),
                    transfers = _transfers.ToList(),
                    privacy = _privacy.ToList(),
                    pongs = _pongs.Select(p => new { ms = p.Ms, bytes = p.Hex }).ToList(),
                    cursor = new { shapes = _cursorShapes, positions = _cursorPositions },
                    frames = new
                    {
                        count = Interlocked.Read(ref _frames),
                        keyFrames = Interlocked.Read(ref _keyFrames),
                        lastAtMs = Interlocked.Read(ref _lastFrameMs) is var last and >= 0 ? last : (long?)null,
                        bytes = Interlocked.Read(ref _videoBytes),
                    },
                    images = _images.ToList(),
                    closed = _closes.LastOrDefault(),
                    closes = _closes.ToList(),
                    otherMessages = new Dictionary<string, int>(_other),
                    errors = _errors.ToList(),
                    seconds = Math.Round(_clock.Elapsed.TotalSeconds, 1),
                    outFolder = _out,
                };
            }
            try { File.WriteAllText(Path.Combine(_out, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions(Json) { WriteIndented = true })); }
            catch (Exception ex) { AddError("result.json: " + ex.Message); }
            return result;
        }

        private void AddError(string text)
        {
            lock (_gate) if (_errors.Count < 500) _errors.Add($"{Ms} ms: {text}");
        }

        private Conn Need() => _conn is { Closed: false } c ? c : throw new InvalidOperationException("not connected");

        private bool Supports(string feature) => _hello?.Features?.Contains(feature) == true;

        // ---- the steps

        private async Task<(bool, D)> Step(string kind, JsonElement s, CancellationToken ct)
        {
            switch (kind)
            {
                case "connect":
                case "reconnect":
                    return await Connect(kind == "reconnect", ct);

                case "start":
                {
                    // {session: "console" | id | "user:<name>", consent, viewOnly, fps, quality, monitor, resume: null | "last" | "<token>",
                    //  features: [..] (missing: what RemoteHub sends; null: none, like older versions), autoSignIn, endAction (Lock / SignOut), sendSignIn, signInPassword}
                    var c = Need();
                    var sessions = c.Client.Hello.Sessions;
                    DesktopSessionInfo? info;
                    int id;
                    var sv = s.TryGetProperty("session", out var v) ? v : default;
                    var text = sv.ValueKind switch { JsonValueKind.Number => sv.GetRawText(), JsonValueKind.String => sv.GetString()!, _ => "console" };
                    if (text.Equals("console", StringComparison.OrdinalIgnoreCase))
                    {
                        info = sessions.FirstOrDefault(x => x.Console);
                        if (info is null) return (false, new D { ["failed"] = "no console session", ["sessions"] = sessions });
                        id = info.Id;
                    }
                    else if (text.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = text[5..];
                        info = sessions.FirstOrDefault(x => x.User.Equals(name, StringComparison.OrdinalIgnoreCase) || x.User.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase));
                        if (info is null) return (false, new D { ["failed"] = "no session of " + name, ["sessions"] = sessions });
                        id = info.Id;
                    }
                    else
                    {
                        id = int.Parse(text, CultureInfo.InvariantCulture); // any id, also one the agent didn't list (to test refusals)
                        info = sessions.FirstOrDefault(x => x.Id == id);
                    }
                    var resumeArg = Str(s, "resume");
                    // "last", "file:<path>" (a token another harness run saved with saveToken) or the token itself
                    var resume = resumeArg == "last" ? _lastResume
                        : resumeArg?.StartsWith("file:", StringComparison.Ordinal) == true ? File.ReadAllText(resumeArg[5..]).Trim()
                        : resumeArg;
                    string[]? features = s.TryGetProperty("features", out var f) ? f.ValueKind == JsonValueKind.Null ? null : Strs(f) : ViewerFeatures;
                    var autoSignIn = Bool(s, "autoSignIn", false);
                    // "auto" (or missing) is what RemoteHub sends: None for a session of the account itself, otherwise Ask
                    var consent = Str(s, "consent") ?? "auto";
                    if (consent == "auto")
                        consent = info is not null && info.User.Equals(c.Client.Hello.Account, StringComparison.OrdinalIgnoreCase) ? DesktopConsent.None : DesktopConsent.Ask;
                    var start = new DesktopStart(id, consent, Bool(s, "viewOnly", false), Int(s, "fps", DesktopProtocol.DefaultFramesPerSecond),
                        Int(s, "quality", DesktopProtocol.DefaultQuality), Int(s, "monitor", 0), resume, features, autoSignIn, Str(s, "endAction"));
                    c.AutoSignIn = autoSignIn;
                    c.SendSignIn = Bool(s, "sendSignIn", true);
                    c.SignInPassword = Str(s, "signInPassword");
                    c.SignInSent = false;
                    lock (_gate) _statusMark = _statuses.Count;
                    c.Client.Send(Msg(DesktopMessage.Start, start));
                    return (true, new D
                    {
                        ["connection"] = c.N, ["session"] = info, ["sessionId"] = id, ["consent"] = start.Consent, ["viewOnly"] = start.ViewOnly,
                        ["fps"] = start.FramesPerSecond, ["quality"] = start.Quality, ["monitor"] = start.Monitor, ["features"] = features,
                        ["autoSignIn"] = autoSignIn, ["resume"] = resumeArg is null ? null : resume is null ? resumeArg + " (no token)" : resumeArg == "last" ? "last" : "given",
                    });
                }

                case "wait":
                {
                    // {state: "<DesktopStatusState>" | [..], anyOf: [..], timeout: s (30)}: a status received after the last start / wait
                    var states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (s.TryGetProperty("state", out var st)) states.UnionWith(Strs(st));
                    if (s.TryGetProperty("anyOf", out var any)) states.UnionWith(Strs(any));
                    var limit = Dbl(s, "timeout", 30);
                    var sw = Stopwatch.StartNew();
                    var conn = _conn;
                    StatusRecord? found = null;
                    var index = -1;
                    bool Check()
                    {
                        lock (_gate)
                            for (var k = _statusMark; k < _statuses.Count; k++)
                                if (states.Contains(_statuses[k].State)) { (found, index) = (_statuses[k], k); _statusMark = k + 1; return true; }
                        return false;
                    }
                    var closed = false;
                    while (!Check())
                    {
                        if (conn is null || conn.Closed) { closed = true; break; }
                        if (sw.Elapsed.TotalSeconds >= limit) break;
                        await Task.Delay(50, ct);
                    }
                    var detail = new D { ["states"] = states.ToArray(), ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2) };
                    if (found is not null)
                    {
                        detail["state"] = found.State;
                        detail["message"] = found.Message;
                        detail["pinAttemptsLeft"] = found.PinAttemptsLeft;
                        detail["index"] = index;
                        return (true, detail);
                    }
                    detail[closed ? "closed" : "timedOut"] = true;
                    return (false, detail);
                }

                case "sleep":
                    await Task.Delay(TimeSpan.FromSeconds(Dbl(s, "seconds", 1)), ct);
                    return (true, new D());

                case "pin":
                {
                    // {pin: "123456"} or {fromFile: path, timeout: s (120)} - the file's trimmed text once it has some
                    var c = Need();
                    var pin = Str(s, "pin");
                    if (pin is null && Str(s, "fromFile") is { } file)
                    {
                        var ok = await Until(() => { try { return File.Exists(file) && File.ReadAllText(file).Trim().Length > 0; } catch (IOException) { return false; } }, Dbl(s, "timeout", 120), ct);
                        if (!ok) return (false, new D { ["timedOut"] = true, ["fromFile"] = file });
                        pin = File.ReadAllText(file).Trim();
                    }
                    if (pin is null) return (false, new D { ["failed"] = "pin or fromFile needed" });
                    c.Client.Send(Msg(DesktopMessage.Pin, new DesktopPin(pin)));
                    return (true, new D { ["pin"] = pin });
                }

                case "signIn":
                {
                    // {password} (default --password): the sign-in request by hand, whatever the agent asked
                    var c = Need();
                    c.Client.Send(DesktopWire.SignInRequest(Str(s, "password") ?? o["password"]));
                    return (true, new D());
                }

                case "key":
                {
                    // {scan: 0x1D | "0x1D" | "0xE05B" (E0 prefix = Extended), flags: ["Extended", "Up"]}
                    var c = Need();
                    var (scan, flags) = Scan(s.GetProperty("scan"));
                    if (s.TryGetProperty("flags", out var fl))
                        foreach (var name in Strs(fl)) flags |= Enum.Parse<RemoteKeyFlags>(name, true);
                    c.Client.Send(DesktopWire.Key(new RemoteKey(scan, flags)));
                    return (true, new D { ["scan"] = scan, ["flags"] = flags.ToString() });
                }

                case "keys":
                {
                    // {text, delayMs (0)}: typed as a US keyboard would (see UsKeys); "\r" is skipped so "\r\n" is one Enter
                    var c = Need();
                    var text = Str(s, "text") ?? "";
                    var missing = text.Where(ch => ch != '\r' && !UsKeys.ContainsKey(ch)).Distinct().ToArray();
                    if (missing.Length > 0) return (false, new D { ["failed"] = "no US key for: " + new string(missing) });
                    var delay = Int(s, "delayMs", 0);
                    foreach (var ch in text)
                    {
                        if (ch == '\r') continue;
                        var (scan, shift) = UsKeys[ch];
                        if (shift) c.Client.Send(DesktopWire.Key(new RemoteKey(0x2A, RemoteKeyFlags.None)));
                        c.Client.Send(DesktopWire.Key(new RemoteKey(scan, RemoteKeyFlags.None)));
                        c.Client.Send(DesktopWire.Key(new RemoteKey(scan, RemoteKeyFlags.Up)));
                        if (shift) c.Client.Send(DesktopWire.Key(new RemoteKey(0x2A, RemoteKeyFlags.Up)));
                        if (delay > 0) await Task.Delay(delay, ct);
                    }
                    return (true, new D { ["chars"] = text.Length });
                }

                case "combo":
                {
                    // {scans: [0x1D, 0x38, "0xE053"], holdMs (50)}: pressed in order, released in reverse
                    var c = Need();
                    var keys = s.GetProperty("scans").EnumerateArray().Select(Scan).ToList();
                    foreach (var (scan, flags) in keys) c.Client.Send(DesktopWire.Key(new RemoteKey(scan, flags)));
                    await Task.Delay(Int(s, "holdMs", 50), ct);
                    for (var k = keys.Count - 1; k >= 0; k--) c.Client.Send(DesktopWire.Key(new RemoteKey(keys[k].Scan, keys[k].Flags | RemoteKeyFlags.Up)));
                    return (true, new D { ["scans"] = keys.Select(k => $"0x{(k.Flags.HasFlag(RemoteKeyFlags.Extended) ? 0xE000 : 0) | k.Scan:X}").ToArray() });
                }

                case "mouse":
                {
                    // {action: Move|Down|Up|Wheel|HorizontalWheel, button: None|Left|Right|Middle|X1|X2, x, y, delta}
                    var c = Need();
                    var m = new RemoteMouse(Enum.Parse<RemoteMouseAction>(Str(s, "action") ?? "Move", true), Enum.Parse<RemoteMouseButton>(Str(s, "button") ?? "None", true),
                        Int(s, "x", 0), Int(s, "y", 0), (short)Int(s, "delta", 0));
                    c.Client.Send(DesktopWire.Mouse(m));
                    return (true, new D { ["action"] = m.Action.ToString(), ["button"] = m.Button.ToString(), ["x"] = m.X, ["y"] = m.Y, ["delta"] = m.Delta });
                }

                case "click":
                {
                    // {x, y} or {fromFile, timeout (120)}: a left click where the test found something in the picture
                    // (it writes "x,y" to the file after reading a frame) - move, down, up as a person clicks
                    var c = Need();
                    int x = Int(s, "x", 0), y = Int(s, "y", 0);
                    if (Str(s, "fromFile") is { } file)
                    {
                        // The test writes the file with Set-Content while this step polls: it can exist and still be
                        // open or empty (run 20261019-193627: "being used by another process" lost the click), so the
                        // step waits until it reads as "x,y"
                        (int X, int Y)? read = null;
                        var ok = await Until(() => { read = ReadXy(file); return read is not null; }, Dbl(s, "timeout", 120), ct);
                        if (!ok || read is not { } got) return (false, new D { ["timedOut"] = true, ["file"] = file });
                        (x, y) = got;
                    }
                    c.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Move, RemoteMouseButton.None, x, y, 0)));
                    await Task.Delay(100, ct);
                    c.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, x, y, 0)));
                    await Task.Delay(80, ct);
                    c.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Up, RemoteMouseButton.Left, x, y, 0)));
                    return (true, new D { ["x"] = x, ["y"] = y });
                }

                case "moves":
                {
                    // {count (100), x0, y0, dx (1), dy (0), delayMs (0)}: a flood of moves
                    var c = Need();
                    int count = Int(s, "count", 100), x = Int(s, "x0", 0), y = Int(s, "y0", 0), dx = Int(s, "dx", 1), dy = Int(s, "dy", 0), delay = Int(s, "delayMs", 0);
                    for (var k = 0; k < count; k++)
                    {
                        c.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Move, RemoteMouseButton.None, x + k * dx, y + k * dy, 0)));
                        if (delay > 0) await Task.Delay(delay, ct);
                    }
                    return (true, new D { ["count"] = count });
                }

                case "cad":
                {
                    // {} or {ifFile: path, timeout (120)}: with ifFile only when the test wrote "cad" there after reading
                    // a frame - Server 2025 shows "Press Ctrl+Alt+Delete" after Switch user, Windows 11 the tiles
                    var c = Need();
                    if (Str(s, "ifFile") is { } file)
                    {
                        string? answer = null;
                        var ok = await Until(() => (answer = ReadAnswer(file)) is not null, Dbl(s, "timeout", 120), ct);
                        if (!ok) return (false, new D { ["timedOut"] = true, ["file"] = file });
                        if (!string.Equals(answer, "cad", StringComparison.OrdinalIgnoreCase)) return (true, new D { ["sent"] = false, ["answer"] = answer });
                    }
                    c.Client.Send(DesktopWire.Empty(DesktopMessage.SecureAttention));
                    return (true, new D { ["sent"] = true });
                }

                case "keyframe":
                    Need().Client.Send(DesktopWire.Empty(DesktopMessage.KeyFrameRequest));
                    return (true, new D());

                case "ping":
                {
                    // {bytes: 16 hex digits (default: a clock stamp), timeout (10)}: waits for the Pong with the same bytes
                    var c = Need();
                    var payload = Str(s, "bytes") is { } hex ? Convert.FromHexString(hex.Replace(" ", "")) : BitConverter.GetBytes(Stopwatch.GetTimestamp());
                    if (payload.Length != 8) return (false, new D { ["failed"] = "8 bytes needed" });
                    var want = Convert.ToHexStringLower(payload);
                    var sentAt = Ms;
                    c.Client.Send([(byte)DesktopMessage.Ping, .. payload]);
                    long? at = null;
                    await Until(() => { lock (_gate) at = _pongs.FirstOrDefault(p => p.Ms >= sentAt && p.Hex == want) is { Hex: not null } p ? p.Ms : null; return at is not null; }, Dbl(s, "timeout", 10), ct);
                    return at is { } got ? (true, new D { ["bytes"] = want, ["pong"] = want, ["roundTripMs"] = got - sentAt }) : (false, new D { ["bytes"] = want, ["timedOut"] = true });
                }

                case "settings":
                {
                    // {monitor (0), fps (30), quality (60), fitWidth (0), fitHeight (0)}
                    var settings = new DesktopSettings(Int(s, "monitor", 0), Int(s, "fps", DesktopProtocol.DefaultFramesPerSecond), Int(s, "quality", DesktopProtocol.DefaultQuality),
                        Int(s, "fitWidth", 0), Int(s, "fitHeight", 0));
                    Need().Client.Send(Msg(DesktopMessage.Settings, settings));
                    return (true, new D { ["settings"] = settings });
                }

                case "privacy":
                {
                    // {style: Off|Black|Message, blockLocalInput}
                    var screen = new DesktopPrivacyScreen(Str(s, "style") ?? DesktopPrivacyStyle.Message, Bool(s, "blockLocalInput", false));
                    Need().Client.Send(Msg(DesktopMessage.PrivacyScreen, screen));
                    return (true, new D { ["style"] = screen.Style, ["blockLocalInput"] = screen.BlockLocalInput });
                }

                case "clipboardText":
                {
                    // {text} or {lengthChars: n} (generated, deterministic): UTF-8 text as RemoteHub sends it (SyncClipboardToRemote)
                    var c = Need();
                    var text = Str(s, "text") ?? Generated(Int(s, "lengthChars", 1000));
                    c.Client.Send(DesktopWire.Text(DesktopMessage.Clipboard, text));
                    return (true, new D { ["length"] = text.Length, ["sha256"] = Sha256(Encoding.UTF8.GetBytes(text)) });
                }

                case "clipboardImage":
                {
                    // {path: .png | .bmp, offer, waitRequest, waitReceipt} - see SendOutgoing
                    Need();
                    var path = Str(s, "path") ?? throw new ArgumentException("path needed");
                    var o2 = ClipboardTransfer.Image(Interlocked.Increment(ref _transferIds), File.ReadAllBytes(path), png: path.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                    return await SendOutgoing(o2, s, ct);
                }

                case "files":
                {
                    // {paths: [..], offer, waitRequest, waitReceipt} - see SendOutgoing
                    Need();
                    var paths = s.TryGetProperty("paths", out var p) ? Strs(p) : [];
                    var o2 = ClipboardTransfer.Files(Interlocked.Increment(ref _transferIds), paths, out var error);
                    if (o2 is null) return (false, new D { ["failed"] = error });
                    return await SendOutgoing(o2, s, ct);
                }

                case "requestFiles":
                    return await RequestFiles(s, ct);

                case "raw":
                {
                    // {hex}: any bytes as one message
                    var bytes = Convert.FromHexString((Str(s, "hex") ?? "").Replace(" ", ""));
                    Need().Client.Send(bytes);
                    return (true, new D { ["bytes"] = bytes.Length });
                }

                case "rawJson":
                {
                    // {type: byte, json: "..."}: the type byte and the text as is (UTF-8)
                    var message = DesktopWire.Text((DesktopMessage)(byte)Int(s, "type", 0), Str(s, "json") ?? "");
                    Need().Client.Send(message);
                    return (true, new D { ["bytes"] = message.Length });
                }

                case "frame":
                    return await Frame(s, ct);

                case "saveToken":
                {
                    // {path}: the last resume token, for a start with resume "file:<path>" in another run (another account)
                    var path = Str(s, "path") ?? throw new ArgumentException("saveToken needs path");
                    if (_lastResume is null) return (false, new D { ["token"] = null });
                    File.WriteAllText(path, _lastResume);
                    return (true, new D { ["path"] = path });
                }

                case "markFile":
                {
                    // {path, text (the ms since the start)}
                    var path = Path.GetFullPath(Str(s, "path") ?? throw new ArgumentException("path needed"));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, Str(s, "text") ?? Ms.ToString(CultureInfo.InvariantCulture));
                    return (true, new D { ["path"] = path });
                }

                case "waitFile":
                {
                    // {path, timeout (120)}
                    var path = Path.GetFullPath(Str(s, "path") ?? throw new ArgumentException("path needed"));
                    var ok = await Until(() => File.Exists(path), Dbl(s, "timeout", 120), ct);
                    return (ok, ok ? new D { ["path"] = path } : new D { ["path"] = path, ["timedOut"] = true });
                }

                case "drop":
                case "close":
                    return await End(kind == "drop");

                default:
                    return (false, new D { ["failed"] = "unknown step: " + kind });
            }
        }

        // ---- connect: RemoteHub's client; a refusal is a result, not an exception

        private async Task<(bool, D)> Connect(bool again, CancellationToken ct)
        {
            if (_conn is { } old && again)
            {
                // the old connection ends normally first (the tab reconnects instead of keeping two)
                old.EndedBy = "close";
                old.Client.Dispose();
                old.Decoder?.Dispose();
                _conn = null;
            }
            var secure = new SecureString();
            foreach (var ch in o["password"]) secure.AppendChar(ch);
            var sw = Stopwatch.StartNew();
            AgentDesktopClient client;
            try
            {
                client = await AgentDesktopClient.ConnectAsync(o["host"], LabJump.Endpoint(o, int.Parse(o["port"], CultureInfo.InvariantCulture), o["pin"]),
                    new PSCredential(o["user"], secure), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _connectError = new D { ["connectError"] = ex.GetBaseException() == ex ? ex.Message : ex.Message + " (" + ex.GetBaseException().Message + ")", ["status"] = HttpStatus(ex), ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2) };
                return (false, _connectError);
            }
            var conn = new Conn(Interlocked.Increment(ref _connections), client);
            client.Received += m => OnMessage(conn, m);
            client.Closed += why => OnClosed(conn, why);
            _hello = client.Hello;
            _conn = conn;
            client.Start();
            return (true, new D { ["connection"] = conn.N, ["hello"] = client.Hello, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2) });
        }

        /// <summary>
        /// The HTTP status of a refused upgrade: RemoteHub keeps only its text, .NET's WebSocketException names it ("The server
        /// returned status code '401' when …"); else RemoteHub's text for it (AgentErrors.Explain).
        /// </summary>
        private static int? HttpStatus(Exception ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
                if (Regex.Match(e.Message, @"status code '(\d{3})'") is { Success: true } m) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var text = ex.Message;
            return text.Contains("rejected the sign-in") ? 401
                : text.Contains("is not allowed to use") || text.Contains("may not use") ? 403
                : text.Contains("isn't available") || text.Contains("turned off by a policy") || text.Contains("doesn't have the remote desktop") ? 404
                : text.Contains("refuses sign-ins") ? 429
                : text.Contains("limit of simultaneous sessions") ? 503
                : text.Contains("could not start PowerShell") ? 500
                : null;
        }

        /// <summary>drop: the socket aborted, no close handshake (a network cut); close: a normal close, then the agent's answer.</summary>
        private async Task<(bool, D)> End(bool abrupt)
        {
            var c = _conn ?? throw new InvalidOperationException("not connected");
            var ws = SocketOf(c.Client);
            var detail = new D { ["connection"] = c.N };
            c.EndedBy = abrupt ? "drop" : "close";
            if (abrupt)
            {
                if (ws is null) { detail["failed"] = "the client's socket can't be reached (AgentDesktopClient._ws renamed?)"; return (false, detail); }
                ws.Abort();
            }
            else if (ws is { State: WebSocketState.Open })
            {
                try
                {
                    using var cts = new CancellationTokenSource(5000);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "lab", cts.Token);
                }
                catch (Exception ex) { detail["closeError"] = ex.GetBaseException().Message; }
                await Until(() => c.Closed, 5, CancellationToken.None); // the agent's close comes back through the receive loop
                detail["closeStatus"] = ws.CloseStatus?.ToString();
                detail["closeDescription"] = ws.CloseStatusDescription;
            }
            c.Client.Dispose();
            c.Decoder?.Dispose();
            if (!c.Closed) OnClosed(c, abrupt ? "dropped by the harness (no close handshake)" : "closed by the harness");
            _conn = null;
            return (true, detail);
        }

        private void OnClosed(Conn c, string why)
        {
            if (c.Closed) return;
            // who ended it, read before Closed is set: the step loop ends its wait on Closed and then marks what is still
            // open as closed by the harness (lab run 20261021-062735, D-RES-07: the agent's close recorded as "close")
            var by = c.EndedBy ?? "agent";
            c.Closed = true;
            string? status = null, description = null;
            try { if (SocketOf(c.Client) is { } ws) (status, description) = (ws.CloseStatus?.ToString(), ws.CloseStatusDescription); } catch { /* disposed */ }
            lock (_gate) _closes.Add(new { ms = Ms, connection = c.N, why, closeStatus = status, closeDescription = description, by });
            foreach (var r in _received.Values) r.TrySetResult(new D { ["failed"] = "connection ended: " + why });
        }

        // ---- everything from the agent (receive thread; an exception here would end the connection)

        private void OnMessage(Conn c, byte[] m)
        {
            var type = DesktopWire.TypeOf(m);
            try
            {
                switch (type)
                {
                    case DesktopMessage.Video:
                    {
                        var h = DesktopWire.ReadVideo(m);
                        c.Client.Send(DesktopWire.Ack(h.FrameId)); // at once, every frame, so the agent keeps sending
                        Interlocked.Increment(ref _frames);
                        if (h.KeyFrame) Interlocked.Increment(ref _keyFrames);
                        Interlocked.Add(ref _videoBytes, m.Length);
                        Interlocked.Exchange(ref _lastFrameMs, Ms);
                        if (_decode && !c.Closed) (c.Decoder ??= new Decoder(this, c)).Enqueue(m);
                        break;
                    }
                    case DesktopMessage.Status:
                    {
                        var s = Read<DesktopStatus>(m);
                        lock (_gate) _statuses.Add(new StatusRecord(Ms, c.N, s.State, s.Message, s.PinAttemptsLeft, s.Resume is { Length: > 0 }));
                        if (s.Resume is { Length: > 0 }) _lastResume = s.Resume;
                        // mirrors AgentDesktopSession.SendSignIn: the password once, only when asked for in this connection
                        if (s.State == DesktopStatusState.SignInReady && c.AutoSignIn && c.SendSignIn && !c.SignInSent)
                        {
                            c.SignInSent = true;
                            c.Client.Send(DesktopWire.SignInRequest(c.SignInPassword ?? o["password"]));
                        }
                        break;
                    }
                    case DesktopMessage.Layout:
                        var layout = Read<DesktopLayout>(m);
                        lock (_gate) _layouts.Add(new { ms = Ms, connection = c.N, layout });
                        break;
                    case DesktopMessage.Stats:
                        var stats = Read<DesktopStats>(m);
                        lock (_gate) _stats.Add(new { ms = Ms, connection = c.N, stats });
                        break;
                    case DesktopMessage.Clipboard:
                        var text = DesktopWire.ReadText(m);
                        lock (_gate) _clipboard.Add(new { ms = Ms, connection = c.N, length = text.Length, sha256 = Sha256(m.AsSpan(1)), head = text.Length > 100 ? text[..100] : text });
                        break;
                    case DesktopMessage.Privacy:
                        var privacy = Read<DesktopPrivacy>(m);
                        lock (_gate) _privacy.Add(new { ms = Ms, connection = c.N, privacy });
                        break;
                    case DesktopMessage.CursorShape:
                        Interlocked.Increment(ref _cursorShapes);
                        break;
                    case DesktopMessage.CursorPosition:
                        Interlocked.Increment(ref _cursorPositions);
                        break;
                    case DesktopMessage.Pong:
                        var hex = Convert.ToHexStringLower(m.AsSpan(1));
                        lock (_gate) _pongs.Add((Ms, hex));
                        break;
                    case DesktopMessage.TransferOffer:
                    {
                        var t = Read<DesktopTransfer>(m);
                        lock (_gate) { _transfers.Add(new { ms = Ms, connection = c.N, kind = "offer", fromDevice = true, transfer = t }); _deviceOffers.Add(t); }
                        break;
                    }
                    case DesktopMessage.TransferRequest:
                    {
                        var idText = DesktopWire.ReadText(m);
                        lock (_gate) _transfers.Add(new { ms = Ms, connection = c.N, kind = "request", fromDevice = true, id = idText });
                        // mirrors AgentDesktopSession.OnTransferRequest: the offered copy goes now
                        if (int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                        {
                            _requests.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(Ms);
                            if (_offered is { } offered && offered.Transfer.Id == id) SendTransfer(c, offered);
                        }
                        break;
                    }
                    case DesktopMessage.TransferEnd:
                    {
                        var end = Read<DesktopTransferEnd>(m);
                        lock (_gate) _transfers.Add(new { ms = Ms, connection = c.N, kind = "end", fromDevice = true, end.Id, end.Ok, end.Reason, end.ToDevice });
                        if (end.ToDevice) { _receipts.GetOrAdd(end.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(end); break; }
                        lock (_receiveGate)
                            if (_receiving?.Transfer.Id == end.Id) { _receiving.Dispose(); _receiving = null; }
                        if (_received.TryGetValue(end.Id, out var done)) done.TrySetResult(new D { ["failed"] = "the device ended the transfer: " + end.Reason, ["ok"] = end.Ok });
                        break;
                    }
                    case DesktopMessage.TransferStart:
                    {
                        var t = Read<DesktopTransfer>(m);
                        lock (_gate) _transfers.Add(new { ms = Ms, connection = c.N, kind = "start", fromDevice = true, transfer = t });
                        lock (_receiveGate) StartReceiving(t);
                        break;
                    }
                    case DesktopMessage.TransferData:
                        lock (_receiveGate) ReceiveData(m);
                        break;
                    default:
                        lock (_gate) _other[type.ToString()] = _other.GetValueOrDefault(type.ToString()) + 1;
                        break;
                }
            }
            catch (Exception ex) { AddError($"received {type}: {ex.GetType().Name}: {ex.Message}"); }
        }

        // ---- copies to the device

        /// <summary>
        /// {offer (default: the agent has transferOnPaste), waitRequest (s, 60 when offering; 0 = don't wait), waitReceipt (s, 30 when the
        /// agent has transferReceipt)}. Mirrors AgentDesktopSession.OfferOrSend: offered (names and sizes), sent when the device asks
        /// for it - also later, after this step - or sent at once without offer; then the device's receipt.
        /// </summary>
        private async Task<(bool, D)> SendOutgoing(ClipboardTransfer.Outgoing t, JsonElement s, CancellationToken ct)
        {
            var c = Need();
            var id = t.Transfer.Id;
            var detail = new D { ["id"] = id, ["kind"] = t.Transfer.Kind, ["items"] = t.Transfer.Items.Length, ["total"] = t.Transfer.Total };
            var offer = Bool(s, "offer", Supports(DesktopFeatures.TransferOnPaste));
            var ok = true;
            var stepStart = Ms;
            var request = _requests.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (offer)
            {
                _offered = t;
                c.Client.Send(Msg(DesktopMessage.TransferOffer, t.Transfer));
                lock (_gate) _transfers.Add(new { ms = Ms, connection = c.N, kind = "offer", fromDevice = false, transfer = t.Transfer });
                detail["offered"] = true;
                var waitRequest = Dbl(s, "waitRequest", 60);
                if (waitRequest <= 0) return (true, detail);
                if (!await Until(() => request.Task.IsCompleted, waitRequest, ct))
                {
                    detail["requested"] = false;
                    return (false, detail);
                }
                detail["requested"] = true;
                detail["requestedAfter"] = Math.Round((request.Task.Result - stepStart) / 1000.0, 2);
            }
            else SendTransfer(c, t);
            var sent = _sent.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            await sent.Task.WaitAsync(ct);
            foreach (var (k, v) in sent.Task.Result) detail[k] = v;
            if (sent.Task.Result.ContainsKey("failed")) return (false, detail);
            var waitReceipt = Dbl(s, "waitReceipt", Supports(DesktopFeatures.TransferReceipt) ? 30 : 0);
            if (waitReceipt > 0)
            {
                var receipt = _receipts.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                if (await Until(() => receipt.Task.IsCompleted, waitReceipt, ct))
                {
                    detail["receipt"] = new { receipt.Task.Result.Ok, receipt.Task.Result.Reason };
                    ok = receipt.Task.Result.Ok;
                }
                else { detail["receipt"] = null; detail["receiptTimedOut"] = true; ok = false; }
            }
            return (ok, detail);
        }

        /// <summary>Mirrors AgentDesktopSession.SendTransfer: start, chunks with at most a few queued, a newer copy cancels ("replaced").</summary>
        private void SendTransfer(Conn c, ClipboardTransfer.Outgoing t)
        {
            _sending?.Cancel();
            var cancel = _sending = new CancellationTokenSource();
            var id = t.Transfer.Id;
            var done = _sent.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            var client = c.Client;
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                long bytes = 0;
                try
                {
                    client.Send(Msg(DesktopMessage.TransferStart, t.Transfer));
                    foreach (var chunk in ClipboardTransfer.Chunks(t))
                    {
                        while (client.Queued > 8 && !cancel.IsCancellationRequested && !c.Closed) await Task.Delay(10);
                        if (cancel.IsCancellationRequested || c.Closed)
                        {
                            if (!c.Closed) client.Send(Msg(DesktopMessage.TransferEnd, new DesktopTransferEnd(id, false, "replaced")));
                            done.TrySetResult(new D { ["failed"] = c.Closed ? "connection ended while sending" : "replaced by a newer copy", ["sentBytes"] = bytes });
                            return;
                        }
                        client.Send(chunk);
                        bytes += chunk.Length - DesktopWire.TransferHeaderSize;
                    }
                    while (client.Queued > 0 && !cancel.IsCancellationRequested && !c.Closed) await Task.Delay(20);
                    var seconds = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    lock (_gate) _transfers.Add(new { ms = Ms, connection = c.N, kind = "sent", fromDevice = false, id, bytes, seconds });
                    done.TrySetResult(c.Closed ? new D { ["failed"] = "connection ended while sending", ["sentBytes"] = bytes } : new D { ["sentBytes"] = bytes, ["sentSeconds"] = seconds });
                }
                catch (Exception ex)
                {
                    client.Send(Msg(DesktopMessage.TransferEnd, new DesktopTransferEnd(id, false, ex.Message)));
                    done.TrySetResult(new D { ["failed"] = ex.Message, ["sentBytes"] = bytes });
                }
            });
        }

        // ---- copies from the device

        /// <summary>
        /// {id (default: the newest offer not yet requested), timeout (s, 60: for the device's offer), receiveTimeout (s, 120)}: asks for
        /// what the device offered (mirrors AgentDesktopSession.FetchFromDevice) and receives it below out\received\&lt;id&gt;.
        /// </summary>
        private async Task<(bool, D)> RequestFiles(JsonElement s, CancellationToken ct)
        {
            var c = Need();
            var want = s.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.Number ? idv.GetInt32() : (int?)null;
            DesktopTransfer? offer = null;
            await Until(() =>
            {
                lock (_gate) offer = _deviceOffers.LastOrDefault(t => want is { } w ? t.Id == w : !_requestedOffers.Contains(t.Id));
                return offer is not null;
            }, Dbl(s, "timeout", 60), ct);
            if (offer is null) return (false, new D { ["timedOut"] = true, ["failed"] = "the device offered nothing" });
            lock (_gate) _requestedOffers.Add(offer.Id);
            var done = _received.GetOrAdd(offer.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            c.Client.Send(DesktopWire.Text(DesktopMessage.TransferRequest, offer.Id.ToString(CultureInfo.InvariantCulture)));
            var detail = new D { ["id"] = offer.Id, ["kind"] = offer.Kind, ["total"] = offer.Total };
            var sw = Stopwatch.StartNew();
            if (!await Until(() => done.Task.IsCompleted, Dbl(s, "receiveTimeout", 120), ct))
            {
                detail["timedOut"] = true;
                return (false, detail);
            }
            detail["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2);
            foreach (var (k, v) in done.Task.Result) detail[k] = v;
            return (!detail.ContainsKey("failed") && detail.GetValueOrDefault("ok") is true, detail);
        }

        /// <summary>Mirrors AgentDesktopSession.StartReceiving, into out\received\&lt;id&gt; (any policy level: what the agent sends is the finding).</summary>
        private void StartReceiving(DesktopTransfer t)
        {
            _receiving?.Dispose();
            _receiving = null;
            var folder = Path.Combine(_out, "received", t.Id.ToString(CultureInfo.InvariantCulture));
            for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(_out, "received", $"{t.Id}-{n}");
            try
            {
                _receivingFolder = folder;
                _receiving = new TransferReceiver(t, t.Kind == DesktopTransferKind.Files ? folder : null);
                if (_receiving.Complete) FinishReceiving();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                _receiving = null;
                Received(t.Id, new D { ["failed"] = "can't be taken: " + ex.Message });
            }
        }

        private void ReceiveData(byte[] m)
        {
            if (_receiving is not { } r) return;
            var (id, offset) = DesktopWire.ReadTransferData(m);
            if (id != r.Transfer.Id) return;
            try
            {
                r.Add(offset, m.AsSpan(DesktopWire.TransferHeaderSize));
                if (r.Complete) FinishReceiving();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                r.Dispose();
                _receiving = null;
                Received(id, new D { ["failed"] = "can't be taken: " + ex.Message });
            }
        }

        /// <summary>Every file checked against the size the device announced, with its SHA-256; an image is saved as image.png / .bmp.</summary>
        private void FinishReceiving()
        {
            var r = _receiving!;
            _receiving = null;
            var t = r.Transfer;
            var (image, png, targets) = (r.Image, r.IsPng, r.Targets);
            r.Dispose(); // complete: nothing is removed
            var files = new List<object>();
            var ok = true;
            if (t.Kind == DesktopTransferKind.Image)
            {
                Directory.CreateDirectory(_receivingFolder!);
                var path = Path.Combine(_receivingFolder!, png ? "image.png" : "image.bmp");
                File.WriteAllBytes(path, image);
                ok = image.Length == t.Total;
                files.Add(new { path, size = image.LongLength, expectedSize = t.Total, sha256 = Sha256(image) });
            }
            else
            {
                for (var k = 0; k < t.Items.Length; k++)
                {
                    var item = t.Items[k];
                    if (item.Folder) { files.Add(new { path = targets[k], folder = true, exists = Directory.Exists(targets[k]) }); ok &= Directory.Exists(targets[k]); continue; }
                    var size = File.Exists(targets[k]) ? new FileInfo(targets[k]).Length : -1;
                    ok &= size == item.Size;
                    files.Add(new { path = targets[k], size, expectedSize = item.Size, sha256 = size >= 0 ? Sha256File(targets[k]) : null });
                }
            }
            Received(t.Id, new D { ["ok"] = ok, ["folder"] = _receivingFolder, ["files"] = files });
        }

        private void Received(int id, D result)
        {
            lock (_gate) _transfers.Add(new { ms = Ms, kind = "received", fromDevice = true, id, result });
            _received.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(result);
        }

        // ---- pictures

        /// <summary>{out: "name.png" (below --out), timeout (30)}: the next picture decoded after this step starts, saved as PNG.</summary>
        private async Task<(bool, D)> Frame(JsonElement s, CancellationToken ct)
        {
            var c = Need();
            if (!_decode) return (false, new D { ["failed"] = "decoding is off (--decode 0)" });
            var path = Path.GetFullPath(Path.Combine(_out, Str(s, "out") ?? $"frame-{Ms}.png"));
            Decoder? d = null;
            var start = 0;
            try
            {
                var got = await Until(() =>
                {
                    if (d is null && c.Decoder is { } now)
                    {
                        d = now; Interlocked.Increment(ref d.Wanted); start = d.Pictures;
                        // a screen that doesn't change sends nothing new: a key frame is decoded while we wait
                        c.Client.Send(DesktopWire.Empty(DesktopMessage.KeyFrameRequest));
                    }
                    return d is not null && (d.Failed is not null || d.Pictures > start);
                }, Dbl(s, "timeout", 30), ct);
                if (d?.Failed is { } failed) return (false, new D { ["failed"] = "decoder: " + failed });
                if (!got || d!.Take() is not { } picture) return (false, new D { ["timedOut"] = true, ["frames"] = Interlocked.Read(ref _frames) });
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                SavePng(path, picture.Bgra, picture.Width, picture.Height);
                var image = new { path, width = picture.Width, height = picture.Height, meanLuma = MeanLuma(picture.Bgra), ms = Ms, connection = c.N };
                lock (_gate) _images.Add(image);
                return (true, new D { ["path"] = path, ["width"] = picture.Width, ["height"] = picture.Height, ["meanLuma"] = image.meanLuma });
            }
            finally { if (d is not null) Interlocked.Decrement(ref d.Wanted); }
        }

        private static void SavePng(string path, byte[] bgra, int w, int h)
        {
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                for (var y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(bgra, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
            }
            finally { bmp.UnlockBits(data); }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        /// <summary>0–255 over every 16th pixel – enough to tell a black (privacy) screen from a desktop.</summary>
        private static int MeanLuma(byte[] bgra)
        {
            long sum = 0, n = 0;
            for (var k = 0; k + 3 < bgra.Length; k += 64) { sum += (bgra[k] * 114 + bgra[k + 1] * 587 + bgra[k + 2] * 299) / 1000; n++; }
            return n == 0 ? 0 : (int)(sum / n);
        }

        /// <summary>
        /// H.264 decoding on its own thread – mirrors AgentDesktopSession.FrameDecoder.Run (private, and it shows into a WPF
        /// bitmap): after a frame that can't be decoded, flush, ask for a key frame and skip to it. Acks go out on arrival
        /// instead (OnMessage). The picture is only copied while a frame step waits for one.
        /// </summary>
        public sealed unsafe class Decoder : IDisposable
        {
            private readonly Script _s;
            private readonly Conn _c;
            private readonly BlockingCollection<byte[]> _queue = new();
            private readonly Thread _thread;
            private readonly object _lock = new();
            private byte[] _last = [];
            private int _w, _h, _pictures;
            public int Wanted;

            public Decoder(Script s, Conn c)
            {
                (_s, _c) = (s, c);
                _thread = new Thread(Run) { IsBackground = true, Name = "Desktop decoder " + c.N };
                _thread.Start();
            }

            public string? Failed { get; private set; }
            public int Pictures => Volatile.Read(ref _pictures);

            public void Enqueue(byte[] m)
            {
                if (Failed is not null) return;
                try { _queue.Add(m); } catch (InvalidOperationException) { /* closing */ }
            }

            public (byte[] Bgra, int Width, int Height)? Take()
            {
                lock (_lock) return _w == 0 ? null : (_last.ToArray(), _w, _h);
            }

            private void Run()
            {
                H264Decoder? decoder = null;
                try
                {
                    decoder = new H264Decoder();
                    var waitForKeyFrame = false;
                    foreach (var m in _queue.GetConsumingEnumerable())
                    {
                        var h = DesktopWire.ReadVideo(m);
                        if (waitForKeyFrame && !h.KeyFrame) continue;
                        waitForKeyFrame = false;
                        try { decoder.Decode(m.AsSpan(DesktopWire.VideoHeaderSize), h.Width, h.Height, Publish); }
                        catch (Exception ex)
                        {
                            _s.AddError($"connection {_c.N}: frame {h.FrameId} could not be decoded - {ex.Message}");
                            try { decoder.Flush(); } catch { /* a key frame starts clean */ }
                            waitForKeyFrame = true;
                            _c.Client.Send(DesktopWire.Empty(DesktopMessage.KeyFrameRequest));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Failed = ex.Message;
                    _s.AddError($"connection {_c.N}: decoder - {ex.Message}");
                }
                finally { decoder?.Dispose(); }
            }

            private void Publish(byte* bgra, int w, int h)
            {
                lock (_lock)
                {
                    if (Volatile.Read(ref Wanted) > 0)
                    {
                        if (_last.Length != w * h * 4) _last = new byte[w * h * 4];
                        new ReadOnlySpan<byte>(bgra, w * h * 4).CopyTo(_last);
                        (_w, _h) = (w, h);
                    }
                    _pictures++;
                }
            }

            public void Dispose()
            {
                _queue.CompleteAdding();
                _thread.Join(TimeSpan.FromSeconds(2));
            }
        }
    }

    // ---- helpers

    private static byte[] Msg<T>(DesktopMessage type, T value) => [(byte)type, .. JsonSerializer.SerializeToUtf8Bytes(value, Json)];

    private static T Read<T>(byte[] m) => JsonSerializer.Deserialize<T>(m.AsSpan(1), Json) ?? throw new InvalidDataException($"Empty {DesktopWire.TypeOf(m)} message.");

    private static async Task<bool> Until(Func<bool> condition, double seconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed.TotalSeconds >= seconds) return false;
            await Task.Delay(50, ct);
        }
        return true;
    }

    /// <summary>The "x,y" a test wrote to <paramref name="path"/>, or null while it is missing, open, empty or partial.</summary>
    private static (int X, int Y)? ReadXy(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        var xy = text.Trim().Split(',');
        return xy.Length == 2 && int.TryParse(xy[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && int.TryParse(xy[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            ? (x, y) : null;
    }

    /// <summary>The trimmed text a test wrote to <paramref name="path"/>, or null while it is missing, open or empty.</summary>
    private static string? ReadAnswer(string path)
    {
        try { return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } t ? t : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string Sha256(ReadOnlySpan<byte> data) =>Convert.ToHexStringLower(SHA256.HashData(data));

    private static string Sha256File(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    /// <summary>Deterministic text of <paramref name="length"/> characters: numbered lines of ASCII, CRLF.</summary>
    private static string Generated(int length)
    {
        var sb = new StringBuilder(length + 100);
        for (var line = 0; sb.Length < length; line++)
            sb.Append(CultureInfo.InvariantCulture, $"RemoteHub lab clipboard line {line:D6} abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789\r\n");
        return sb.ToString(0, length);
    }

    /// <summary>A scan code as a number or "0x.." text; 0xE0xx is the extended key xx (0xE05B = left Windows key).</summary>
    private static (ushort Scan, RemoteKeyFlags Flags) Scan(JsonElement v)
    {
        var n = v.ValueKind == JsonValueKind.Number ? v.GetInt64() : ParseNumber(v.GetString()!);
        return (n >> 8) == 0xE0 ? ((ushort)(n & 0xFF), RemoteKeyFlags.Extended) : ((ushort)n, RemoteKeyFlags.None);
    }

    private static long ParseNumber(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? long.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : long.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>
    /// US keyboard, scan code set 1 (what RemoteKey carries): the character → its key and whether Shift is held.
    /// a-z A-Z 0-9, space, all ASCII punctuation, "\n" Enter, "\t" Tab, "\b" Backspace.
    /// </summary>
    private static readonly Dictionary<char, (ushort Scan, bool Shift)> UsKeys = BuildUsKeys();

    private static Dictionary<char, (ushort, bool)> BuildUsKeys()
    {
        var d = new Dictionary<char, (ushort, bool)>();
        void Row(string plain, string shifted, ushort first)
        {
            for (var i = 0; i < plain.Length; i++)
            {
                d[plain[i]] = ((ushort)(first + i), false);
                d[shifted[i]] = ((ushort)(first + i), true);
            }
        }
        Row("1234567890-=", "!@#$%^&*()_+", 0x02);
        Row("qwertyuiop[]", "QWERTYUIOP{}", 0x10);
        Row("asdfghjkl;'`", "ASDFGHJKL:\"~", 0x1E);
        Row("\\zxcvbnm,./", "|ZXCVBNM<>?", 0x2B);
        d[' '] = (0x39, false);
        d['\n'] = (0x1C, false);
        d['\t'] = (0x0F, false);
        d['\b'] = (0x0E, false);
        return d;
    }

    // steps file fields: missing (or of another kind) → the default

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v)
        ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(), _ => null }
        : null;

    private static int Int(JsonElement e, string name, int def) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String
        ? (int)(v.ValueKind == JsonValueKind.Number ? v.GetInt64() : ParseNumber(v.GetString()!)) : def;

    private static double Dbl(JsonElement e, string name, double def) => e.TryGetProperty(name, out var v)
        ? v.ValueKind switch { JsonValueKind.Number => v.GetDouble(), JsonValueKind.String => double.Parse(v.GetString()!, CultureInfo.InvariantCulture), _ => def }
        : def;

    private static bool Bool(JsonElement e, string name, bool def) => e.TryGetProperty(name, out var v)
        ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.String => v.GetString() is "1" or "true", JsonValueKind.Number => v.GetInt64() != 0, _ => def }
        : def;

    /// <summary>A string or an array of strings (numbers as their text).</summary>
    private static string[] Strs(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Array => v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()).ToArray(),
        JsonValueKind.String => [v.GetString()!],
        _ => [],
    };
}
