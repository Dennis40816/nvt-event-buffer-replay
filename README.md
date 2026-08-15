# NVT Event Buffer Replay

Offline C# and Avalonia workstation for replaying Novatek touch-controller
captures as a vehicle head unit would consume them, reviewing protocol and
ASIL evidence, marking significant ranges, and exporting reproducible results.

This repository is the production implementation. The sibling Python project
`nvt_event_buf_parser` remains a reference decoder, simulator, and golden
generator; the desktop application does not require Python at runtime.

## Status

Milestone 1, **C# Parity Core**, is in progress. The committed product boundary
is documented in [the product specification](docs/product-spec.md) and
[roadmap](ROADMAP.md).

The first vertical slice includes:

- streaming NDS, Saleae, KingstVIS, DSL, Excel, and canonical I2C adapters with
  stable source locations and transport provenance;
- an explicit format registry for Common `0x82`–`0x85` and Desay `0x97`;
- source detection that never silently chooses Event Buffer Version or Benz Palm;
- CLI discovery and probe commands with optional JSON output; and
- an Avalonia shell centered on Paint, Review Queue, Inspector, and the
  physical/logical/evidence timeline.

The desktop now loads and indexes supported captures in the background, opens
in Raw Explorer before semantic configuration is complete, and keeps the
indexed records in memory while the operator switches among Common
`0x82`–`0x85`. Selecting a decoded frame synchronizes its physical record,
source location, stable ID, raw evidence, and decoded fields in the Inspector.

Decoded-I2C adapters normalize LA exports into the same physical record model.
Page/write tracking resolves reads such as `FF 09 90 00` to `0x99000`, while
the Raw Explorer retains slave, commands, ACK/NAK data, source-specific fields,
and diagnostics. A C# simulator produces equivalent Saleae, KingstVIS, DSL,
Excel, and canonical TXT fixtures. See [source adapters](docs/source-adapters.md).

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
and MAX playback, idle-gap compression, and an In/Out loop. Per-contact colors
stay consistent across the point, coordinate label, and trajectory. Trajectory
retention can show a recent 2–120 frame window, retain each gesture until its
Break, or preserve completed gestures for the session; Clear affects only the
view and never mutates capture evidence. Invalid frames
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
`nvt-replay export` renders selected ranges to raw RGB and pipes them to an
operator-reviewed FFmpeg executable, or atomically falls back to a PNG
sequence when no compatible encoder is available. See [replay video export](docs/video-export.md).

Repeatable performance gates cover 1 GiB input, one million physical records,
an eight-hour timeline, sparse seek checkpoints, and 60 FPS Paint rendering.
See [performance and scale gates](docs/performance.md).

The desktop UI includes a keyboard-first command palette, explicit automation
names, textual severity cues, and reviewed dark/light palettes. See
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
dotnet run --project src/Nvt.Replay.Avalonia -- ./capture.txt --event-version 0x97 --palm-profile Benz-Palm
```

The desktop startup options are explicit operator choices for reproducible QA
and screenshot runs. They do not infer Event Buffer Version or Benz Palm.

Windows preview and stable packaging use a pinned version, committed dependency
locks, a closed payload allowlist, SHA-256 manifests, and fresh-extraction
smoke checks. See the [release process](docs/release.md).

Private captures, firmware, QA records, and golden payloads must not be
committed. Commit only synthetic fixtures, schemas, hashes, provenance, and
reviewed observations.
