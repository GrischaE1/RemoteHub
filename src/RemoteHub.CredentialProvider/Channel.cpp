#include "Channel.h"

#include "Logon.h"
#include "Trace.h"
#include "Wire.h"

#include <dpapi.h>

namespace rhcp
{
    HMODULE Module = nullptr;

    namespace
    {
        constexpr wchar_t PipeName[] = L"\\\\.\\pipe\\RemoteHubAgent.AutoSignIn";
        constexpr wchar_t ServiceName[] = L"RemoteHubAgent";
#ifdef RHCP_LAB_SERVER
        // Lab configuration only (never in the MSI): the phase 1 test server stands in for the agent service.
        constexpr wchar_t ServerImage[] = L"RemoteHubCpTestServer.exe";
#else
        constexpr wchar_t ServerImage[] = L"RemoteHubAgent.exe";
#endif
        constexpr DWORD RetryMs = 2000;
        constexpr DWORD TicketTimeoutMs = 5000;
        constexpr DWORD WriteTimeoutMs = 5000;
        // How long LogonUI gets to take a ticket for an account its user list lacks before the service is told it won't
        // (NotListed). Windows 11 never takes one for a locked or disabled account; a ticket it uses it takes within
        // milliseconds of the enumeration (lab C-AUTO-20 trace). Server may take one even for an account it doesn't list
        // (C-PROV-03), possibly only once the lock-screen curtain is gone – hence a margin well above that, still far
        // below the service's 50 s wait for a result.
        constexpr ULONGLONG NotOfferedGraceMs = 10000;

        volatile long g_threads = 0;

        struct Handle
        {
            HANDLE Value = nullptr;
            Handle() = default;
            explicit Handle(HANDLE h) : Value(h == INVALID_HANDLE_VALUE ? nullptr : h) {}
            Handle(const Handle&) = delete;
            Handle& operator=(const Handle&) = delete;
            ~Handle() { if (Value) CloseHandle(Value); }
            explicit operator bool() const { return Value != nullptr; }
        };

        struct ServiceHandle
        {
            SC_HANDLE Value;
            explicit ServiceHandle(SC_HANDLE h) : Value(h) {}
            ServiceHandle(const ServiceHandle&) = delete;
            ServiceHandle& operator=(const ServiceHandle&) = delete;
            ~ServiceHandle() { if (Value) CloseServiceHandle(Value); }
        };

        DWORD OwnSession()
        {
            DWORD session = 0;
            return ProcessIdToSessionId(GetCurrentProcessId(), &session) ? session : MAXDWORD;
        }

        bool InConsoleSession() { return OwnSession() == WTSGetActiveConsoleSessionId(); }

        bool SameText(const wchar_t* a, const wchar_t* b)
        {
            return CompareStringOrdinal(a, -1, b, -1, TRUE) == CSTR_EQUAL;
        }

        // The agent's exe lives next to this DLL (both in Program Files\RemoteHub Agent).
        bool ExpectedServerPath(wchar_t (&path)[MAX_PATH])
        {
            const DWORD n = GetModuleFileNameW(Module, path, MAX_PATH);
            if (n == 0 || n >= MAX_PATH) return false;
            wchar_t* slash = wcsrchr(path, L'\\');
            if (slash == nullptr) return false;
            *(slash + 1) = L'\0';
            return wcscat_s(path, ServerImage) == 0;
        }

