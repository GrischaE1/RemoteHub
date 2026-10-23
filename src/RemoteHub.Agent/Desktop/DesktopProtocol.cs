using System.Buffers.Binary;
using System.Threading;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RemoteHub.Agent;

/// <summary>
/// Remote desktop through the agent – the wire contract (compiled into RemoteHub and the agent).
/// <para>
/// <c>GET /v1/desktop</c> is a WebSocket upgrade on the agent's port, with the same pinned TLS and Windows sign-in as
/// <c>/v1/powershell</c>. Every binary WebSocket message is one <see cref="DesktopMessage"/>: a type byte followed by
/// its payload. Control messages carry UTF-8 JSON, the frequent ones (video, input, cursor) a compact binary layout.
/// The messages don't depend on the transport, so a WebRTC data channel or a relay can carry them later unchanged.
/// </para>
/// Flow: agent → Hello (rights, sessions, allowed consent modes) · RemoteHub → Start (session, consent mode, view-only,
/// quality) · agent → Status (waiting for the user / enter the code / connected / denied) · then Layout, Video, Cursor
/// from the agent and Ack, Mouse, Key, Clipboard, Settings from RemoteHub.
/// </summary>
public static class DesktopProtocol
{
    public const int Version = 1;
    public const string Path = "/v1/desktop";
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>Frames the agent sends ahead of the acknowledgements – keeps the delay low when the network is slow.</summary>
    public const int MaxFramesInFlight = 2;

    public const int DefaultFramesPerSecond = 30;
    public const int DefaultQuality = 60;

    /// <summary><see cref="DesktopSettings.Monitor"/> / <see cref="DesktopLayout.Monitor"/> for all monitors in one picture.</summary>
    public const int AllMonitors = -1;

    /// <summary>Largest picture the H.264 encoder takes; a larger "all monitors" picture is scaled down.</summary>
    public const int MaxVideoWidth = 4096, MaxVideoHeight = 2304;

    /// <summary>Resolutions RemoteHub may ask the device for (<see cref="DesktopSettings.FitWidth"/>): the virtual display's range at most.</summary>
    public const int MinFitWidth = 320, MinFitHeight = 200, MaxFitWidth = 7680, MaxFitHeight = 4320;

    /// <summary>
    /// A requested resolution as the agent applies it: even, at most <see cref="MaxFitWidth"/> × <see cref="MaxFitHeight"/>;
    /// (0, 0) – the device's own – when either side is below the minimum (or negative).
    /// </summary>
    public static (int Width, int Height) ValidFitSize(int width, int height) =>
        width < MinFitWidth || height < MinFitHeight ? (0, 0) : (Math.Min(width, MaxFitWidth) & ~1, Math.Min(height, MaxFitHeight) & ~1);

    /// <summary>Clipboard images and files: at most this much per copy, sent in chunks of <see cref="TransferChunkBytes"/>.</summary>
    public const long MaxTransferBytes = 100L * 1024 * 1024;
    public const int MaxTransferItems = 1000;
    /// <summary>The folder on the signed-in user's Desktop that files dropped on the tab are saved in (<see cref="DesktopTransferKind.Drop"/>).</summary>
    public const string DropFolder = "From RemoteHub";
    public const int TransferChunkBytes = 256 * 1024;
}

