#include "Trace.h"

#include <cstdarg>
#include <cstdio>
#include <string>

namespace rhcp
{
    namespace
    {
        // The queue: fixed lines, no allocation while the lock is held.
        SRWLOCK g_queueLock = SRWLOCK_INIT;
        char g_lines[MaxQueuedTraceLines][MaxTraceLineChars + 3] = {}; // the text, "\r\n" and the terminator
        size_t g_count = 0;
        size_t g_dropped = 0;
        bool g_scheduled = false;

        // One writer at a time, so lines reach the file in the order they were queued.
        SRWLOCK g_fileLock = SRWLOCK_INIT;
        wchar_t g_testFolder[MAX_PATH] = {};
        bool g_manual = false; // tests: only FlushTrace writes

        // The agent's log folder; false if it isn't there (or is a reparse point – the provider runs as SYSTEM and
        // must not follow a link out of the folder the agent secured).
        bool Folder(wchar_t (&folder)[MAX_PATH])
        {
            if (g_testFolder[0] != L'\0')
            {
                if (wcsncpy_s(folder, g_testFolder, _TRUNCATE) != 0) return false;
            }
            else
            {
                const DWORD n = ExpandEnvironmentStringsW(L"%ProgramData%\\RemoteHub\\Agent\\logs\\", folder, MAX_PATH);
                if (n == 0 || n > MAX_PATH || folder[0] == L'%') return false;
            }
            const DWORD attributes = GetFileAttributesW(folder);
            return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 &&
                (attributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0;
        }

        void WriteQueued()
        {
            // Reserved before the queue's lock, so appending under it can't allocate (or throw).
            std::string text;
            try { text.reserve(MaxQueuedTraceLines * (MaxTraceLineChars + 2) + 64); }
            catch (...)
            {
                // Out of memory: the lines stay queued, and the next one schedules another try.
                AcquireSRWLockExclusive(&g_queueLock);
                g_scheduled = false;
                ReleaseSRWLockExclusive(&g_queueLock);
                return;
            }

            AcquireSRWLockExclusive(&g_fileLock);
            AcquireSRWLockExclusive(&g_queueLock);
            for (size_t i = 0; i < g_count; i++) text.append(g_lines[i]);
            const size_t dropped = g_dropped;
            g_count = g_dropped = 0;
            g_scheduled = false; // lines queued from now on schedule another write
            ReleaseSRWLockExclusive(&g_queueLock);

            if (dropped != 0)
            {
                char note[64];
                const int n = _snprintf_s(note, _TRUNCATE, "(%zu trace lines dropped: the queue was full)\r\n", dropped);
                if (n > 0) text.append(note, static_cast<size_t>(n));
            }

            wchar_t folder[MAX_PATH];
            if (!text.empty() && Folder(folder))
            {
                SYSTEMTIME now;
                GetLocalTime(&now);
                wchar_t path[MAX_PATH];
                if (_snwprintf_s(path, _TRUNCATE, L"%lsprovider-%04u%02u%02u.log", folder, now.wYear, now.wMonth, now.wDay) > 0)
                {
                    const HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
                    if (file != INVALID_HANDLE_VALUE)
                    {
                        DWORD written = 0;
                        WriteFile(file, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
                        CloseHandle(file);
                    }
                }
            }
            ReleaseSRWLockExclusive(&g_fileLock);
        }

        VOID CALLBACK WriteCallback(PTP_CALLBACK_INSTANCE instance, PVOID module)
        {
            // The reference taken in Schedule keeps this DLL loaded until the callback has returned.
            FreeLibraryWhenCallbackReturns(instance, static_cast<HMODULE>(module));
            try { WriteQueued(); }
            catch (...) { /* tracing must never disturb LogonUI */ }
        }

        bool Schedule()
        {
            HMODULE self = nullptr;
            if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&WriteCallback), &self))
                return false;
            if (TrySubmitThreadpoolCallback(WriteCallback, self, nullptr)) return true;
            FreeLibrary(self);
            return false;
        }
    }

    void Trace(const char* format, ...) noexcept
    {
        char text[MaxTraceLineChars];
        va_list args;
        va_start(args, format);
        const int length = _vsnprintf_s(text, _TRUNCATE, format, args);
        va_end(args);
        if (length < 0 && text[0] == '\0') return; // -1 with text is only a cut line

        SYSTEMTIME now;
        GetLocalTime(&now);
        char line[MaxTraceLineChars + 3];
        // Process and thread: LogonUI may hold several providers, and its callbacks and the pipe threads interleave.
        int n = _snprintf_s(line, MaxTraceLineChars + 1, _TRUNCATE, "%04u-%02u-%02u %02u:%02u:%02u.%03u  [%lu/%lu] %s",
            now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds,
            GetCurrentProcessId(), GetCurrentThreadId(), text);
        if (n < 0) n = static_cast<int>(strnlen(line, MaxTraceLineChars));
        line[n] = '\r';
        line[n + 1] = '\n';
        line[n + 2] = '\0';

        bool schedule = false;
        AcquireSRWLockExclusive(&g_queueLock);
        if (g_count < MaxQueuedTraceLines) memcpy(g_lines[g_count++], line, static_cast<size_t>(n) + 3);
        else g_dropped++;
        if (!g_scheduled && !g_manual) g_scheduled = schedule = true;
        ReleaseSRWLockExclusive(&g_queueLock);

        if (schedule && !Schedule())
        {
            AcquireSRWLockExclusive(&g_queueLock);
            g_scheduled = false; // the next line tries again
            ReleaseSRWLockExclusive(&g_queueLock);
        }
    }

    void FlushTrace() noexcept
    {
        try { WriteQueued(); }
        catch (...) {}
    }

    void SetTraceFolderForTests(const wchar_t* folder) noexcept
    {
        AcquireSRWLockExclusive(&g_fileLock);
        if (folder == nullptr || wcsncpy_s(g_testFolder, folder, _TRUNCATE) != 0) g_testFolder[0] = L'\0';
        AcquireSRWLockExclusive(&g_queueLock);
        g_manual = g_testFolder[0] != L'\0';
        ReleaseSRWLockExclusive(&g_queueLock);
        ReleaseSRWLockExclusive(&g_fileLock);
    }
}
