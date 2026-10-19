#include "Wire.h"

#include <cstring>

namespace rhcp
{
    namespace
    {
        constexpr size_t HeaderBytes = 4;

        // A cursor over the payload; every read checks the remaining length first.
        class Reader
        {
        public:
            Reader(const uint8_t* data, size_t size) : _data(data), _size(size) {}

            bool Bytes(uint8_t* out, size_t count)
            {
                if (_size - _pos < count) return false;
                memcpy(out, _data + _pos, count);
                _pos += count;
                return true;
            }

            bool U16(uint16_t& out)
            {
                uint8_t b[2];
                if (!Bytes(b, 2)) return false;
                out = static_cast<uint16_t>(b[0] | (b[1] << 8));
                return true;
            }

            bool U32(uint32_t& out)
            {
                uint8_t b[4];
                if (!Bytes(b, 4)) return false;
                out = static_cast<uint32_t>(b[0]) | (static_cast<uint32_t>(b[1]) << 8) |
                      (static_cast<uint32_t>(b[2]) << 16) | (static_cast<uint32_t>(b[3]) << 24);
                return true;
            }

            template <size_t N>
            bool String(FixedString<N>& out)
            {
                uint16_t length;
                if (!U16(length) || length > N) return false;
                for (uint16_t i = 0; i < length; i++)
                {
                    uint16_t unit;
                    if (!U16(unit)) return false;
                    out.Chars[i] = static_cast<wchar_t>(unit);
                }
                out.Chars[length] = L'\0';
                out.Length = length;
                return true;
            }

            bool AtEnd() const { return _pos == _size; }

        private:
            const uint8_t* _data;
            size_t _size;
            size_t _pos = 0;
        };

        class Writer
        {
        public:
            Writer(uint8_t* buffer, size_t capacity) : _buffer(buffer), _capacity(capacity) {}

            void Bytes(const uint8_t* data, size_t count)
            {
                if (!_ok || _capacity - _pos < count) { _ok = false; return; }
                memcpy(_buffer + _pos, data, count);
                _pos += count;
            }

            void U8(uint8_t value) { Bytes(&value, 1); }

            void U16(uint16_t value)
            {
                const uint8_t b[2] = { static_cast<uint8_t>(value), static_cast<uint8_t>(value >> 8) };
                Bytes(b, 2);
            }

            void U32(uint32_t value)
            {
                const uint8_t b[4] = { static_cast<uint8_t>(value), static_cast<uint8_t>(value >> 8),
                                       static_cast<uint8_t>(value >> 16), static_cast<uint8_t>(value >> 24) };
                Bytes(b, 4);
            }

            template <size_t N>
            void String(const FixedString<N>& value)
            {
                if (value.Length > N) { _ok = false; return; }
                U16(value.Length);
                for (uint16_t i = 0; i < value.Length; i++) U16(static_cast<uint16_t>(value.Chars[i]));
            }

            // Writes the header in front of the payload once the payload is complete.
            size_t Finish(MessageType type)
            {
                if (!_ok || _pos < HeaderBytes || _pos - HeaderBytes > 0xFFFF || _pos > MaxMessageBytes) return 0;
                const size_t payload = _pos - HeaderBytes;
                _buffer[0] = WireVersion;
                _buffer[1] = static_cast<uint8_t>(type);
                _buffer[2] = static_cast<uint8_t>(payload);
                _buffer[3] = static_cast<uint8_t>(payload >> 8);
                return _pos;
            }

            void SkipHeader()
            {
                if (_capacity < HeaderBytes) { _ok = false; return; }
                _pos = HeaderBytes;
            }

        private:
            uint8_t* _buffer;
            size_t _capacity;
            size_t _pos = 0;
            bool _ok = true;
        };

        bool Payload(const uint8_t* data, size_t size, MessageType expected, Reader& reader)
        {
            MessageType type;
            if (!ReadHeader(data, size, type) || type != expected) return false;
            reader = Reader(data + HeaderBytes, size - HeaderBytes);
            return true;
        }

        bool ParseNonceSession(const uint8_t* data, size_t size, MessageType expected, PendingMessage& out)
        {
            Reader r(nullptr, 0);
            if (Payload(data, size, expected, r) && r.Bytes(out.Nonce, NonceBytes) && r.U32(out.Session) && r.AtEnd())
                return true;
            Wipe(out);
            return false;
        }