/// <summary>What an agent can do beyond protocol version 1 (in <see cref="DesktopHello.Features"/>; older agents send none).</summary>
public static class DesktopFeatures
{
    public const string AllMonitors = "allMonitors";
    public const string Stats = "stats";
    public const string Ping = "ping";
    public const string Resume = "resume";
    public const string Transfer = "transfer";
    /// <summary>The device confirms a transfer to it (TransferEnd with ToDevice, Ok) once the files are on its clipboard.</summary>
    public const string TransferReceipt = "transferReceipt";
    /// <summary>Copies from RemoteHub are offered and only sent when pasted on the device (TransferOffer / TransferRequest).</summary>
    public const string TransferOnPaste = "transferOnPaste";
    /// <summary>The admin can black out the device's screen (<see cref="DesktopMessage.PrivacyScreen"/>); Windows 10 2004 and later.</summary>
    public const string PrivacyScreen = "privacyScreen";
    /// <summary>The user at the device can hide their screen; the agent reports it (<see cref="DesktopMessage.Privacy"/>).</summary>
    public const string UserPrivacy = "userPrivacy";
    /// <summary>The user at the device can hide chosen apps; <see cref="DesktopPrivacy.HiddenApps"/> and <see cref="DesktopPrivacy.Regions"/> say so.</summary>
    public const string HideApps = "hideApps";
    /// <summary>
    /// Automatic sign-in on the console when nobody is signed in (<see cref="DesktopStart.AutoSignIn"/>, then
    /// <see cref="DesktopStatusState.SignInReady"/> and <see cref="DesktopMessage.SignInRequest"/>).
    /// </summary>
    public const string AutoSignIn = "autoSignIn";
    /// <summary>Files dropped on the tab are saved on the device's Desktop (<see cref="DesktopTransferKind.Drop"/>).</summary>
    public const string DropFiles = "dropFiles";
}

/// <summary>What may go through the clipboard (policy <c>DesktopClipboard</c>).</summary>
public static class DesktopClipboardLevel
{
    public const int Off = 0, Text = 1, Images = 2, Files = 3;
}

public enum DesktopMessage : byte
{
    // JSON
    Hello = 1,
    Start = 2,
    Status = 3,
    Pin = 4,
    Layout = 5,
    Settings = 6,
    /// <summary>UTF-8 text, both ways.</summary>
    Clipboard = 7,
    /// <summary>Agent → RemoteHub every 2 s: what the device sends (<see cref="DesktopStats"/>).</summary>
    Stats = 8,
    /// <summary>Both ways: a clipboard image or files follow as <see cref="TransferData"/> (<see cref="DesktopTransfer"/>).</summary>
    TransferStart = 9,
    /// <summary>Both ways: a transfer was cancelled or failed (<see cref="DesktopTransferEnd"/>); a complete one needs no end.</summary>
    TransferEnd = 10,
    /// <summary>
    /// RemoteHub → agent: files or an image were copied on this PC (<see cref="DesktopTransfer"/>, names and sizes only).
    /// The device's clipboard offers them; nothing is sent until someone pastes there (<see cref="TransferRequest"/>).
    /// </summary>
    TransferOffer = 11,
    /// <summary>Agent → RemoteHub: someone pastes on the device – send the offered transfer now (text: its id).</summary>
    TransferRequest = 12,
    /// <summary>RemoteHub → agent: black out the device's screen, or stop it (<see cref="DesktopPrivacyScreen"/>).</summary>
    PrivacyScreen = 13,
    /// <summary>Agent → RemoteHub: what is hidden now (<see cref="DesktopPrivacy"/>) – sent on every change.</summary>
    Privacy = 14,

    // binary
    Video = 20,
    Ack = 21,
    Mouse = 22,
    Key = 23,
    CursorShape = 24,
    CursorPosition = 25,
    /// <summary>Ctrl+Alt+Del (no payload).</summary>
    SecureAttention = 26,
    /// <summary>The viewer lost a frame: send a key frame (no payload).</summary>
    KeyFrameRequest = 27,
    /// <summary>RemoteHub → agent: 8 bytes, answered by <see cref="Pong"/> with the same bytes (round-trip time).</summary>
    Ping = 28,
    Pong = 29,
    /// <summary>Both ways: transfer id (4), offset (8), data – the items of the transfer one after the other.</summary>
    TransferData = 30,
    /// <summary>
    /// RemoteHub → agent, only after <see cref="DesktopStatusState.SignInReady"/>: the password for the automatic sign-in –
    /// length (2, in bytes), UTF-16LE. Never JSON; the agent reads it into protected memory (<see cref="DesktopWire.SignInRequest"/>).
    /// </summary>
    SignInRequest = 31,
}

