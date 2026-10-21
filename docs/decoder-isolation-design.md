# RemoteHub – Decoding a device's data outside the console

Status: draft for review (2026-10-22). Nothing is built yet. The decisions to make are in §9.
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
| Clipboard image, pushed | `AgentDesktopSession.FinishReceiving` | WPF `BitmapDecoder.Create` (WIC; any format WIC recognises) on the UI thread | **High** – native parser, and WIC tries every installed codec |
| Clipboard image, offered | `ClipboardOffer.PngToDib` | `BitmapDecoder` + `FormatConvertedBitmap` on the offer's STA thread | **High** – same |
| Clipboard BMP | `ClipboardOffer` | Not parsed by RemoteHub: the bytes go on the clipboard as CF_DIB | Medium – parsed later by whatever program the user pastes into |
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

### 3.6 Small fixes that don't need the process

- Check the video width and height against `MaxVideoWidth/Height` on the console side (the console doesn't check them
  today).
- A `Video` message shorter than its header ends the session through an exception outside the decoder's handler.
  Treat it as a bad frame instead.

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

## 9. Decisions to make

1. **One decoder per session (proposed) or one for all sessions.** Per session confines a take-over to one device,
   and costs ~25–40 MB per open session.
2. **When no sandbox can be created:** (a) fall back to a low-integrity restricted token, and if that also fails, refuse
   to show the picture with a message (proposed); or (b) fall back to decoding in-process with a warning.
3. **Re-encode every clipboard image from pixels, including BMP (proposed).** This changes what other programs receive
   when pasting: always a fresh PNG/DIB, never the device's bytes.
