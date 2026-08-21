# Replay video export

Avalonia Paint and headless export consume the same UI-independent
`ReplayScene`. A scene contains Reported Frame contacts, Host State contacts,
global Palm, logical/captured clocks, diagnostics, markers, and the coordinate
extent. Export is C# raster rendering; it never screen-records the desktop.

The desktop Output workspace defaults to an MP4 preview. Its scrubber and
playback use the same `ReplayFramePlan`, logical-frame sampling, render settings,
and 1280×720 RGB pixels as the eventual desktop export. The preview control
only scales that completed frame to its available screen area, so labels,
legend placement, axes, and contact geometry cannot diverge because of a second
preview layout. It reuses the RGB, RGBA, and `WriteableBitmap` storage across
frames; changing dimensions is the only operation that reallocates them.
While Output is open, the unrelated Paint transport and Inspector rail are removed;
the Preview player supplies play, repeat, full-screen, time, and direct range
scrubbing. A fixed high-visibility settings rail keeps clock, speed, frame rate,
resolution, and range summary next to Preview because they define the output plan.
The default clock is Frame-paced 120 Hz; Recorded follows capture gaps, while
Frame-paced displays each decoded frame at 120 Hz and retains the original REC
timestamp in the rendered frame.

`nvt-replay export` accepts a one-based inclusive range, Recorded or Frame
clock, speed, frame rate, even output dimensions, an optional review sidecar,
and an optional FFmpeg executable:

```powershell
dotnet run --project src/Nvt.Replay.Cli -- export capture.txt `
  --event-buffer-version 0x83 `
  --output replay.mp4 `
  --range 1:500 --size 1280x720 --fps 30 --speed 1 `
  --clock recorded --review capture.nvtreplay.json `
  --ffmpeg C:\Tools\ffmpeg\bin\ffmpeg.exe
```

C# renders deterministic RGB24 frames and pipes them directly to FFmpeg. The
reviewed encoder order is Media Foundation `h264_mf`, LGPL `libopenh264`, then
`libx264` for an explicitly supplied compatible runtime; output uses `yuv420p`
for common playback compatibility. The adjacent `.mp4.json` records source/config/hash, range,
clock, Paint mode, dimensions, frame rate, speed, output-frame count, duration,
and encoder identity.

Preview playback is anchored to a monotonic absolute 30 FPS clock and skips a
late display frame rather than accumulating render time as drift. Main replay
uses the same absolute-deadline principle at the selected Recorded/Frame clock
and speed. Alarm/QA and optional Break/All Break pause frames remain mandatory
even while catching up. Paint and raster export also share the same deterministic
hard-avoidance label placement, so coordinate labels do not overlap at normal
preview/export sizes.

If no reviewed encoder is found or it exits incompatibly, MP4 export stops with
a visible error and creates no fallback output. The temporary MP4 is removed on
failure or cancellation, and an existing MP4 or manifest is never overwritten.
The rendering library retains an explicit opt-in PNG-sequence API for tests and
specialized callers, but the desktop and CLI never invoke it implicitly.

## Encoder boundary and provenance

Windows release packaging downloads one pinned BtbN LGPL shared build described
by `eng/ffmpeg-runtime.json`, verifies the archive SHA-256, extracts only the
closed runtime allowlist, and records every shipped file hash in
`tools/ffmpeg/FFMPEG-RUNTIME.json`. `NOTICE.txt`, the upstream license, exact
FFmpeg source commit, and build-source URL ship beside the binaries. Release
smoke tests require the reviewed H.264 encoders and a working `ffprobe` before a
package is accepted. Developers may still use `--ffmpeg` or
`NVT_REPLAY_FFMPEG` to probe another executable explicitly.
