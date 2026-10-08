using System.Globalization;
using System.Reflection;

namespace RemoteHub.Services;

/// <summary>
/// A release version: major.minor.patch with an optional pre-release suffix (0.9.1-beta.1), compared like SemVer 2.0:
/// a pre-release is lower than the release of the same number, and suffix parts compare numerically when both are numbers.
/// </summary>
public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>Pre-release suffix without the dash ("beta.1"), or empty.</summary>
    public string PreRelease { get; }
    public bool IsPreRelease => PreRelease.Length > 0;

    private AppVersion(int major, int minor, int patch, string pre) { Major = major; Minor = minor; Patch = patch; PreRelease = pre; }

    /// <summary>Parses "0.9.1", "v0.9.1", "0.9.1-beta.1" (build metadata after '+' is ignored). Null if it isn't a version.</summary>
    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        if (s.IndexOf('+') is var plus and >= 0) s = s[..plus];
        var pre = "";
        if (s.IndexOf('-') is var dash and >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0 || pre.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        }
        var parts = s.Split('.');
        if (parts.Length != 3) return null;
        var n = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return new AppVersion(n[0], n[1], n[2], pre);
    }

    /// <summary>The running RemoteHub version (from the informational version, without the source revision).</summary>
    public static AppVersion Current { get; } =
        Parse(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? Parse(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3))
        ?? new AppVersion(0, 0, 0, "");

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (!IsPreRelease || !other.IsPreRelease) return other.IsPreRelease.CompareTo(IsPreRelease); // release > pre-release
        var a = PreRelease.Split('.');
        var b = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(AppVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is AppVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}
