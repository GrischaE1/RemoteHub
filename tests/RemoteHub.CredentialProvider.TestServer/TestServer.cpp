// Phase 1 lab tool (credential-provider-design.md §13 build order): stands in for the agent service on the pipe, so
// the Lab build of the credential provider (RemoteHubCredentialProvider.Lab.dll) can sign in a test account before
// the service side exists. Lab VMs only – never packaged.
//
//   RemoteHubCpTestServer.exe <ticket file> <log file> [timeout seconds = 300]
//
// The ticket file holds two UTF-8 lines, "DOMAIN\user" (or "user" for a local account) and the password. It is read
// and deleted at once. Runs as SYSTEM (a scheduled task) from the provider's folder, which the Lab DLL checks.
// The pipe is created as §7.1 says (SYSTEM-only DACL, first instance, no remote clients) and the client is checked as
// §7.2 says (LogonUI.exe from System32, SYSTEM, the console session). The log file gets one line per step and ends
// with "result status=0x... substatus=0x..." or "error ...". Exit code 0 = LogonUI reported a result.
#include "../../src/RemoteHub.CredentialProvider/Wire.h"

#include <windows.h>
#include <bcrypt.h>
#include <sddl.h>

#include <cstdarg>
#include <cstdio>
#include <memory>
#include <string>

using namespace rhcp;

namespace
{
    FILE* g_log = nullptr;

    void Log(const char* format, ...)
    {
        SYSTEMTIME t;
        GetLocalTime(&t);
        va_list args;
        va_start(args, format);
        if (g_log)
        {
            fprintf(g_log, "%02d:%02d:%02d.%03d ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
            vfprintf(g_log, format, args);
            fputc('\n', g_log);
            fflush(g_log);
        }
        va_end(args);
    }

    int Fail(const char* what)
    {
        Log("error %s (Win32 %lu)", what, GetLastError());
        return 1;
    }

    std::wstring Widen(const std::string& text)
    {
        if (text.empty()) return {};
        const int n = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
        std::wstring out(n, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), out.data(), n);
        return out;
    }

    bool SameText(const wchar_t* a, const wchar_t* b) { return CompareStringOrdinal(a, -1, b, -1, TRUE) == CSTR_EQUAL; }

    bool IsSystem(HANDLE process)
    {
        HANDLE token;
        if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return false;
        BYTE buffer[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE];
        DWORD size;
        const bool ok = GetTokenInformation(token, TokenUser, buffer, sizeof(buffer), &size) &&
            IsWellKnownSid(reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid, WinLocalSystemSid);
        CloseHandle(token);
        return ok;
    }

