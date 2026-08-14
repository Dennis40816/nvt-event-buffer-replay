# Performance and scale gates

`nvt-replay benchmark` measures the same load, decode/index, sparse Host State seek, and shared Paint renderer used by the product. Its JSON result records OS, .NET runtime, processor count, source size, physical/logical counts, maximum 64-bit byte offset, timeline duration, checkpoint count, stage timings, render FPS, and process peak working set.

```powershell
dotnet run --project src/Nvt.Replay.Cli -c Release --no-build -- benchmark capture.txt `
  --event-buffer-version 0x83 --sample-seeks 10000 --render-frames 120 --json
```

The repository gate creates three independent deterministic NDS fixtures:

- size: at least 1 GiB, with ignored padding and three valid event records;
- physical volume: exactly 1,000,000 two-byte frame-counter reads;
- timeline: 28,801 valid Common frames spanning exactly eight hours.

Splitting these axes keeps each result diagnostic: a regression can be attributed to hashing/parsing, compact physical indexing, or replay/timeline/rendering. Run the full gate manually on the release machine:

```powershell
./scripts/performance-gate.ps1 -Mode Full
```

CI runs `-Mode Smoke` with smaller fixtures but the same invariants and code paths. Full thresholds are 2 GiB peak working set per process, five minutes load time per fixture, and at least 60 FPS for 640×360 deterministic Paint rendering. Smoke uses 1 GiB and one minute. Both emit a machine-readable report beneath `artifacts/performance`; generated fixtures and reports are intentionally git-ignored.

Source offsets and record indices are 64-bit. Logical replay indices remain 32-bit because the one-million-record requirement fits safely and compactly. Host State checkpoints are created every 256 logical frames plus the final frame; tests assert sparse cardinality and backward/forward reconstruction.

Load, hashing, source iteration, analysis, sidecar writes, and replay export observe cancellation tokens. A cancelled operation never returns a partially constructed `CaptureSession`; file outputs use temporary files or staging directories before replacement.
