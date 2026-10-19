// A short trace of what LogonUI asks of the provider and what the pipe thread does (credential-provider-design.md §8.6).
// It exists for the cases the service can't see through the pipe – lab C-AUTO-20, where Windows 11 never delivered
// ReportResult for a locked or disabled account – so the lab can tell which calls LogonUI made, on which instance.
//
// Lines hold call names, HRESULT and NTSTATUS codes, counts and instance numbers only: never account names, SIDs,
// passwords or anything else from a ticket.
//
// Trace() never touches a file on the caller's thread (LogonUI's threads must not wait for a disk): it queues the line
// in a fixed buffer, and a thread-pool callback appends what is queued to
// %ProgramData%\RemoteHub\Agent\logs\provider-yyyyMMdd.log. The provider never creates that folder (the agent does, with
// its SYSTEM and Administrators only ACL); without it, or when the queue is full, lines are dropped.
#pragma once

#include <windows.h>

#include <sal.h>

namespace rhcp
{
    // Lines waiting for the writer at most; further ones are counted and dropped.
    constexpr size_t MaxQueuedTraceLines = 256;
    // Characters per line at most, including the time stamp; longer text is cut.
    constexpr size_t MaxTraceLineChars = 240;

    void Trace(_In_z_ _Printf_format_string_ const char* format, ...) noexcept;

    // Writes what is queued now, on this thread. Tests only; the provider leaves it to the thread pool.
    void FlushTrace() noexcept;

    // Tests only: the folder the trace goes to instead (ending with a backslash), and lines are then written only by
    // FlushTrace, so a test sees exactly what it queued. Null goes back to the agent's folder and the thread pool.
    void SetTraceFolderForTests(_In_opt_z_ const wchar_t* folder) noexcept;
}
