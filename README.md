# NVT Event Buffer Replay

Offline C# and Avalonia workstation for replaying Novatek touch-controller
captures as a vehicle head unit would consume them, reviewing protocol and
ASIL evidence, marking significant ranges, and exporting reproducible results.

This repository is the production implementation. The sibling Python project
`nvt_event_buf_parser` remains a reference decoder, simulator, and golden
generator; the desktop application does not require Python at runtime.

## Status

Milestones 1 through 5 are implemented. The built-in portion of Milestone 6 is
also complete; custom register-profile import remains evidence-gated. The
committed product boundary is documented in [the product specification](docs/product-spec.md)
and [roadmap](ROADMAP.md).

The first vertical slice includes:

- streaming NDS, Saleae, KingstVIS, DSL, Acute, Excel, and canonical I2C adapters with
  stable source locations and transport provenance;
- register-aware communication rows with profile-scoped IC maps, confirmed FW
  command/reset meanings, and raw-only fallback for unknown values;
- an explicit format registry for Common `0x82`–`0x85` and Desay `0x97`;
- source detection that never silently chooses Event Buffer Version or Benz Palm;
- CLI discovery and probe commands with optional JSON output; and
- an Avalonia shell centered on Paint, Review Queue, Inspector, and the
  physical/logical/evidence timeline.

The desktop now loads and indexes supported captures in the background, opens
in Raw Explorer before semantic configuration is complete, and keeps the
indexed records in memory while the operator switches among Common
`0x82`–`0x85`. There is no separate Decode action: confirming a Common version
starts decoding immediately, while Desay `0x97` waits until IC and Palm profiles
are both explicit. Selecting a decoded frame synchronizes its physical record,
source location, stable ID, raw evidence, and decoded fields in the Inspector.

Decoded-I2C adapters normalize LA exports into the same physical record model.
Page/write tracking resolves reads such as `FF 09 90 00` to `0x99000`, while
the Raw Explorer retains slave, commands, ACK/NAK data, source-specific fields,
and diagnostics. A C# simulator produces equivalent Saleae, KingstVIS, DSL,
Acute, Excel, and canonical TXT fixtures. Acute currently follows the
conservative English row-per-transaction report contract; continuation rows
remain evidence-gated. See [source adapters](docs/source-adapters.md).

Common `0x82`–`0x85` decoding is now available through the NDS inspection
slice. Shared finger semantics are implemented once; version-specific tails,
CRC evidence, ASIL transitions, All Break/Break, bus counters, EMS bitmap, and
global Palm diagnostics remain traceable to the source record. See the
[Common Event Buffer contract](docs/common-event-buffer.md).

Desay `0x97` uses a dedicated two-transaction assembler before decoding and
requires an explicit Standard or Benz Palm profile. The current offset `0x00`
full-reread contract and its remaining Benz evidence gate are documented in
the [Desay Event Buffer contract](docs/desay97-event-buffer.md).

The Common replay slice reduces each logical frame into separate Reported
Frame and Host State views. Paint supports deterministic forward/backward
seeking, sparse checkpoints, Recorded and synthetic Frame clocks, 0.1×–10×
and MAX playback, idle-gap compression, and a draggable two-handle loop range.
Playback is scheduled against an absolute clock: a busy UI may skip obsolete
intermediate drawings to stay on time, but never skips an Alarm or QA pause.
Loop wrap uses a short crossfade instead of a hard visual cut.
Per-contact colors
stay consistent across the point, coordinate label, and trajectory. Trajectory
retention can show a recent 2–120 frame window, retain each gesture until its
Break, or preserve completed gestures for the session; Clear affects only the
view and never mutates capture evidence. Labels use point-aware placement,
two-line coordinates, leader lines, and Finger/Glove/Palm/Reserved glyphs;
the canvas includes a matching ID/type legend and adjustable coordinate grid.
The legend scores the complete capture once to choose the least occupied
corner, stays fixed during playback, and supports manual corner pinning,
direct compact/expanded toggling, and `L` show/hide.
Invalid frames
remain visible as evidence without mutating Host State; a capture that ends
with active contacts raises a warning instead of inventing an All Break.