/// <summary>How the signed-in user of the device is involved before their screen is shown.</summary>
public static class DesktopConsent
{
    /// <summary>The user is asked and must click Allow.</summary>
    public const string Ask = "Ask";
    /// <summary>The device shows a code that the user reads out; the admin enters it in RemoteHub.</summary>
    public const string Pin = "Pin";
    /// <summary>No question (needs the ConsentBypass right); the banner still shows.</summary>
    public const string None = "None";

    public static readonly string[] All = [Ask, Pin, None];
}

/// <summary>How the user at the device hides their screen from the admin (<see cref="DesktopPrivacy.User"/>).</summary>
public static class DesktopScreenHide
{
    public const string None = "None";
    /// <summary>Nothing is sent: one black frame, then no picture and no pointer until they show it again.</summary>
    public const string Black = "Black";
    /// <summary>The picture is blurred on the device so nothing can be read, at most 2 frames per second.</summary>
    public const string Blur = "Blur";

    public static string Valid(string? mode) => mode is Black or Blur ? mode : None;
}

/// <summary>What the admin's privacy screen shows the person at the device (<see cref="DesktopPrivacyScreen.Style"/>).</summary>
public static class DesktopPrivacyStyle
{
    public const string Off = "Off";
    public const string Black = "Black";
    /// <summary>The navy background with the logo and "Remote Support Session in Progress".</summary>
    public const string Message = "Message";

    public static string Valid(string? style) => style is Black or Message ? style : Off;
}

/// <summary>What happens to a session created by automatic sign-in when its connection ends on purpose (§9.2).</summary>
public static class AutoSignInEndActions
{
    public const string Lock = "Lock";
    public const string SignOut = "SignOut";

    /// <summary>Lock or SignOut; anything else is null (not set).</summary>
    public static string? Valid(string? value) =>
        string.Equals(value?.Trim(), SignOut, StringComparison.OrdinalIgnoreCase) ? SignOut
        : string.Equals(value?.Trim(), Lock, StringComparison.OrdinalIgnoreCase) ? Lock
        : null;
}

/// <summary>State of a Windows session as RemoteHub shows it.</summary>
public static class DesktopSessionState
{
    /// <summary>A user is signed in and the session is unlocked – the user is asked first (per consent mode).</summary>
    public const string Active = "Active";
    /// <summary>Signed in, but locked: the lock screen is shown, no question.</summary>
    public const string Locked = "Locked";
    /// <summary>Nobody is signed in: the Windows sign-in screen is shown.</summary>
    public const string SignIn = "SignIn";
    /// <summary>Signed in but without a screen (disconnected remote session) – can't be shown.</summary>
    public const string Disconnected = "Disconnected";
}

public static class DesktopStatusState
{
    public const string Starting = "Starting";
    public const string WaitingForUser = "WaitingForUser";
    public const string EnterPin = "EnterPin";
    public const string Connected = "Connected";
    /// <summary>
    /// While connected: the Windows session has no screen at the moment (its RDP connection was closed – "disconnected").
    /// The picture stands still until someone connects to it again; then Connected follows. Older RemoteHub versions ignore it.
    /// </summary>
    public const string Paused = "Paused";
    public const string Denied = "Denied";
    public const string Ended = "Ended";
    public const string Failed = "Failed";

    // Automatic sign-in (DesktopStart.AutoSignIn), while Connected to the sign-in screen. Older RemoteHub versions never ask for it.
    /// <summary>The agent waits for <see cref="DesktopMessage.SignInRequest"/> (30 s).</summary>
    public const string SignInReady = "SignInReady";
    public const string SigningIn = "SigningIn";
    /// <summary>Windows signed the account in and the agent checked it is the right one; the picture follows into the new session.</summary>
    public const string SignedIn = "SignedIn";
    /// <summary>No automatic sign-in; the message says why. The connection stays on the sign-in screen.</summary>
    public const string SignInFailed = "SignInFailed";
}

