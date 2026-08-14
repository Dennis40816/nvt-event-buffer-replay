# Replay video export

Avalonia Paint and headless export consume the same UI-independent
`ReplayScene`. A scene contains Reported Frame contacts, Host State contacts,
global Palm, logical/captured clocks, diagnostics, markers, and the coordinate
extent. Export is C# raster rendering; it never screen-records the desktop.

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
reviewed invocation uses H.264 (`libx264`) and `yuv420p` for common playback
compatibility. The adjacent `.mp4.json` records source/config/hash, range,
clock, Paint mode, dimensions, frame rate, speed, output-frame count, duration,
and encoder identity.

If no encoder is found or it exits incompatibly, export creates an atomic
`<name>.mp4.frames` PNG-sequence directory with the same scenes and an
`export-manifest.json`. The temporary MP4 or sequence is removed on failure or
cancellation. Existing MP4, manifest, or fallback directories are never
overwritten.

## Encoder boundary and provenance

FFmpeg is optional and is not bundled in the NVT Event Buffer Replay release.
The [FFmpeg download page](https://ffmpeg.org/download.html) provides source
and links to third-party Windows builds. Its
[legal page](https://ffmpeg.org/legal.html) explains that configuration choices
can change the applicable LGPL/GPL terms.

The MVP validation used the gyan.dev 8.1.2 essentials build linked by FFmpeg's
download page. The exact GitHub release asset reported SHA-256
`db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec`;
`ffmpeg -version` identified it as GPLv3 with libx264. It was used only as an
ignored local test tool. `ffprobe` confirmed the generated validation MP4 as
H.264/yuv420p, 640x360, nine frames, and 0.300 seconds.

Before distributing any encoder binary with the application, release owners
must select a specific build, record its source/configuration and hash, review
its license obligations, and update the closed package allowlist. Until that
review happens, PNG sequence is the offline-safe built-in export.
