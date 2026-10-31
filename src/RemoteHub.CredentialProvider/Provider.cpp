#include "Provider.h"

#include "Channel.h"
#include "Credential.h"
#include "Logon.h"
#include "Trace.h"
#include "UserList.h"

#define RHCP_GUARD_BEGIN try {
#define RHCP_GUARD_END } catch (...) { return E_UNEXPECTED; }

namespace rhcp
{
    // {718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}
    const CLSID ProviderClsid = { 0x718fffd2, 0x6671, 0x4fa4, { 0x9f, 0xa8, 0x86, 0xd1, 0xcd, 0x4d, 0x84, 0xc3 } };

    namespace
    {
        volatile long g_objects = 0;
        volatile long g_providerIds = 0;

        struct FieldInfo
        {
            CREDENTIAL_PROVIDER_FIELD_TYPE Type;
            const wchar_t* Label;
        };

        const FieldInfo Fields[FieldCount] = {
            { CPFT_LARGE_TEXT, L"RemoteHub" },
            { CPFT_SMALL_TEXT, L"Status" },
            { CPFT_SUBMIT_BUTTON, L"Sign in" },
        };
    }

    void AddObject() { InterlockedIncrement(&g_objects); }
    void ReleaseObject() { InterlockedDecrement(&g_objects); }
    long LiveObjects() { return g_objects; }

    HRESULT Provider::Create(REFIID riid, void** ppv)
    {
        RHCP_GUARD_BEGIN
        auto* provider = new (std::nothrow) Provider();
        if (provider == nullptr) return E_OUTOFMEMORY;
        const HRESULT hr = provider->QueryInterface(riid, ppv);
        provider->Release();
        return hr;
        RHCP_GUARD_END
    }

    Provider::Provider() : _id(InterlockedIncrement(&g_providerIds))
    {
        AddObject();
        Trace("provider %ld: created", _id);
    }

    Provider::~Provider()
    {
        Trace("provider %ld: released", _id);
        if (_channel) _channel->Stop();
        if (_credential) _credential->Release();
        if (_users) _users->Release();
        ReleaseObject();
    }

    IFACEMETHODIMP Provider::QueryInterface(REFIID riid, void** ppv)
    {
        if (ppv == nullptr) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_ICredentialProvider)
            *ppv = static_cast<ICredentialProvider*>(this);
        else if (riid == IID_ICredentialProviderSetUserArray)
            *ppv = static_cast<ICredentialProviderSetUserArray*>(this);
        else
        {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    IFACEMETHODIMP_(ULONG) Provider::AddRef() { return InterlockedIncrement(&_refs); }

    IFACEMETHODIMP_(ULONG) Provider::Release()
    {
        const long refs = InterlockedDecrement(&_refs);
        if (refs == 0) delete this;
        return refs;
    }

    IFACEMETHODIMP Provider::SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD flags)
    {
        RHCP_GUARD_BEGIN
        Trace("provider %ld: SetUsageScenario %d, flags 0x%08lX%s", _id, static_cast<int>(cpus), flags,
            !TakesScenario(cpus) ? " - not taken" : _channel ? " - again" : "");
        // §8.2, §12.1: the sign-in screen and the lock screen only (Logon.h). Everything else is refused, so the provider
        // never runs in a user's process through CredUI.
        if (!TakesScenario(cpus)) return E_NOTIMPL;
        if (!_channel)
        {
            _channel = Channel::Start(_id); // null: stays inert, LogonUI and the other providers work as before
            if (_channel)
            {
                _credential = new (std::nothrow) Credential(_channel, _id);
                if (_credential == nullptr)
                {
                    _channel->Stop();
                    _channel.reset();
                }
            }
        }
        if (_credential) _credential->SetUnlock(cpus == CPUS_UNLOCK_WORKSTATION);
        return S_OK;
        RHCP_GUARD_END
    }

