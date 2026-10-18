#include "Credential.h"

#include "Channel.h"
#include "Logon.h"
#include "Provider.h"
#include "Trace.h"

#include <string>

// Every COM method: nothing may escape into LogonUI (§8.3).
#define RHCP_GUARD_BEGIN try {
#define RHCP_GUARD_END } catch (...) { return E_UNEXPECTED; }

namespace rhcp
{
    namespace
    {
        // From ntstatus.h, which clashes with windows.h's definitions.
        constexpr NTSTATUS StatusSuccess = 0;
        constexpr NTSTATUS StatusUnsuccessful = static_cast<NTSTATUS>(0xC0000001L);
    }

    HRESULT DuplicateString(PCWSTR source, PWSTR* copy)
    {
        if (copy == nullptr) return E_POINTER;
        *copy = nullptr;
        if (source == nullptr) return E_INVALIDARG;
        const size_t bytes = (wcslen(source) + 1) * sizeof(wchar_t);
        *copy = static_cast<PWSTR>(CoTaskMemAlloc(bytes));
        if (*copy == nullptr) return E_OUTOFMEMORY;
        memcpy(*copy, source, bytes);
        return S_OK;
    }

    Credential::Credential(std::shared_ptr<Channel> channel, long id) : _channel(std::move(channel)), _id(id) { AddObject(); }
    Credential::~Credential() { ReleaseObject(); }

    IFACEMETHODIMP Credential::QueryInterface(REFIID riid, void** ppv)
    {
        if (ppv == nullptr) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_ICredentialProviderCredential || riid == IID_ICredentialProviderCredential2)
        {
            *ppv = static_cast<ICredentialProviderCredential2*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) Credential::AddRef() { return InterlockedIncrement(&_refs); }

    IFACEMETHODIMP_(ULONG) Credential::Release()
    {
        const long refs = InterlockedDecrement(&_refs);
        if (refs == 0) delete this;
        return refs;
    }

    IFACEMETHODIMP Credential::Advise(ICredentialProviderCredentialEvents*)
    {
        Trace("provider %ld: tile Advise", _id);
        return S_OK;
    }

    IFACEMETHODIMP Credential::UnAdvise()
    {
        Trace("provider %ld: tile UnAdvise", _id);
        return S_OK;
    }

    IFACEMETHODIMP Credential::SetSelected(BOOL* autoLogon)
    {
        RHCP_GUARD_BEGIN
        if (autoLogon == nullptr) return E_POINTER;
        *autoLogon = _channel && _channel->HasTicket() ? TRUE : FALSE;
        Trace("provider %ld: tile SetSelected, auto sign-in %s", _id, *autoLogon ? "yes" : "no");
        return S_OK;
        RHCP_GUARD_END
    }

    IFACEMETHODIMP Credential::SetDeselected()
    {
        Trace("provider %ld: tile SetDeselected", _id);
        return S_OK;
    }

    IFACEMETHODIMP Credential::GetFieldState(DWORD field, CREDENTIAL_PROVIDER_FIELD_STATE* state, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactive)
    {
        if (state == nullptr || interactive == nullptr) return E_POINTER;
        if (field >= FieldCount) return E_INVALIDARG;
        *state = field == FieldLabel ? CPFS_DISPLAY_IN_BOTH : CPFS_DISPLAY_IN_SELECTED_TILE;
        *interactive = CPFIS_NONE;
        return S_OK;
    }

    IFACEMETHODIMP Credential::GetStringValue(DWORD field, PWSTR* value)
    {
        RHCP_GUARD_BEGIN
        if (value == nullptr) return E_POINTER;
        *value = nullptr;
        if (field == FieldLabel) return DuplicateString(L"RemoteHub", value);
        if (field == FieldStatus)
        {
            // §8.2: on failure no detail here – the reason goes only to the operator.
            if (_failed) return DuplicateString(L"Automatic sign-in failed", value);
            std::wstring account, sid;
            if (_channel && _channel->Account(account, sid))
                return DuplicateString((L"Signing in " + account + L"…").c_str(), value);
            return DuplicateString(L"", value);
        }
        return E_INVALIDARG;
        RHCP_GUARD_END
    }

