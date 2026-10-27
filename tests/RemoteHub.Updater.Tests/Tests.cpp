// Unit tests for RemoteHubUpdate.exe's checks (no test framework: plain checks, exit code = number of failures): what it
// accepts on its command line, what it passes to Windows PowerShell, and the environment it gives it.
#include "../../src/RemoteHub.Updater/Update.h"

#include <cstdio>
#include <cwchar>
#include <string>
#include <vector>

using namespace rhup;

namespace
{
    int g_failures = 0;
    const char* g_test = "";

#define CHECK(condition) \
    do { if (!(condition)) { std::printf("FAIL %s: %s (line %d)\n", g_test, #condition, __LINE__); g_failures++; } } while (0)

    const std::wstring Sha = L"AB" + std::wstring(62, L'0');
    const std::wstring Id = L"0123456789abcdef0123456789abcdef";
    const std::wstring Msi = L"C:\\Users\\O'Brien\\AppData\\Local\\Temp\\RemoteHub\\Updates\\RemoteHub-0.9.1-x64.msi";

    std::optional<Request> ParseList(const std::vector<std::wstring>& args)
    {
        std::vector<const wchar_t*> argv;
        for (const auto& a : args) argv.push_back(a.c_str());
        return Parse(static_cast<int>(argv.size()), argv.data());
    }

    std::optional<Request> WithMsi(const std::wstring& msi) { return ParseList({L"--msi", msi, L"--sha256", Sha, L"--pid", L"4242", L"--id", Id}); }

    void ParsesItsFourValuesInAnyOrder()
    {
        g_test = "ParsesItsFourValuesInAnyOrder";
        const auto r = WithMsi(Msi);
        CHECK(r.has_value());
        if (r)
        {
            CHECK(r->Msi == Msi);
            CHECK(r->Sha256 == Sha);
            CHECK(r->WaitPid == 4242);
            CHECK(r->Id == Id);
        }
        const auto reordered = ParseList({L"--id", Id, L"--pid", L"4294967295", L"--msi", L"d:\\x.MSI", L"--sha256", std::wstring(64, L'f')});
        CHECK(reordered.has_value() && reordered->WaitPid == 4294967295u && reordered->Msi == L"d:\\x.MSI");
    }

    void RefusesAnythingElse()
    {
        g_test = "RefusesAnythingElse";
        // the wrong number of arguments, a value missing, a switch twice or unknown
        CHECK(!ParseList({}).has_value());
        CHECK(!ParseList({L"--msi", Msi, L"--sha256", Sha, L"--pid", L"4242"}).has_value());
        CHECK(!ParseList({L"--msi", Msi, L"--sha256", Sha, L"--pid", L"4242", L"--id", Id, L"--publisher", L"CN=x"}).has_value());
        CHECK(!ParseList({L"--msi", Msi, L"--msi", Msi, L"--pid", L"4242", L"--id", Id}).has_value());
        CHECK(!ParseList({L"--msi", Msi, L"--sha256", Sha, L"--pid", L"4242", L"--publisher", L"CN=x"}).has_value());
        CHECK(!ParseList({L"-msi", Msi, L"--sha256", Sha, L"--pid", L"4242", L"--id", Id}).has_value());
        // checksum, id and process id
        CHECK(!ParseList({L"--msi", Msi, L"--sha256", Sha.substr(1), L"--pid", L"4242", L"--id", Id}).has_value());
        CHECK(!ParseList({L"--msi", Msi, L"--sha256", Sha + L"0", L"--pid", L"4242", L"--id", Id}).has_value());
        CHECK(!ParseList({L"--msi", Msi, L"--sha256", L"G" + Sha.substr(1), L"--pid", L"4242", L"--id", Id}).has_value());
        CHECK(!ParseList({L"--msi", Msi, L"--sha256", Sha, L"--pid", L"4242", L"--id", Id + L"0"}).has_value());
        CHECK(!ParseList({L"--msi", Msi, L"--sha256", Sha, L"--pid", L"4242", L"--id", L"../" + Id.substr(3)}).has_value());
        for (const wchar_t* pid : {L"0", L"-1", L"4294967296", L"12345678901", L"42a", L"", L" 42", L"0x10"})
            CHECK(!ParseList({L"--msi", Msi, L"--sha256", Sha, L"--pid", pid, L"--id", Id}).has_value());
        // the package's path: local, absolute, a .msi file, nothing that names something else
        for (const wchar_t* path : {
                 L"RemoteHub.msi", L"C:RemoteHub.msi", L"\\Temp\\RemoteHub.msi", L"\\\\server\\share\\RemoteHub.msi",
                 L"\\\\?\\C:\\Temp\\RemoteHub.msi", L"\\\\.\\C:\\Temp\\RemoteHub.msi", L"C:\\Temp\\RemoteHub.exe",
                 L"C:\\Temp\\RemoteHub.msi.exe", L"C:\\Temp\\a:b.msi", L"C:\\Temp\\..\\Windows\\x.msi", L"C:\\Temp\\.\\x.msi",
                 L"C:\\Temp\\\\x.msi", L"C:/Temp/x.msi", L"C:\\Temp\\*.msi", L"C:\\Temp\\x?.msi", L"C:\\Temp\\\"x.msi",
                 L"C:\\Temp\\x\t.msi", L"1:\\Temp\\x.msi", L".msi"})
            CHECK(!WithMsi(path).has_value());
        // "C:\" + name + ".msi": MaxPath characters at most
        CHECK(!WithMsi(L"C:\\" + std::wstring(MaxPath - 6, L'a') + L".msi").has_value());
        CHECK(WithMsi(L"C:\\" + std::wstring(MaxPath - 7, L'a') + L".msi").has_value());
    }

    void PassesTheValuesToItsScriptOnly()
    {
        g_test = "PassesTheValuesToItsScriptOnly";
        const auto r = WithMsi(Msi);
        CHECK(r.has_value());
        if (!r) return;
        const std::wstring line = CommandLine(L"C:\\Windows\\system32\\WindowsPowerShell\\v1.0\\powershell.exe",
            L"C:\\Program Files\\RemoteHub\\InstallUpdate.ps1", L"C:\\Program Files\\RemoteHub\\RemoteHubUpdate.exe", *r);
        CHECK(line == L"\"C:\\Windows\\system32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass "
                      L"-WindowStyle Hidden -File \"C:\\Program Files\\RemoteHub\\InstallUpdate.ps1\" -Source \"" + Msi + L"\" -Sha256 " + Sha +
                      L" -WaitPid 4242 -Id " + Id + L" -Signer \"C:\\Program Files\\RemoteHub\\RemoteHubUpdate.exe\"");
    }

    std::wstring Value(const Variables& variables, const std::wstring& name)
    {
        for (const auto& [n, v] : variables)
            if (_wcsicmp(n.c_str(), name.c_str()) == 0) return v;
        return L"<missing>";
    }

    void LeavesOutWhatAUserCanSet()
    {
        g_test = "LeavesOutWhatAUserCanSet";
        // What HKCU\Environment could hand an elevated process: a profiler, another module path, startup hooks.
        SetEnvironmentVariableW(L"COR_ENABLE_PROFILING", L"1");
        SetEnvironmentVariableW(L"COR_PROFILER_PATH", L"C:\\Users\\Public\\p.dll");
        SetEnvironmentVariableW(L"CORECLR_ENABLE_PROFILING", L"1");
        SetEnvironmentVariableW(L"DOTNET_STARTUP_HOOKS", L"C:\\Users\\Public\\h.dll");
        SetEnvironmentVariableW(L"PSModulePath", L"C:\\Users\\Public\\Modules");
        SetEnvironmentVariableW(L"PATH", L"C:\\Users\\Public;C:\\Windows\\system32");
        SetEnvironmentVariableW(L"windir", L"C:\\Users\\Public");
        const Variables variables = CleanEnvironment();
        CHECK(!variables.empty());
        const std::vector<std::wstring> expected = {
            L"ALLUSERSPROFILE", L"APPDATA", L"CommonProgramFiles", L"CommonProgramFiles(x86)", L"CommonProgramW6432", L"ComSpec",
            L"LOCALAPPDATA", L"NUMBER_OF_PROCESSORS", L"PATH", L"PATHEXT", L"PROCESSOR_ARCHITECTURE", L"ProgramData", L"ProgramFiles",
            L"ProgramFiles(x86)", L"ProgramW6432", L"PSModulePath", L"SystemDrive", L"SystemRoot", L"TEMP", L"TMP", L"USERPROFILE", L"windir"};
        CHECK(variables.size() == expected.size());
        for (const auto& name : expected) CHECK(Value(variables, name) != L"<missing>");
        for (const auto& [name, value] : variables) CHECK(value.find(L"Public") == std::wstring::npos);

        wchar_t windows[MAX_PATH + 1] = {};
        GetSystemWindowsDirectoryW(windows, MAX_PATH + 1);
        wchar_t system[MAX_PATH + 1] = {};
        GetSystemDirectoryW(system, MAX_PATH + 1);
        CHECK(Value(variables, L"windir") == windows);
        CHECK(Value(variables, L"PATH").rfind(std::wstring(system) + L";", 0) == 0);
        CHECK(Value(variables, L"PSModulePath") == Value(variables, L"ProgramFiles") + L"\\WindowsPowerShell\\Modules;" + system + L"\\WindowsPowerShell\\v1.0\\Modules");
        CHECK(Value(variables, L"TEMP") == std::wstring(windows) + L"\\Temp");
    }

    void BuildsASortedBlock()
    {
        g_test = "BuildsASortedBlock";
        const std::wstring block = EnvironmentBlock({{L"windir", L"C:\\Windows"}, {L"PATH", L"C:\\Windows\\system32"}, {L"ALLUSERSPROFILE", L"C:\\ProgramData"}});
        const std::wstring expected = std::wstring(L"ALLUSERSPROFILE=C:\\ProgramData") + L'\0' + L"PATH=C:\\Windows\\system32" + L'\0' +
                                      L"windir=C:\\Windows" + L'\0' + L'\0';
        CHECK(block == expected);
    }
}

int main()
{
    ParsesItsFourValuesInAnyOrder();
    RefusesAnythingElse();
    PassesTheValuesToItsScriptOnly();
    LeavesOutWhatAUserCanSet();
    BuildsASortedBlock();
    std::printf(g_failures == 0 ? "All updater tests passed.\n" : "%d check(s) failed.\n", g_failures);
    return g_failures;
}
