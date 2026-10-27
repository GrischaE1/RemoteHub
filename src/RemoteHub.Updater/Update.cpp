#include "Update.h"

#include <shlobj.h>

#include <algorithm>
#include <cwctype>
#include <memory>

namespace rhup
{
    namespace
    {
        bool IsHex(const std::wstring& value, size_t length)
        {
            return value.size() == length && std::all_of(value.begin(), value.end(), [](wchar_t c) { return std::iswxdigit(c) != 0; });
        }

        bool EndsWithMsi(const std::wstring& path)
        {
            return path.size() > 4 && CompareStringOrdinal(path.c_str() + path.size() - 4, 4, L".msi", 4, TRUE) == CSTR_EQUAL;
        }

        bool IsLocalMsiPath(const std::wstring& path)
        {
            if (path.size() < 7 || path.size() > MaxPath || !EndsWithMsi(path)) return false;
            // C:\… – not \\server\share, \\?\ or \\.\ (a device), and not relative to a drive's current folder.
            if (!((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) || path[1] != L':' || path[2] != L'\\') return false;
            for (size_t i = 0; i < path.size(); i++)
            {
                const wchar_t c = path[i];
                // A second colon would name a stream; quotes and wildcards have no place in a file's path.
                if (c < 0x20 || c == L'"' || c == L'*' || c == L'?' || c == L'<' || c == L'>' || c == L'|' || c == L'/' || (c == L':' && i != 1)) return false;
            }
            // No "." or ".." segment and no empty one (\\).
            size_t start = 3;
            while (start <= path.size())
            {
                const size_t end = std::min(path.find(L'\\', start), path.size());
                const std::wstring segment = path.substr(start, end - start);
                if (segment.empty() || segment == L"." || segment == L"..") return false;
                start = end + 1;
            }
            return true;
        }

        std::optional<DWORD> ParsePid(const std::wstring& value)
        {
            if (value.empty() || value.size() > 10 || !std::all_of(value.begin(), value.end(), [](wchar_t c) { return c >= L'0' && c <= L'9'; })) return std::nullopt;
            const unsigned long long pid = std::stoull(value);
            if (pid == 0 || pid > 0xFFFFFFFFull) return std::nullopt;
            return static_cast<DWORD>(pid);
        }

        std::wstring KnownFolder(REFKNOWNFOLDERID id)
        {
            PWSTR path = nullptr;
            const HRESULT hr = SHGetKnownFolderPath(id, KF_FLAG_DONT_VERIFY, nullptr, &path);
            const std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> owned(path, &CoTaskMemFree);
            return SUCCEEDED(hr) && path ? std::wstring(path) : std::wstring();
        }

        std::wstring SystemFolder(UINT (WINAPI *read)(LPWSTR, UINT))
        {
            wchar_t buffer[MAX_PATH + 1] = {};
            const UINT length = read(buffer, MAX_PATH + 1);
            return length > 0 && length <= MAX_PATH ? std::wstring(buffer, length) : std::wstring();
        }
    }

    std::optional<Request> Parse(int argc, const wchar_t* const* argv)
    {
        if (argc != 8) return std::nullopt;
        Request request;
        bool msi = false, sha = false, pid = false, id = false;
        for (int i = 0; i < argc; i += 2)
        {
            const std::wstring name = argv[i];
            const std::wstring value = argv[i + 1];
            if (name == L"--msi" && !msi && IsLocalMsiPath(value)) { request.Msi = value; msi = true; }
            else if (name == L"--sha256" && !sha && IsHex(value, 64)) { request.Sha256 = value; sha = true; }
            else if (name == L"--id" && !id && IsHex(value, 32)) { request.Id = value; id = true; }
            else if (name == L"--pid" && !pid)
            {
                const auto parsed = ParsePid(value);
                if (!parsed) return std::nullopt;
                request.WaitPid = *parsed;
                pid = true;
            }
            else return std::nullopt;
        }
        return request;
    }

    std::wstring CommandLine(const std::wstring& powershell, const std::wstring& script, const std::wstring& self, const Request& request)
    {
        return L"\"" + powershell + L"\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script +
               L"\" -Source \"" + request.Msi + L"\" -Sha256 " + request.Sha256 + L" -WaitPid " + std::to_wstring(request.WaitPid) +
               L" -Id " + request.Id + L" -Signer \"" + self + L"\"";
    }

    Variables CleanEnvironment()
    {
        const std::wstring windows = SystemFolder(&GetSystemWindowsDirectoryW);
        const std::wstring system = SystemFolder(&GetSystemDirectoryW);
        const std::wstring programFiles = KnownFolder(FOLDERID_ProgramFiles);
        const std::wstring programFilesX86 = KnownFolder(FOLDERID_ProgramFilesX86);
        const std::wstring common = KnownFolder(FOLDERID_ProgramFilesCommon);
        const std::wstring commonX86 = KnownFolder(FOLDERID_ProgramFilesCommonX86);
        const std::wstring programData = KnownFolder(FOLDERID_ProgramData);
        const std::wstring profile = KnownFolder(FOLDERID_Profile);
        const std::wstring roaming = KnownFolder(FOLDERID_RoamingAppData);
        const std::wstring local = KnownFolder(FOLDERID_LocalAppData);
        if (windows.size() < 3 || system.empty() || programFiles.empty() || programFilesX86.empty() || common.empty() ||
            commonX86.empty() || programData.empty() || profile.empty() || roaming.empty() || local.empty())
            return {};
        SYSTEM_INFO info = {};
        GetNativeSystemInfo(&info);
        return {
            {L"ALLUSERSPROFILE", programData},
            {L"APPDATA", roaming},
            {L"CommonProgramFiles", common},
            {L"CommonProgramFiles(x86)", commonX86},
            {L"CommonProgramW6432", common},
            {L"ComSpec", system + L"\\cmd.exe"},
            {L"LOCALAPPDATA", local},
            {L"NUMBER_OF_PROCESSORS", std::to_wstring(info.dwNumberOfProcessors)},
            {L"PATH", system + L";" + windows + L";" + system + L"\\Wbem;" + system + L"\\WindowsPowerShell\\v1.0\\"},
            {L"PATHEXT", L".COM;.EXE;.BAT;.CMD"},
            {L"PROCESSOR_ARCHITECTURE", info.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_ARM64 ? L"ARM64" : L"AMD64"},
            {L"ProgramData", programData},
            {L"ProgramFiles", programFiles},
            {L"ProgramFiles(x86)", programFilesX86},
            {L"ProgramW6432", programFiles},
            {L"PSModulePath", programFiles + L"\\WindowsPowerShell\\Modules;" + system + L"\\WindowsPowerShell\\v1.0\\Modules"},
            {L"SystemDrive", windows.substr(0, 2)},
            {L"SystemRoot", windows},
            {L"TEMP", windows + L"\\Temp"},
            {L"TMP", windows + L"\\Temp"},
            {L"USERPROFILE", profile},
            {L"windir", windows},
        };
    }

    std::wstring EnvironmentBlock(Variables variables)
    {
        std::sort(variables.begin(), variables.end(), [](const auto& a, const auto& b) {
            return CompareStringOrdinal(a.first.c_str(), static_cast<int>(a.first.size()), b.first.c_str(), static_cast<int>(b.first.size()), TRUE) == CSTR_LESS_THAN;
        });
        std::wstring block;
        for (const auto& [name, value] : variables)
        {
            block += name;
            block += L'=';
            block += value;
            block += L'\0';
        }
        block += L'\0';
        return block;
    }
}
