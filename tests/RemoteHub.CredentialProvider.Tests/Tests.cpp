// Unit tests for the credential provider's pipe messages and logon packing (no test framework: plain checks, exit
// code = number of failures). With "--corpus <dir>" it also writes well-formed messages there as seeds for the fuzzer.
#include "../../src/RemoteHub.CredentialProvider/Channel.h"
#include "../../src/RemoteHub.CredentialProvider/Logon.h"
#include "../../src/RemoteHub.CredentialProvider/Wire.h"

#include <ntsecapi.h>
#include <combaseapi.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

using namespace rhcp;

namespace
{
    int g_failures = 0;
    const char* g_test = "";

#define CHECK(condition) \
    do { if (!(condition)) { std::printf("FAIL %s: %s (line %d)\n", g_test, #condition, __LINE__); g_failures++; } } while (0)

    template <size_t N>
    void Set(FixedString<N>& target, const wchar_t* text)
    {
        target.Length = static_cast<uint16_t>(wcslen(text));
        wcscpy_s(target.Chars, text);
    }

    template <size_t N>
    bool Equals(const FixedString<N>& value, const wchar_t* text)
    {
        return value.Length == wcslen(text) && wcscmp(value.Chars, text) == 0;
    }

    TicketMessage SampleTicket()
    {
        TicketMessage t;
        for (size_t i = 0; i < NonceBytes; i++) t.Nonce[i] = static_cast<uint8_t>(0xA0 + i);
        Set(t.Domain, L"CONTOSO");
        Set(t.User, L"admin");
        Set(t.Sid, L"S-1-5-21-1-2-3-500");
        Set(t.Password, L"Pässwörd!");
        return t;
    }

    std::vector<uint8_t> Bytes(size_t (*write)(const TicketMessage&, uint8_t*, size_t), const TicketMessage& m)
    {
        std::vector<uint8_t> buffer(MaxMessageBytes);
        buffer.resize(write(m, buffer.data(), buffer.size()));
        return buffer;
    }

    void TicketRoundTrip()
    {
        g_test = "TicketRoundTrip";
        const TicketMessage sent = SampleTicket();
        const auto bytes = Bytes(WriteTicket, sent);
        CHECK(!bytes.empty());
        TicketMessage got;
        CHECK(ParseTicket(bytes.data(), bytes.size(), got));
        CHECK(memcmp(got.Nonce, sent.Nonce, NonceBytes) == 0);
        CHECK(Equals(got.Domain, L"CONTOSO"));
        CHECK(Equals(got.User, L"admin"));
        CHECK(Equals(got.Sid, L"S-1-5-21-1-2-3-500"));
        CHECK(Equals(got.Password, L"Pässwörd!"));
        MessageType type;
        CHECK(ReadHeader(bytes.data(), bytes.size(), type) && type == MessageType::Ticket);
    }

    void SmallMessagesRoundTrip()
    {
        g_test = "SmallMessagesRoundTrip";
        PendingMessage pending;
        pending.Nonce[0] = 1;
        pending.Nonce[15] = 0xFF;
        pending.Session = 0x01020304;
        uint8_t buffer[64];
        size_t n = WritePending(pending, buffer, sizeof(buffer));
        CHECK(n == 4 + 16 + 4);
        PendingMessage got;
        CHECK(ParsePending(buffer, n, got) && got.Session == 0x01020304 && got.Nonce[15] == 0xFF);
        ClaimMessage claim;
        CHECK(!ParseClaim(buffer, n, claim)); // a Pending is not a Claim

        n = WriteClaim(pending, buffer, sizeof(buffer));
        CHECK(ParseClaim(buffer, n, claim) && claim.Session == 0x01020304);

        ResultMessage result;
        result.Status = static_cast<int32_t>(0xC000006D);  // STATUS_LOGON_FAILURE
        result.Substatus = static_cast<int32_t>(0xC000006A);
        n = WriteResult(result, buffer, sizeof(buffer));
        ResultMessage r;
        CHECK(ParseResult(buffer, n, r) && r.Status == result.Status && r.Substatus == result.Substatus);
    }

    void RejectsMalformed()
    {
        g_test = "RejectsMalformed";
        const auto good = Bytes(WriteTicket, SampleTicket());
        TicketMessage t;

        // Every truncation, and one byte too many.
        for (size_t size = 0; size < good.size(); size++) CHECK(!ParseTicket(good.data(), size, t));
        auto longer = good;
        longer.push_back(0);
        CHECK(!ParseTicket(longer.data(), longer.size(), t));

        auto bad = good;
        bad[0] = 2; // version
        CHECK(!ParseTicket(bad.data(), bad.size(), t));
        bad = good;
        bad[1] = 9; // type
        MessageType type;
        CHECK(!ReadHeader(bad.data(), bad.size(), type));
        bad = good;
        bad[2]++; // length doesn't match
        CHECK(!ParseTicket(bad.data(), bad.size(), t));

        // A string length over the limit (the domain's length field follows the nonce).
        bad = good;
        bad[4 + 16] = 0xFF;
        bad[4 + 16 + 1] = 0xFF;
        CHECK(!ParseTicket(bad.data(), bad.size(), t));

        // Nothing usable is left behind after a failed parse.
        CHECK(t.Password.Length == 0 && t.Password.Chars[0] == 0 && t.User.Length == 0);

        // Over 4 KB is refused before anything else.
        std::vector<uint8_t> huge(MaxMessageBytes + 1, 0);
        CHECK(!ReadHeader(huge.data(), huge.size(), type));
        CHECK(!ReadHeader(nullptr, 10, type));
    }

    void RejectsEmptyAccount()
    {
        g_test = "RejectsEmptyAccount";
        TicketMessage m = SampleTicket();
        Set(m.User, L"");
        auto bytes = Bytes(WriteTicket, m);
        TicketMessage t;
        CHECK(!bytes.empty() && !ParseTicket(bytes.data(), bytes.size(), t));
        m = SampleTicket();
        Set(m.Password, L"");
        bytes = Bytes(WriteTicket, m);
        CHECK(!bytes.empty() && !ParseTicket(bytes.data(), bytes.size(), t));
    }

    void WriterLimits()
    {
        g_test = "WriterLimits";
        TicketMessage m = SampleTicket();
        uint8_t tooSmall[20];
        CHECK(WriteTicket(m, tooSmall, sizeof(tooSmall)) == 0);
        m.Password.Length = MaxPasswordChars + 1; // claims more than the array holds
        std::vector<uint8_t> buffer(MaxMessageBytes);
        CHECK(WriteTicket(m, buffer.data(), buffer.size()) == 0);

        // The longest legal ticket still fits into one message.
        m = SampleTicket();
        std::wstring longName(MaxNameChars, L'x'), longPassword(MaxPasswordChars, L'p');
        Set(m.Domain, longName.c_str());
        Set(m.User, longName.c_str());
        Set(m.Sid, longName.c_str());
        Set(m.Password, longPassword.c_str());
        const size_t n = WriteTicket(m, buffer.data(), buffer.size());
        TicketMessage t;
        CHECK(n > 0 && n <= MaxMessageBytes && ParseTicket(buffer.data(), n, t) && t.Password.Length == MaxPasswordChars);
    }

    void PacksInteractiveLogon()
    {
        g_test = "PacksInteractiveLogon";
        const wchar_t domain[] = L"CONTOSO", user[] = L"admin", password[] = L"secret";
        BYTE* buffer = nullptr;
        ULONG bytes = 0;
        CHECK(SUCCEEDED(PackInteractiveLogon(domain, 7, user, 5, password, 6, &buffer, &bytes)));
        CHECK(bytes == sizeof(KERB_INTERACTIVE_UNLOCK_LOGON) + (7 + 5 + 6) * sizeof(wchar_t));
        if (buffer == nullptr) return;

        const auto* logon = reinterpret_cast<const KERB_INTERACTIVE_UNLOCK_LOGON*>(buffer);
        CHECK(logon->Logon.MessageType == KerbInteractiveLogon);
        CHECK(logon->LogonId.LowPart == 0 && logon->LogonId.HighPart == 0);
        auto text = [&](const UNICODE_STRING& s) {
            const auto offset = reinterpret_cast<ULONG_PTR>(s.Buffer);
            if (offset + s.Length > bytes) return std::wstring(L"<out of range>");
            return std::wstring(reinterpret_cast<const wchar_t*>(buffer + offset), s.Length / sizeof(wchar_t));
        };
        CHECK(text(logon->Logon.LogonDomainName) == L"CONTOSO");
        CHECK(text(logon->Logon.UserName) == L"admin");
        CHECK(text(logon->Logon.Password) == L"secret");
        CHECK(logon->Logon.Password.MaximumLength == logon->Logon.Password.Length);
        CoTaskMemFree(buffer);

        // A local account may come without a domain; a user name and the pointers are required.
        CHECK(SUCCEEDED(PackInteractiveLogon(nullptr, 0, user, 5, password, 6, &buffer, &bytes)));
        CoTaskMemFree(buffer);
        CHECK(PackInteractiveLogon(domain, 7, user, 0, password, 6, &buffer, &bytes) == E_INVALIDARG && buffer == nullptr);
        CHECK(PackInteractiveLogon(domain, 7, user, 5, password, 6, nullptr, &bytes) == E_POINTER);
    }

    // C-END-10: LogonUI reports the result and releases the provider (the stop) right after it; the pipe thread must
    // still see the result first, or the service never learns that the account was signed in.
    void ResultWinsOverStop()
    {
        g_test = "ResultWinsOverStop";
        const HANDLE result = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        const HANDLE stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        const HANDLE message = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        CHECK(result != nullptr && stop != nullptr && message != nullptr);
        if (result != nullptr && stop != nullptr && message != nullptr)
        {
            CHECK(WaitServe(result, stop, message, 0) == Wake::Failed); // nothing signalled: the time limit
            SetEvent(message);
            CHECK(WaitServe(result, stop, message, 0) == Wake::Message);
            SetEvent(stop);
            CHECK(WaitServe(result, stop, message, 0) == Wake::Stop);   // a stop still wins over the next message ...
            SetEvent(result);
            CHECK(WaitServe(result, stop, message, 0) == Wake::Result); // ... but not over a result to send
            CHECK(WaitServe(result, stop, message, 0) == Wake::Stop);   // the result event resets once taken
        }
        if (result != nullptr) CloseHandle(result);
        if (stop != nullptr) CloseHandle(stop);
        if (message != nullptr) CloseHandle(message);
    }

    void FindsNegotiate()
    {
        g_test = "FindsNegotiate";
        ULONG package = 0xFFFFFFFF;
        CHECK(SUCCEEDED(NegotiatePackage(&package)));
        CHECK(package != 0xFFFFFFFF);
    }

    void WriteCorpus(const char* dir)
    {
        auto save = [&](const char* name, const uint8_t* data, size_t size) {
            std::string path = std::string(dir) + "\\" + name;
            FILE* f = nullptr;
            if (fopen_s(&f, path.c_str(), "wb") == 0 && f) { fwrite(data, 1, size, f); fclose(f); }
        };
        auto ticket = Bytes(WriteTicket, SampleTicket());
        save("ticket", ticket.data(), ticket.size());
        uint8_t buffer[64];
        PendingMessage p;
        p.Session = 1;
        save("pending", buffer, WritePending(p, buffer, sizeof(buffer)));
        save("claim", buffer, WriteClaim(p, buffer, sizeof(buffer)));
        ResultMessage r;
        save("result", buffer, WriteResult(r, buffer, sizeof(buffer)));
    }
}

int main(int argc, char** argv)
{
    if (argc == 3 && strcmp(argv[1], "--corpus") == 0)
    {
        WriteCorpus(argv[2]);
        return 0;
    }
    TicketRoundTrip();
    SmallMessagesRoundTrip();
    RejectsMalformed();
    RejectsEmptyAccount();
    WriterLimits();
    PacksInteractiveLogon();
    FindsNegotiate();
    ResultWinsOverStop();
    std::printf(g_failures == 0 ? "All credential provider tests passed.\n" : "%d check(s) failed.\n", g_failures);
    return g_failures;
}
