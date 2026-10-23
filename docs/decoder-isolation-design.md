# RemoteHub – Decoding a device's data outside the console

Status: phase 0 built, phase 1 (spike) done – 2026-10-23. Phases 2–4 (the decoder process) are next; see §10 for the
phases and §11 for what the spike found. The decisions in §9 are made.
Owner: Grischa Ernst

## 1. Goal

A device that has been compromised must not be able to take over the administrator's PC through RemoteHub. Today the
console decodes two kinds of data the device chooses in its own process: the agent desktop's video and the images it
puts on the clipboard. Both go through native Windows decoders, where memory-corruption bugs are found every year. A
bug there runs the attacker's code as the administrator, inside the process that holds the unlocked credential vault
(threats T9.1 and T9.2 in [security-test-lab-design.md](security-test-lab-design.md)).

The goal: the native decoders run in a separate process that can do nothing useful when taken over. It has no network,
no access to the user's files, the clipboard or other windows, and no keys. The console only gets pixels back, and
checks their size before it touches them.

Not a goal: protecting against a compromised *console* (it already controls every device), or managed parsing (JSON,
the wire framing, file names). That code is memory-safe and stays where it is.

## 2. What the console decodes today

| Data | Where | Decoder | Risk |
|---|---|---|---|
| Video | `AgentDesktopSession.cs` `FrameDecoder` (thread "Desktop decoder") → `VideoCodec.cs` `H264Decoder` | Microsoft's software H.264 MFT (`CLSID_MSH264DecoderMFT`), driven through COM vtables; NV12 → BGRA in managed code | **High** – native parser of a stream the device writes, every frame |
| Clipboard image, pushed | `AgentDesktopSession.FinishReceiving` | Was: WPF `BitmapDecoder.Create` (WIC; any format WIC recognises) on the UI thread. Since phase 0: `DeviceImage` on the receiving thread (§3.6) | **High** – native parser; phase 0 narrows it to three decoders after a size check |
| Clipboard image, offered | `ClipboardOffer.Render` | Was: `BitmapDecoder` + `FormatConvertedBitmap` on the offer's STA thread (`PngToDib`). Since phase 0: `DeviceImage` | **High** – same |
| Clipboard BMP | `ClipboardOffer` | Was: not parsed – the bytes went on the clipboard as CF_DIB. Since phase 0: decoded and rebuilt from pixels | Was medium – parsed later by whatever program the user pastes into |
| Cursor shape | `OnMessage` → `CursorFiles.Create` | BGRA pixels (checked, ≤ 1024²); RemoteHub builds the `.cur` itself | Low – RemoteHub's own header around checked pixels |
| JSON messages, framing, transfer metadata | `DesktopProtocol`, `ClipboardTransfer` | System.Text.Json, managed readers with length checks | Low – managed |

There is no hardware decoding (no DXVA device manager), so the video path is CPU-only today, ~30 fps, at most
4096×2304 per picture. Nothing in the console runs in a separate process yet.

## 3. Design

### 3.1 The decoder process

`RemoteHubDecoder.exe`: a small .NET console program in RemoteHub's install folder, signed like `RemoteHub.exe`. It
contains the H.264 decoder (`VideoCodec.cs`, linked as today), the NV12 → BGRA conversion and an image decoder (WIC). It
has no other code from RemoteHub, references no settings or database code, and never opens a file or a socket.

**One process per agent desktop session.** A crash or a take-over is confined to one device's session. Images from a
session's clipboard go to that session's decoder. The cost is ~25–40 MB of memory per open desktop session.

### 3.2 What it may do

RemoteHub starts it with:

- **An AppContainer with no capabilities** (`SECURITY_CAPABILITIES` in `CreateProcess`, a profile `RemoteHub.Decoder`
  created once per user with `CreateAppContainerProfile`). It has no network, no access to the user's profile or files,
  no registry writes outside its own key, and no clipboard. Media Foundation's software decoders and WIC work in an
  AppContainer, because Store apps use them.