    IFACEMETHODIMP Credential::GetBitmapValue(DWORD, HBITMAP* bitmap)
    {
        if (bitmap) *bitmap = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP Credential::GetCheckboxValue(DWORD, BOOL*, PWSTR*) { return E_NOTIMPL; }

    IFACEMETHODIMP Credential::GetSubmitButtonValue(DWORD field, DWORD* adjacentTo)
    {
        if (adjacentTo == nullptr) return E_POINTER;
        if (field != FieldSubmit) return E_INVALIDARG;
        *adjacentTo = FieldStatus;
        return S_OK;
    }

    IFACEMETHODIMP Credential::GetComboBoxValueCount(DWORD, DWORD*, DWORD*) { return E_NOTIMPL; }
    IFACEMETHODIMP Credential::GetComboBoxValueAt(DWORD, DWORD, PWSTR*) { return E_NOTIMPL; }
    IFACEMETHODIMP Credential::SetStringValue(DWORD, PCWSTR) { return E_NOTIMPL; }
    IFACEMETHODIMP Credential::SetCheckboxValue(DWORD, BOOL) { return E_NOTIMPL; }
    IFACEMETHODIMP Credential::SetComboBoxSelectedValue(DWORD, DWORD) { return E_NOTIMPL; }
    IFACEMETHODIMP Credential::CommandLinkClicked(DWORD) { return E_NOTIMPL; }

    IFACEMETHODIMP Credential::GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response,
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* serialization, PWSTR* statusText, CREDENTIAL_PROVIDER_STATUS_ICON* statusIcon)
    {
        RHCP_GUARD_BEGIN
        if (response == nullptr || serialization == nullptr) return E_POINTER;
        if (statusText) *statusText = nullptr;
        if (statusIcon) *statusIcon = CPSI_NONE;
        *response = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
        ZeroMemory(serialization, sizeof(*serialization));

        // Only the ticket already received; nothing here waits for the pipe (§8.2).
        if (!_channel || !_channel->HasTicket())
        {
            Trace("provider %ld: GetSerialization without a ticket", _id);
            return S_OK;
        }

        ULONG package = 0;
        HRESULT hr = NegotiatePackage(&package);
        if (FAILED(hr))
        {
            Trace("provider %ld: GetSerialization, no Negotiate package: 0x%08lX", _id, static_cast<unsigned long>(hr));
            return hr;
        }
        BYTE* buffer = nullptr;
        ULONG bytes = 0;
        hr = _channel->TakeSerialization(&buffer, &bytes);
        if (FAILED(hr))
        {
            Trace("provider %ld: GetSerialization, packing failed: 0x%08lX", _id, static_cast<unsigned long>(hr));
            _failed = true;
            _channel->ReportResult(StatusUnsuccessful, 0); // tell the service; the ticket is used up anyway
            return hr;
        }
        Trace("provider %ld: GetSerialization, ticket handed to LogonUI", _id);
        _failed = false;
        serialization->ulAuthenticationPackage = package;
        serialization->clsidCredentialProvider = ProviderClsid;
        serialization->cbSerialization = bytes;
        serialization->rgbSerialization = buffer; // LogonUI's now (API contract); our copies are wiped
        *response = CPGSR_RETURN_CREDENTIAL_FINISHED;
        return S_OK;
        RHCP_GUARD_END
    }

    IFACEMETHODIMP Credential::ReportResult(NTSTATUS status, NTSTATUS substatus, PWSTR* statusText, CREDENTIAL_PROVIDER_STATUS_ICON* statusIcon)
    {
        RHCP_GUARD_BEGIN
        if (statusText) *statusText = nullptr;
        if (statusIcon) *statusIcon = CPSI_NONE;
        // Never retries (lockout): the result goes to the service and the ticket is gone (§8.2).
        const bool queued = _channel && _channel->ReportResult(status, substatus);
        Trace("provider %ld: ReportResult 0x%08lX / 0x%08lX - %s", _id, static_cast<unsigned long>(status),
            static_cast<unsigned long>(substatus), queued ? "queued for the agent" : "dropped, no ticket was taken");
        if (status != StatusSuccess)
        {
            _failed = true;
            if (statusText) DuplicateString(L"Automatic sign-in failed", statusText);
            if (statusIcon) *statusIcon = CPSI_ERROR;
        }
        return S_OK;
        RHCP_GUARD_END
    }

    IFACEMETHODIMP Credential::GetUserSid(PWSTR* sid)
    {
        RHCP_GUARD_BEGIN
        if (sid == nullptr) return E_POINTER;
        *sid = nullptr;
        std::wstring account, value;
        const bool given = _channel && _channel->Account(account, value) && !value.empty();
        Trace("provider %ld: tile GetUserSid -> %s", _id, given ? "the ticket's SID" : "none");
        if (given) return DuplicateString(value.c_str(), sid);
        return S_FALSE; // no SID: the tile goes under "Other user"
        RHCP_GUARD_END
    }
}
