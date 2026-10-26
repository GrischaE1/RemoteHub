using System.Security.Cryptography;

namespace RemoteHub.Broker;

/// <summary>
/// This PC's key towards one RemoteHub Broker organisation (broker-design.md §6.3): ECDSA P-256 in CNG for the current
/// Windows user, made here and never exportable, so a copied database or settings file holds nothing to sign in with.
/// The broker knows only its public part.
/// </summary>
internal static class BrokerKey
{
    private static string Name(string consoleId) => "RemoteHub Broker console " + consoleId;

    public static ECDsa Create(string consoleId)
    {
        var parameters = new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
            KeyUsage = CngKeyUsages.Signing,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
        };
        return new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, Name(consoleId), parameters));
    }

    /// <summary>The key made for this console, or null when it is gone (another Windows user, a reinstalled PC).</summary>
    public static ECDsa? Open(string consoleId)
    {
        try { return CngKey.Exists(Name(consoleId)) ? new ECDsaCng(CngKey.Open(Name(consoleId))) : null; }
        catch (CryptographicException) { return null; }
    }

    public static void Delete(string consoleId)
    {
        try
        {
            if (CngKey.Exists(Name(consoleId))) CngKey.Open(Name(consoleId)).Delete();
        }
        catch (CryptographicException) { /* already gone */ }
    }
}