        bool IsSystem(HANDLE process)
        {
            HANDLE raw;
            if (!OpenProcessToken(process, TOKEN_QUERY, &raw)) return false;
            Handle token(raw);
            BYTE buffer[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE];
            DWORD size;
            if (!GetTokenInformation(token.Value, TokenUser, buffer, sizeof(buffer), &size)) return false;
            return IsWellKnownSid(reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid, WinLocalSystemSid) != FALSE;
        }

#ifndef RHCP_LAB_SERVER
        // The PID the SCM reports for the agent service.
        bool ServicePid(DWORD& pid)
        {
            ServiceHandle scm(OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
            if (!scm.Value) return false;
            ServiceHandle service(OpenServiceW(scm.Value, ServiceName, SERVICE_QUERY_STATUS));
            if (!service.Value) return false;
            SERVICE_STATUS_PROCESS status;
            DWORD size;
            if (!QueryServiceStatusEx(service.Value, SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &size))
                return false;
            if (status.dwCurrentState != SERVICE_RUNNING) return false;
            pid = status.dwProcessId;
            return pid != 0;
        }
#endif

        // §7.2: the server is the agent service – SYSTEM, the installed exe, the PID the SCM reports.
        bool VerifyServer(HANDLE pipe)
        {
            ULONG pid = 0;
            if (!GetNamedPipeServerProcessId(pipe, &pid) || pid == 0) return false;
#ifndef RHCP_LAB_SERVER
            DWORD servicePid;
            if (!ServicePid(servicePid) || servicePid != pid) return false;
#endif
            Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
            if (!process) return false;
            wchar_t image[MAX_PATH];
            DWORD chars = MAX_PATH;
            if (!QueryFullProcessImageNameW(process.Value, 0, image, &chars)) return false;
            wchar_t expected[MAX_PATH];
            return ExpectedServerPath(expected) && SameText(image, expected) && IsSystem(process.Value);
        }

        // Waits for an overlapped operation; false on stop, timeout or failure (the operation is then cancelled). A null
        // stop means only the time limit ends the wait.
        bool Await(HANDLE pipe, OVERLAPPED& ov, HANDLE stop, DWORD timeoutMs, DWORD& transferred)
        {
            const HANDLE waits[2] = { stop, ov.hEvent };
            const DWORD r = stop != nullptr ? WaitForMultipleObjects(2, waits, FALSE, timeoutMs)
                : WaitForSingleObject(ov.hEvent, timeoutMs) == WAIT_OBJECT_0 ? WAIT_OBJECT_0 + 1 : WAIT_TIMEOUT;
            if (r == WAIT_OBJECT_0 + 1 && GetOverlappedResult(pipe, &ov, &transferred, FALSE)) return true;
            CancelIoEx(pipe, &ov);
            GetOverlappedResult(pipe, &ov, &transferred, TRUE);
            return false;
        }

        bool WriteMessage(HANDLE pipe, HANDLE stop, const uint8_t* data, size_t size)
        {
            OVERLAPPED ov = {};
            Handle done(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!done) return false;
            ov.hEvent = done.Value;
            DWORD written = 0;
            if (!WriteFile(pipe, data, static_cast<DWORD>(size), nullptr, &ov) && GetLastError() != ERROR_IO_PENDING) return false;
            return Await(pipe, ov, stop, WriteTimeoutMs, written) && written == size;
        }
    }

    // Everything the LogonUI threads and the pipe thread share; guarded by Lock.
    struct Channel::State
    {
        SRWLOCK Lock = SRWLOCK_INIT;
        Handle StopEvent{ CreateEventW(nullptr, TRUE, FALSE, nullptr) };
        Handle ResultEvent{ CreateEventW(nullptr, FALSE, FALSE, nullptr) };

        ICredentialProviderEvents* Events = nullptr;
        UINT_PTR EventsContext = 0;

        // The ticket: names in the clear (they're shown on the tile), the password protected in its own pages.
        bool Ticket = false;
        bool Taken = false;
        uint8_t Nonce[NonceBytes] = {};
        std::wstring Domain, User, Sid;
        wchar_t* Password = nullptr;   // VirtualAlloc'd, CryptProtectMemory(SAME_PROCESS)
        size_t PasswordChars = 0;
        size_t PasswordBytes = 0;      // a multiple of CRYPTPROTECTMEMORY_BLOCK_SIZE

        bool ResultPending = false;
        bool NotOfferedPending = false; // instead of a result: LogonUI won't take the ticket (Result.Nonce holds it)
        ULONGLONG NotListedAt = 0;      // when LogonUI's list was first seen without the account; 0: listed or no list
        ResultMessage Result;

