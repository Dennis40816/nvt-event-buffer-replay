# Common Event Buffer 0x82–0x85

The operator selects the Event Buffer Version. Source probing, chip address,
slave address, and byte count never infer it.

## Shared 80-byte layout

| Offset | Size | Meaning |
| ---: | ---: | --- |
| `0x00` | 1 | Reserved `[7:5]`, TP ASIL error `[4]`, valid touch count `[3:0]` |
| `0x01` | 70 | Ten seven-byte finger slots |
| `0x47` | 8 | Version-specific tail before CRC |
| `0x4F` | 1 | CRC8 |

Each finger slot contains `ID[7:4]`, `Type[3:2]`, `Status[1:0]`, big-endian
X/Y, and two preserved reserved bytes. Status values are No Finger, Enter,
Move, and Break. Types are Finger, Glove, Palm, and Reserved.

`NumTouches` is the number of valid touches after the current FW calculation.
A released contact may therefore appear as Break in a zero-touch frame. A
following All Break frame has zero touches and all 70 finger bytes equal to
`FF`. The decoder does not mistake the all-`FF` unused slots for real Break
contacts.

## Version tails

- `0x82`: two Button bytes, ASIL byte, raw Knob status/angle marked TODO,
  three reserved bytes, CRC.
- `0x83`: two Button bytes, ASIL byte, five reserved bytes, CRC.
- `0x84`: three Button bytes, ASIL byte, three reserved bytes, bus counters,
  CRC.
- `0x85`: three Button bytes, ASIL byte, EMS/Palm byte, two reserved bytes,
  bus counters, CRC.

Button content is valid only when its first byte is `F8`; its mapping is
project-defined. ASIL error codes 0, 1, and 2 mean None, Initial Fail, and
Runtime Fail; 3–7 remain Reserved. Short, Open, a nonzero error code, or the TP
ASIL header bit produces an Alarm. The deprecated 0x82 Resume Flag stays
numeric. EMS is exposed as its raw four-bit bitmap and LSB-first bits without
invented bit names. Bus counters are four-bit values expected to clamp at 15;
only an observed increase after the session baseline emits a warning.

Global Palm On suppresses touch reporting and is expected with All Break.
Coordinate-reported Palm is a separate host-reporting mode. An inconsistent
combination emits a warning but captured bytes are never changed.

## CRC and invalid data

CRC uses polynomial `0x1D`, init `0`, non-reflected input/output, xorout `0`
over offsets `0x00`–`0x4E`; offset `0x4F` is the captured CRC. `0x83` and
`0x84` CRC behavior is capture-verified. `0x82` and `0x85` remain Provisional.

CRC-failed frames remain inspectable as evidence but are marked ineligible for
Host State. Short frames are rejected with a structured length diagnostic.
Trailing bytes are preserved as unconsumed evidence. No decoder repairs,
clamps, or rewrites the input.
