#pragma once
// RemoteHubUpdate.exe – the program Windows asks administrator approval for when RemoteHub installs an update, so the
// prompt names "RemoteHub Update" and its publisher instead of Windows PowerShell. It starts RemoteHub's update step
// (InstallUpdate.ps1, next to it) in Windows PowerShell and ends; the step copies, checks and installs the MSI
// (docs/security-test-lab-design.md, SEC-UPD-02 and SEC-UPD-04).
//
// It is signed by RemoteHub's publisher and runs elevated, so it must not be usable for anything else: it takes four
// values, checks each, and runs only that script. The publisher the step checks the MSI against comes from this
// program's own signature, never from its command line. Windows PowerShell gets a fresh environment built from the
// system's folders: nothing a user can set (HKCU\Environment) reaches the elevated process.
#include <windows.h>

#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace rhup
{
    // What RemoteHub asks for: the downloaded MSI, its SHA-256 (as RemoteHub checked it), RemoteHub's process id (the
    // step waits for it to exit) and the attempt's id (it names the outcome file).
    struct Request
    {
        std::wstring Msi;
        std::wstring Sha256;
        DWORD WaitPid = 0;
        std::wstring Id;
    };

    // The arguments after the program's name: --msi <path> --sha256 <64 hex> --pid <n> --id <32 hex>, each exactly
    // once, in any order. Anything else – another switch, a value missing or malformed – gives nothing.
    // The path must be a local absolute path to a .msi file: drive letter, no device or UNC prefix, no stream, no
    // relative segments, no wildcards or quotes, at most MaxPath characters.
    std::optional<Request> Parse(int argc, const wchar_t* const* argv);

    constexpr size_t MaxPath = 1024;

    // Windows PowerShell's command line: -File <script> with the checked values and -Signer <this program>. None of the
    // values can contain a quote, so each is passed quoted as it is.
    std::wstring CommandLine(const std::wstring& powershell, const std::wstring& script, const std::wstring& self, const Request& request);

    using Variables = std::vector<std::pair<std::wstring, std::wstring>>;

    // The variables Windows PowerShell gets: Windows' and the program folders, a PATH and PSModulePath of system folders
    // only, TEMP in Windows' own temp folder, and the user's profile folders as the system reports them. Empty when a
    // folder can't be read.
    Variables CleanEnvironment();

    // name=value\0 … \0 sorted by name without regard to case, as CreateProcess expects with CREATE_UNICODE_ENVIRONMENT.
    std::wstring EnvironmentBlock(Variables variables);
}
