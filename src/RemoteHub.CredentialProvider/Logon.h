// Packs the credentials LogonUI hands to Winlogon/LSA (credential-provider-design.md §8.2).
#pragma once

#include <windows.h>
#include <credentialprovider.h>

namespace rhcp
{
    // The scenarios the provider takes: the sign-in screen, and the lock screen of the account's own session (§8.2,
    // §12.1 – the service hands a ticket there only for the session's own user). Everything else – CredUI, changing a
    // password, PLAP – is refused, so the provider never runs in a user's process through CredUI.
    inline bool TakesScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus) { return cpus == CPUS_LOGON || cpus == CPUS_UNLOCK_WORKSTATION; }

    // A KERB_INTERACTIVE_UNLOCK_LOGON – MessageType KerbInteractiveLogon for a sign-in, KerbWorkstationUnlockLogon for an
    // unlock – in the self-relative form LogonUI expects: the strings follow the structure, and each UNICODE_STRING.Buffer
    // holds the offset from the start of the buffer instead of a pointer.
    // The buffer is allocated with CoTaskMemAlloc; it belongs to LogonUI once returned from GetSerialization. Lengths
    // are in characters, without a terminating null.
    HRESULT PackInteractiveLogon(
        const wchar_t* domain, size_t domainChars,
        const wchar_t* user, size_t userChars,
        const wchar_t* password, size_t passwordChars,
        bool unlock,
        BYTE** serialization, ULONG* serializationBytes);

    // The Negotiate package's id for CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION.ulAuthenticationPackage.
    HRESULT NegotiatePackage(ULONG* package);
}
