using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>Reads Microsoft .rdp files ("key:type:value" lines) into a <see cref="Connection"/>.</summary>
public static class RdpFileParser
{
    public static Connection Parse(string path)
    {
        // .rdp files written by mstsc are usually UTF-16 LE with BOM; ReadAllLines detects the BOM.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim('﻿', ' ', '\t');
            var first = line.IndexOf(':');
            if (first <= 0) continue;
            var second = line.IndexOf(':', first + 1);
            if (second < 0) continue;
            values[line[..first].Trim()] = line[(second + 1)..].Trim();
        }

        string? S(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        int? I(string key) => S(key) is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
        bool? B(string key) => I(key) is { } i ? i != 0 : null;

        var (host, port) = SplitAddress(S("full address") ?? S("alternate full address") ?? "");
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidDataException($"\"{Path.GetFileName(path)}\" does not contain a computer name (full address).");
        if (I("server port") is { } sp) port = sp;

        var c = new Connection
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Host = host,
            Port = port,
        };

        var user = S("username");
        if (user is not null)
        {
            c.InheritCredentials = false;
            var domain = S("domain");
            var slash = user.IndexOf('\\');
            if (slash > 0 && domain is null) { domain = user[..slash]; user = user[(slash + 1)..]; }
            c.Credentials = new CredentialSet { Username = user, Domain = domain, Password = TryDecryptPassword(S("password 51")) };
        }

        var o = c.Options;
        o.RedirectClipboard = B("redirectclipboard");
        o.RedirectPrinters = B("redirectprinters");
        o.RedirectSmartCards = B("redirectsmartcards");
        o.UseMultimon = B("use multimon");
        o.AdminSession = B("administrative session");
        o.AutoReconnect = B("autoreconnection enabled");
        if (S("drivestoredirect") is { } drives) o.RedirectDrives = drives.Length > 0;
        else if (B("redirectdrives") is { } rd) o.RedirectDrives = rd;
        if (I("audiomode") is { } am and >= 0 and <= 2) o.Audio = (AudioMode)am;
        if (I("keyboardhook") is { } kh) o.ForwardWindowsKeys = kh != 0;
        if (I("authentication level") is { } al and >= 0 and <= 2) o.ServerAuthentication = (ServerAuth)al;
        if (I("enablecredsspsupport") is { } ssp) o.EnableNla = ssp != 0;
        if (I("session bpp") is { } bpp && bpp is 15 or 16 or 24 or 32) o.ColorDepth = bpp;
        if (I("smart sizing") == 1) o.Resize = DisplayResize.SmartSizing;
        else if (I("dynamic resolution") == 1) o.Resize = DisplayResize.DynamicResolution;
        if (I("screen mode id") == 1 && I("desktopwidth") is { } w && I("desktopheight") is { } h && w > 0 && h > 0)
        {
            o.Resize ??= DisplayResize.Fixed;
            o.FixedResolution = $"{w}x{h}";
        }

        var gw = S("gatewayhostname");
        if (gw is not null) c.Notes = $"Imported from .rdp – gateway in file: {gw}";
        return c;
    }

    /// <summary>
    /// What an imported connection would do that the user should know before connecting to a server named by a file
    /// from outside (like mstsc's warning for unsigned .rdp files): who signs in, and what weakens or shares something.
    /// </summary>
    public static List<string> Risks(Connection c)
    {
        var o = c.Options;
        var list = new List<string>
        {
            c.InheritCredentials
                ? "Signs in with your default credentials from RemoteHub."
                : $"Signs in as {(c.Credentials?.Domain is { Length: > 0 } d ? d + "\\" : "")}{c.Credentials?.Username} (from the file).",
        };
        if (o.ServerAuthentication == ServerAuth.ConnectWithoutWarning) list.Add("Doesn't check the server's identity (authentication level 0).");
        if (o.EnableNla == false) list.Add("Network Level Authentication off: the server isn't verified before you sign in.");
        if (o.RedirectDrives == true) list.Add("Shares this PC's drives with the server.");
        if (o.RedirectSmartCards == true) list.Add("Shares your smart cards with the server.");
        if (o.RedirectClipboard == true) list.Add("Shares the clipboard with the server.");
        if (o.RedirectPrinters == true) list.Add("Shares your printers with the server.");
        return list;
    }

    /// <summary>"password 51:b:" is DPAPI-protected by mstsc for the current user; only readable on the same account.</summary>
    private static Secret? TryDecryptPassword(string? hex)
    {
        if (hex is null) return null;
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(Convert.FromHexString(hex), null, DataProtectionScope.CurrentUser);
            return Secret.From(MemoryMarshal.Cast<byte, char>(plain.AsSpan(0, plain.Length & ~1)).TrimEnd('\0'));
        }
        catch { return null; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public static (string Host, int Port) SplitAddress(string address)
    {
        address = address.Trim();
        // IPv6 literal: [fe80::1]:3390
        if (address.StartsWith('['))
        {
            var end = address.IndexOf(']');
            if (end > 0)
            {
                var h = address[1..end];
                return address.Length > end + 2 && int.TryParse(address[(end + 2)..], out var p6) ? (h, p6) : (h, 3389);
            }
        }
        var colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out var p))
            return (address[..colon], p);
        return (address, 3389);
    }
}
