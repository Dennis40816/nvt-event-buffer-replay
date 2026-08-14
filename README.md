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

- a streaming NDS communication-log probe/reader with continuation-line support
  and stable source locations;
- an explicit format registry for Common `0x82`–`0x85` and Desay `0x97`;
- source detection that never silently chooses Event Buffer Version or Benz Palm;
- CLI discovery and probe commands with optional JSON output; and
- an Avalonia shell centered on Paint, Review Queue, Inspector, and the
  physical/logical/evidence timeline.

The desktop now loads and indexes NDS captures in the background, opens
in Raw Explorer before semantic configuration is complete, and keeps the
indexed records in memory while the operator switches among Common
`0x82`–`0x85`. Selecting a decoded frame synchronizes its physical record,
source location, stable ID, raw evidence, and decoded fields in the Inspector.

Common `0x82`–`0x85` decoding is now available through the NDS inspection
slice. Shared finger semantics are implemented once; version-specific tails,
CRC evidence, ASIL transitions, All Break/Break, bus counters, EMS bitmap, and
global Palm diagnostics remain traceable to the source record. See the
[Common Event Buffer contract](docs/common-event-buffer.md).

## Build

Requirements: .NET SDK 10.0.303 or a compatible later .NET 10 feature band.

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet run --project src/Nvt.Replay.Cli -- formats
dotnet run --project src/Nvt.Replay.Cli -- probe ./capture.txt --json
dotnet run --project src/Nvt.Replay.Cli -- inspect ./capture.txt --event-buffer-version 0x83
dotnet run --project src/Nvt.Replay.Avalonia
dotnet run --project src/Nvt.Replay.Avalonia -- ./capture.txt
```

Private captures, firmware, QA records, and golden payloads must not be
committed. Commit only synthetic fixtures, schemas, hashes, provenance, and
reviewed observations.