    // §7.2, the service's side: LogonUI.exe from System32, running as SYSTEM, in the console session.
    bool VerifyClient(HANDLE pipe)
    {
        ULONG pid = 0, session = 0;
        if (!GetNamedPipeClientProcessId(pipe, &pid) || !GetNamedPipeClientSessionId(pipe, &session)) return false;
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!process) return false;
        wchar_t image[MAX_PATH], expected[MAX_PATH];
        DWORD chars = MAX_PATH;
        const UINT n = GetSystemDirectoryW(expected, MAX_PATH);
        const bool ok = QueryFullProcessImageNameW(process, 0, image, &chars) && n > 0 && n < MAX_PATH - 14 &&
            wcscat_s(expected, L"\\LogonUI.exe") == 0 && SameText(image, expected) && IsSystem(process) &&
            session == WTSGetActiveConsoleSessionId();
        Log("client pid %lu session %lu image %ls -> %s", pid, session, ok ? image : L"(rejected)", ok ? "accepted" : "rejected");
        CloseHandle(process);
        return ok;
    }

    bool Await(HANDLE pipe, OVERLAPPED& ov, DWORD timeoutMs, DWORD& transferred)
    {
        if (WaitForSingleObject(ov.hEvent, timeoutMs) == WAIT_OBJECT_0 && GetOverlappedResult(pipe, &ov, &transferred, FALSE)) return true;
        CancelIoEx(pipe, &ov);
        GetOverlappedResult(pipe, &ov, &transferred, TRUE);
        return false;
    }

    bool Write(HANDLE pipe, const uint8_t* data, size_t size)
    {
        OVERLAPPED ov = {};
        ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        DWORD n = 0;
        const bool ok = (WriteFile(pipe, data, static_cast<DWORD>(size), nullptr, &ov) || GetLastError() == ERROR_IO_PENDING) &&
            Await(pipe, ov, 5000, n) && n == size;
        CloseHandle(ov.hEvent);
        return ok;
    }

    bool Read(HANDLE pipe, uint8_t* buffer, size_t capacity, DWORD timeoutMs, DWORD& size)
    {
        OVERLAPPED ov = {};
        ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        const bool ok = (ReadFile(pipe, buffer, static_cast<DWORD>(capacity), nullptr, &ov) || GetLastError() == ERROR_IO_PENDING) &&
            Await(pipe, ov, timeoutMs, size);
        CloseHandle(ov.hEvent);
        return ok;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3)
    {
        fwprintf(stderr, L"usage: RemoteHubCpTestServer.exe <ticket file> <log file> [timeout seconds]\n");
        return 2;
    }
    if (_wfopen_s(&g_log, argv[2], L"a") != 0) return 2;
    const DWORD timeoutMs = (argc > 3 ? static_cast<DWORD>(_wtoi(argv[3])) : 300) * 1000;
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;

    // ---- The ticket: read, delete, split.
    std::string content;
    {
        FILE* f = nullptr;
        if (_wfopen_s(&f, argv[1], L"rb") != 0 || !f) return Fail("can't read the ticket file");
        char chunk[512];
        size_t n;
        while ((n = fread(chunk, 1, sizeof(chunk), f)) > 0) content.append(chunk, n);
        fclose(f);
        DeleteFileW(argv[1]);
    }
    if (content.size() >= 3 && static_cast<uint8_t>(content[0]) == 0xEF) content.erase(0, 3); // UTF-8 BOM
    const size_t eol = content.find('\n');
    if (eol == std::string::npos) return Fail("the ticket file needs two lines");
    std::wstring account = Widen(content.substr(0, eol));
    std::wstring password = Widen(content.substr(eol + 1));
    SecureZeroMemory(content.data(), content.size());
    while (!account.empty() && (account.back() == L'\r' || account.back() == L' ')) account.pop_back();
    while (!password.empty() && (password.back() == L'\r' || password.back() == L'\n')) password.pop_back();

    std::wstring domain, user;
    const size_t slash = account.find(L'\\');
    if (slash == std::wstring::npos)
    {
        wchar_t computer[MAX_COMPUTERNAME_LENGTH + 1];
        DWORD size = MAX_COMPUTERNAME_LENGTH + 1;
        GetComputerNameW(computer, &size);
        domain = computer;
        user = account;
    }
    else
    {
        domain = account.substr(0, slash);
        user = account.substr(slash + 1);
    }

    BYTE sidBuffer[SECURITY_MAX_SID_SIZE];
    DWORD sidSize = sizeof(sidBuffer), domainChars = 0;
    SID_NAME_USE use;
    wchar_t referenced[256];
    domainChars = 256;
    std::wstring sidText;
    const std::wstring qualified = domain + L"\\" + user;
    if (LookupAccountNameW(nullptr, qualified.c_str(), sidBuffer, &sidSize, referenced, &domainChars, &use))
    {
        wchar_t* s = nullptr;
        if (ConvertSidToStringSidW(sidBuffer, &s)) { sidText = s; LocalFree(s); }
    }
    Log("ticket for %ls\\%ls (SID %ls)", domain.c_str(), user.c_str(), sidText.empty() ? L"unknown" : sidText.c_str());

    // ---- The pipe, as §7.1 says.
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)", SDDL_REVISION_1, &sd, nullptr)) return Fail("security descriptor");
    SECURITY_ATTRIBUTES sa = { sizeof(sa), sd, FALSE };
    HANDLE pipe = CreateNamedPipeW(L"\\\\.\\pipe\\RemoteHubAgent.AutoSignIn",
        PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, static_cast<DWORD>(MaxMessageBytes), static_cast<DWORD>(MaxMessageBytes), 0, &sa);
    LocalFree(sd);
    if (pipe == INVALID_HANDLE_VALUE) return Fail("CreateNamedPipe (is another server already running?)");
    Log("pipe created");

    int exitCode = 1;
    for (;;)
    {
        const ULONGLONG now = GetTickCount64();
        if (now >= deadline) { Log("error timeout waiting for the provider"); break; }

        OVERLAPPED ov = {};
        ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        DWORD unused = 0;
        bool connected = ConnectNamedPipe(pipe, &ov) != FALSE;
        if (!connected)
        {
            const DWORD e = GetLastError();
            connected = e == ERROR_PIPE_CONNECTED || (e == ERROR_IO_PENDING && Await(pipe, ov, static_cast<DWORD>(deadline - now), unused));
        }
        CloseHandle(ov.hEvent);
        if (!connected) continue;
        if (!VerifyClient(pipe)) { DisconnectNamedPipe(pipe); continue; }

        // Pending -> Claim -> Ticket -> Result
        PendingMessage pending;
        BCryptGenRandom(nullptr, pending.Nonce, static_cast<ULONG>(NonceBytes), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
        pending.Session = WTSGetActiveConsoleSessionId();
        uint8_t buffer[MaxMessageBytes];
        size_t n = WritePending(pending, buffer, sizeof(buffer));
        DWORD read = 0;
        ClaimMessage claim;
        if (!Write(pipe, buffer, n)) { Log("send Pending failed"); DisconnectNamedPipe(pipe); continue; }
        Log("sent Pending (session %lu)", pending.Session);
        if (!Read(pipe, buffer, sizeof(buffer), 10000, read) || !ParseClaim(buffer, read, claim) ||
            memcmp(claim.Nonce, pending.Nonce, NonceBytes) != 0 || claim.Session != pending.Session)
        {
            Log("no valid Claim");
            DisconnectNamedPipe(pipe);
            continue;
        }
        Log("got Claim");

        auto ticket = std::make_unique<TicketMessage>();
        memcpy(ticket->Nonce, pending.Nonce, NonceBytes);
        auto set = [](auto& target, const std::wstring& text) {
            target.Length = static_cast<uint16_t>(text.size());
            wmemcpy(target.Chars, text.data(), text.size());
        };
        if (domain.size() > MaxNameChars || user.size() > MaxNameChars || sidText.size() > MaxNameChars || password.size() > MaxPasswordChars)
            return Fail("ticket field too long");
        set(ticket->Domain, domain);
        set(ticket->User, user);
        set(ticket->Sid, sidText);
        set(ticket->Password, password);
        n = WriteTicket(*ticket, buffer, sizeof(buffer));
        Wipe(*ticket);
        SecureZeroMemory(password.data(), password.size() * sizeof(wchar_t));
        const bool sent = n > 0 && Write(pipe, buffer, n);
        Wipe(buffer, sizeof(buffer));
        if (!sent) { Log("error send Ticket failed"); break; }
        Log("sent Ticket");

        ResultMessage result;
        NotOfferedMessage notOffered;
        if (!Read(pipe, buffer, sizeof(buffer), 120000, read)) { Log("error no valid Result"); break; }
        if (ParseNotOffered(buffer, read, notOffered) && memcmp(notOffered.Nonce, pending.Nonce, NonceBytes) == 0)
        {
            Log("not offered: the sign-in screen doesn't list the account");
            break; // single use
        }
        if (!ParseResult(buffer, read, result) || memcmp(result.Nonce, pending.Nonce, NonceBytes) != 0)
        {
            Log("error no valid Result");
            break;
        }
        Log("result status=0x%08X substatus=0x%08X", static_cast<unsigned>(result.Status), static_cast<unsigned>(result.Substatus));
        exitCode = 0;
        break; // single use
    }
    DisconnectNamedPipe(pipe);
    CloseHandle(pipe);
    fclose(g_log);
    return exitCode;
}
