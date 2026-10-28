# User value and log rule files (per-carmaker adaptation)

Status: Proposed design draft. No product code, no accepted contract.
Target version: 1.1.0 or later, not 1.0.0. This file is not an ADR. If the
owner accepts the direction, the decisions move into an ADR (see
[open items](#open-items)).

The file has two parts. The first part, "User value", starts from the work the
user must finish. The rest is the design of log rule files, which is one piece
of that work.

Every sample in this file is synthetic. The "Acme Motors" log format below is
invented. No real carmaker log, device name or firmware string appears here.

## User value

Marks used in this section:

- **[Doc]**: stated in an existing document, which is named.
- **[Assumption]**: the author's hypothesis. The owner has not confirmed it.

Nothing in this section is validated against a real case.

### The user and the trigger

- [Doc] The user is a firmware or failure-analysis engineer. The
  [product specification](product-spec.md) ("Product outcome") says the
  application helps this engineer reproduce what the head unit would have
  observed from a captured touch stream.
- [Assumption] The trigger is a customer report of a ghost point or a false
  touch. The report comes with a package of logs: a touch capture and system
  logs such as a kernel log and a firmware log.

### The answers the user needs

All four are [Assumption].

1. Does this package show a problem?
2. At which moment did it happen?
3. What is the likely cause: the IC, the firmware, the host, or the
   environment?
4. What evidence can go back to the customer?

### What NFU answers today

[Doc] The product specification says a user can load a supported capture,
locate the first important event, replay its context, annotate it, and export
an MP4 in under two minutes. The application has Raw Explorer, replay, heatmap,
markers, a Review Queue and exports with a manifest.

NFU therefore answers "what happened on the touch bus". It covers question 4
well: replay and exports are evidence. It covers question 2 only by hand and
with the help of the Review Queue, which lists the diagnostics that the decoder
raises.

[Doc] The specification orders its priorities as replay fidelity, then
exportable evidence, then automated anomaly discovery. So questions 1 and 3 are
mostly the engineer's own work today. No existing document describes ranking
suspicious moments across a capture and the system logs.

### Three gaps

All three are [Assumption].

| Gap | Question it serves | What is missing | Where this file covers it |
| --- | --- | --- | --- |
| Find | 1, 2 | Pick suspicious moments automatically and rank them, across the capture and the logs | Not designed here (see open items) |
| Explain | 3 | Put system log events and raw data on one timeline. Carmaker log formats differ, so data files adapt the tool | The rest of this file: log rule files |
| Accumulate | 1 (faster next time), 4 | Save a solved case as a rule so the next package is recognized | Rule files are the container for the log-line half. Matching a case across sources is not designed here |

### Synthetic walk-through

This case is invented. A customer reports one ghost point near the top edge
during a 30-minute drive. The package holds one I2C capture and one log in the
invented Acme format used later in this file.

Today [Assumption]: the engineer loads the capture, scans the Review Queue,
marks a suspect frame, then reads the log by hand in a text editor and matches times
mentally.

With a rule file: the engineer selects the Acme rule file for the log. The log
shows `esd detected` and `reset` events. The engineer aligns one log event to
one capture frame (manual alignment is a pending owner decision, see "Time").
The events then appear next to the ghost point. The engineer
exports that range with the log events. The rule file keeps the signature, so
the next package with the same lines shows the same events without manual
reading.

Version 1 recognizes single lines only. A signature made of several lines in a
sequence is a later step.

### What would validate this

The owner provides one real past case, de-identified: the customer's log
package, how it was investigated, how long it took, and how the reply went. Until
then only synthetic cases exist, and every [Assumption] above stays open.

## Log rule files: purpose

Each carmaker writes kernel and firmware logs in its own line format. An
operator needs to see the lines that matter (resets, ESD recovery, lost frames)
as events next to a capture. The operator should not need a new application
release for each carmaker.

A log rule file is a small data file the operator edits. It holds keywords,
regular expressions and field captures. It maps log lines to event types. The
application reads the file, applies it to a raw log, and shows the resulting
events. It serves the "Explain" and "Accumulate" gaps above.

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

Two byte sequences exist and they differ. The draft keeps them apart.

- Saved bytes: the canonical JSON, indented, in UTF-8 without a BOM, with one
  final LF.
- Hash input bytes: the same canonical content written as compact JSON, with
  no indentation, no final newline, and without the `contentSha256` field.
  `contentSha256` is the lowercase SHA-256 of these bytes.

Canonical order, so that equivalent files give the same bytes and the same
hash:

- object properties in the fixed order of the schema;
- `eventTypes` and `rules` sorted by `id`, `tests` sorted by `name`, `captures`
  sorted by `name`, and the `contains` list sorted, all with ordinal
  comparison;
- keys of an `enum` `map` and of `scope` sorted by ordinal comparison.

Existing ambiguity, not decided here: the
[register profile schema](register-profile-schema.md) prose says canonical
serialization has "one final newline" and that this gives the same content
identity. The code (`NvtRegisterProfileFile.ComputeContentSha256`) hashes the
compact bytes without that newline, while `SerializeCanonical` writes indented
JSON plus a final LF. This draft follows the code for rule files. Whether the
register profile prose should be corrected is a separate change.

The hash proves integrity. It does not prove that the rules are correct. The
hash covers `fileKind`, so a rule file and a register profile never have the
same hash by accident.

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
  `string`, `integer`, `hex`, or `enum`. The raw captured text is always kept.
  The conversion rules are in [Typed captures](#typed-captures).
  An `enum` capture has a `map` from captured text to a label. Text that is not
  in the map is kept as the raw text and marked unmapped. The application never
  invents a label.
- `message`: a template. It may use `{name}` placeholders for captured fields.
  It has no operators, no functions and no conditions. This is the same
  placeholder style as the `{col}` placeholder planned in FWU-09 of the
  [ROADMAP](../ROADMAP.md).

Every rule is evaluated on every line, independently. A line can match more
than one rule and then creates more than one event. The result does not depend
on the order of the rules. This is why the file can be sorted into canonical
form without changing its meaning. The preview marks lines that match more than
one rule so the operator can decide whether that is intended.

### Typed captures

All conversions are culture-invariant and use ASCII characters only. The raw
captured text is stored next to every converted value.

| Type | Accepted text | Value |
| --- | --- | --- |
| `string` | Any text | The text, unchanged |
| `integer` | `-?[0-9]+`, ASCII digits only, no `+`, no separators, no spaces | Signed 64-bit integer |
| `hex` | `0x` or `0X`, then 1 to 16 ASCII hex digits | Unsigned 64-bit integer |
| `enum` | Exact ordinal match of the text with a key of `map` | The label of that key |

Failure rules:

- A capture group that does not take part in the match (an optional group)
  gives an absent field. The field is neither empty nor zero. A `message`
  placeholder for an absent field renders as `<absent>`.
- Text that does not fit the accepted form, or does not fit the 64-bit range,
  is a conversion failure. The event is still created. The field has no value
  and is marked invalid. The raw text stays. The application writes a warning
  diagnostic with the rule ID, the field name and the source location.
  It never substitutes zero or any other value.
- `enum` text that is not a key of `map` is not a failure. The field is marked
  unmapped and keeps its raw text.
- Non-ASCII digits, which the `\d` class of the regular expression engine
  accepts, fail the lexical form check of `integer` and `hex`.

Whether a conversion failure should create the event, or suppress it, is a
choice this draft makes for visibility of evidence. The owner can change it
(see [open items](#open-items)).

### Regular expression dialect

The dialect is the .NET regular expression syntax, run with the non-backtracking
engine only. There is no fallback to the backtracking engine.

The engine, not this list, decides what is accepted. Every expression in the
file is built with the non-backtracking engine at validation, including the
`timestamp` expression and every `regex` in `rules`. The validator rejects
what the engine rejects, and shows the engine's message. The known rejected
constructs are: lookahead and lookbehind, backreferences, atomic groups,
balancing groups, conditionals such as `(?(name)yes|no)`, and the `\G` anchor.
Options that change the engine, such as right-to-left matching, are not
allowed.

Guarantee, stated narrowly: for one fixed pattern, matching time is linear in
the length of the line. This does not limit the time to build a pattern or the
total work of many patterns. Version 1 therefore also has these limits, all
checked at validation:

- file size: 1 MiB;
- rules: 500; event types: 100; tests: 1000;
- pattern length: 1024 characters; capture groups per pattern: 20;
- time to build one pattern: 1 second. A pattern that needs more is rejected;
- line length: 8192 characters. A longer line is not matched. It creates a
  warning diagnostic and the line stays visible as unmatched.

Preview, test, apply and the command line all check the cancellation token
between lines. A cancelled run writes no output.

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

The accepted text of each format is fixed and culture-invariant:

- `secondsSinceBoot` and `epochSeconds`: ASCII digits with one optional
  decimal point. `epochSeconds` is UTC.
- `isoOffset`: ISO 8601 date and time with an explicit offset. A value
  without an offset is a conversion failure.
- `timeOfDay`: `HH:mm:ss` with optional fraction. It carries no date and no
  time zone. The application never applies the local date or the local time
  zone of the PC.

A time that does not fit its format follows the conversion failure rule in
[Typed captures](#typed-captures): the event is created with no log time and a
warning.

The log clock is not the capture clock. The rule file does not align them.
Whether the application offers any alignment is a pending owner decision (see
[Time](#time) and [open items](#open-items)).

## Evaluation

The evaluation is a pure function of the rule file and the log bytes. The same
inputs always give the same events in the same order. Determinism does not
depend on the culture, the time zone or the sort order of the PC:

- regular expressions run with culture-invariant matching. A case-insensitive
  match, including an inline `(?i)`, uses invariant rules. `ignoreCase` on
  `contains` means ordinal case-insensitive comparison;
- all number and time conversions follow [Typed captures](#typed-captures) and
  [Timestamp](#timestamp);
- `id` values are ASCII. Every sort of IDs and keys uses ordinal comparison.

1. Read the log once. Compute its SHA-256 from the same bytes that are
   evaluated (the evaluated hash, see
   [What a log event holds](#what-a-log-event-holds)). Read the bytes as strict
   UTF-8. Split lines at LF or CRLF. The line keeps its byte offset and its
   1-based line number.
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
| Log source ID | The evidence reference |
| Evaluated log SHA-256 | The application, computed from the exact bytes it evaluated |
| Line number and byte offset | The log (`SourceLocation`) |
| Raw line text | The log |
| Rule ID and rule file identity `(profileId, contentSha256)` | The rule file |
| Event type, category, severity | The rule file |
| Message | The rule `message` template |
| Captures: name, type, raw text, mapped label or "unmapped" | The rule file and the line |
| Log time: value, format, clock kind | The `timestamp` block, when it reads |

#### Log hash

The hash in an event is the evaluated hash. It is never copied from the
evidence reference. `ReplayEvidenceReference.Sha256` is optional, and the
sidecar loader reports a hash mismatch or a failed verification without
blocking the review. A copied value could be missing, or could name other bytes
than the ones evaluated. The reference hash is only an expectation to compare
with:

| Reference hash | Evaluated hash | Result |
| --- | --- | --- |
| Present | Equal | Log events are available |
| Absent | Computed | Log events are available. They are marked "no reference hash". The sidecar binding stores the evaluated hash after the operator confirms it once |
| Present | Different | Log events are unavailable. The raw log stays visible. The operator can accept the evaluated hash as a new binding. That creates a new identity and does not rename the old events |
| Present or absent | Not computable (file missing, unreadable, read error) | Log events are unavailable. The raw log stays visible |

In every unavailable case the application keeps the raw evidence and shows no
derived interpretation. It does not fall back to the reference hash.

#### Event key

The event key is the full tuple:

```text
(profileId, contentSha256, evaluatedLogSha256, lineByteOffset, ruleId)
```

`profileId` and `contentSha256` are the rule file identity. Rule IDs are unique
only inside one file, so two rule files, or two revisions of one file, can
produce events with the same `ruleId` on the same line. They have different
keys and are never merged. One rule creates at most one event per line, so the
key is unique. The line number is stored but is not part of the key, because
the byte offset already names the line.

A short ID may be shown to the operator, for example the first 16 hex
characters of the SHA-256 of the key written as one UTF-8 line. It is only a
display alias. Storage, sidecars and exports use the full key.

### Time

The log clock and the capture clock are different clocks. A boot-relative time
such as `12.345678` has no fixed relation to the time in a capture.
Multi-clock alignment is a deferred capability in the
[product specification](product-spec.md). **Whether and how to align is a
pending owner decision** (open item 4). The rule file format does not depend on
it. This section only states what holds without any decision, and then the
smallest step this draft proposes.

Holds without any decision:

- No automatic alignment. The application never guesses an offset.
- Log events are always listed in log order in a log panel, with their log
  time.
- Events with no readable log time are never drawn on the timeline.

Proposal, pending owner decision (one-point manual alignment):

- An event is drawn on the timeline only after the operator sets an offset.
  The operator picks one log event and one capture frame and states that they
  are the same moment. The offset and the operator note go into the sidecar.
  Alignment lives in the sidecar, as ADR 0003 says.
- If the owner chooses two-point alignment or no alignment, only this list
  changes. The user-value walk-through that places events next to the ghost
  point depends on this proposal.

### Sidecar and exports

- The sidecar records which rule file, by identity, was applied to which
  evidence reference, together with the evaluated log hash. This needs a new
  sidecar schema version. Opening a newer sidecar in an older application
  already fails safely. ADR 0005 also needs a sidecar version for custom
  register profiles. Both changes should share one version bump.
- Migration of existing reviews. A sidecar of the current schema version 1
  opens in the newer application with no rule binding and no alignment. The
  application invents neither. A binding or an alignment exists only after an
  explicit operator action. The application does not rewrite the opened file.
  Saving writes the new schema version, and the operator is told that older
  applications can no longer open the saved file.
- Migration of rule files. A rule file with a newer `schemaVersion` is rejected
  by an application that does not know it. A later schema version never
  changes a file in place. Moving a rule file to a new schema creates a new
  immutable revision with a new hash. The original file and its identity stay.
  Existing bindings keep pointing at the original identity until the operator
  binds the new revision. Events derived from the two identities have
  different event keys.
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
validation, so `<computed>` stands in for it. The sample is written in reading
order. The tool saves it in the canonical order described above.

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

1. A real past case. The owner provides one de-identified case: the customer's log package, how it was investigated, how long it took and how the reply went. Until then the value statements in "User value" stay [Assumption]. Recommendation: pick one recent ghost-point or false-touch case.
2. The "Find" gap. Ranking suspicious moments across a capture and logs is not designed here. Recommendation: design it after item 1, from a real case, as a separate draft.
3. Real log families. Which carmaker formats come first, and which log kinds
   (Linux kernel, Android, firmware simulator)? Needs de-identified samples from
   the owner. Recommendation: keep the version 1 file format as drafted. Add no
   format-specific feature until a real sample asks for it.
4. Clock alignment. Is the manual one-point offset enough, or does the owner
   want two-point alignment (offset and drift)? Recommendation: one point in
   version 1.
5. Review Queue. Should log events join the Review Queue, and under which
   category and lifecycle? Recommendation: not in version 1. Decide after the
   owner has seen real log events.
6. Document form. Make this a new ADR, or extend ADR 0005? ADR 0005 is
   specific to register profiles and says the first import activates base
   addresses only. Recommendation: a new ADR after the owner accepts this
   direction. It reuses the ADR 0005 identity and provenance rules by
   reference. Files under `docs/adr/` are owner-gated.
7. Sidecar version. Share one schema bump with the custom register profile
   work, or bump twice? Recommendation: one bump, planned with the register
   profile import.
8. Regex dialect. Is the .NET non-backtracking subset acceptable for the
   people who write rule files? Lookarounds and backreferences are not
   available. Recommendation: accept the subset for version 1 and list the
   limits in the editor.
9. Provenance metadata format. ADR 0005 leaves the associated metadata format
   to its implementation. This work waits for that format.
10. Where it ships. Whether the engine stays in this application or moves to a
    shared Core module is out of scope. It depends on the Launcher direction.
11. Conversion failure. This draft creates the event with an invalid field and a
    warning, so the evidence stays visible. The alternative is to suppress the
    event. Recommendation: keep the draft behavior.
12. Register profile prose. The register profile schema prose and code differ on
    the final newline in the hash input (see "Identity and canonical form").
    Recommendation: a separate small change that corrects the prose to match
    the code. This draft does not change that document.
