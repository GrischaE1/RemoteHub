# RemoteHub – Decoding a device's data outside the console

Status: built – 2026-10-23. The device's video and clipboard images are decoded in `RemoteHubDecoder.exe`, in an
AppContainer (§12 has what was built and how it differs from the plan). Open: the lab cases with a rogue agent
(SEC-DEC-01 … 05, §10). The decisions in §9 are made; §11 has what the spike found.
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
| Video | `AgentDesktopSession.cs` `FrameDecoder` (thread "Desktop decoder") → `VideoCodec.cs` `H264Decoder` | Microsoft's software H.264 MFT (`CLSID_MSH264DecoderMFT`), driven through COM vtables; NV12 → BGRA in managed code. Since phase 2: in `RemoteHubDecoder.exe` (§12) | **High** – native parser of a stream the device writes, every frame |
| Clipboard image, pushed | `AgentDesktopSession.FinishReceiving` | Was: WPF `BitmapDecoder.Create` (WIC; any format WIC recognises) on the UI thread. Since phase 0: `DeviceImage`, since phase 3 in `RemoteHubDecoder.exe` | **High** – native parser; phase 0 narrows it to three decoders after a size check |
| Clipboard image, offered | `ClipboardOffer.Render` | Was: `BitmapDecoder` + `FormatConvertedBitmap` on the offer's STA thread (`PngToDib`). Since phase 0: `DeviceImage`, since phase 3 decoded when it arrives, in `RemoteHubDecoder.exe` | **High** – same |
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

