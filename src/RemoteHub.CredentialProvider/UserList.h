// Whether the ticket's account is among the users LogonUI lists (credential-provider-design.md §8.2, lab C-AUTO-20).
//
// Windows 11 leaves locked-out and disabled accounts out of LogonUI's user list and never signs in a tile for them –
// neither under the account's SID nor under "Other user" (both tried in the lab): no GetSerialization, so the service
// waited 50 s for an answer that couldn't come. Not being listed isn't proof on its own: Windows Server signs in the SID
// tile of an account it doesn't list (C-PROV-03). So the tile is always offered, and only when a list is there, lacks the
// account, and LogonUI hasn't taken the ticket within a short grace does the provider drop it and tell the service
// (NotOffered, Wire.h; Channel::NotListed), which finds the reason itself. Without a list nothing changes.
#pragma once

#include <windows.h>

#include <string>
#include <vector>

namespace rhcp
{
    enum class UserListed { Yes, No, NoList };

    // listed: the SIDs LogonUI handed over (SetUserArray), or null when it handed over none. A ticket without a SID
    // can't be looked up and counts as NoList (its tile goes under "Other user", as before).
    inline UserListed FindUser(const std::wstring& sid, const std::vector<std::wstring>* listed)
    {
        if (listed == nullptr || sid.empty()) return UserListed::NoList;
        for (const auto& user : *listed)
            if (CompareStringOrdinal(user.c_str(), -1, sid.c_str(), -1, TRUE) == CSTR_EQUAL) return UserListed::Yes;
        return UserListed::No;
    }

    // False only when LogonUI's list is known and lacks the account: then the grace for its ticket runs.
    inline bool SignInScreenTakesTicket(UserListed listed) { return listed != UserListed::No; }

    inline const char* ToText(UserListed listed)
    {
        return listed == UserListed::Yes ? "yes" : listed == UserListed::No ? "no, NotOffered unless taken within the grace" : "no user list";
    }
}
