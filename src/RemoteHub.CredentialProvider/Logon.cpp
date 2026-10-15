#include "Logon.h"

#include <ntsecapi.h>
#include <combaseapi.h>
#include <cstring>

#define SECURITY_WIN32
#include <security.h>

namespace rhcp
{
    namespace
    {
        constexpr size_t MaxChars = 0x7FFF; // a UNICODE_STRING's length is a USHORT in bytes

        // Copies the string behind the structure and stores its offset in Buffer.
        void Place(UNICODE_STRING& target, BYTE* base, size_t& offset, const wchar_t* text, size_t chars)
        {
            const auto bytes = static_cast<USHORT>(chars * sizeof(wchar_t));
            target.Length = bytes;
            target.MaximumLength = bytes;
            if (bytes > 0) memcpy(base + offset, text, bytes);
            target.Buffer = reinterpret_cast<PWSTR>(static_cast<ULONG_PTR>(offset));
            offset += bytes;
        }
    }

    HRESULT PackInteractiveLogon(
        const wchar_t* domain, size_t domainChars,
        const wchar_t* user, size_t userChars,
        const wchar_t* password, size_t passwordChars,
        BYTE** serialization, ULONG* serializationBytes)
    {
        if (serialization == nullptr || serializationBytes == nullptr) return E_POINTER;
        *serialization = nullptr;
        *serializationBytes = 0;
        if ((domainChars > 0 && domain == nullptr) || user == nullptr || password == nullptr) return E_INVALIDARG;
        if (userChars == 0 || domainChars > MaxChars || userChars > MaxChars || passwordChars > MaxChars) return E_INVALIDARG;

        const size_t total = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON) + (domainChars + userChars + passwordChars) * sizeof(wchar_t);
        auto* buffer = static_cast<BYTE*>(CoTaskMemAlloc(total));
        if (buffer == nullptr) return E_OUTOFMEMORY;
        memset(buffer, 0, total);

        auto* logon = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(buffer);
        logon->Logon.MessageType = KerbInteractiveLogon;
        size_t offset = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON);
        Place(logon->Logon.LogonDomainName, buffer, offset, domain, domainChars);
        Place(logon->Logon.UserName, buffer, offset, user, userChars);
        Place(logon->Logon.Password, buffer, offset, password, passwordChars);

        *serialization = buffer;
        *serializationBytes = static_cast<ULONG>(total);
        return S_OK;
    }

    HRESULT NegotiatePackage(ULONG* package)
    {
        if (package == nullptr) return E_POINTER;
        HANDLE lsa;
        NTSTATUS status = LsaConnectUntrusted(&lsa);
        if (status != 0) return HRESULT_FROM_NT(status);

        LSA_STRING name;
        name.Buffer = const_cast<PCHAR>(NEGOSSP_NAME_A);
        name.Length = static_cast<USHORT>(strlen(NEGOSSP_NAME_A));
        name.MaximumLength = name.Length;
        ULONG id = 0;
        status = LsaLookupAuthenticationPackage(lsa, &name, &id);
        LsaDeregisterLogonProcess(lsa);
        if (status != 0) return HRESULT_FROM_NT(status);
        *package = id;
        return S_OK;
    }
}
