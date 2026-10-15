// DLL entry points and the class factory. DllMain does nothing beyond DisableThreadLibraryCalls (§8.1): no work under
// the loader lock.
#include "Channel.h"
#include "Provider.h"

#include <new>

namespace
{
    volatile long g_locks = 0;
    volatile long g_factories = 0; // a caller holding a factory keeps the DLL loaded, also before any provider exists

    class ClassFactory final : public IClassFactory
    {
    public:
        ClassFactory() { InterlockedIncrement(&g_factories); }
        ~ClassFactory() { InterlockedDecrement(&g_factories); }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (ppv == nullptr) return E_POINTER;
            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *ppv = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const long refs = InterlockedDecrement(&_refs);
            if (refs == 0) delete this;
            return refs;
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
        {
            if (ppv == nullptr) return E_POINTER;
            *ppv = nullptr;
            if (outer != nullptr) return CLASS_E_NOAGGREGATION;
            return rhcp::Provider::Create(riid, ppv);
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock) InterlockedIncrement(&g_locks); else InterlockedDecrement(&g_locks);
            return S_OK;
        }

    private:
        volatile long _refs = 1;
    };
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        rhcp::Module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

_Check_return_ STDAPI DllCanUnloadNow()
{
    // Also the channel threads: they run code in this DLL until they have ended.
    return g_locks == 0 && g_factories == 0 && rhcp::LiveObjects() == 0 && rhcp::Channel::RunningThreads() == 0 ? S_OK : S_FALSE;
}

_Check_return_ STDAPI DllGetClassObject(_In_ REFCLSID clsid, _In_ REFIID riid, _Outptr_ LPVOID* ppv)
{
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;
    if (clsid != rhcp::ProviderClsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) ClassFactory();
    if (factory == nullptr) return E_OUTOFMEMORY;
    const HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}
