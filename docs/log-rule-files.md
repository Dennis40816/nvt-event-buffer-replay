# Log rule files (per-carmaker adaptation)

Status: Proposed design draft. No product code, no accepted contract.
Target version: 1.1.0 or later, not 1.0.0. This file is not an ADR. If the
owner accepts the direction, the decisions move into an ADR (see
[open items](#open-items)).

Every sample in this file is synthetic. The "Acme Motors" log format below is
invented. No real carmaker log, device name or firmware string appears here.

## Purpose

Each carmaker writes kernel and firmware logs in its own line format. An
operator needs to see the lines that matter (resets, ESD recovery, lost frames)
as events next to a capture. The operator should not need a new application
release for each carmaker.

A log rule file is a small data file the operator edits. It holds keywords,
regular expressions and field captures. It maps log lines to event types. The
application reads the file, applies it to a raw log, and shows the resulting
events.

## Boundaries

These rules come from existing documents. The proposal keeps all of them.

- Data only. A rule file has no script, callback, plugin, assembly name or URI.
  The [product specification](product-spec.md) defers arbitrary third-party
  DLL plugins, and the register profile schema has no executable hook.
- Source bytes, line order and line locations stay immutable. Events are a
  derived layer that points back to the source line
  ([ADR 0003](adr/0003-replay-time-and-evidence.md)).
- A meaning the owner has not defined is shown as unknown or unavailable. A
  line that no rule matches creates no event.
- A rule file is explicitly selected, never guessed from a file name, a path
  or the log content ([ADR 0005](adr/0005-custom-register-profile-import.md)
  uses the same principle for register profiles).
- The first version works on synthetic samples only. Real Linux and Android
  logs are on the ROADMAP evidence list. They need de-identified samples from
  the owner before any rule can be called validated.
- Core adoption and the Launcher direction are out of scope.

## Terms

| Term | Meaning |
| --- | --- |
| Log source | One raw log file attached as evidence (a kernel log, firmware log or other file). |
| Log line | One line of the log source, with a 1-based line number and a byte offset. |
| Rule file | One JSON document that holds event types, rules and self-tests. |
| Rule | One match definition: what a line must contain, which fields to capture, and which event type to create. |
| Event type | A named kind of event, such as `touch.reset`. A rule creates events of exactly one type. |
| Log event | One event created by one rule from one log line. |

## Rule file format (version 1)

The format reuses the envelope of the
[register profile schema](register-profile-schema.md). It does not invent a
second identity scheme.

| Field | Contract |
| --- | --- |
| `schemaVersion` | Exactly `1`. Unknown versions fail closed. |
| `fileKind` | Exactly `logRules`. A register profile file has no such field, so each loader rejects the other kind with a clear error. |
| `profileId` | Stable ASCII ID (`A-Z`, `a-z`, `0-9`, `.`, `_`, `-`), same pattern as a register profile. |
| `displayName` | Operator-facing name. |
| `scope` | Opaque metadata: carmaker label, platform and firmware note. Stored and shown. Never executed or compared. |
| `lineEncoding` | `utf-8` in version 1. |
| `timestamp` | Optional. How to read the time from a line (see below). |
| `eventTypes` | The event types the file may create. |
| `rules` | The match rules. |
| `tests` | Self-tests: sample line, expected rule, expected fields. |
| `contentSha256` | Lowercase SHA-256 of the canonical content, excluding this field. |

Unknown JSON properties fail closed, as in the register profile schema.

### Identity and canonical form

The identity of a rule file is the pair `(profileId, contentSha256)`. This
matches ADR 0005. Two files with the same `profileId` and different content
are two immutable revisions. Neither replaces the other without an explicit
operator choice.

Canonical serialization follows the register profile rules: fixed property
order, `eventTypes` and `rules` sorted by `id`, `tests` sorted by `name`,
UTF-8 without a BOM, one final newline. Equivalent files give the same bytes
and the same hash. The hash proves integrity. It does not prove that the rules
are correct.

The identity space is shared in the sense that the hash covers `fileKind`.
A rule file and a register profile can never have the same hash by accident.

### Event types

```json
{ "id": "touch.reset", "displayName": "Touch reset",
  "category": "deviceState", "severity": "Warning" }
```

- `id`: ASCII, unique in the file.
- `category`: one of the existing categories in the
  [Review Queue](review-queue.md): `protocolDiagnostic`, `deviceState`,
  `behaviorFinding`, `qaResult`, `annotation`.
- `severity`: one of the existing severities `Info`, `Warning`, `Alarm`,
  `Error`. A rule may state its own severity. Without it, the event type value
  applies.

### Rules

```json
{ "id": "reset-reason",
  "eventType": "touch.reset",
  "contains": ["tp_drv:", "reset reason="],
  "regex": "reset reason=(?<code>0x[0-9a-f]+) \\((?<name>[a-z]+)\\)",
  "captures": [
    { "name": "code", "type": "hex" },
    { "name": "name", "type": "string" }
  ],
  "message": "Reset, reason {name} ({code})" }
```

- `id`: ASCII, unique in the file.
- `contains`: optional list of literal substrings. The line must contain all
  of them. The comparison is ordinal and case-sensitive unless the rule sets
  `ignoreCase`. This is the keyword part. It is cheap and it filters lines
  before any regular expression runs.
- `regex`: optional. If present, it must match the line. A rule needs at least
  one of `contains` and `regex`.
- `captures`: the named groups of `regex` that become event fields. Each
  capture has a `name` that is a named group of the regex, and a `type`:
  `string`, `integer` (decimal), `hex`, or `enum`. An `enum` capture has a
  `map` from captured text to a label. Text that is not in the map is kept as
  the raw text and marked unmapped. The application never invents a label.
- `message`: a template. It may use `{name}` placeholders for captured fields.
  It has no operators, no functions and no conditions. This is the same
  placeholder style as the `{col}` placeholder planned in FWU-09 of the
  [ROADMAP](../ROADMAP.md).

Every rule is evaluated on every line, independently. A line can match more
than one rule and then creates more than one event. The result does not depend
on the order of the rules. This is why the file can be sorted into canonical
form without changing its meaning. The preview marks lines that match more than
one rule so the operator can decide whether that is intended.

### Regular expression dialect

The dialect is the .NET regular expression syntax, run with the non-backtracking
engine. This gives linear-time matching, so a bad expression cannot stall the
application. The following constructs are not available in this engine, so the
validator rejects them with a message: lookahead and lookbehind, backreferences,
atomic groups, balancing groups.

Limits in version 1, all checked at validation:

- file size: 1 MiB;
- rules: 500; event types: 100; tests: 1000;
- line length: 8192 characters. A longer line is not matched. It creates a
  warning diagnostic and the line stays visible as unmatched.

### Timestamp

```json
"timestamp": {
  "regex": "^\\[\\s*(?<ts>\\d+\\.\\d+)\\]",
  "format": "secondsSinceBoot"
}
```

`format` is one of a closed list: `secondsSinceBoot`, `epochSeconds`,
`isoOffset`, `timeOfDay`. The list grows only through a schema version. The
value is stored as the time of the log clock. A line without a readable time
still creates events. Such an event has no log time and is shown in log order.

The log clock is not the capture clock. Version 1 does not align them (see
[Relation to existing events](#relation-to-existing-events)).

## Evaluation

The evaluation is a pure function of the rule file and the log bytes. The same
inputs always give the same events in the same order.

1. Read the log as strict UTF-8. Split lines at LF or CRLF. The line keeps its
   byte offset and its 1-based line number.
2. A line that is not valid UTF-8 is not matched. It creates a warning
   diagnostic with the line location. The raw bytes stay available.
3. For each line, read the timestamp when the file defines one.
4. For each rule, test `contains`, then `regex`. A match creates one log event.
5. Events are ordered by line number, then by rule `id`.

Unmatched lines are counted and stay in the log view. A count of unmatched
lines is not an error. A rule that never fires on a given log is reported in
the preview but is not an error either.

## Relation to existing events

Today the application has three related things.

- Capture diagnostics. Decoding a capture creates `ReplayDiagnostic` values.
  Each one has a severity, a code, a message, a source record ID and a source
  location. They belong to the capture.
- Evidence references. A replay marker in the
  [sidecar](replay-sidecar.md) can point to a raw Kernel, FW or other log file
  with an optional SHA-256. The reference is evidence only. The application
  does not read the log.
- Review state. The [Review Queue](review-queue.md) holds the ASIL lifecycle
  (`Asserted`, `Acknowledged`, `Cleared`, `Resolved`) for captured events.

Log events are a new layer next to these. They do not replace any of them.

- A log event is not a capture diagnostic. A log line is a statement by another
  system. It is not part of the captured bus evidence. Log events therefore do
  not enter the diagnostic stream of a capture and do not change capture
  decoding. This keeps the physical, logical and evidence timelines apart, as
  [ADR 0003](adr/0003-replay-time-and-evidence.md) requires.
- A log event points to the evidence reference it came from. It never edits the
  log, the reference or the capture.

### What a log event holds

Every field is either copied from the log or produced by a named rule.

| Field | Source |
| --- | --- |
| Log source ID and SHA-256 | The evidence reference |
| Line number and byte offset | The log (`SourceLocation`) |
| Raw line text | The log |
| Rule ID and rule file identity `(profileId, contentSha256)` | The rule file |
| Event type, category, severity | The rule file |
| Message | The rule `message` template |
| Captures: name, type, raw text, mapped label or "unmapped" | The rule file and the line |
| Log time: value, format, clock kind | The `timestamp` block, when it reads |

The stable ID is deterministic: the first 16 characters of the log SHA-256,
the line number and the rule ID. The same log and the same rule file always
give the same IDs.

### Time

The log clock and the capture clock are different clocks. A boot-relative time
such as `12.345678` has no fixed relation to the time in a capture.
Multi-clock alignment is a deferred capability in the
[product specification](product-spec.md). Version 1 takes the smallest step:

- No automatic alignment. The application never guesses an offset.
- Log events are always listed in log order in a log panel, with their log
  time.
- An event is drawn on the timeline only after the operator sets an offset.
  The operator picks one log event and one capture frame and states that they
  are the same moment. The offset and the operator note go into the sidecar.
  Alignment lives in the sidecar, as ADR 0003 says.
- Events with no readable log time are never drawn on the timeline.

### Sidecar and exports

- The sidecar records which rule file, by identity, was applied to which
  evidence reference. This needs a new sidecar schema version. Opening a newer
  sidecar in an older application already fails safely. ADR 0005 also needs a
  sidecar version for custom register profiles. Both changes should share one
  version bump.
- If the rule file named in the sidecar is missing, invalid or changed, the log
  events are unavailable. The raw log stays visible. There is no fallback to
  another rule file.
- Log events export as their own JSON and CSV files. The manifest records the
  rule file identity. The existing analysis exports of a capture do not change.
- Review Queue and the ASIL lifecycle do not include log events in version 1
  (see [open items](#open-items)).

## Preview and test

One engine does the work for preview, test and apply. A preview is not a second
implementation. This is the same rule the MP4 preview follows.

### Preview

The operator opens a rule file and a sample log. The preview shows:

- each line with its result: unmatched, or the rules that matched, the
  captured fields and the created event;
- counts per rule, and the number of unmatched lines;
- rules that never fired on this log;
- lines that matched more than one rule;
- enum captures with unmapped text;
- lines the engine skipped, with the reason (invalid UTF-8, too long).

The preview runs off the UI thread and can be cancelled. The view keeps the
first 10,000 results. The counts stay exact. The preview never writes anything
and never changes the rule file.

### Validation and self-tests

- Schema validation checks version, `fileKind`, unknown properties, ID
  uniqueness, limits, regex syntax and dialect, capture names against regex
  groups, `message` placeholders against capture names, and the hash.
- Each entry in `tests` holds a sample line and the expected result: the
  matching rules and the captured values, or an empty list for "no rule
  matches". Self-test lines must be synthetic or de-identified.
- A schema error makes the file unusable. A failing self-test makes the file
  unavailable for selection and shows which test failed. The operator can
  still open the file in preview to fix it.

### Command line

A headless entry makes the same checks possible in scripts and in CI. The
names below are proposals.

```text
nvt-replay log-rules validate <rules.json>
nvt-replay log-rules test     <rules.json>
nvt-replay log-rules apply    <rules.json> <log> [--json <events.json>]
```

`apply` writes the same JSON as the application export. Golden tests compare
that file byte for byte.

## Import and export

Import follows ADR 0005. Import is not selection.

1. The operator chooses a rule file.
2. The application validates it and runs its self-tests.
3. It shows the ID, scope, hash, counts of event types, rules and tests, the
   self-test result and the provenance status. The operator confirms.
4. The file becomes available. It is applied to a log only after the operator
   selects it for that log.

Other points:

- Editing never changes a file in place. "Save as new revision" writes a new
  canonical file with a new hash. The old file stays. The same `profileId` with
  different content is a distinct immutable revision, never a silent
  replacement.
- Export writes the canonical bytes of the file. Sharing a review sidecar
  includes the canonical rule file in the package, as ADR 0005 decided for
  register profiles.
- No folder discovery. No automatic detection of the carmaker from a file
  name, a path or a log line.
- Provenance (source document or sample reference, evidence SHA-256, review
  status) is not in the JSON. ADR 0005 puts it in associated metadata bound to
  the file hash. Rule files use that same mechanism. This draft defines no
  second one.
- A rule file for a real carmaker may contain carmaker-specific strings. This
  repository is public, so such files stay out of it. Only synthetic example
  files are committed, under the test fixtures.

## Synthetic example

The log below is invented. The "Acme Motors" format does not exist.

```text
[    1.204511] tp_drv: probe done, ic_count=1
[   12.345678] tp_drv: esd detected cnt=3, recovering
[   12.402190] tp_drv: reset reason=0x04
[   15.000120] tp_drv: frame lost seq=118
[   15.000900] input: unrelated line
[   20.511000] tp_drv: reset reason=0x07
```

The rule file below is shown without a real hash. The tool computes
`contentSha256` from the canonical form. A value written by hand would fail
validation, so `<computed>` stands in for it.

```json
{
  "schemaVersion": 1,
  "fileKind": "logRules",
  "profileId": "acme.example.v1",
  "displayName": "Acme Motors example (synthetic)",
  "scope": { "carmaker": "Acme Motors (synthetic)", "platform": "example", "note": "invented format" },
  "lineEncoding": "utf-8",
  "timestamp": { "regex": "^\\[\\s*(?<ts>\\d+\\.\\d+)\\]", "format": "secondsSinceBoot" },
  "eventTypes": [
    { "id": "touch.esd", "displayName": "ESD detected", "category": "deviceState", "severity": "Warning" },
    { "id": "touch.frameLost", "displayName": "Frame lost", "category": "behaviorFinding", "severity": "Warning" },
    { "id": "touch.reset", "displayName": "Touch reset", "category": "deviceState", "severity": "Warning" }
  ],
  "rules": [
    { "id": "esd-detected", "eventType": "touch.esd",
      "contains": ["tp_drv:", "esd detected"],
      "regex": "esd detected cnt=(?<cnt>\\d+)",
      "captures": [ { "name": "cnt", "type": "integer" } ],
      "message": "ESD detected, count {cnt}" },
    { "id": "frame-lost", "eventType": "touch.frameLost",
      "contains": ["tp_drv:", "frame lost"],
      "regex": "frame lost seq=(?<seq>\\d+)",
      "captures": [ { "name": "seq", "type": "integer" } ],
      "message": "Frame lost, sequence {seq}" },
    { "id": "reset-reason", "eventType": "touch.reset",
      "contains": ["tp_drv:", "reset reason="],
      "regex": "reset reason=(?<code>0x[0-9a-f]+)",
      "captures": [
        { "name": "code", "type": "enum",
          "map": { "0x01": "power-on", "0x04": "watchdog" } }
      ],
      "message": "Reset, reason {code}" }
  ],
  "tests": [
    { "name": "esd-basic",
      "line": "[   12.345678] tp_drv: esd detected cnt=3, recovering",
      "expect": [ { "rule": "esd-detected", "fields": { "cnt": "3" } } ] },
    { "name": "unrelated-line",
      "line": "[   15.000900] input: unrelated line",
      "expect": [] }
  ],
  "contentSha256": "<computed>"
}
```

Expected result on the sample log:

| Line | Result |
| --- | --- |
| 1 | Unmatched |
| 2 | `touch.esd`, `cnt` = 3, log time 12.345678 (boot clock) |
| 3 | `touch.reset`, `code` = `0x04`, label `watchdog` |
| 4 | `touch.frameLost`, `seq` = 118, log time 15.000120 |
| 5 | Unmatched |
| 6 | `touch.reset`, `code` = `0x07`, **unmapped**. The raw text is kept. No label is invented. |

Lines 1 and 5 create no event. Line 6 shows the "unknown stays unknown" rule.

## Not in version 1

- Negative match (`unless`), conditional severity, numeric comparison. These
  wait for the deterministic expression language of FWU-09.
- Records that span several lines.
- Encodings other than UTF-8.
- Embedding a rule file in a sidecar.
- Automatic carmaker or log-family detection.
- Folder discovery of rule files.
- Any meaning for a real Linux or Android log. That needs owner evidence first.

## Open items

Each item needs an owner decision or owner evidence. The recommendation is the
default this draft follows.

1. Real log families. Which carmaker formats come first, and which log kinds
   (Linux kernel, Android, firmware simulator)? Needs de-identified samples from
   the owner. Recommendation: keep the version 1 file format as drafted. Add no
   format-specific feature until a real sample asks for it.
2. Clock alignment. Is the manual one-point offset enough, or does the owner
   want two-point alignment (offset and drift)? Recommendation: one point in
   version 1.
3. Review Queue. Should log events join the Review Queue, and under which
   category and lifecycle? Recommendation: not in version 1. Decide after the
   owner has seen real log events.
4. Document form. Make this a new ADR, or extend ADR 0005? ADR 0005 is
   specific to register profiles and says the first import activates base
   addresses only. Recommendation: a new ADR after the owner accepts this
   direction. It reuses the ADR 0005 identity and provenance rules by
   reference. Files under `docs/adr/` are owner-gated.
5. Sidecar version. Share one schema bump with the custom register profile
   work, or bump twice? Recommendation: one bump, planned with the register
   profile import.
6. Regex dialect. Is the .NET non-backtracking subset acceptable for the
   people who write rule files? Lookarounds and backreferences are not
   available. Recommendation: accept the subset for version 1 and list the
   limits in the editor.
7. Provenance metadata format. ADR 0005 leaves the associated metadata format
   to its implementation. This work waits for that format.
8. Where it ships. Whether the engine stays in this application or moves to a
   shared Core module is out of scope. It depends on the Launcher direction.