/// <summary>What the signed-in admin may do on this device (view is implied by a successful sign-in).</summary>
/// <param name="PrivacyScreen">May black out the device's screen (right PrivacyScreen, policy, Windows 10 2004 or later).</param>
/// <param name="PrivacyInputBlock">May also block the mouse and keyboard at the device while it is blacked out (policy PrivacyScreen = 2).</param>
/// <param name="AutoSignIn">May sign in automatically on the console when nobody is signed in (right, policy, credential provider installed and signed).</param>
public sealed record DesktopRights(bool Control, bool SecureDesktop, bool ConsentBypass, bool PrivacyScreen = false, bool PrivacyInputBlock = false,
    bool AutoSignIn = false);

/// <param name="Client">For a remote (RDP) session: the name of the PC it is connected from ("" otherwise).</param>
public sealed record DesktopSessionInfo(int Id, string User, string State, bool Console, string Station, string Client = "");

/// <param name="Features">See <see cref="DesktopFeatures"/> (null from older agents).</param>
/// <param name="Clipboard">See <see cref="DesktopClipboardLevel"/> (older agents: text).</param>
public sealed record DesktopHello(int Protocol, string Device, string Account, DesktopRights Rights, string[] ConsentModes,
    DesktopSessionInfo[] Sessions, int MaxFramesPerSecond, int MaxQuality, int ConsentTimeoutSeconds,
    string[]? Features = null, int Clipboard = DesktopClipboardLevel.Text, string? Version = null);

/// <param name="Resume">
/// The token from the last <see cref="DesktopStatus.Resume"/>: continues a session that was cut off a moment ago – the
/// user who allowed it isn't asked again while they are still the one signed in.
/// </param>
/// <param name="Features">What this RemoteHub supports (<see cref="DesktopFeatures"/>; null from older versions).</param>
/// <param name="AutoSignIn">Sign in automatically with the account that signed in to the agent – only for the console in the SignIn state, not view-only.</param>
/// <param name="AutoSignInEndAction">Lock or SignOut when the connection ends (the device's policy wins); null: Lock.</param>
public sealed record DesktopStart(int SessionId, string Consent, bool ViewOnly, int FramesPerSecond, int Quality, int Monitor, string? Resume = null,
    string[]? Features = null, bool AutoSignIn = false, string? AutoSignInEndAction = null);

/// <param name="Resume">With Connected: a single-use token to continue after a dropped connection (see <see cref="DesktopStart.Resume"/>).</param>
/// <param name="EndAction">With SignedIn: what happens to the session when the connection ends on purpose (Lock or SignOut).</param>
public sealed record DesktopStatus(string State, string Message, int PinAttemptsLeft = 0, string? Resume = null, string? EndAction = null);

/// <summary>What the device sends: frames in the last 2 s per second, the encoder's current quality, how it captures.</summary>
/// <param name="Fit">The last resolution change for the window: asked and what the monitor runs at now ("" when none).</param>
/// <param name="FrameRateLimit">The frame rate the slow-network steps allow now, when it is below what the viewer asked for (0: not lowered).</param>
public sealed record DesktopStats(int FramesPerSecond, int Quality, string Capture, int Width, int Height, string Fit = "", int FrameRateLimit = 0);

/// <summary>A file or folder of a clipboard transfer; <see cref="Path"/> is relative, with '/' between folders.</summary>
public sealed record DesktopTransferItem(string Path, long Size, bool Folder = false);

public static class DesktopTransferKind
{
    /// <summary>One item: a PNG or BMP file (by its name's extension).</summary>
    public const string Image = "image";
    public const string Files = "files";
    /// <summary>
    /// Files dropped on the agent desktop tab: sent right away (never offered) and saved in the signed-in user's
    /// Desktop, folder <c>From RemoteHub</c> – not put on the clipboard. Same right as copying files.
    /// </summary>
    public const string Drop = "drop";
}