- **Control pipe** (an anonymous pipe pair, inherited): length-prefixed messages (4-byte length; requests at most
  100 MB, the clipboard's limit; answers at most 64 KB), `DecoderProtocol.cs`. Requests are `Video(frameId, width,
  height, key frame, access unit)`, `Image(id, section, width, height, file)`, `Probe` and `Exit`. Answers are
  `Ready`, `Frame(frameId, buffer, width, height)`, `NoFrame(frameId)`, `ImageDone(id, width, height)`,
  `Failed(id, reason)` (the input couldn't be decoded; the decoder is fine) and `ProbeResult(json)`.
- **Pixels** through shared memory, never the pipe. Video: one section (`CreateFileMapping`, made by RemoteHub,
  inherited by the decoder) with two buffers of 4096 × 2304 × 4 bytes, the decoder filling them in turn. Images: a
  section per image of exactly the size RemoteHub read from the header (up to 8192 × 8192 × 4 bytes), handed to the
  decoder with `DuplicateHandle` and closed by it once written – an image larger than a video buffer still fits.
- **The console doesn't trust the answers.** A taken-over decoder can say anything. Before reading, the console checks
  that the buffer index is 0 or 1, that `width × height × 4` fits the buffer, that the size is within
  `MaxVideoWidth/Height`, and that `frameId` is one it sent. Anything else ends the decoder (§3.4). The worst a decoder
  can then do is show wrong pixels in that session's tab.
- Nothing is named (no named pipes, no named sections), so no other process can sit in the middle or pretend to be the
  decoder.

**Cost per frame:** the console copies the picture from the section into its back buffer, as it copied the decoder's
output before, so there's no extra copy. A pipe round trip adds ~0.1 ms. The decoding itself is unchanged (§11).

### 3.4 When it crashes or misbehaves

- Pipe closed, exit code, a timeout (no answer to a frame within 2 s) or an answer that fails §3.3's checks: RemoteHub
  ends the process (job), writes a DiagLog line with the reason (no device names), starts a new decoder and asks the
  agent for a key frame. The session continues; the picture pauses for a moment.
- More than 3 restarts in a minute: the session ends with "The picture can't be shown – the device keeps sending data
  RemoteHub can't decode. Disconnect and check the device." Restarting endlessly would hand an attacker unlimited tries.
  When the last failure was a decoder that didn't start, nothing from the device had reached it: the session ends with
  "… RemoteHub's picture decoder doesn't start on this PC. Connect again; if it keeps happening, restart RemoteHub."
  instead (the lab saw three starts of over 10 s in a row on a busy VM).
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

Built (`tests/RemoteHub.Tests/DecoderProcessTests.cs`, `DeviceImageTests.cs`, `ClipboardOfferTests.cs`;
`lab/tests/SEC-DEC.Tests.ps1`):

- **Unit:** the answer checks in §3.3 (wrong kind, length, frame or image id, buffer, a picture larger than asked for,
  an image of another size), the restart limit with a clock (the fourth failure in a minute gives up; failures spread
  over more than a minute don't), the DIB built from pixels, the header parsing and the video message checks of §3.6.
- **Integration (CI, no VM):** the real `RemoteHubDecoder.exe` started in its AppContainer decodes an H.264 stream
  RemoteHub encodes and a PNG, BMP and JPEG (pixels as expected); its probes – network, loopback, the profile, the
  database folder, writing, HKCU, the clipboard, a new process, RemoteHub's process, another window – are all blocked.
  The same at low integrity (forced): no writing, clipboard, process, RemoteHub's process or windows. A decoder killed
  from outside is reported within the frame timeout and the session starts a new one.
- **Fuzz (SEC-DEC-01/02 in CI):** 120 truncated, bit-flipped, noisy and doubled H.264 access units (some declaring
  4096 × 2304) and 120 broken PNG, BMP and JPEG files fed to the decoder process. Every input ends in a picture, a bad
  frame or a contained failure and a new decoder within the time limit, and a good stream decodes afterwards; no image
  comes back larger than its header states.
- **Lab:**
  - SEC-DEC-06: the installed build's decoder on Windows 11 and Server, run by normaluser (`RemoteHub.exe
    --decoder-check`, §12): AppContainer, decoding works, every probe blocked.
  - SEC-DEC-01 … 05 need the rogue agent (security-test-lab-design.md §5.1) – not built yet.
  - The D-VID cases for frame rate and latency stay within today's results.
- **View test (STA):** the session tab after a decoder restart (still connected) and after the limit (the message,
  *Connect* to start again).

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
| 2 | `RemoteHubDecoder.exe` for video: protocol, section, the console's checks, restarts and limit, audit, view tests | **Built** – §12 |
| 3 | Clipboard images through the session's decoder (phase 0's `DeviceImage` moves into it) | **Built** |
| 4 | Integration, sandbox probe, fuzz, lab case SEC-DEC-06, MSI, docs | **Built**; the rogue agent and SEC-DEC-01 … 05 are open; D-VID to be run |

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
(§3.3) is to be checked there (kept as it is – §12). The low-integrity fallback (§9, decision 2) wasn't needed on this
PC and gets its own test in phase 4 (done – §12).

## 12. As built (phases 2–4)

- **Programs and code:** `src/RemoteHub.Decoder` (`RemoteHubDecoder.exe`: the H.264 decoder of `VideoCodec.cs`,
  `DeviceImage.Decode` and `DecoderProtocol.cs`, nothing else of RemoteHub's). In RemoteHub, `Remoting/Decoder`:
  `DecoderSandbox` (AppContainer, low-integrity fallback, job, handle list), `DecoderHost` (one process: requests,
  time limits, the answer checks), `SessionDecoder` (one per agent desktop session: start on first use, restarts, the
  limit) and `DecoderCheck`.
- **Installed:** `build-installer.ps1` publishes the decoder self-contained into RemoteHub's folder, on the same runtime,
  and signs it – a release now signs 5 files (6 with the credential provider). In a development build it is copied
  next to `RemoteHub.exe`; there its folder gets read access for all application packages (as Program Files has), or
  the decoder starts at low integrity.
- **Time limits:** 10 s to start (≈ 0.3 s usually; the first picture of a session waits for it), 2 s per frame, 10 s
  per image. A decoder past its limit is ended by closing its job.
- **Job:** as §3.2, plus `DIE_ON_UNHANDLED_EXCEPTION`, so a crash never waits on an error dialog.
- **Video section:** committed at creation (`PAGE_READWRITE`): 75 MB of commit charge per open desktop session, but
  physical memory only for the pages the decoder writes. `SEC_RESERVE` would save the charge at the cost of committing
  from the decoder; not worth it for a few sessions.
- **Clipboard images:** RemoteHub reads the size from the header (§3.6), the decoder decodes into the image's own
  section, and RemoteHub builds the clipboard data from those pixels. An image is decoded once, when it arrives, in
  the session's decoder; `ClipboardOffer` only re-encodes the pixels.
- **The audit event** `desktop.decoder_stopped` ("Desktop decoder stopped") names the connection and the reason (e.g.
  "no answer within 2 s while decoding a frame"), and says when the session was ended.
- **AppContainer profile:** `RemoteHub.Decoder`, made once per Windows user on the first desktop session
  (`%LOCALAPPDATA%\Packages\remotehub.decoder`, which also holds the decoder's own temporary folder). Uninstalling
  RemoteHub (per machine) doesn't remove it.
- **Low integrity, measured:** the forced fallback blocks writing, HKCU, the clipboard, new processes, RemoteHub's
  process and other windows, but – unlike the AppContainer – lets the decoder read most files and use the network.
- **`RemoteHub.exe --decoder-check <file>`** starts the decoder as a session does, decodes a test stream and images,
  runs the probes and writes the result as JSON – SEC-DEC-06 runs it on the installed build; support can too.