- **A job object**: killed when RemoteHub closes the job (`KILL_ON_JOB_CLOSE`), one active process (no children), a
  memory limit of 1 GB, and UI limits (no desktop switching, no clipboard read or write, no access to other processes'
  windows, no system parameters, no global atoms).
- **An explicit handle list** (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`): it inherits exactly its two pipe ends and the
  shared-memory section, nothing else. This follows the agent's `ChildProcess.Start` pattern.
- No arguments that carry anything from the device.

If the AppContainer can't be created on a PC, the fallback is a restricted token at low integrity (see §9, decision 2).
It's weaker – low integrity can still read most files – but it still keeps out of the vault, the user's writable files
and other processes.

### 3.3 Talking to it

- **Control pipe** (an anonymous pipe pair, inherited): length-prefixed messages, using the existing
  `WriteFramedAsync` / `ReadFramedAsync` (4-byte length, at most 16 MB). Requests are `Video(frameId, flags, bitstream)`,
  `Flush`, `Image(id, bytes)` and `Exit`. Answers are `Frame(frameId, buffer, width, height)`, `Image(id, buffer,
  width, height)`, `Skipped(frameId, reason)` and `Error(reason)`.
- **Pixels** through a shared-memory section (`CreateFileMapping`, made by RemoteHub, inherited by the decoder). It holds
  two buffers of 4096 × 2304 × 4 bytes (≈ 75 MB in total, committed only as used), so the decoder fills one while the
  console shows the other. Images larger than a buffer are refused in the decoder (§3.5).
- **The console doesn't trust the answers.** A taken-over decoder can say anything. Before reading, the console checks
  that the buffer index is 0 or 1, that `width × height × 4` fits the buffer, that the size is within
  `MaxVideoWidth/Height`, and that `frameId` is one it sent. Anything else ends the decoder (§3.4). The worst a decoder
  can then do is show wrong pixels in that session's tab.
- Nothing is named (no named pipes, no named sections), so no other process can sit in the middle or pretend to be the
  decoder.

**Cost per frame:** the console already copies every frame into its back buffer before `WritePixels`. With the section,
`WritePixels` reads from the mapped view instead, so there's no extra copy. A pipe round trip adds ~0.1 ms. The decoding
itself is unchanged.

### 3.4 When it crashes or misbehaves

- Pipe closed, exit code, a timeout (no answer to a frame within 2 s) or an answer that fails §3.3's checks: RemoteHub
  ends the process (job), writes a DiagLog line with the reason (no device names), starts a new decoder and asks the
  agent for a key frame. The session continues; the picture pauses for a moment.
- More than 3 restarts in a minute: the session ends with "The picture can't be shown – the device keeps sending data
  RemoteHub can't decode. Disconnect and check the device." Restarting endlessly would hand an attacker unlimited tries.
- The audit log records it ("Desktop decoder stopped"), so an attack leaves a trace.

### 3.5 Clipboard images

- A pushed or offered image goes to the session's decoder as bytes. It is decoded only as PNG, BMP or JPEG (WIC with
  that decoder's CLSID, not WIC's auto-detection, which would try every installed codec). The image must be at most
  8192 × 8192, and the frame size is read before decoding. The decoder answers with BGRA pixels in the section.
- RemoteHub then builds the clipboard data itself from the pixels: `BitmapSource.Create` for the WPF clipboard and a DIB
  header it writes for CF_DIB. Device BMP bytes are no longer put on the clipboard unread. Every image reaches the
  clipboard re-encoded from pixels, so a crafted file can't reach the program the user pastes into either.
- Files from the device are unchanged: they are written to `%TEMP%\RemoteHub\Clipboard\…` and never opened by
  RemoteHub.

### 3.6 Small fixes that don't need the process (built, phase 0)

- The console checks a video message before the decoder sees it (`AgentDesktopSession.CheckVideo`): a picture size
  outside 1 × 1 – `MaxVideoWidth` × `MaxVideoHeight`, a message shorter than its header or one with no picture data
  is a bad frame – logged, the decoder flushed, a key frame asked for – instead of ending the session (a short message
  used to end it through an exception outside the decoder's handler). Before, a header of 65535 × 65535 reached the
  conversion, where `w × h × 4` overflows `int`.
- The decoder's output is only converted when a row fits its stride (`VideoCodec.cs`), and the console takes no frame
  larger than the protocol's limit.
- Clipboard images are decoded as §3.5 describes, still in RemoteHub's process until phase 3
  (`Services/DeviceImage.cs`): PNG, BMP and JPEG only, each with its own WIC decoder (`PngBitmapDecoder`,
  `BmpBitmapDecoder`, `JpegBitmapDecoder`); the size read from the header by RemoteHub's own code first (at most
  8192 × 8192, PNG IHDR, both BMP headers, the JPEG frame header), and the decoded frame must have that size; colour
  profiles ignored. What reaches this PC's clipboard – the pushed image, and both formats of an offered one (PNG and
  CF_DIB) – is built from the pixels; the device's bytes, BMP included, never are. An image that doesn't pass shows
  "The image copied on the device can't be taken – only PNG, BMP and JPEG up to 8192 × 8192 pixels are."

## 4. What changes for the user

Nothing they can see while it works. On a decoder failure, the picture pauses for a moment, or the message in §3.4
appears. The install folder gets one more file, and Task Manager shows one `RemoteHubDecoder.exe` per open desktop
session.

## 5. Policy

None. Isolation is always on, like the other protections. An administrator gains nothing from turning it off, and an
"off" switch would be a way around it.

## 6. Tests

- **Unit:** the answer checks in §3.3 (bad index, oversized, unknown frame), the restart limit, and the DIB built from
  pixels. A fake decoder program crashes, hangs and answers garbage to test the console's side.
- **Integration (CI, no VM):** the real decoder process decodes a recorded H.264 clip and a PNG, BMP and JPEG, with the
  same pixels as decoding in-process. A probe mode of the decoder, run in its sandbox, tries to connect a socket,
  open a file in the user's profile, read the clipboard and start a process – each must fail.
- **Fuzz (SEC-DEC-01/02):** truncated, oversized and bit-flipped H.264 and images fed to the decoder process. Pass when
  every input ends in a frame, a "skipped", or a contained crash and a restart, never a hang of the console.
- **Lab:**
  - SEC-DEC-01/02 through the harness's raw desktop client, with a device that sends a malformed stream and malformed
    clipboard images. RemoteHub stays responsive, restarts the decoder, ends the session after the limit, and audits it.
  - The D-VID cases for frame rate and latency stay within today's results.
- **View test (STA):** the session tab after a decoder restart and after the limit (message, buttons).

## 7. Effort

| Step | Days |
|---|---|
| Spike: framework-dependent .NET in an AppContainer (reading `C:\Program Files\dotnet`), Media Foundation and WIC there, handle list and section | 1–2 |
| Decoder program, protocol, console side (process, checks, restart), clipboard images through it | 4–5 |
| Tests: unit, integration, sandbox probe, fuzz harness | 2–3 |
| Lab cases, docs, MSI (one more file, signed) | 1–2 |
| **Total** | **8–12** |

The spike decides whether the AppContainer works as planned. If .NET can't start in it without extra file access,
§9 decision 2 applies.

## 8. Alternatives considered

- **Native decoder in C++ instead of .NET:** a smaller process with less code in the sandbox, but a second native
  codebase to keep safe. It would only be worth it if the .NET runtime can't start in the AppContainer.
- **Hardware decoding (DXVA) in the sandbox:** possible later (AppContainers can use D3D11). It isn't needed today, and
  the shared section would become a shared texture.
- **Keep decoding in-process, add a restricted thread or CFG/ACG:** this doesn't confine anything; the decoder runs
  with the console's rights.

## 9. Decisions (made 2026-10-23)

1. **One decoder per session.** Per session confines a take-over to one device. The spike measured 21 MB private
   memory at 640 × 360, 42 MB at 1080p and 112 MB at 4K per decoder (§11).
2. **When no sandbox can be created:** fall back to a low-integrity restricted token, and if that also fails, refuse to
   show the picture with a message. Never decode in-process with a warning.
3. **Re-encode every clipboard image from pixels, including BMP.** Built in phase 0: other programs receive a fresh PNG
   or DIB, never the device's bytes.

## 10. Phases

| Phase | Content | State |
|---|---|---|
| 0 | §3.6, and §3.5's clipboard rules in RemoteHub's process | **Built** |
| 1 | Spike: .NET, Media Foundation and WIC in an AppContainer with no capabilities, job, handle list, section | **Done** – §11 |
| 2 | `RemoteHubDecoder.exe` for video: protocol, section, the console's checks, restarts and limit, audit, view tests | Next |
| 3 | Clipboard images through the session's decoder (phase 0's `DeviceImage` moves into it) | – |
| 4 | Integration, sandbox probe, fuzz (SEC-DEC-01/02 with the rogue agent), lab cases, MSI, docs; D-VID within today's results | – |

## 11. Spike results (phase 1)

A throwaway program (not in the repository) started itself the way §3.2 describes: `CreateAppContainerProfile` with
no capabilities, `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES` and `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` (two anonymous
pipe ends and one section), `CREATE_SUSPENDED`, assigned to a job (`KILL_ON_JOB_CLOSE`, one active process, 1 GB per
process, all UI limits), then resumed. The program was in a folder `ALL APPLICATION PACKAGES` can read, as Program
Files is. Windows Server 2025, .NET 10.0.12 framework-dependent (`C:\Program Files\dotnet`), the software H.264
decoder of Media Foundation, WPF's WIC decoders. Not yet tried on Windows 11 – phase 4's lab cases run there too.

**It works as designed.** The framework-dependent .NET program starts in the AppContainer with no extra file access
(ready after ~300 ms), Media Foundation's H.264 decoder and WIC's PNG, BMP and JPEG decoders work, and the inherited
section maps. No change to §3 is needed, and the C++ alternative (§8) isn't.

| Measured | Result |
|---|---|
| Start to "ready" | 283–324 ms |
| H.264, 60 frames, decode in the container + write to the section, round trip from the console | 640 × 360: 1.5 ms median · 1080p: 9.4 ms (in-process today: 9.9 ms) · 4K: 34.2 ms (in-process: 34.8 ms). The pipe and section add nothing measurable, as the console no longer copies the frame |
| Picture | Mean difference from the source 1.2–1.4 of 255 (H.264 at quality 80), the same as in-process |
| Images | PNG 53–58 ms for the first (WIC starting), BMP 0.3 ms, JPEG 0.5 ms |
| Memory per decoder | 640 × 360: 51 MB working set / 21 MB private · 1080p: 87 / 42 · 4K: 204 / 112 |
| A decoder that hangs | Ended by closing the job (`KILL_ON_JOB_CLOSE`) |

What the decoder can't do (each tried from inside):

| Probe | Result |
|---|---|
| Token | AppContainer, low integrity (S-1-16-4096) |
| TCP to the console's loopback listener, TCP to 1.1.1.1:443, DNS | Blocked |
| Read a file in the user's profile, list the profile, list `%LOCALAPPDATA%\RemoteHub` (the database) | Access denied |
| Write `%TEMP%`, write `C:\Users\Public` | Access denied |
| Write HKCU | Access denied |
| Read the clipboard | Access denied (job UI limit) |
| Start a process | Refused (job: one active process) |
| Open RemoteHub's process for reading its memory | Access denied |
| Use another process's window (`Shell_TrayWnd`) | Found by `FindWindow`, but `GetWindowText` fails with 1400 (invalid handle: job UI limit) |
| Read `C:\Windows\System32\drivers\etc\hosts`, read HKCU `…\Explorer` | Allowed – world-readable system data, nothing of the user's |

Left for phase 2: the section's pages are committed with `PAGE_READWRITE` here; `SEC_RESERVE` and committing as used
(§3.3) is to be checked there. The low-integrity fallback (§9, decision 2) wasn't needed on this PC and gets its own
test in phase 4.
