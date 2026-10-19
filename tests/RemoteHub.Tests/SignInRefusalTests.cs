extern alias agent;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// How the agent reads Windows' refusals: <c>LogonUser</c> errors for the network sign-in (outcome, HTTP status, audit
/// reason) and LogonUI's NTSTATUS / substatus for the automatic sign-in (the operator's message).
/// </summary>
public class SignInRefusalTests
{
    [Theory]
    [InlineData(1326, A.AuthOutcome.BadCredentials, 401, "user name or password is wrong")]
    [InlineData(1317, A.AuthOutcome.BadCredentials, 401, "doesn't exist")]
    [InlineData(1355, A.AuthOutcome.BadCredentials, 401, "domain in the user name")]
    [InlineData(1327, A.AuthOutcome.BadCredentials, 401, "blank password")]
    [InlineData(1328, A.AuthOutcome.BadCredentials, 401, "time of day")]
    [InlineData(1329, A.AuthOutcome.BadCredentials, 401, "on this device")]
    [InlineData(1330, A.AuthOutcome.BadCredentials, 401, "password has expired")]
    [InlineData(1907, A.AuthOutcome.BadCredentials, 401, "password must be changed")]
    [InlineData(1331, A.AuthOutcome.BadCredentials, 401, "account is disabled")]
    [InlineData(1793, A.AuthOutcome.BadCredentials, 401, "account has expired")]
    [InlineData(1909, A.AuthOutcome.BadCredentials, 401, "account is locked")]
    [InlineData(1385, A.AuthOutcome.NotAllowed, 403, "Access this computer from the network")]
    [InlineData(1935, A.AuthOutcome.NotAllowed, 403, "authentication firewall")]
    [InlineData(1311, A.AuthOutcome.Error, 500, "No domain controller")]
    [InlineData(1786, A.AuthOutcome.Error, 500, "trust relationship")]
    [InlineData(1787, A.AuthOutcome.Error, 500, "trust relationship")]
    [InlineData(1788, A.AuthOutcome.Error, 500, "trust relationship")]
    [InlineData(1789, A.AuthOutcome.Error, 500, "trust relationship")]
    public void Windows_sign_in_errors_map_to_an_outcome_a_status_and_a_reason(int error, A.AuthOutcome outcome, int http, string reason)
    {
        var result = A.SignInRefusal.FromWin32(error);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(http, A.SignInRefusal.HttpStatus(result.Outcome));
        Assert.Contains(reason, result.Reason);
        Assert.EndsWith($"(Windows error {error})", result.Reason);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void An_unknown_Windows_error_is_an_error_with_its_number()
    {
        var result = A.SignInRefusal.FromWin32(5);
        Assert.Equal(A.AuthOutcome.Error, result.Outcome);
        Assert.Equal(500, A.SignInRefusal.HttpStatus(result.Outcome));
        Assert.StartsWith("Windows couldn't check the sign-in: ", result.Reason);
        Assert.EndsWith("(Windows error 5)", result.Reason);
    }

    [Theory]
    [InlineData(A.AuthOutcome.BadCredentials, 401)]
    [InlineData(A.AuthOutcome.NotAllowed, 403)]
    [InlineData(A.AuthOutcome.Error, 500)]
    public void A_refused_sign_in_gets_its_HTTP_status(A.AuthOutcome outcome, int http) =>
        Assert.Equal(http, A.SignInRefusal.HttpStatus(outcome));

    [Theory]
    // The reason itself as the status.
    [InlineData(0xC0000234, 0u, "The account is locked.")]
    [InlineData(0xC0000072, 0u, "The account is disabled.")]
    [InlineData(0xC0000193, 0u, "The account has expired.")]
    [InlineData(0xC0000071, 0u, "The password has expired and must be changed at the device.")]
    [InlineData(0xC0000224, 0u, "The password has expired and must be changed at the device.")]
    [InlineData(0xC000015B, 0u, "The account may not sign in on this device's screen (\"Allow log on locally\").")]
    [InlineData(0xC000006F, 0u, "The account may not sign in at this time of day.")]
    [InlineData(0xC0000070, 0u, "The account may not sign in on this device.")]
    [InlineData(0xC0000064, 0u, "The account doesn't exist on the device.")]
    [InlineData(0xC000006A, 0u, "The password was wrong (the stored password may be out of date).")]
    [InlineData(0xC00002FA, 0u, "The account must sign in with a smart card.")]
    [InlineData(0xC0000413, 0u, "The sign-in was blocked by the domain's authentication firewall.")]
    [InlineData(0xC000005E, 0u, "No domain controller could be reached to check the account.")]
    [InlineData(0xC000018D, 0u, "The device's trust relationship with its domain failed.")]
    [InlineData(0xC0000133, 0u, "The device's clock differs too much from the domain controller's.")]
    // STATUS_ACCOUNT_RESTRICTION with the reason in the substatus (a disabled account on Windows Server 2025).
    [InlineData(0xC000006E, 0xC0000072, "The account is disabled.")]
    [InlineData(0xC000006E, 0xC0000193, "The account has expired.")]
    [InlineData(0xC000006E, 0xC0000071, "The password has expired and must be changed at the device.")]
    [InlineData(0xC000006E, 0xC0000224, "The password has expired and must be changed at the device.")]
    [InlineData(0xC000006E, 0xC000006F, "The account may not sign in at this time of day.")]
    [InlineData(0xC000006E, 0xC0000070, "The account may not sign in on this device.")]
    [InlineData(0xC000006E, 0xC0000234, "The account is locked.")]
    [InlineData(0xC000006E, 0xC00002FA, "The account must sign in with a smart card.")]
    [InlineData(0xC000006E, 0u, "Account restrictions prevent this sign-in (for example a blank password, or the account needs a smart card or Windows Hello).")]
    [InlineData(0xC000006E, 0xC000006E, "Account restrictions prevent this sign-in (for example a blank password, or the account needs a smart card or Windows Hello).")]
    [InlineData(0xC000006E, 0xC0001234, "Account restrictions prevent this sign-in (for example a blank password, or the account needs a smart card or Windows Hello).")]
    // STATUS_LOGON_FAILURE with the reason in the substatus.
    [InlineData(0xC000006D, 0xC0000064, "The account doesn't exist on the device.")]
    [InlineData(0xC000006D, 0xC000006A, "The password was wrong (the stored password may be out of date).")]
    [InlineData(0xC000006D, 0xC0000072, "The account is disabled.")]
    [InlineData(0xC000006D, 0xC0000234, "The account is locked.")]
    [InlineData(0xC000006D, 0u, "The password was wrong (the stored password may be out of date).")]
    [InlineData(0xC000006D, 0xC0001234, "The password was wrong (the stored password may be out of date).")]
    // Anything else: the status, and a substatus never overrides a status that isn't one of the two general ones.
    [InlineData(0xC0000001, 0u, "Windows refused the sign-in (0xC0000001).")]
    [InlineData(0xC0000234, 0xC0000072, "The account is locked.")]
    [InlineData(0xC0001234, 0xC0000072, "Windows refused the sign-in (0xC0001234).")]
    public void LogonUIs_status_and_substatus_name_the_reason(uint status, uint substatus, string message) =>
        Assert.Equal(message, A.AutoSignInStatus.Describe(unchecked((int)status), unchecked((int)substatus)));
}
