// RemoteHubUpdate.exe (Update.h): starts RemoteHub's update step and ends – the install it starts replaces this file.
// Exit code: the step's process id, which RemoteHub's restart helper waits for; 0 when nothing was started.
#include "Update.h"

#include <shellapi.h>

#include <memory>

namespace
{
    std::wstring ModulePath()
    {
        std::wstring path(MAX_PATH, L'\0');
        for (;;)
        {
            const DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
            if (length == 0) return {};
            if (length < path.size()) return path.substr(0, length);
            if (path.size() >= 32768) return {};
            path.resize(path.size() * 2);
        }
    }

    bool IsPlainFile(const std::wstring& path)
    {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) == 0;
    }
}

int WINAPI wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ PWSTR, _In_ int)
{
    // DLLs only from System32, never from the current or this program's folder.
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);

    int argc = 0;
    const std::unique_ptr<LPWSTR, decltype(&LocalFree)> argv(CommandLineToArgvW(GetCommandLineW(), &argc), &LocalFree);
    if (!argv || argc < 1) return 0;
    const auto request = rhup::Parse(argc - 1, argv.get() + 1);
    if (!request) return 0;

    const std::wstring self = ModulePath();
    const size_t slash = self.find_last_of(L'\\');
    if (slash == std::wstring::npos) return 0;
    const std::wstring script = self.substr(0, slash + 1) + L"InstallUpdate.ps1";
    if (!IsPlainFile(script)) return 0;

    wchar_t system[MAX_PATH + 1] = {};
    const UINT systemLength = GetSystemDirectoryW(system, MAX_PATH + 1);
    if (systemLength == 0 || systemLength > MAX_PATH) return 0;
    const std::wstring powershell = std::wstring(system, systemLength) + L"\\WindowsPowerShell\\v1.0\\powershell.exe";

    auto variables = rhup::CleanEnvironment();
    if (variables.empty()) return 0;
    std::wstring environment = rhup::EnvironmentBlock(std::move(variables));
    std::wstring commandLine = rhup::CommandLine(powershell, script, self, *request);

    STARTUPINFOW startup = {};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION process = {};
    if (!CreateProcessW(powershell.c_str(), commandLine.data(), nullptr, nullptr, FALSE, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                        environment.data(), system, &startup, &process))
        return 0;
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return static_cast<int>(process.dwProcessId);
}