        void FreePassword()
        {
            if (Password != nullptr)
            {
                SecureZeroMemory(Password, PasswordBytes);
                VirtualFree(Password, 0, MEM_RELEASE);
            }
            Password = nullptr;
            PasswordChars = PasswordBytes = 0;
        }
    };

    Channel::Channel(long id) : _s(std::make_unique<State>()), _id(id) {}

    Channel::~Channel()
    {
        AcquireSRWLockExclusive(&_s->Lock);
        _s->FreePassword();
        if (_s->Events) _s->Events->Release();
        _s->Events = nullptr;
        ReleaseSRWLockExclusive(&_s->Lock);
    }

    std::shared_ptr<Channel> Channel::Start(long id)
    {
        std::shared_ptr<Channel> channel(new Channel(id));
        if (!channel->_s->StopEvent || !channel->_s->ResultEvent) return nullptr;
        // The thread owns a reference, so the channel outlives the provider until the thread has ended.
        auto* self = new std::shared_ptr<Channel>(channel);
        InterlockedIncrement(&g_threads);
        HANDLE thread = CreateThread(nullptr, 0, ThreadMain, self, 0, nullptr);
        if (thread == nullptr)
        {
            InterlockedDecrement(&g_threads);
            delete self;
            return nullptr;
        }
        CloseHandle(thread);
        return channel;
    }

    long Channel::RunningThreads() { return g_threads; }

    DWORD WINAPI Channel::ThreadMain(void* parameter)
    {
        auto* self = static_cast<std::shared_ptr<Channel>*>(parameter);
        try { (*self)->Run(); }
        catch (...) { /* never let anything out of the thread – LogonUI must keep working */ }
        delete self;
        InterlockedDecrement(&g_threads);
        return 0;
    }

    void Channel::Stop()
    {
        SetEvent(_s->StopEvent.Value);
        SetEvents(nullptr, 0);
    }

    void Channel::SetEvents(ICredentialProviderEvents* events, UINT_PTR context)
    {
        if (events) events->AddRef();
        AcquireSRWLockExclusive(&_s->Lock);
        ICredentialProviderEvents* old = _s->Events;
        _s->Events = events;
        _s->EventsContext = context;
        ReleaseSRWLockExclusive(&_s->Lock);
        if (old) old->Release();
    }

    void Channel::NotifyChanged()
    {
        AcquireSRWLockShared(&_s->Lock);
        ICredentialProviderEvents* events = _s->Events;
        const UINT_PTR context = _s->EventsContext;
        if (events) events->AddRef();
        ReleaseSRWLockShared(&_s->Lock);
        if (events)
        {
            const HRESULT hr = events->CredentialsChanged(context);
            events->Release();
            Trace("provider %ld: CredentialsChanged -> 0x%08lX", _id, static_cast<unsigned long>(hr));
        }
        else Trace("provider %ld: CredentialsChanged not sent, LogonUI isn't advised", _id);
    }

    bool Channel::HasTicket()
    {
        AcquireSRWLockShared(&_s->Lock);
        const bool has = _s->Ticket && !_s->Taken;
        ReleaseSRWLockShared(&_s->Lock);
        return has;
    }

    bool Channel::Account(std::wstring& qualifiedName, std::wstring& sid)
    {
        AcquireSRWLockShared(&_s->Lock);
        const bool has = _s->Ticket;
        if (has)
        {
            qualifiedName = _s->Domain.empty() ? _s->User : _s->Domain + L"\\" + _s->User;
            sid = _s->Sid;
        }
        ReleaseSRWLockShared(&_s->Lock);
        return has;
    }

    HRESULT Channel::TakeSerialization(BYTE** serialization, ULONG* serializationBytes)
    {
        AcquireSRWLockExclusive(&_s->Lock);
        HRESULT hr = E_UNEXPECTED;
        if (_s->Ticket && !_s->Taken && _s->Password != nullptr)
        {
            _s->Taken = true; // single use, whatever happens next
            if (CryptUnprotectMemory(_s->Password, static_cast<DWORD>(_s->PasswordBytes), CRYPTPROTECTMEMORY_SAME_PROCESS))
                hr = PackInteractiveLogon(_s->Domain.c_str(), _s->Domain.size(), _s->User.c_str(), _s->User.size(),
                    _s->Password, _s->PasswordChars, serialization, serializationBytes);
            else
                hr = HRESULT_FROM_WIN32(GetLastError());
            _s->FreePassword(); // zeroes the clear text before the pages go
        }
        ReleaseSRWLockExclusive(&_s->Lock);
        return hr;
    }

