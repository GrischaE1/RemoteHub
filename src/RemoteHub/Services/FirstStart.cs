using RemoteHub.Models;

namespace RemoteHub.Services;

public enum FirstStartKind { None, Welcome, WhatsNew }

/// <summary>A version's highlights, as the What's new window shows them after an upgrade.</summary>
public sealed record WhatsNewEntry(string Version, string[] Highlights)
{
    public AppVersion ParsedVersion => AppVersion.Parse(Version) ?? throw new FormatException($"'{Version}' isn't a version.");
}

/// <summary>
/// What RemoteHub shows once on its first start: the welcome tour after a new install, What's new after an upgrade.
/// The last version that started is kept in <see cref="UiState.LastSeenVersion"/>.
/// </summary>
public static class FirstStart
{
    /// <summary>
    /// Highlights per version, newest first – written with each release, like the release highlights. A version with
    /// nothing worth telling the user has no entry, and its upgrade shows nothing.
    /// </summary>
    public static readonly WhatsNewEntry[] Entries =
    [
        new("0.21.0",
        [
            "Favourites – pin connections to the top of the navigation; they stay in their folders and keep what they inherit.",
            "Filter and sort in both file panes, the registry, services and processes – the first match is selected, so Enter opens it.",
            "Uninstall the RemoteHub Agent from a device: right-click the connection → Uninstall RemoteHub Agent…",
            "Welcome tour – a short introduction on the first start. Settings → Help & about shows it again.",
            "What's new – after an update, RemoteHub shows what changed, like this.",
            "Updates on the Beta channel also offer a GA release when it's newer than the last pre-release.",
            "The agent no longer loses a program's output on a busy PC, so a failed virtual display install says why.",
        ]),
    ];

    /// <summary>
    /// What to show on this start. Without a remembered version, settings with connections come from a version before
    /// What's new existed (an upgrade); empty settings are a new install.
    /// </summary>
    public static FirstStartKind Decide(string? lastSeen, bool hasConnections, AppVersion current)
    {
        if (AppVersion.Parse(lastSeen) is not { } last) return hasConnections ? Shown(null, current) : FirstStartKind.Welcome;
        return last < current ? Shown(last, current) : FirstStartKind.None;
    }

    private static FirstStartKind Shown(AppVersion? last, AppVersion current) =>
        Since(last, current).Count > 0 ? FirstStartKind.WhatsNew : FirstStartKind.None;

    /// <summary>
    /// The entries an upgrade from <paramref name="last"/> to <paramref name="current"/> brought, newest first. Without
    /// a known <paramref name="last"/>, only the newest entry up to <paramref name="current"/>.
    /// </summary>
    public static IReadOnlyList<WhatsNewEntry> Since(AppVersion? last, AppVersion current)
    {
        var upTo = Entries.Where(e => e.ParsedVersion <= current).OrderByDescending(e => e.ParsedVersion).ToList();
        return last is null ? upTo.Take(1).ToList() : upTo.Where(e => e.ParsedVersion > last).ToList();
    }
}
