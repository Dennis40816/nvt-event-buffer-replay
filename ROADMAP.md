# Roadmap

## M1 — C# Parity Core

- [x] Stable domain records, diagnostics, provenance, and format descriptors
- [x] Source probing and NDS/Saleae/KingstVIS/DSL/Acute/Excel/Canonical adapters
- [x] Common 0x82-0x85 decoding and semantic parity
- [x] Desay 0x97 two-transaction assembly and Standard/Benz Palm decode
- [x] `nvt-replay formats`, `sources`, `probe`, `inspect`, and `analyze`
- [x] Frozen Python parity observation plus synthetic/public fixtures and deterministic artifact goldens

## M2 — Replay Slice

- [x] Load and confirmation-driven auto Decode Configuration (no separate Decode action)
- [x] Reported Frame and Host State Paint
- [x] Recorded/Frame clocks, absolute-deadline playback, late-frame coalescing, and loop crossfade
- [x] Logical stepping, physical drill-down, range, and sparse checkpoints
- [x] Basic multi-track timeline

## M3 — Analysis Slice

- [x] Protocol diagnostics and ASIL lifecycle
- [x] Review Queue and occurrence grouping
- [x] Event/Raw/Transport/Evidence inspector
- [x] Marker and `.nvtreplay.json` round trip

## M4 — Export Slice

- [x] Shared ReplayScene
- [x] Exact 1280×720 desktop MP4 preview with scrubber and reusable frame buffers
- [x] Selected-range FFmpeg MP4 with PNG fallback
- [x] Heatmap PNG
- [x] Parsed/diagnostic JSON and CSV
- [x] Source/config/hash manifest

## M5 — Hardening

- [x] One-GB/one-million-record/eight-hour performance gate
- [x] Cancellation, recovery journal, and atomic output
- [x] Keyboard, command palette, scaling, theme, accessibility, and Skia headless UI checks
- [x] Windows self-contained portable package and release gates

## M6 — Register-aware communication log

- [x] Shared register catalog for NDS and decoded-I2C sources; annotations never change source bytes
- [x] Built-in IC address profiles for 51923, 51926, 51927, 51929/51932, and 51950/51951
- [x] Profile-collision guard: `0x80800` remains unresolved until 51929/51932 or 51950/51951 is selected
- [x] Readable Raw Explorer column with IC candidate, region, register, raw value, and confirmed meaning
- [x] Separate FW Command parser for Event Buffer `+0x50`; unknown opcodes stay visible as raw values
- [x] Operator-selected built-in register profile persisted in sidecars/manifests and reapplied before decode
- [x] Export a paired `communication-readable.csv/jsonl` beside the immutable original log
- [x] Register activity strip, search, and filters for reads, writes, changes, commands, reset, and ambiguous mappings
- [ ] Custom register profile import (versioned JSON with validation and provenance)

### Confirmed register knowledge

| Address / offset | Confirmed interpretation | Current decode rule |
| --- | --- | --- |
| `0xFF000`–`0xFF003` | Chip ID (4 bytes) | Raw bytes, no product inference |
| `0xFF0FE ← 0x69` | Software Reset | Decode only for a write whose first byte is exactly `0x69` |
| Event Buffer `+0x00` | Event Buffer | Register/region label; protocol decode still requires explicit Event Buffer Version |
| Event Buffer `+0x50` | FW Command mailbox | Route write payload to the separate FW Command parser |
| FW Command `0x23` | Baseline Reset | Confirmed command name; remaining payload stays raw |
| Event Buffer `+0x60` | FW State | Only `0xA3 = Normal Run` is semantic; `0x00`, `0xA1`, `0xA2`, and other values stay raw until defined |
| Event Buffer `+0x70` | Two-byte frame counter | Raw byte order, change tracking only; no endian guess |
| Event Buffer `+0x76` | DP Version | Raw bytes |
| Event Buffer `+0x78` | TP FW Version | Raw bytes |
| Common Buffer base | Bulk-data transfer buffer used through handshake + read | Region label only until handshake protocol is specified |
| History base | FW event history storage | Region label and raw bytes |

### Built-in IC address profiles

| IC family | Event Buffer | Common Buffer | History |
| --- | ---: | ---: | ---: |
| 51923 | `0x94000` | `0x941C0` | `0x9ACA0` |
| 51926 | `0x96A00` | `0x97B9C` | `0x9BCA0` |
| 51927 | `0x99000` | `0x8EC98` | `0x99200` |
| 51929/51932 | `0x80800` | `0xA5200` | `0x9D130` |
| 51950/51951 | `0x80800` | `0xAAD8C` | `0xA445C` |

`0x80800` appearing in two rows is a numeric collision between two independent
IC profiles, not a shared register map. Without an explicit profile, the
readable log must retain both candidates and leave register meaning unresolved.
After an operator selects a profile, absolute Event/Common/History addresses
must match that profile; an address owned only by another profile remains raw.
Offset-only decoded-I2C reads with an unknown page are the sole exception and
may retain their transport-level Event Buffer offset meaning.

### Data still needed from FW/project owners

- Register profile identity: IC/project name, FW version range, Event/Common/History bases, aliases, and source document/golden provenance.
- Register contract: absolute address or base+offset, byte width, read/write permission, access side effects, and whether values are sampled or edge-triggered.
- Value semantics: enums, bitmaps/bitfields, masks, signedness, scale/unit, valid/reserved ranges, clamp/wrap behavior, and byte order where confirmed.
- FW Command table for Event Buffer `+0x50`: opcode, name, request payload schema/length, response or handshake, timeout, side effects, and FW-version differences.
- Common Buffer handshake: request/ready/ack registers and values, transfer length/format, chunking, completion/error states, and representative NDS logs.
- History layout: entry header, event IDs, length/timestamp rules, wrap behavior, clear behavior, and representative normal/failure logs.
- Reset/control sequences beyond `0xFF0FE ← 0x69`, including required delays and observable follow-up state.
- Golden evidence covering known values, unknown/reserved values, malformed byte counts, repeated reads, and profile-address collisions.
- Conflict policy when the same address/offset differs by customer project or FW version; decoding must require an explicit profile rather than silently choosing.

## Post-MVP evidence gates

- Acute real-export validation and continuation-row evidence
- raw waveform exports
- real Linux/Android kernel log capture
- scoped QA records suitable for rule conversion
- multi-source clock alignment and comparison workflows
- complete register maps, FW command dictionaries, Common Buffer handshake, and History layouts
