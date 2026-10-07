# Register knowledge

Confirmed register interpretations, built-in IC address profiles and the open data requests for the register-aware communication log (milestone M6 in [ROADMAP.md](../ROADMAP.md)). This page moved out of the roadmap. It is reference data, not a plan.

## Confirmed register knowledge

| Address / offset | Confirmed interpretation | Current decode rule |
| --- | --- | --- |
| `0xFF000`–`0xFF003` | Chip ID (4 bytes) | Raw bytes, no product inference |
| `0xFF0FE ← 0x69` | Software Reset | Decode only for a write whose first byte is exactly `0x69` |
| `0xFF00E`, `0xFF01A`, `0xFF06A` | `REG_MODE_FG`, `REG_MODE_FG2`, `REG_WAKEUP_SOURCE` | Common names only; values remain raw |
| `0xFF43A`, `0xFF805`, `0xFF926` | TCON Calibration Enable, DP Error State, TP Ready Control | Common names only; values remain raw |
| Event Buffer `+0x00` | Event Buffer | Register/region label; protocol decode still requires explicit Event Buffer Version |
| Event Buffer `+0x50` | FW Command mailbox | Route write payload to the separate FW Command parser |
| FW Command `0x23` | Baseline Reset | Confirmed command name; remaining payload stays raw |
| FW Command `0x11`–`0x1C` (known subset) | Common scan-mode commands | Packed category/command byte; undefined values stay raw |
| FW Command `0x41`–`0x4C` | Common MP-test commands | English command name only; payload/result schema still pending |
| FW Command `0xD1`–`0xD4` | Common auto-engineering commands | English command name only; payload/handshake still pending |
| Event Buffer `+0x60` | FW State | Only `0xA3 = Normal Run` is semantic; `0x00`, `0xA1`, `0xA2`, and other values stay raw until defined |
| Event Buffer `+0x70` | Two-byte frame counter | Raw byte order, change tracking only; no endian guess |
| Event Buffer `+0x76` | DP Version | Raw bytes |
| Event Buffer `+0x78` | TP FW Version | Raw bytes |
| Common Buffer base | Bulk-data transfer buffer used through handshake + read | Region label only until handshake protocol is specified |
| History base | FW event history storage | Region label and raw bytes |

## Built-in IC address profiles

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

## Data still needed from FW/project owners

- Register profile identity: IC/project name, FW version range, Event/Common/History bases, aliases, and source document/golden provenance.
- Register contract: absolute address or base+offset, byte width, read/write permission, access side effects, and whether values are sampled or edge-triggered.
- Value semantics: enums, bitmaps/bitfields, masks, signedness, scale/unit, valid/reserved ranges, clamp/wrap behavior, and byte order where confirmed.
- Remaining FW Command table for Event Buffer `+0x50`: owner-confirmed aliases, request payload schema/length, response or handshake, timeout, side effects, and FW-version differences. `0x23` retains the product term Baseline Reset even though newer common FW also calls the operation force calibration.
- Common Buffer handshake: request/ready/ack registers and values, transfer length/format, chunking, completion/error states, and representative NDS logs.
- History layout: entry header, event IDs, length/timestamp rules, wrap behavior, clear behavior, and representative normal/failure logs.
- Reset/control sequences beyond `0xFF0FE ← 0x69`, including required delays and observable follow-up state.
- Golden evidence covering known values, unknown/reserved values, malformed byte counts, repeated reads, and profile-address collisions.
- Conflict policy when the same address/offset differs by customer project or FW version; decoding must require an explicit profile rather than silently choosing.
