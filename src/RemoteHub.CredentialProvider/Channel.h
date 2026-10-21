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
        // Starts the background thread. Null if it couldn't be started (the provider then stays inert). The id is the
        // provider's number in the trace (Trace.h).
        static std::shared_ptr<Channel> Start(long id);

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

        // LogonUI's verdict on the last ticket taken; the thread sends it to the service. False when no taken ticket
        // waits for one: the result is then dropped.
        bool ReportResult(NTSTATUS status, NTSTATUS substatus);

        // Whether LogonUI's user list lacks the ticket's account (UserList.h), from each enumeration. When it does and
        // LogonUI hasn't taken the ticket within a short grace, the thread drops the ticket and tells the service
        // (NotOffered) instead of waiting for a result that won't come. Never waits; false disarms it.
        void NotListed(bool notListed);

        // Number of channel threads still running in the process (DllCanUnloadNow).
        static long RunningThreads();

    private:
        explicit Channel(long id);
        static DWORD WINAPI ThreadMain(void* parameter);
        void Run();
        void Serve(HANDLE pipe);
        bool SendPendingResult(HANDLE pipe, bool& sent);
        DWORD NotOfferedWaitMs();   // the grace left for an unlisted account's ticket; INFINITE when none runs
        bool ReportNotOffered();    // the grace is over and the ticket untaken: used up and wiped, NotOffered queued
        void ClearTicket(bool notify);
        void NotifyChanged();

        struct State;
        std::unique_ptr<State> _s;
        const long _id;
    };

    // What woke the pipe thread while it waits for the next message (Channel::Serve).
    enum class Wake { Result, Stop, Message, Timeout, Failed };

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
        case WAIT_TIMEOUT: return Wake::Timeout;
        default: return Wake::Failed;
        }
    }

    // Whether LogonUI is told to enumerate again (CredentialsChanged) once a used ticket is cleared after its result was
    // sent: after a failed sign-in, or a ticket LogonUI didn't take, so the tile goes instead of staying selected with
    // "Signing in ..." (lab C-END-13). Not after a success: LogonUI releases the provider then.
    inline bool TileGoesAfterResult(bool notOffered, NTSTATUS status) { return notOffered || status != 0; }

    // The module handle of this DLL (set in DllMain); used to find the agent's folder.
    extern HMODULE Module;
}