    bool Channel::ReportResult(NTSTATUS status, NTSTATUS substatus)
    {
        AcquireSRWLockExclusive(&_s->Lock);
        const bool accepted = _s->Ticket && _s->Taken;
        if (accepted)
        {
            memcpy(_s->Result.Nonce, _s->Nonce, NonceBytes);
            _s->Result.Status = status;
            _s->Result.Substatus = substatus;
            _s->ResultPending = true;
        }
        ReleaseSRWLockExclusive(&_s->Lock);
        SetEvent(_s->ResultEvent.Value);
        return accepted;
    }

    void Channel::NotListed(bool notListed)
    {
        bool wake = false;
        AcquireSRWLockExclusive(&_s->Lock);
        if (!notListed) _s->NotListedAt = 0;
        else if (_s->Ticket && !_s->Taken && _s->NotListedAt == 0)
        {
            _s->NotListedAt = GetTickCount64();
            wake = true; // the thread starts counting the grace
        }
        ReleaseSRWLockExclusive(&_s->Lock);
        if (wake)
        {
            Trace("provider %ld: account not in LogonUI's list, %llu ms for LogonUI to take the ticket", _id, NotOfferedGraceMs);
            SetEvent(_s->ResultEvent.Value);
        }
    }

    DWORD Channel::NotOfferedWaitMs()
    {
        AcquireSRWLockShared(&_s->Lock);
        const bool armed = _s->Ticket && !_s->Taken && _s->NotListedAt != 0;
        const ULONGLONG since = armed ? GetTickCount64() - _s->NotListedAt : 0;
        ReleaseSRWLockShared(&_s->Lock);
        if (!armed) return INFINITE;
        return since >= NotOfferedGraceMs ? 0 : static_cast<DWORD>(NotOfferedGraceMs - since);
    }

    bool Channel::ReportNotOffered()
    {
        AcquireSRWLockExclusive(&_s->Lock);
        const bool expired = _s->Ticket && !_s->Taken && _s->NotListedAt != 0 && GetTickCount64() - _s->NotListedAt >= NotOfferedGraceMs;
        if (expired)
        {
            _s->Taken = true;     // single use: no GetSerialization can take it now
            _s->FreePassword();   // never used, gone at once
            memcpy(_s->Result.Nonce, _s->Nonce, NonceBytes);
            _s->NotOfferedPending = true;
        }
        ReleaseSRWLockExclusive(&_s->Lock);
        return expired;
    }

    void Channel::ClearTicket(bool notify)
    {
        AcquireSRWLockExclusive(&_s->Lock);
        const bool had = _s->Ticket;
        _s->Ticket = _s->Taken = _s->ResultPending = _s->NotOfferedPending = false;
        _s->NotListedAt = 0;
        Wipe(_s->Nonce);
        _s->Domain.clear();
        _s->User.clear();
        _s->Sid.clear();
        _s->FreePassword();
        ReleaseSRWLockExclusive(&_s->Lock);
        if (had && notify) NotifyChanged();
    }

