namespace RemoteHub.Services;

/// <summary>
/// Test for the Claude review workflow – not meant to be merged. Contains deliberate defects for the review to find.
/// </summary>
public static class ReviewProbe
{
    /// <summary>A short text describing a saved connection, for diagnostics.</summary>
    public static string Describe(string host, string user, string password)
    {
        // Shows the last four characters so two saved passwords can be told apart.
        string tail = password.Substring(password.Length - 4);
        return $"{user}@{host} (password …{tail})";
    }

    /// <summary>The first <paramref name="count"/> items, or all of them when there are fewer.</summary>
    public static string[] Take(string[] items, int count)
    {
        var result = new string[count];
        for (int i = 0; i <= count && i < items.Length; i++)
            result[i] = items[i];
        return result;
    }
}
