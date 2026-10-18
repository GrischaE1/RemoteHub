// libFuzzer target (configuration Fuzz, /fsanitize=fuzzer,address): every parser over arbitrary bytes, and a parsed
// message written back must parse to the same bytes (credential-provider-design.md §8.3).
#include "../../src/RemoteHub.CredentialProvider/Wire.h"

#include <cstdlib>
#include <cstring>

using namespace rhcp;

extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size)
{
    static uint8_t again[MaxMessageBytes];
    MessageType type;
    if (!ReadHeader(data, size, type)) return 0;

    size_t n = 0;
    switch (type)
    {
    case MessageType::Pending: { PendingMessage m; if (ParsePending(data, size, m)) n = WritePending(m, again, sizeof(again)); break; }
    case MessageType::Claim: { ClaimMessage m; if (ParseClaim(data, size, m)) n = WriteClaim(m, again, sizeof(again)); break; }
    case MessageType::Ticket: { TicketMessage m; if (ParseTicket(data, size, m)) n = WriteTicket(m, again, sizeof(again)); break; }
    case MessageType::Result: { ResultMessage m; if (ParseResult(data, size, m)) n = WriteResult(m, again, sizeof(again)); break; }
    case MessageType::NotOffered: { NotOfferedMessage m; if (ParseNotOffered(data, size, m)) n = WriteNotOffered(m, again, sizeof(again)); break; }
    }
    // A message that parsed is canonical: written back, it is the same bytes.
    if (n != 0 && (n != size || memcmp(again, data, size) != 0)) abort();
    return 0;
}
