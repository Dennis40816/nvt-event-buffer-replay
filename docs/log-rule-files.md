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
