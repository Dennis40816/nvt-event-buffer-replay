# Windows MVP performance baseline

Full gate recorded 2026-08-14 on Windows `10.0.26200.0`, .NET `10.0.11`, 20 logical processors. Command: `./scripts/performance-gate.ps1 -Mode Full` from Release build `760ad76`.

| Gate | Fixture | Load | Decode/index | Peak working set | Additional result |
|---|---:|---:|---:|---:|---:|
| Source size | 1,073,751,327 bytes | 8,066 ms | 15 ms | 50.1 MiB | 64-bit max offset recorded |
| Physical volume | 1,000,000 records | 6,366 ms | 40 ms | 1,292.3 MiB | exact physical count |
| Timeline/replay | 28,801 frames / 8 h | 981 ms | 298 ms | 194.1 MiB | 10,000 seeks: 176 ms |

The 8-hour gate created 114 sparse Host State checkpoints. Shared 640×360 Paint rendered 120 sampled frames at 930.4 FPS. All values passed the release thresholds of 2 GiB peak working set, five minutes per-fixture load, and 60 FPS.

These numbers are a regression baseline, not a hardware guarantee. The JSON report is generated locally under `artifacts/performance-full` and includes the full machine-readable measurements; the large synthetic fixtures are not committed.

## Interactive replay cache check

On 2026-08-19 the same machine also ran the app-facing benchmark against the private KingstVIS decoded-I2C golden (the capture itself is not committed): 4,160 physical records and 2,080 logical frames. Decode plus immutable frame/trail indexing completed in 730.9 ms, 10,000 cached random seeks completed in 33.2 ms, and 120 sampled 640×360 frames with production trails and labels rendered at 63.2 FPS. Peak working set was 83.5 MiB. This check covers the richer interactive path and remains above the 60 FPS release floor.
