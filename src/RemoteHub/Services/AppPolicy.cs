using Microsoft.Win32;

namespace RemoteHub.Services;

/// <summary>
/// Settings IT can enforce under HKLM (or HKCU) \SOFTWARE\Policies\RemoteHub. A value set there wins over the user's
/// choice, and the settings window shows it as managed.
/// </summary>
public static class AppPolicy
{
    private const string Key = @"SOFTWARE\Policies\RemoteHub";

    /// <summary>DWORD DisableTelemetry = 1: no usage data is sent.</summary>
    public static bool TelemetryDisabled => Dword("DisableTelemetry") == 1;

    /// <summary>DWORD DisableUpdateCheck = 1: RemoteHub never contacts the update server (e.g. when updates come via software deployment).</summary>
    public static bool UpdateCheckDisabled => Dword("DisableUpdateCheck") == 1;

    /// <summary>String UpdateChannel = "stable" or "beta": fixes the update channel.</summary>
    public static string? UpdateChannel => String("UpdateChannel") is { } c && UpdateService.NormalizeChannel(c) is { } n ? n : null;

    /// <summary>DWORD DisableRecording = 1: tabs can't be recorded (the Record button is hidden).</summary>
    public static bool RecordingDisabled => Dword("DisableRecording") == 1;

    /// <summary>String RecordingFolder (environment variables allowed): where recordings are saved; users can't change it.</summary>
    public static string? RecordingFolder => String("RecordingFolder") is { } f ? Environment.ExpandEnvironmentVariables(f) : null;

    private static int? Dword(string name)
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = root.OpenSubKey(Key);
                if (k?.GetValue(name) is int v) return v;
            }
            catch { /* no access – ignore */ }
        }
        return null;
    }

    private static string? String(string name)
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = root.OpenSubKey(Key);
                if (k?.GetValue(name) is string s && s.Trim().Length > 0) return s.Trim();
            }
            catch { /* no access – ignore */ }
        }
        return null;
    }
}
