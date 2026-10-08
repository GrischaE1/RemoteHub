using System.Security.Cryptography;
using System.Text;
using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>Writes a connection (with its effective, inherited settings) as a Microsoft .rdp file.</summary>
public static class RdpFileWriter
{
    /// <param name="includePassword">
    /// Stores the password as "password 51:b:" (DPAPI, current Windows user). mstsc on this PC/account can use it;
    /// it is useless anywhere else.
    /// </param>
    public static void Write(string path, Connection c, RdpOptions o, CredentialSet cred, bool includePassword)
    {
        var sb = new StringBuilder();
        void S(string key, string value) => sb.Append(key).Append(":s:").Append(value).Append("\r\n");
        void I(string key, int value) => sb.Append(key).Append(":i:").Append(value).Append("\r\n");
        int B(bool? b) => b == true ? 1 : 0;

        var host = c.Host.Contains(':') && !c.Host.StartsWith('[') ? $"[{c.Host}]" : c.Host; // IPv6 literal
        S("full address", c.Port == 3389 ? host : $"{host}:{c.Port}");

        if (!cred.IsEmpty)
        {
            S("username", string.IsNullOrEmpty(cred.Domain) ? cred.Username! : $"{cred.Domain}\\{cred.Username}");
            if (includePassword && !string.IsNullOrEmpty(cred.Password))
            {
                var blob = ProtectedData.Protect(Encoding.Unicode.GetBytes(cred.Password), null, DataProtectionScope.CurrentUser);
                sb.Append("password 51:b:").Append(Convert.ToHexString(blob)).Append("\r\n");
            }
        }
        I("prompt for credentials", 0);

        I("screen mode id", 1); // windowed
        if (o.Resize == DisplayResize.Fixed && Rdp.RdpSession.TryParseResolution(o.FixedResolution, out var w, out var h))
        {
            I("desktopwidth", w);
            I("desktopheight", h);
        }
        I("smart sizing", o.Resize == DisplayResize.SmartSizing ? 1 : 0);
        I("dynamic resolution", o.Resize == DisplayResize.DynamicResolution ? 1 : 0);
        I("session bpp", o.ColorDepth ?? 32);
        I("use multimon", B(o.UseMultimon));

        I("redirectclipboard", B(o.RedirectClipboard));
        I("redirectprinters", B(o.RedirectPrinters));
        I("redirectsmartcards", B(o.RedirectSmartCards));
        S("drivestoredirect", o.RedirectDrives == true ? "*" : "");
        I("audiomode", (int)(o.Audio ?? AudioMode.PlayLocal));
        I("keyboardhook", o.ForwardWindowsKeys == false ? 0 : 1);

        I("enablecredsspsupport", o.EnableNla == false ? 0 : 1);
        I("authentication level", (int)(o.ServerAuthentication ?? ServerAuth.Warn));
        I("administrative session", B(o.AdminSession));
        I("autoreconnection enabled", o.AutoReconnect == false ? 0 : 1);

        switch (o.Quality ?? VisualQuality.Balanced)
        {
            case VisualQuality.Best:
                I("disable wallpaper", 0); I("allow font smoothing", 1); I("allow desktop composition", 1);
                break;
            case VisualQuality.Balanced:
                I("disable wallpaper", 1); I("allow font smoothing", 1); I("allow desktop composition", 1); I("disable menu anims", 1);
                break;
            default:
                I("disable wallpaper", 1); I("allow font smoothing", 0); I("allow desktop composition", 0);
                I("disable full window drag", 1); I("disable menu anims", 1); I("disable themes", 1);
                break;
        }

        // mstsc writes UTF-16 LE with BOM.
        File.WriteAllText(path, sb.ToString(), Encoding.Unicode);
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return clean.Length == 0 ? "connection" : clean;
    }
}