        size_t WriteNonceSession(const PendingMessage& message, MessageType type, uint8_t* buffer, size_t capacity)
        {
            Writer w(buffer, capacity);
            w.SkipHeader();
            w.Bytes(message.Nonce, NonceBytes);
            w.U32(message.Session);
            return w.Finish(type);
        }
    }

    bool ReadHeader(const uint8_t* data, size_t size, MessageType& type)
    {
        if (data == nullptr || size < HeaderBytes || size > MaxMessageBytes) return false;
        if (data[0] != WireVersion) return false;
        if (data[1] < static_cast<uint8_t>(MessageType::Pending) || data[1] > static_cast<uint8_t>(MessageType::NotOffered)) return false;
        const size_t payload = static_cast<size_t>(data[2]) | (static_cast<size_t>(data[3]) << 8);
        if (payload != size - HeaderBytes) return false;
        type = static_cast<MessageType>(data[1]);
        return true;
    }

    bool ParsePending(const uint8_t* data, size_t size, PendingMessage& out)
    {
        return ParseNonceSession(data, size, MessageType::Pending, out);
    }

    bool ParseClaim(const uint8_t* data, size_t size, ClaimMessage& out)
    {
        return ParseNonceSession(data, size, MessageType::Claim, out);
    }

    bool ParseTicket(const uint8_t* data, size_t size, TicketMessage& out)
    {
        Reader r(nullptr, 0);
        if (Payload(data, size, MessageType::Ticket, r) && r.Bytes(out.Nonce, NonceBytes) &&
            r.String(out.Domain) && r.String(out.User) && r.String(out.Sid) && r.String(out.Password) && r.AtEnd() &&
            out.User.Length > 0 && out.Password.Length > 0)
            return true;
        Wipe(out);
        return false;
    }

    bool ParseResult(const uint8_t* data, size_t size, ResultMessage& out)
    {
        Reader r(nullptr, 0);
        uint32_t status, substatus;
        if (Payload(data, size, MessageType::Result, r) && r.Bytes(out.Nonce, NonceBytes) && r.U32(status) && r.U32(substatus) && r.AtEnd())
        {
            out.Status = static_cast<int32_t>(status);
            out.Substatus = static_cast<int32_t>(substatus);
            return true;
        }
        Wipe(out);
        return false;
    }

    bool ParseNotOffered(const uint8_t* data, size_t size, NotOfferedMessage& out)
    {
        Reader r(nullptr, 0);
        if (Payload(data, size, MessageType::NotOffered, r) && r.Bytes(out.Nonce, NonceBytes) && r.AtEnd()) return true;
        Wipe(out);
        return false;
    }

    size_t WritePending(const PendingMessage& message, uint8_t* buffer, size_t capacity)
    {
        return WriteNonceSession(message, MessageType::Pending, buffer, capacity);
    }

    size_t WriteClaim(const ClaimMessage& message, uint8_t* buffer, size_t capacity)
    {
        return WriteNonceSession(message, MessageType::Claim, buffer, capacity);
    }

    size_t WriteTicket(const TicketMessage& message, uint8_t* buffer, size_t capacity)
    {
        Writer w(buffer, capacity);
        w.SkipHeader();
        w.Bytes(message.Nonce, NonceBytes);
        w.String(message.Domain);
        w.String(message.User);
        w.String(message.Sid);
        w.String(message.Password);
        return w.Finish(MessageType::Ticket);
    }

    size_t WriteResult(const ResultMessage& message, uint8_t* buffer, size_t capacity)
    {
        Writer w(buffer, capacity);
        w.SkipHeader();
        w.Bytes(message.Nonce, NonceBytes);
        w.U32(static_cast<uint32_t>(message.Status));
        w.U32(static_cast<uint32_t>(message.Substatus));
        return w.Finish(MessageType::Result);
    }

    size_t WriteNotOffered(const NotOfferedMessage& message, uint8_t* buffer, size_t capacity)
    {
        Writer w(buffer, capacity);
        w.SkipHeader();
        w.Bytes(message.Nonce, NonceBytes);
        return w.Finish(MessageType::NotOffered);
    }

    void Wipe(void* data, size_t size)
    {
        // The same as SecureZeroMemory, without needing <windows.h> here (the fuzzer and tests build this file alone).
        volatile uint8_t* p = static_cast<volatile uint8_t*>(data);
        while (size--) *p++ = 0;
    }
}
