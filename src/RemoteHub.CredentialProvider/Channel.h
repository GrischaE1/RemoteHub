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

        // Asks the thread to end. It still sends a result LogonUI has already reported (within the write's own time
        // limit), then closes the pipe, wipes the ticket and exits. The caller never waits for the thread.
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
        bool SendPendingResult(HANDLE pipe, bool& sent);
        void ClearTicket(bool notify);
        void NotifyChanged();

        struct State;
        std::unique_ptr<State> _s;
    };

    // What woke the pipe thread while it waits for the next message (Channel::Serve).
    enum class Wake { Result, Stop, Message, Failed };

    // Waits for a result to send, the stop or the next message – in that order when several are signalled. The result
    // comes first because after a successful sign-in LogonUI calls ReportResult and releases the provider (Stop) right
    // after it: with the stop first, the thread left without sending the result, and the service never learnt that the
    // account was signed in (lab C-END-10, credential-provider-design.md §8.2).
    inline Wake WaitServe(HANDLE result, HANDLE stop, HANDLE message, DWORD timeoutMs)
    {
        const HANDLE waits[3] = { result, stop, message };
        switch (WaitForMultipleObjects(3, waits, FALSE, timeoutMs))
        {
        case WAIT_OBJECT_0: return Wake::Result;
        case WAIT_OBJECT_0 + 1: return Wake::Stop;
        case WAIT_OBJECT_0 + 2: return Wake::Message;
        default: return Wake::Failed;
        }
    }

    // The module handle of this DLL (set in DllMain); used to find the agent's folder.
    extern HMODULE Module;
}
