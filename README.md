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

## Build

Requirements: .NET SDK 10.0.303 or a compatible later .NET 10 feature band.

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet run --project src/Nvt.Replay.Cli -- formats
dotnet run --project src/Nvt.Replay.Avalonia
```

Private captures, firmware, QA records, and golden payloads must not be
committed. Commit only synthetic fixtures, schemas, hashes, provenance, and
reviewed observations.
