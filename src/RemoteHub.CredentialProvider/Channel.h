// The provider's side of the pipe to the agent service (credential-provider-design.md §7, §8.2).
//
// One background thread per provider instance does all pipe I/O, so no LogonUI call ever waits for the pipe. The
// thread connects only while this LogonUI runs in the console session, checks that the server is the agent service,
// and claims a pending ticket at once: the tile appears (CredentialsChanged) only when the ticket is already here.
#pragma once

#include <windows.h>
#include <credentialprovider.h>

#include <memory>
#include <string>

namespace rhcp
{
    class Channel
    {
    public:
        // Starts the background thread. Null if it couldn't be started (the provider then stays inert).
        static std::shared_ptr<Channel> Start();

        Channel(const Channel&) = delete;
        Channel& operator=(const Channel&) = delete;
        ~Channel();

        // Asks the thread to end. It closes the pipe, wipes the ticket and exits without waiting for anything.
        void Stop();

        // Who CredentialsChanged goes to; null clears it (UnAdvise).
        void SetEvents(ICredentialProviderEvents* events, UINT_PTR context);

        // A ticket is here and not used yet.
        bool HasTicket();

        // The account of the ticket, for the tile. False when there is none.
        bool Account(std::wstring& qualifiedName, std::wstring& sid);

        // Takes the ticket (single use) and packs it for LogonUI. Fails without a ticket.
        HRESULT TakeSerialization(BYTE** serialization, ULONG* serializationBytes);

        // LogonUI's verdict on the last ticket taken; the thread sends it to the service.
        void ReportResult(NTSTATUS status, NTSTATUS substatus);

        // Number of channel threads still running in the process (DllCanUnloadNow).
        static long RunningThreads();

    private:
        Channel();
        static DWORD WINAPI ThreadMain(void* parameter);
        void Run();
        void Serve(HANDLE pipe);
        void ClearTicket(bool notify);
        void NotifyChanged();

        struct State;
        std::unique_ptr<State> _s;
    };

    // The module handle of this DLL (set in DllMain); used to find the agent's folder.
    extern HMODULE Module;
}
