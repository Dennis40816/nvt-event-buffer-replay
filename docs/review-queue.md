# Review Queue and ASIL lifecycle

The Review Queue is a view over immutable `ReplayDiagnostic` evidence. Human
review state is stored separately and cannot change decoded frames, transport
records, raw bytes, source locations, or diagnostic details.

## Event model

Events are categorized as protocol diagnostic, device state, behavior finding,
QA result, or annotation. Severity is always shown as text (`INFO`, `WARNING`,
`ALARM`, or `ERROR`) in addition to color.

Repeated events share a correlation group while each `ReviewOccurrence` keeps
its original diagnostic and logical-frame link. ASIL Assert and Clear events
share one group. Counter warnings distinguish the named counter; frame-counter
samples group together while preserving every read.

## ASIL state

Two independent state tracks are maintained:

- Captured: `Asserted` or `Cleared`, derived only from parser output.
- Human: `Open`, `Acknowledged`, or `Resolved`, plus `Expected` or
  `FalsePositive` disposition.

The displayed lifecycle is Asserted → Acknowledged → Cleared → Resolved. A
captured ASIL alarm cannot be resolved until it is acknowledged and a Clear is
present. Disposition does not suppress or delete the alarm.

The Common Inspector prints the original ASIL byte and numeric error code;
reserved codes 3–7 stay reserved. EMS remains a raw bitmap.

## Replay behavior

Replay pauses on Alarm and QA Fail by default. The Review Queue checkbox changes
both gates for the current in-memory session. MAX playback searches the skipped
range and stops at the first matching occurrence instead of jumping over it.

Selecting a group exposes each occurrence. Selecting an occurrence seeks the
logical timeline and synchronizes Paint, decoded fields, physical transaction,
raw bytes, and source line. Filters do not destroy groups or occurrences.

## Device-state reads

Reads at the following 51927 register addresses become grouped device-state
evidence:

- `0x99060`: FW State; only `0xA3 = Normal Run` is labeled, other values remain raw.
- `0x99070`: two-byte frame-counter sample; raw byte order is retained and no
  numeric endianness is guessed. Change from the prior captured sample is noted.
- `0x99076`: DP Version, raw bytes.
- `0x99078`: TP FW Version, raw bytes.

Routine `Info` device-state samples remain part of diagnostics and analysis
evidence, but are shown in Raw Explorer's register activity strip and Inspector
instead of the actionable Review rail. The Review rail keeps warnings, alarms,
ASIL lifecycle groups, QA results, and operator annotations in focus.

