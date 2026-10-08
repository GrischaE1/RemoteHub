using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>Who owns a credential dialog window.</summary>
public enum PromptOwner { Ours, None, Other }

/// <summary>Decides which connecting session a Windows Security dialog belongs to – or none, if that is not certain.</summary>
public static class CredentialPromptMatcher
{
    /// <summary>A dialog without readable text is only attributed to a session that started connecting this recently.</summary>
    public static readonly TimeSpan UnreadableDialogWindow = TimeSpan.FromSeconds(60);

    public static bool UserMatches(string text, CredentialSet c)
    {
        if (string.IsNullOrEmpty(c.Username)) return false;
        var u = c.Username;
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        return text.Equals(u, cmp)
            || text.EndsWith("\\" + u, cmp)      // DOMAIN\user, .\user, SERVER\user
            || text.StartsWith(u + "@", cmp);    // user@domain
    }

    /// <param name="candidates">Sessions that are connecting and have a stored password.</param>
    /// <param name="owner">Owner of the dialog window.</param>
    /// <param name="texts">Texts read from the dialog (often empty: the dialog's content is not always readable).</param>
    public static T? Pick<T>(IReadOnlyList<T> candidates, Func<T, CredentialSet> credentials, Func<T, DateTime> started,
                             PromptOwner owner, IReadOnlyList<string> texts, DateTime nowUtc) where T : class
    {
        if (candidates.Count == 0) return null;

        if (texts.Count > 0)
        {
            var byUser = candidates.Where(s => texts.Any(t => UserMatches(t, credentials(s)))).ToList();
            if (byUser.Count == 0) return null; // the dialog shows someone else
            // Several sessions with the same user: only safe if they all use the same password.
            return byUser.Select(s => credentials(s).Password).Distinct().Count() == 1
                ? byUser.OrderByDescending(started).First()
                : null;
        }

        // Dialog text unreadable: accept only if the dialog is ours (or ownerless), exactly one session is
        // connecting, and that session started connecting just now.
        if (owner == PromptOwner.Other || candidates.Count != 1) return null;
        var only = candidates[0];
        return nowUtc - started(only) <= UnreadableDialogWindow ? only : null;
    }
}
