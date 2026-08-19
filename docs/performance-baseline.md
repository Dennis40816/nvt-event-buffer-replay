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

On 2026-08-19 the same machine also ran the app-facing benchmark against the private KingstVIS decoded-I2C golden (the capture itself is not committed): 4,160 physical records and 2,080 logical frames. Decode plus immutable frame/trail/auto-pause indexing completed in 669.2 ms, 10,000 cached random seeks completed in 19.1 ms, and 120 sampled 640×360 frames with production trails and hard-avoidance labels rendered at 69.4 FPS. Peak working set was 83.7 MiB. This check covers the richer interactive path and remains above the 60 FPS release floor.

## Lossless trail chunk gate

The 2026-08-19 trail renderer follow-up replaces visual downsampling with lossless 2,048-point chunks. The automated gate retains 100,003 report points as 49 continuous chunks, renders the complete line and all point markers through batched Skia calls, and performs 250 non-sequential frame switches. Completed chunks are reused; each arbitrary seek rebuilds no more than the 2,047-point active tail. A 640×360 headless render of the complete 100,003-point scene must finish within five seconds. The private 2,080-frame KingstVIS golden was also replayed from frame 1 to frame 2,080 at the 120 Hz frame-paced setting without a UI stall.