public sealed record DesktopTransfer(int Id, string Kind, DesktopTransferItem[] Items, long Total);

/// <param name="ToDevice">About a transfer to the device (its receipt, or why it failed there) – the two directions number their transfers separately.</param>
/// <param name="Folder">With a receipt for dropped files: where they were saved, relative to the user's Desktop ("From RemoteHub", or a dated folder in it).</param>
public sealed record DesktopTransferEnd(int Id, bool Ok, string Reason = "", bool ToDevice = false, string Folder = "");

public sealed record DesktopPin(string Pin);

public sealed record DesktopRect(int X, int Y, int Width, int Height);

/// <param name="Style"><see cref="DesktopPrivacyStyle"/>; Off turns it off.</param>
/// <param name="BlockLocalInput">Also block the mouse and keyboard at the device (only with <see cref="DesktopRights.PrivacyInputBlock"/>).</param>
public sealed record DesktopPrivacyScreen(string Style, bool BlockLocalInput = false);

/// <param name="User">What the user at the device hid (<see cref="DesktopScreenHide"/>).</param>
/// <param name="Screen">The admin's privacy screen as it is on the device (<see cref="DesktopPrivacyStyle"/>).</param>
/// <param name="LocalInputBlocked">The mouse and keyboard at the device are blocked.</param>
/// <param name="Reason">Why the last privacy screen request didn't work ("" when it did).</param>
/// <param name="HiddenApps">How many apps the user at the device hides – never which. Apps only the policy hides aren't counted.</param>
/// <param name="Regions">Where hidden apps (the user's and the policy's) cover the picture now (picture pixels), for the viewer's labels; null when none.</param>
/// <param name="PolicyHiddenApps">
/// How many apps the device's policy always hides (policy HiddenApps) – never which. Older agents don't send it (0), and
/// older RemoteHub versions ignore it.
/// </param>
public sealed record DesktopPrivacy(string User, string Screen, bool LocalInputBlocked, string Reason = "", int HiddenApps = 0, DesktopRect[]? Regions = null, int PolicyHiddenApps = 0)
{
    public static readonly DesktopPrivacy Nothing = new(DesktopScreenHide.None, DesktopPrivacyStyle.Off, false);
}

public sealed record DesktopMonitor(int Index, string Name, int X, int Y, int Width, int Height, bool Primary);

/// <summary>Monitors of the session and which one is shown; sent at the start and whenever it changes.</summary>
public sealed record DesktopLayout(DesktopMonitor[] Monitors, int Monitor, int Width, int Height, bool SecureDesktop, bool ViewOnly);

/// <summary>
/// RemoteHub changes the monitor or the quality during the session. FitWidth/FitHeight (pixels, 0 = off): set the shown
/// monitor's resolution to this size – the viewer's window, or a size the admin chose, larger than the window too (the
/// nearest mode the display supports, checked with <see cref="DesktopProtocol.ValidFitSize"/>); it is restored at the end.
/// </summary>
public sealed record DesktopSettings(int Monitor, int FramesPerSecond, int Quality, int FitWidth = 0, int FitHeight = 0);

public enum RemoteMouseAction : byte { Move = 0, Down = 1, Up = 2, Wheel = 3, HorizontalWheel = 4 }

public enum RemoteMouseButton : byte { None = 0, Left = 1, Right = 2, Middle = 3, X1 = 4, X2 = 5 }

[Flags]
public enum RemoteKeyFlags : byte { None = 0, Extended = 1, Up = 2 }

public readonly record struct VideoHeader(uint FrameId, int Width, int Height, bool KeyFrame);
public readonly record struct RemoteMouse(RemoteMouseAction Action, RemoteMouseButton Button, int X, int Y, short Delta);
public readonly record struct RemoteKey(ushort ScanCode, RemoteKeyFlags Flags);
public readonly record struct RemoteCursor(int X, int Y, bool Visible);

