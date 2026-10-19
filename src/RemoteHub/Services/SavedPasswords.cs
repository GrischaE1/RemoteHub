using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>
/// Policy AllowSavedPasswords = 0 (docs/admx-design.md §5.6, §14 Q2): stored passwords aren't used while it is set
/// (<see cref="Inheritance"/> leaves them out), and RemoteHub removes them from this PC's settings after telling the user.
/// Never from a shared settings file: other PCs reading it may allow them.
/// </summary>
public static class SavedPasswords
{
    /// <summary>Clears every stored password – folders, connections, shares, the global defaults and saved credentials – and returns how many.</summary>
    public static int Remove(AppData data, IEnumerable<NodeBase> nodes)
    {
        var count = 0;
        void Clear(CredentialSet c)
        {
            if (string.IsNullOrEmpty(c.Password)) return;
            c.Password = null;
            count++;
        }
        foreach (var n in nodes)
        {
            Clear(n.Credentials);
            if (n is Connection c) foreach (var s in c.Shares) Clear(s.Credentials);
        }
        Clear(data.DefaultCredentials);
        foreach (var s in data.SavedCredentials)
            if (!string.IsNullOrEmpty(s.Password)) { s.Password = null; count++; }
        return count;
    }
}