    IFACEMETHODIMP Provider::SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) { return E_NOTIMPL; }

    IFACEMETHODIMP Provider::Advise(ICredentialProviderEvents* events, UINT_PTR context)
    {
        RHCP_GUARD_BEGIN
        Trace("provider %ld: Advise", _id);
        if (_channel) _channel->SetEvents(events, context);
        return S_OK;
        RHCP_GUARD_END
    }

    IFACEMETHODIMP Provider::UnAdvise()
    {
        RHCP_GUARD_BEGIN
        Trace("provider %ld: UnAdvise", _id);
        if (_channel) _channel->SetEvents(nullptr, 0);
        return S_OK;
        RHCP_GUARD_END
    }

    IFACEMETHODIMP Provider::GetFieldDescriptorCount(DWORD* count)
    {
        if (count == nullptr) return E_POINTER;
        *count = FieldCount;
        return S_OK;
    }

    IFACEMETHODIMP Provider::GetFieldDescriptorAt(DWORD index, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** descriptor)
    {
        RHCP_GUARD_BEGIN
        if (descriptor == nullptr) return E_POINTER;
        *descriptor = nullptr;
        if (index >= FieldCount) return E_INVALIDARG;
        auto* d = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
        if (d == nullptr) return E_OUTOFMEMORY;
        ZeroMemory(d, sizeof(*d));
        d->dwFieldID = index;
        d->cpft = Fields[index].Type;
        const HRESULT hr = DuplicateString(Fields[index].Label, &d->pszLabel);
        if (FAILED(hr))
        {
            CoTaskMemFree(d);
            return hr;
        }
        *descriptor = d;
        return S_OK;
        RHCP_GUARD_END
    }

    IFACEMETHODIMP Provider::GetCredentialCount(DWORD* count, DWORD* defaultIndex, BOOL* autoLogonWithDefault)
    {
        RHCP_GUARD_BEGIN
        if (count == nullptr || defaultIndex == nullptr || autoLogonWithDefault == nullptr) return E_POINTER;
        // §8.2: with no ticket pending there is nothing – no tile, nothing a person at the device could use.
        UserListed listed = UserListed::NoList;
        if (_channel && _credential && _channel->HasTicket())
        {
            *count = 1;
            *defaultIndex = 0;
            *autoLogonWithDefault = TRUE;
            // The tile is offered all the same (Server takes it even for an account it doesn't list, C-PROV-03); for an
            // unlisted account the pipe thread only starts the grace after which it reports NotOffered (UserList.h).
            // Decided on every enumeration, with the user list LogonUI handed over last.
            listed = CheckTicketUser();
            _channel->NotListed(!SignInScreenTakesTicket(listed));
        }
        else
        {
            *count = 0;
            *defaultIndex = CREDENTIAL_PROVIDER_NO_DEFAULT;
            *autoLogonWithDefault = FALSE;
        }
        if (_channel) Trace("provider %ld: GetCredentialCount -> %lu%s%s", _id, *count,
            *count != 0 ? ", account in LogonUI's user list: " : "", *count != 0 ? ToText(listed) : "");
        return S_OK;
        RHCP_GUARD_END
    }

    IFACEMETHODIMP Provider::GetCredentialAt(DWORD index, ICredentialProviderCredential** credential)
    {
        RHCP_GUARD_BEGIN
        if (credential == nullptr) return E_POINTER;
        *credential = nullptr;
        if (index != 0 || _credential == nullptr) return E_INVALIDARG;
        const HRESULT hr = _credential->QueryInterface(IID_ICredentialProviderCredential, reinterpret_cast<void**>(credential));
        Trace("provider %ld: GetCredentialAt %lu -> 0x%08lX", _id, index, static_cast<unsigned long>(hr));
        return hr;
        RHCP_GUARD_END
    }

    IFACEMETHODIMP Provider::SetUserArray(ICredentialProviderUserArray* users)
    {
        RHCP_GUARD_BEGIN
        if (users) users->AddRef();
        if (_users) _users->Release();
        _users = users;
        DWORD count = 0;
        if (users == nullptr || FAILED(users->GetCount(&count))) count = 0;
        Trace("provider %ld: SetUserArray, %lu users", _id, count);
        return S_OK;
        RHCP_GUARD_END
    }

    UserListed Provider::CheckTicketUser()
    {
        UserListed listed = UserListed::NoList;
        std::wstring account, sid;
        DWORD count = 0;
        if (_channel && _channel->Account(account, sid) && _users != nullptr && SUCCEEDED(_users->GetCount(&count)))
        {
            std::vector<std::wstring> sids;
            for (DWORD i = 0; i < count; i++)
            {
                ICredentialProviderUser* user = nullptr;
                if (FAILED(_users->GetAt(i, &user)) || user == nullptr) continue;
                PWSTR userSid = nullptr;
                if (SUCCEEDED(user->GetSid(&userSid)) && userSid != nullptr) sids.emplace_back(userSid);
                CoTaskMemFree(userSid);
                user->Release();
            }
            listed = FindUser(sid, &sids);
        }
        return listed;
    }
}
