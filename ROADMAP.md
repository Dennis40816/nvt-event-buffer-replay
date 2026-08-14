# Roadmap

## M1 — C# Parity Core

- [x] Stable domain records, diagnostics, provenance, and format descriptors
- [x] Source probing and NDS/Saleae/KingstVIS/DSL/Excel/Canonical adapters
- [x] Common 0x82-0x85 decoding and semantic parity
- [x] Desay 0x97 two-transaction assembly and Standard/Benz Palm decode
- [x] `nvt-replay formats`, `sources`, `probe`, `inspect`, and `analyze`
- [x] Frozen Python parity observation plus synthetic/public fixtures and deterministic artifact goldens

## M2 — Replay Slice

- [x] Load and progressive Decode Configuration
- [x] Reported Frame and Host State Paint
- [x] Recorded/Frame clocks and playback controls
- [x] Logical stepping, physical drill-down, range, and sparse checkpoints
- [x] Basic multi-track timeline

## M3 — Analysis Slice

- [x] Protocol diagnostics and ASIL lifecycle
- [x] Review Queue and occurrence grouping
- [x] Event/Raw/Transport/Evidence inspector
- [x] Marker and `.nvtreplay.json` round trip

## M4 — Export Slice

- [x] Shared ReplayScene
- [x] Selected-range FFmpeg MP4 with PNG fallback
- [x] Heatmap PNG
- [x] Parsed/diagnostic JSON and CSV
- [x] Source/config/hash manifest

## M5 — Hardening

- [x] One-GB/one-million-record/eight-hour performance gate
- [x] Cancellation, recovery journal, and atomic output
- [x] Keyboard, command palette, scaling, theme, and accessibility checks
- [x] Windows self-contained portable package and release gates

## Post-MVP evidence gates

- Acute real export
- raw waveform exports
- real Linux/Android kernel log capture
- scoped QA records suitable for rule conversion
- multi-source clock alignment and comparison workflows
