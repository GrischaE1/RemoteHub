// The credential provider LogonUI loads (credential-provider-design.md §8.2). Inert unless a ticket is pending: no
// tile, no fields, no effect on the other providers.
#pragma once

#include <windows.h>
#include <credentialprovider.h>

#include <memory>

namespace rhcp
{
    class Channel;
    class Credential;

    // {718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3} – fixed for good, so IT can exclude it (ExcludedCredentialProviders).
    extern const CLSID ProviderClsid;

    // Objects alive in this DLL (DllCanUnloadNow).
    void AddObject();
    void ReleaseObject();
    long LiveObjects();

    // The fields of the one tile.
    enum Field : DWORD
    {
        FieldLabel = 0,     // "RemoteHub"
        FieldStatus = 1,    // "Signing in DOMAIN\user…" / "Automatic sign-in failed"
        FieldSubmit = 2,
        FieldCount = 3,
    };

    class Provider final : public ICredentialProvider, public ICredentialProviderSetUserArray
    {
    public:
        static HRESULT Create(REFIID riid, void** ppv);

        // IUnknown
        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
        IFACEMETHODIMP_(ULONG) AddRef() override;
        IFACEMETHODIMP_(ULONG) Release() override;

        // ICredentialProvider
        IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD flags) override;
        IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* serialization) override;
        IFACEMETHODIMP Advise(ICredentialProviderEvents* events, UINT_PTR context) override;
        IFACEMETHODIMP UnAdvise() override;
        IFACEMETHODIMP GetFieldDescriptorCount(DWORD* count) override;
        IFACEMETHODIMP GetFieldDescriptorAt(DWORD index, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** descriptor) override;
        IFACEMETHODIMP GetCredentialCount(DWORD* count, DWORD* defaultIndex, BOOL* autoLogonWithDefault) override;
        IFACEMETHODIMP GetCredentialAt(DWORD index, ICredentialProviderCredential** credential) override;

        // ICredentialProviderSetUserArray: not needed for the tile, but LogonUI's V2 UI expects providers to take it.
        IFACEMETHODIMP SetUserArray(ICredentialProviderUserArray* users) override;

    private:
        Provider();
        ~Provider();

        volatile long _refs = 1;
        std::shared_ptr<Channel> _channel;   // null unless CPUS_LOGON in the console session
        Credential* _credential = nullptr;
    };
}