/// <summary>Encoding and decoding of the messages (little-endian).</summary>
public static class DesktopWire
{
    public static DesktopMessage TypeOf(ReadOnlySpan<byte> message) => message.Length > 0 ? (DesktopMessage)message[0] : 0;

    public static byte[] Json<T>(DesktopMessage type, T value, JsonTypeInfo<T> info)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, info);
        var m = new byte[json.Length + 1];
        m[0] = (byte)type;
        json.CopyTo(m, 1);
        return m;
    }

    public static T ReadJson<T>(ReadOnlySpan<byte> message, JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(message[1..], info) ?? throw new InvalidDataException($"Empty {TypeOf(message)} message.");

    public static byte[] Empty(DesktopMessage type) => [(byte)type];

    public static byte[] Text(DesktopMessage type, string text)
    {
        var m = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        m[0] = (byte)type;
        Encoding.UTF8.GetBytes(text, m.AsSpan(1));
        return m;
    }

    public static string ReadText(ReadOnlySpan<byte> message) => Encoding.UTF8.GetString(message[1..]);

    // Video: type, frame id (4), width (2), height (2), flags (1), H.264 Annex B data
    public const int VideoHeaderSize = 10;

    public static byte[] Video(VideoHeader h, ReadOnlySpan<byte> data)
    {
        var m = new byte[VideoHeaderSize + data.Length];
        m[0] = (byte)DesktopMessage.Video;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), h.FrameId);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(5), (ushort)h.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(7), (ushort)h.Height);
        m[9] = h.KeyFrame ? (byte)1 : (byte)0;
        data.CopyTo(m.AsSpan(VideoHeaderSize));
        return m;
    }

    public static VideoHeader ReadVideo(ReadOnlySpan<byte> m)
    {
        if (m.Length < VideoHeaderSize) throw new InvalidDataException("Short video message.");
        return new VideoHeader(BinaryPrimitives.ReadUInt32LittleEndian(m[1..]), BinaryPrimitives.ReadUInt16LittleEndian(m[5..]),
            BinaryPrimitives.ReadUInt16LittleEndian(m[7..]), (m[9] & 1) != 0);
    }

    public static byte[] Ack(uint frameId)
    {
        var m = new byte[5];
        m[0] = (byte)DesktopMessage.Ack;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), frameId);
        return m;
    }

    public static uint ReadAck(ReadOnlySpan<byte> m) => m.Length >= 5 ? BinaryPrimitives.ReadUInt32LittleEndian(m[1..]) : throw new InvalidDataException("Short ack.");

    // Mouse: type, action (1), button (1), x (4), y (4), delta (2) – x/y in pixels of the shown monitor
    public static byte[] Mouse(RemoteMouse i)
    {
        var m = new byte[13];
        m[0] = (byte)DesktopMessage.Mouse;
        m[1] = (byte)i.Action;
        m[2] = (byte)i.Button;
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(3), i.X);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(7), i.Y);
        BinaryPrimitives.WriteInt16LittleEndian(m.AsSpan(11), i.Delta);
        return m;
    }

    public static RemoteMouse ReadMouse(ReadOnlySpan<byte> m) => m.Length >= 13
        ? new RemoteMouse((RemoteMouseAction)m[1], (RemoteMouseButton)m[2], BinaryPrimitives.ReadInt32LittleEndian(m[3..]), BinaryPrimitives.ReadInt32LittleEndian(m[7..]),
            BinaryPrimitives.ReadInt16LittleEndian(m[11..]))
        : throw new InvalidDataException("Short mouse message.");

    // Key: type, scan code (2), flags (1)
    public static byte[] Key(RemoteKey k)
    {
        var m = new byte[4];
        m[0] = (byte)DesktopMessage.Key;
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(1), k.ScanCode);
        m[3] = (byte)k.Flags;
        return m;
    }

    public static RemoteKey ReadKey(ReadOnlySpan<byte> m) => m.Length >= 4
        ? new RemoteKey(BinaryPrimitives.ReadUInt16LittleEndian(m[1..]), (RemoteKeyFlags)m[3])
        : throw new InvalidDataException("Short key message.");

    // Cursor shape: type, width (2), height (2), hotspot x (2), hotspot y (2), BGRA premultiplied-free pixels
    public static byte[] CursorShape(int width, int height, int hotX, int hotY, ReadOnlySpan<byte> bgra)
    {
        var m = new byte[9 + bgra.Length];
        m[0] = (byte)DesktopMessage.CursorShape;
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(1), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(3), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(5), (ushort)hotX);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(7), (ushort)hotY);
        bgra.CopyTo(m.AsSpan(9));
        return m;
    }

    public static (int Width, int Height, int HotX, int HotY) ReadCursorShape(ReadOnlySpan<byte> m)
    {
        if (m.Length < 9) throw new InvalidDataException("Short cursor message.");
        int w = BinaryPrimitives.ReadUInt16LittleEndian(m[1..]), h = BinaryPrimitives.ReadUInt16LittleEndian(m[3..]);
        if (m.Length < 9 + w * h * 4) throw new InvalidDataException("Short cursor message.");
        return (w, h, BinaryPrimitives.ReadUInt16LittleEndian(m[5..]), BinaryPrimitives.ReadUInt16LittleEndian(m[7..]));
    }

    public const int CursorShapeHeaderSize = 9;

    public static byte[] Cursor(RemoteCursor p)
    {
        var m = new byte[10];
        m[0] = (byte)DesktopMessage.CursorPosition;
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(1), p.X);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(5), p.Y);
        m[9] = p.Visible ? (byte)1 : (byte)0;
        return m;
    }

    public static RemoteCursor ReadCursor(ReadOnlySpan<byte> m) => m.Length >= 10
        ? new RemoteCursor(BinaryPrimitives.ReadInt32LittleEndian(m[1..]), BinaryPrimitives.ReadInt32LittleEndian(m[5..]), m[9] != 0)
        : throw new InvalidDataException("Short cursor message.");

    // Ping / Pong: type, 8 bytes (the sender's clock)
    public static byte[] Ping(long stamp, DesktopMessage type = DesktopMessage.Ping)
    {
        var m = new byte[9];
        m[0] = (byte)type;
        BinaryPrimitives.WriteInt64LittleEndian(m.AsSpan(1), stamp);
        return m;
    }

    public static long ReadPing(ReadOnlySpan<byte> m) => m.Length >= 9 ? BinaryPrimitives.ReadInt64LittleEndian(m[1..]) : throw new InvalidDataException("Short ping.");

    // Sign-in request: type, length in bytes (2), the password as UTF-16LE
    public const int SignInRequestHeaderSize = 3;
    public const int MaxSignInPasswordChars = 256;

    /// <summary>
    /// The password as a sign-in request – into a new array the caller wipes after sending (it holds the password in
    /// the clear). Never through a string the caller keeps.
    /// </summary>
    public static byte[] SignInRequest(ReadOnlySpan<char> password)
    {
        if (password.Length is 0 or > MaxSignInPasswordChars) throw new ArgumentException("The password is empty or too long.");
        var m = new byte[SignInRequestHeaderSize + password.Length * 2];
        m[0] = (byte)DesktopMessage.SignInRequest;
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(1), (ushort)(password.Length * 2));
        for (var i = 0; i < password.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(SignInRequestHeaderSize + i * 2), password[i]);
        return m;
    }

    /// <summary>The password bytes (UTF-16LE) of a sign-in request, or empty if it is malformed.</summary>
    public static ReadOnlySpan<byte> ReadSignInRequest(ReadOnlySpan<byte> m)
    {
        if (m.Length < SignInRequestHeaderSize || m[0] != (byte)DesktopMessage.SignInRequest) return default;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(m[1..]);
        return length is > 0 and <= MaxSignInPasswordChars * 2 && length % 2 == 0 && m.Length == SignInRequestHeaderSize + length
            ? m.Slice(SignInRequestHeaderSize, length) : default;
    }

    // Transfer data: type, id (4), offset (8), data
    public const int TransferHeaderSize = 13;

    public static byte[] TransferData(int id, long offset, ReadOnlySpan<byte> data)
    {
        var m = new byte[TransferHeaderSize + data.Length];
        m[0] = (byte)DesktopMessage.TransferData;
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(1), id);
        BinaryPrimitives.WriteInt64LittleEndian(m.AsSpan(5), offset);
        data.CopyTo(m.AsSpan(TransferHeaderSize));
        return m;
    }

    public static (int Id, long Offset) ReadTransferData(ReadOnlySpan<byte> m) => m.Length >= TransferHeaderSize
        ? (BinaryPrimitives.ReadInt32LittleEndian(m[1..]), BinaryPrimitives.ReadInt64LittleEndian(m[5..]))
        : throw new InvalidDataException("Short transfer message.");

    /// <summary>
    /// A transfer item's path made safe to write below a folder: relative, no "..", no drive or invalid characters, no
    /// device names (CON, NUL, COM1 …). Null if nothing usable is left.
    /// </summary>
    public static string? SafeRelativePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string(p.Where(c => c >= 32 && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim().TrimEnd('.'))
            .ToList();
        if (parts.Count == 0 || parts.Any(p => p.Length == 0 || p is "." or ".." || IsDeviceName(p))) return null;
        return Path.Combine([.. parts]);
    }

    /// <summary>
    /// The full path of <paramref name="path"/> (made safe, see <see cref="SafeRelativePath"/>) below
    /// <paramref name="folder"/>, or null if it would end up anywhere else.
    /// </summary>
    public static string? PathBelow(string folder, string path)
    {
        if (SafeRelativePath(path) is not { } relative) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9' or '¹' or '²' or '³';
    }

    /// <summary>Length-prefixed messages on a byte stream (the pipe between the agent service and its desktop helper).</summary>
    public static async Task WriteFramedAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, message.Length);
        await stream.WriteAsync(length, ct).ConfigureAwait(false);
        await stream.WriteAsync(message, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Null at the end of the stream.</summary>
    public static async Task<byte[]?> ReadFramedAsync(Stream stream, CancellationToken ct)
    {
        var length = new byte[4];
        if (!await ReadExactlyOrEnd(stream, length, ct).ConfigureAwait(false)) return null;
        var n = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (n is <= 0 or > DesktopProtocol.MaxMessageBytes) throw new InvalidDataException($"Invalid message length {n}.");
        var m = new byte[n];
        if (!await ReadExactlyOrEnd(stream, m, ct).ConfigureAwait(false)) throw new EndOfStreamException();
        return m;
    }

    private static async Task<bool> ReadExactlyOrEnd(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException();
            read += n;
        }
        return true;
    }
}

[JsonSerializable(typeof(DesktopHello))]
[JsonSerializable(typeof(DesktopStart))]
[JsonSerializable(typeof(DesktopStatus))]
[JsonSerializable(typeof(DesktopPin))]
[JsonSerializable(typeof(DesktopLayout))]
[JsonSerializable(typeof(DesktopSettings))]
[JsonSerializable(typeof(DesktopStats))]
[JsonSerializable(typeof(DesktopTransfer))]
[JsonSerializable(typeof(DesktopTransferEnd))]
[JsonSerializable(typeof(DesktopPrivacyScreen))]
[JsonSerializable(typeof(DesktopPrivacy))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class DesktopJson : JsonSerializerContext;
