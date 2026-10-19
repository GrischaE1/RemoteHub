// The one tile shown while a ticket is pending. It has no editable field: no password field ever holds the value, and
// GetStringValue never returns it (credential-provider-design.md §8.2).
#pragma once

#include <windows.h>
#include <credentialprovider.h>

#include <memory>

namespace rhcp
{
    class Channel;

    class Credential final : public ICredentialProviderCredential2
    {
    public:
        // The id is the provider's number in the trace (Trace.h).
        Credential(std::shared_ptr<Channel> channel, long id);

        // IUnknown
        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
        IFACEMETHODIMP_(ULONG) AddRef() override;
        IFACEMETHODIMP_(ULONG) Release() override;

        // ICredentialProviderCredential
        IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* events) override;
        IFACEMETHODIMP UnAdvise() override;
        IFACEMETHODIMP SetSelected(BOOL* autoLogon) override;
        IFACEMETHODIMP SetDeselected() override;
        IFACEMETHODIMP GetFieldState(DWORD field, CREDENTIAL_PROVIDER_FIELD_STATE* state, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactive) override;
        IFACEMETHODIMP GetStringValue(DWORD field, PWSTR* value) override;
        IFACEMETHODIMP GetBitmapValue(DWORD field, HBITMAP* bitmap) override;
        IFACEMETHODIMP GetCheckboxValue(DWORD field, BOOL* checked, PWSTR* label) override;
        IFACEMETHODIMP GetSubmitButtonValue(DWORD field, DWORD* adjacentTo) override;
        IFACEMETHODIMP GetComboBoxValueCount(DWORD field, DWORD* count, DWORD* selected) override;
        IFACEMETHODIMP GetComboBoxValueAt(DWORD field, DWORD item, PWSTR* value) override;
        IFACEMETHODIMP SetStringValue(DWORD field, PCWSTR value) override;
        IFACEMETHODIMP SetCheckboxValue(DWORD field, BOOL checked) override;
        IFACEMETHODIMP SetComboBoxSelectedValue(DWORD field, DWORD selected) override;
        IFACEMETHODIMP CommandLinkClicked(DWORD field) override;
        IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response,
            CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* serialization, PWSTR* statusText, CREDENTIAL_PROVIDER_STATUS_ICON* statusIcon) override;
        IFACEMETHODIMP ReportResult(NTSTATUS status, NTSTATUS substatus, PWSTR* statusText, CREDENTIAL_PROVIDER_STATUS_ICON* statusIcon) override;

        // ICredentialProviderCredential2
        IFACEMETHODIMP GetUserSid(PWSTR* sid) override;

    private:
        ~Credential();

        volatile long _refs = 1;
        std::shared_ptr<Channel> _channel;
        const long _id;
        bool _failed = false;
    };

    // CoTaskMemAlloc'd copy (LogonUI frees what providers return).
    HRESULT DuplicateString(PCWSTR source, PWSTR* copy);
}
