// The messages between the agent service and the credential provider (credential-provider-design.md §7.3).
//
// One message per pipe message (message mode), little-endian:
//   [u8 version = 1][u8 type][u16 payload length][payload]
// Payloads:
//   Pending  service -> provider   nonce[16] session:u32
//   Claim    provider -> service   nonce[16] session:u32
//   Ticket   service -> provider   nonce[16] domain user sid password   (each: u16 length in UTF-16 units, then the units)
//   Result   provider -> service   nonce[16] status:i32 substatus:i32
// Nothing else exists. Anything that doesn't parse exactly – a wrong version or type, a length that doesn't match, a
// string over its limit, bytes left over – is refused, and the caller closes the connection.
//
// No allocation depends on a length from the peer: every string is copied into a fixed-size array.
#pragma once

#include <cstddef>
#include <cstdint>

namespace rhcp
{
    constexpr uint8_t WireVersion = 1;
    constexpr size_t MaxMessageBytes = 4096;
    constexpr size_t NonceBytes = 16;
    constexpr size_t MaxNameChars = 256;      // domain, user name, SID
    constexpr size_t MaxPasswordChars = 256;  // Windows' own limit for a password

    enum class MessageType : uint8_t
    {
        Pending = 1,
        Claim = 2,
        Ticket = 3,
        Result = 4,
    };

    // A UTF-16 string of at most N units, not null-terminated on the wire; Chars[Length] is always 0 here.
    template <size_t N>
    struct FixedString
    {
        uint16_t Length = 0;
        wchar_t Chars[N + 1] = {};
    };

    struct PendingMessage
    {
        uint8_t Nonce[NonceBytes] = {};
        uint32_t Session = 0;
    };

    using ClaimMessage = PendingMessage;

    struct TicketMessage
    {
        uint8_t Nonce[NonceBytes] = {};
        FixedString<MaxNameChars> Domain;
        FixedString<MaxNameChars> User;
        FixedString<MaxNameChars> Sid;
        FixedString<MaxPasswordChars> Password;
    };

    struct ResultMessage
    {
        uint8_t Nonce[NonceBytes] = {};
        int32_t Status = 0;
        int32_t Substatus = 0;
    };

    // The type of a well-formed header, or false. The payload length must match the bytes that follow exactly.
    bool ReadHeader(const uint8_t* data, size_t size, MessageType& type);

    // Each parser checks the header (type included) and the whole payload. On false the output is zeroed.
    bool ParsePending(const uint8_t* data, size_t size, PendingMessage& out);
    bool ParseClaim(const uint8_t* data, size_t size, ClaimMessage& out);
    bool ParseTicket(const uint8_t* data, size_t size, TicketMessage& out);
    bool ParseResult(const uint8_t* data, size_t size, ResultMessage& out);

    // Each writer returns the number of bytes written, or 0 if the buffer is too small or a string is too long.
    size_t WritePending(const PendingMessage& message, uint8_t* buffer, size_t capacity);
    size_t WriteClaim(const ClaimMessage& message, uint8_t* buffer, size_t capacity);
    size_t WriteTicket(const TicketMessage& message, uint8_t* buffer, size_t capacity);
    size_t WriteResult(const ResultMessage& message, uint8_t* buffer, size_t capacity);

    // Zeroes memory in a way the compiler can't remove (the password, buffers that held it).
    void Wipe(void* data, size_t size);

    template <typename T>
    void Wipe(T& value) { Wipe(&value, sizeof(value)); }
}