    void Channel::Run()
    {
        const HANDLE stop = _s->StopEvent.Value;
        while (WaitForSingleObject(stop, 0) != WAIT_OBJECT_0)
        {
            // §7.2: LogonUI in an RDP session never opens the pipe.
            if (InConsoleSession())
            {
                Handle pipe(CreateFileW(PipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                    FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
                if (pipe)
                {
                    DWORD mode = PIPE_READMODE_MESSAGE;
                    if (SetNamedPipeHandleState(pipe.Value, &mode, nullptr, nullptr) && VerifyServer(pipe.Value))
                    {
                        Trace("provider %ld: pipe connected to the agent", _id);
                        Serve(pipe.Value);
                        Trace("provider %ld: pipe closed", _id);
                    }
                    else Trace("provider %ld: pipe server not verified, closed", _id);
                    ClearTicket(true);
                }
            }
            WaitForSingleObject(stop, RetryMs);
        }
        ClearTicket(false);
    }

    // Sends the result LogonUI reported, or that it won't take the ticket (NotOffered), if one waits (sent = true then).
    // The stop doesn't cut this write short – only its own time limit does – so a provider released right after
    // ReportResult still delivers it (§8.2). False when the write failed.
    bool Channel::SendPendingResult(HANDLE pipe, bool& sent)
    {
        AcquireSRWLockExclusive(&_s->Lock);
        const bool notOffered = _s->NotOfferedPending;
        sent = _s->ResultPending || notOffered;
        ResultMessage result = _s->Result;
        _s->ResultPending = _s->NotOfferedPending = false;
        ReleaseSRWLockExclusive(&_s->Lock);
        if (!sent) return true;
        uint8_t out[64];
        size_t n;
        if (notOffered)
        {
            NotOfferedMessage message;
            memcpy(message.Nonce, result.Nonce, NonceBytes);
            n = WriteNotOffered(message, out, sizeof(out));
        }
        else n = WriteResult(result, out, sizeof(out));
        // §8.2: the ticket is gone after ReportResult, whatever the outcome - and after a failure the tile with it
        ClearTicket(TileGoesAfterResult(notOffered, result.Status));
        const bool written = n != 0 && WriteMessage(pipe, nullptr, out, n);
        if (notOffered) Trace("provider %ld: not offered sent to the agent: %s", _id, written ? "yes" : "write failed");
        else Trace("provider %ld: result 0x%08lX sent to the agent: %s", _id, static_cast<unsigned long>(result.Status), written ? "yes" : "write failed");
        return written;
    }

    // One connection: Pending -> Claim -> Ticket -> (LogonUI) -> Result, any number of times. Anything unexpected ends it.
    void Channel::Serve(HANDLE pipe)
    {
        const HANDLE stop = _s->StopEvent.Value;
        Handle readDone(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!readDone) return;

        uint8_t buffer[MaxMessageBytes];
        enum class Phase { Idle, AwaitTicket, HaveTicket } phase = Phase::Idle;
        uint8_t nonce[NonceBytes] = {};

        struct Cleanup
        {
            uint8_t* Buffer; uint8_t* Nonce;
            ~Cleanup() { Wipe(Buffer, MaxMessageBytes); Wipe(Nonce, NonceBytes); }
        } cleanup{ buffer, nonce };

        for (;;)
        {
            OVERLAPPED ov = {};
            ov.hEvent = readDone.Value;
            ResetEvent(readDone.Value);
            if (!ReadFile(pipe, buffer, sizeof(buffer), nullptr, &ov) && GetLastError() != ERROR_IO_PENDING) return;

            // Wait for LogonUI's result to send, the stop, or the message (WaitServe's order).
            DWORD read = 0;
            bool gotMessage = false;
            for (;;)
            {
                const DWORD grace = phase == Phase::HaveTicket ? NotOfferedWaitMs() : INFINITE;
                const DWORD timeout = phase == Phase::AwaitTicket ? TicketTimeoutMs : grace;
                const Wake wake = WaitServe(_s->ResultEvent.Value, stop, readDone.Value, timeout);
                if (wake == Wake::Timeout && grace != INFINITE)
                {
                    // The grace for an account LogonUI doesn't list has run out (NotListed). If LogonUI still hasn't
                    // taken the ticket, it won't: drop it and tell the service. If it has, wait for its result as usual.
                    if (ReportNotOffered())
                    {
                        bool sent = false;
                        if (!SendPendingResult(pipe, sent)) break;
                        if (sent) phase = Phase::Idle;
                        NotifyChanged(); // the tile goes
                    }
                    continue;
                }
                if (wake == Wake::Message)
                {
                    // ERROR_MORE_DATA (a message over 4 KB) fails here and ends the connection.
                    gotMessage = GetOverlappedResult(pipe, &ov, &read, FALSE) != FALSE;
                    break;
                }
                if (wake == Wake::Result)
                {
                    bool sent = false;
                    if (!SendPendingResult(pipe, sent)) break;
                    if (sent) phase = Phase::Idle;
                    continue;
                }
                // Stop, timeout or failure. A result reported just before still goes out: it is the only way the
                // service learns that the account was signed in (C-END-10).
                Trace("provider %ld: %s while %s", _id, wake == Wake::Stop ? "stopped" : wake == Wake::Timeout ? "no ticket in time" : "wait failed",
                    phase == Phase::HaveTicket ? "a ticket was here" : "no ticket was here");
                bool sent = false;
                SendPendingResult(pipe, sent);
                break;
            }
            if (!gotMessage)
            {
                CancelIoEx(pipe, &ov);
                GetOverlappedResult(pipe, &ov, &read, TRUE);
                return;
            }

            MessageType type;
            if (!ReadHeader(buffer, read, type)) return;

            if (type == MessageType::Pending && phase == Phase::Idle)
            {
                PendingMessage pending;
                if (!ParsePending(buffer, read, pending) || pending.Session != OwnSession() || !InConsoleSession()) return;
                memcpy(nonce, pending.Nonce, NonceBytes);
                ClaimMessage claim = pending;
                uint8_t out[64];
                const size_t n = WriteClaim(claim, out, sizeof(out));
                if (n == 0 || !WriteMessage(pipe, stop, out, n)) return;
                Trace("provider %ld: Pending claimed", _id);
                phase = Phase::AwaitTicket;
            }
            else if (type == MessageType::Ticket && phase == Phase::AwaitTicket)
            {
                auto ticket = std::make_unique<TicketMessage>();
                struct WipeTicket { TicketMessage* T; ~WipeTicket() { Wipe(*T); } } wipeTicket{ ticket.get() };
                if (!ParseTicket(buffer, read, *ticket) || memcmp(ticket->Nonce, nonce, NonceBytes) != 0) return;
                Wipe(buffer, read);

                const size_t bytes = ((ticket->Password.Length * sizeof(wchar_t) + CRYPTPROTECTMEMORY_BLOCK_SIZE - 1) /
                    CRYPTPROTECTMEMORY_BLOCK_SIZE) * CRYPTPROTECTMEMORY_BLOCK_SIZE;
                auto* password = static_cast<wchar_t*>(VirtualAlloc(nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
                if (password == nullptr) return;
                memcpy(password, ticket->Password.Chars, ticket->Password.Length * sizeof(wchar_t));
                if (!CryptProtectMemory(password, static_cast<DWORD>(bytes), CRYPTPROTECTMEMORY_SAME_PROCESS))
                {
                    SecureZeroMemory(password, bytes);
                    VirtualFree(password, 0, MEM_RELEASE);
                    return;
                }

                AcquireSRWLockExclusive(&_s->Lock);
                _s->FreePassword();
                _s->Ticket = true;
                _s->Taken = false;
                _s->NotListedAt = 0;
                memcpy(_s->Nonce, nonce, NonceBytes);
                _s->Domain.assign(ticket->Domain.Chars, ticket->Domain.Length);
                _s->User.assign(ticket->User.Chars, ticket->User.Length);
                _s->Sid.assign(ticket->Sid.Chars, ticket->Sid.Length);
                _s->Password = password;
                _s->PasswordChars = ticket->Password.Length;
                _s->PasswordBytes = bytes;
                ReleaseSRWLockExclusive(&_s->Lock);
                phase = Phase::HaveTicket;
                Trace("provider %ld: ticket received (account SID %s), telling LogonUI", _id, ticket->Sid.Length != 0 ? "given" : "none");
                NotifyChanged();
            }
            else
            {
                // Out of order, or a type the provider never receives.
                Trace("provider %ld: message type %d unexpected in phase %d, closing", _id, static_cast<int>(type), static_cast<int>(phase));
                return;
            }
        }
    }
}
