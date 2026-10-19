using System.ComponentModel;
using Microsoft.AspNetCore.Http;

namespace RemoteHub.Agent;

/// <summary>
/// What a failed network sign-in (<c>LogonUser</c>, LOGON32_LOGON_NETWORK) means: Windows refusing the account is a
/// refusal (401, or 403 when the account may not sign in this way), and only Windows being unable to check it at all is
/// an error (500). The reason goes to the audit log (event 2000) – never the password.
/// </summary>
public static class SignInRefusal
{
    // winerror.h
    public const int ErrorNoLogonServers = 1311, ErrorNoSuchUser = 1317, ErrorLogonFailure = 1326, ErrorAccountRestriction = 1327,
        ErrorInvalidLogonHours = 1328, ErrorInvalidWorkstation = 1329, ErrorPasswordExpired = 1330, ErrorAccountDisabled = 1331,
        ErrorNoSuchDomain = 1355, ErrorLogonTypeNotGranted = 1385, ErrorNoTrustLsaSecret = 1786, ErrorNoTrustSamAccount = 1787,
        ErrorTrustedDomainFailure = 1788, ErrorTrustedRelationshipFailure = 1789, ErrorAccountExpired = 1793,
        ErrorPasswordMustChange = 1907, ErrorAccountLockedOut = 1909, ErrorAuthenticationFirewallFailed = 1935;

    /// <summary>The outcome and reason for a <c>LogonUser</c> error.</summary>
    public static AuthResult FromWin32(int error)
    {
        var (outcome, reason) = error switch
        {
            ErrorLogonFailure => (AuthOutcome.BadCredentials, "The user name or password is wrong."),
            ErrorNoSuchUser => (AuthOutcome.BadCredentials, "The account doesn't exist."),
            ErrorNoSuchDomain => (AuthOutcome.BadCredentials, "The domain in the user name doesn't exist."),
            ErrorAccountRestriction => (AuthOutcome.BadCredentials,
                "Account restrictions prevent this sign-in (for example a blank password, which Windows allows only at the device's screen)."),
            ErrorInvalidLogonHours => (AuthOutcome.BadCredentials, "The account may not sign in at this time of day."),
            ErrorInvalidWorkstation => (AuthOutcome.BadCredentials, "The account may not sign in on this device."),
            ErrorPasswordExpired => (AuthOutcome.BadCredentials, "The password has expired."),
            ErrorPasswordMustChange => (AuthOutcome.BadCredentials, "The password must be changed before the account can sign in."),
            ErrorAccountDisabled => (AuthOutcome.BadCredentials, "The account is disabled."),
            ErrorAccountExpired => (AuthOutcome.BadCredentials, "The account has expired."),
            ErrorAccountLockedOut => (AuthOutcome.BadCredentials, "The account is locked."),
            ErrorLogonTypeNotGranted => (AuthOutcome.NotAllowed,
                "The account may not sign in from the network on this device (\"Access this computer from the network\")."),
            ErrorAuthenticationFirewallFailed => (AuthOutcome.NotAllowed, "The domain's authentication firewall doesn't allow the account on this device."),
            ErrorNoLogonServers => (AuthOutcome.Error, "No domain controller could be reached to check the account."),
            ErrorNoTrustLsaSecret or ErrorNoTrustSamAccount or ErrorTrustedDomainFailure or ErrorTrustedRelationshipFailure =>
                (AuthOutcome.Error, "The device's trust relationship with its domain failed."),
            _ => (AuthOutcome.Error, $"Windows couldn't check the sign-in: {new Win32Exception(error).Message}"),
        };
        return AuthResult.Fail(outcome, $"{reason} (Windows error {error})");
    }

    /// <summary>The HTTP status for a refused sign-in.</summary>
    public static int HttpStatus(AuthOutcome outcome) => outcome switch
    {
        AuthOutcome.BadCredentials => StatusCodes.Status401Unauthorized,
        AuthOutcome.NotAllowed => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status500InternalServerError,
    };
}