The Review Queue groups recurring diagnostics without dropping occurrences,
keeps captured ASIL Assert/Clear separate from human acknowledgement and
disposition, and pauses replay on Alarm or QA Fail by default. Selecting an
occurrence synchronizes Paint, decoded fields, transport, raw evidence, and
source location. See [Review Queue and ASIL lifecycle](docs/review-queue.md).

Markers, ranges, QA case references, raw Kernel/FW log attachments, review
state, and visibility preferences round-trip through a versioned
`.nvtreplay.json` sidecar without modifying the capture. Saves are atomic;
capture/configuration mismatches require explicit operator confirmation, and
missing or changed evidence stays visible. See [replay review sidecars](docs/replay-sidecar.md).

The Analysis workspace and `nvt-replay analyze` export the same reproducible
anomaly/ASIL aggregates, parsed event and diagnostic JSON/CSV, source/config
manifest, and deterministic Reported Frame heatmap PNG. Every aggregate links
back to diagnostic, event, and physical source IDs. See [analysis outputs](docs/analysis-outputs.md).

Avalonia Paint and deterministic headless export now share one `ReplayScene`.
The desktop MP4 preview also uses the exact export frame plan and 1280×720
raster; only the on-screen presentation is scaled. Preview RGB/RGBA buffers and
the Avalonia bitmap are reused between frames to avoid playback GC churn.
`nvt-replay export` renders selected ranges to raw RGB and pipes them to an
operator-reviewed FFmpeg executable, or atomically falls back to a PNG
sequence when no compatible encoder is available. See [replay video export](docs/video-export.md).

Repeatable performance gates cover 1 GiB input, one million physical records,
an eight-hour timeline, sparse seek checkpoints, and 60 FPS Paint rendering.
See [performance and scale gates](docs/performance.md).

The desktop UI includes a keyboard-first command palette, explicit automation
names, textual severity cues, compact 34–36 px controls, and reviewed dark/light
palettes. A Skia-backed Avalonia headless suite verifies auto-decode, layout,
rail defaults, and both rendered themes in CI. See
[UI interaction and accessibility](docs/ui-accessibility.md).

Supported inputs and deliberately evidence-gated post-MVP work are summarized
in [MVP evidence boundaries and deferrals](docs/mvp-limitations.md).

## Build

Requirements: .NET SDK 10.0.303 or a compatible later .NET 10 feature band.

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet run --project src/Nvt.Replay.Cli -- formats
dotnet run --project src/Nvt.Replay.Cli -- probe ./capture.txt --json
dotnet run --project src/Nvt.Replay.Cli -- inspect ./capture.txt --event-buffer-version 0x83
dotnet run --project src/Nvt.Replay.Cli -- analyze ./capture.txt --event-buffer-version 0x83 --output ./analysis
dotnet run --project src/Nvt.Replay.Cli -- export ./capture.txt --event-buffer-version 0x83 --output ./replay.mp4
dotnet run --project src/Nvt.Replay.Avalonia
dotnet run --project src/Nvt.Replay.Avalonia -- ./capture.txt
dotnet run --project src/Nvt.Replay.Avalonia -- ./capture.txt --event-version 0x83
dotnet run --project src/Nvt.Replay.Avalonia -- ./capture.txt --event-version 0x97 --register-profile 51927 --palm-profile Benz-Palm
dotnet run --project src/Nvt.Replay.Avalonia -- ./capture.txt --event-version 0x83 --register-profile 51927
dotnet run --project src/Nvt.Replay.Cli -- readable ./capture.txt --output ./analysis --register-profile 51927
```

The desktop startup options and IC register profiles are explicit operator choices for reproducible QA
and screenshot runs. They do not infer Event Buffer Version or Benz Palm.

Windows preview and stable packaging use a pinned version, committed dependency
locks, a closed payload allowlist, SHA-256 manifests, and fresh-extraction
smoke checks. See the [release process](docs/release.md).

Private captures, firmware, QA records, and golden payloads must not be
committed. Commit only synthetic fixtures, schemas, hashes, provenance, and
reviewed observations.
