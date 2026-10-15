// Packs the credentials LogonUI hands to Winlogon/LSA (credential-provider-design.md §8.2).
#pragma once

#include <windows.h>

namespace rhcp
{
    // A KERB_INTERACTIVE_UNLOCK_LOGON for an interactive sign-in (MessageType KerbInteractiveLogon), in the
    // self-relative form LogonUI expects: the strings follow the structure, and each UNICODE_STRING.Buffer holds the
    // offset from the start of the buffer instead of a pointer.
    // The buffer is allocated with CoTaskMemAlloc; it belongs to LogonUI once returned from GetSerialization. Lengths
    // are in characters, without a terminating null.
    HRESULT PackInteractiveLogon(
        const wchar_t* domain, size_t domainChars,
        const wchar_t* user, size_t userChars,
        const wchar_t* password, size_t passwordChars,
        BYTE** serialization, ULONG* serializationBytes);

    // The Negotiate package's id for CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION.ulAuthenticationPackage.
    HRESULT NegotiatePackage(ULONG* package);
}
