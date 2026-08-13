# NVT Event Buffer Replay product specification

Status: accepted design, 2026-08-14.

## Product outcome

The application helps a firmware or failure-analysis engineer reproduce what
the vehicle head unit would have observed from a captured touch stream. Its
priority is replay fidelity, followed by exportable evidence, then automated
anomaly discovery. A user must be able to load a supported capture, locate the
first important event, replay its context, annotate it, and export an MP4 in
under two minutes.

The product is C#-only at runtime and fully offline. Avalonia is the desktop
surface. The existing Python decoder is an oracle for semantic parity and a
place for simulation and exploratory protocol work, not a deployed dependency.

## Input and configuration

MVP source adapters:

1. NDS Communication Log
2. Saleae decoded I2C
3. KingstVIS decoded I2C
4. DSL decoded I2C
5. Excel decoded I2C
6. Canonical I2C TXT

Source format detection and event-buffer interpretation are separate:

- source probing returns ranked candidates, confidence, and reasons;
- a high-confidence source may be preselected but remains visible;
- event-buffer family/version is supplied by a preset or confirmed manually;
- NDS transport properties such as slave address and byte count never imply an
  event-buffer version;
- Desay 0x97 always requires Standard or Benz Palm selection;
- incomplete configuration opens Raw Explorer without semantic parsing.

A Capture Bundle has one primary touch capture and optional evidence files.
Project Presets are portable `.nvtproject.json` files. Per-capture markers,
alignment, acknowledgement, and source hashes live in `.nvtreplay.json`.

## Protocol boundary

Common 0x82 through 0x85 share one 80-byte core layout and version-specific
tails. Desay 0x97 uses a transport assembler before payload decoding:

```text
physical transaction -> assembler -> logical frame -> payload decoder
```

The Desay assembler consumes a one-byte probe at `0x99000`, then the
`1 + 5*n` continuation at `0x99001`. Zero touches still require phase two.
Missing, orphan, intervening, invalid-length, or invalid-CRC packets create
diagnostics and do not update Host State. No decoder repairs captured bytes.

Each result is traceable from finding to parsed event, logical frame, physical
records, and source offsets. Stable identifiers are deterministic from source
identity and offsets, not random per process.

## Replay model

The timeline has three levels:

- physical transactions;
- logical events;
- evidence events.

Recorded Time preserves captured intervals. Frame Time spaces logical events
uniformly for motion inspection. Unreliable timestamps are explicitly marked
as a synthetic timeline. Long idle gaps are compressed during normal playback
but remain represented and can be replayed at real duration.

The Paint surface renders both the current Reported Frame and persistent Host
State. Invalid packets leave the prior Host State intact. The capture ending
with active contacts creates a warning rather than an invented All Break.
Seeking uses sparse Host State checkpoints; backward stepping is supported,
continuous reverse playback is not required for MVP.

## Workspace

The primary composition is a restrained engineering workstation:

```text
project/parser/status/export
review queue | Paint | Event/Raw/Transport/Evidence inspector
multi-track timeline
replay controls, clock, speed, range
```

Paint owns the largest area. Panels resize and collapse but do not require a
general docking framework. Quick Replay and Analysis are visibility presets of
one workspace, not separate applications. Dark is the default; light mode is
supported. Color is never the only severity or touch-type signal.

Logical events are the default step unit. Physical transactions are available
through drill-down. Playback supports 0.1x through 10x and Max, event/frame
navigation, looped In/Out ranges, keyboard operation, and a command palette.

## Diagnostics, findings, and human review

Special events retain both category and severity. Categories are protocol
diagnostic, device state, behavior finding, QA result, and annotation.
Severities are Info, Warning, Alarm, and Error.

ASIL lifecycle separates captured and human state:

```text
Asserted -> Acknowledged -> Cleared -> Resolved
```

Alarm and QA Fail pause replay by default. Repeated events collapse into an
occurrence range while retaining every raw occurrence. Human disposition can
mark Expected or False Positive but cannot delete evidence. Review Queue is a
filtered view over the same events, ordered by urgency.

## QA and kernel/FW evidence

Evidence precedence is scoped, not destructive: formal specification,
FW-owner project override, golden observation, QA record, then heuristic.
Conflicts remain visible. QA natural language does not directly control a
decoder; confirmed expectations may later become versioned rules.

MVP permits a QA case ID on annotations and a raw Kernel/FW log attachment.
Semantic kernel parsing, Evidence Catalog UI, executable QA rule imports, and
multi-clock alignment are post-MVP. PID1615 supplies useful FW simulator log
vocabulary but is not sufficient evidence for Linux/Android kernel grammar.

## Export

MVP exports selected-range MP4, heatmap PNG, parsed and diagnostic JSON/CSV,
replay sidecar, and a source/config/hash manifest. UI and export share a
`ReplayScene`; MP4 is not a screen recording. C# renders frames and pipes them
to a reviewed FFmpeg build. If no encoder is available, PNG sequence remains
available.

HTML/PDF reports, evidence ZIP packaging, and redaction UI are post-MVP.

## Non-functional requirements

- Windows 10/11 is the first-class tested platform; core code remains portable.
- No telemetry, uploads, or automatic updates by default.
- MVP performance gate: 1 GB, one million physical records, eight-hour
  timeline, cancellable indexing, responsive UI, and 60 FPS normal Paint.
- Indexes use 64-bit offsets and compact in-memory offsets/checkpoints. A
  persistent disk cache is added only if measurements require it.
- Background jobs write temporary output and atomically replace the target on
  success; cancellation never overwrites existing output.
- Production target is 18,000-22,000 handwritten C#/XAML lines. At 25,000 an
  architecture review is mandatory. 30,000 is a hard cap. Tests are counted
  separately; generated code cannot hide product logic.

## Deferred capabilities

- Acute until a real export is available
- raw waveform adapters
- growing files, socket input, and hardware live input
- multiple capture tabs and synchronized comparison
- arbitrary third-party DLL plugins
- persistent disk index
- kernel semantic parser and multi-source time alignment
- Evidence Catalog and QA rule importer
- interactive HTML/PDF and `.nvtevidence.zip`
