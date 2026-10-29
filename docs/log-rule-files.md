# User value and log rule files (per-carmaker adaptation)

Status: Design document. [ADR 0006](adr/0006-log-rule-files.md) is accepted
for Decisions 1 and 4 and the clock part of Decision 3 (pull request #131
merged on 2026-10-29 at 15:41 Taipei, after the owner's approval at 15:01).
Decisions 2, 3 (Review Queue part) and 5 stay open (see
[decisions for the owner](#decisions-for-the-owner)). The first code, the rule
file model (parse, validate, canonical JSON and hash), is in pull request #132.
It reads no file and runs no rule.
Target version: 1.1.0 or later, not 1.0.0. This file is not an ADR.

The file has three parts:

1. "User value": the whole job from a customer log package to an answer, and
   where NFU helps in each step.
2. "Decisions for the owner": five questions. On 2026-10-29 the owner answered
   Decisions 1 and 4 and the clock part of Decision 3. Every other question
   is a pending owner decision with a recommendation.
3. The design of log rule files. They act in two of the seven steps: finding
   the cause and accumulating experience.

Every sample in this file is synthetic. The "Acme Motors" log format below is
invented. No real carmaker log, device name or firmware string appears here.

## User value

The goal: NFU shortens the time from receiving a customer log package to
delivering an answer that is backed by evidence.

NFU does not replace the engineer's judgment. It makes each step faster and
better supported, and it lets the result stay for the next case.

Marks used in this section:

- **[Doc]**: stated in an existing document, which is named.
- **[Assumption]**: the author's hypothesis. The owner has not confirmed it.

The "done today" column below is [Assumption] in every row. Nothing in this
section is validated against a real case.

### The user and the trigger

- [Doc] The user is a firmware or failure-analysis engineer. The
  [product specification](product-spec.md) ("Product outcome") says the
  application helps this engineer reproduce what the head unit would have
  observed from a captured touch stream.
- [Assumption] The trigger is a customer report of a ghost point or a false
  touch. The report comes with a package of logs: a touch capture and system
  logs such as a kernel log and a firmware log.

### Seven steps from log package to answer

For each step: how it is mostly done today, what NFU can do now, and what could
be added later. "Planned" means [ROADMAP.md](../ROADMAP.md) lists it. Everything
else in the "later" column is an idea. It has no schedule.

| Step | Done today [Assumption] | NFU can do now | Could be added later |
| --- | --- | --- | --- |
| 1. Intake: the customer sends a package | Check the file format and source. Open the files in a text editor or a private script | Open the supported capture formats offline. NFU never changes the source file | Format probing for the first raw formats (planned, FWU-02). Batch analysis of a folder. Live viewing (ideas) |
| 2. Judgment: is it a ghost point, a false touch, a stuck point, or something else? | Read the touch points by hand and rely on experience | Replay, heatmap and Review Queue turn the stream into pictures and a list of diagnostics. The engineer decides | Find and rank suspicious moments across the capture and logs. Jump between them. Filter by type (ideas; not designed here) |
| 3. Find the cause: where, when, and what the system did at that moment | Find the same moment in the system logs by hand and match times in the head | Markers and notes on a suspect moment. NFU shows no system log | **Log rule files** (this draft): lines that match a rule appear as events on the timeline with a plain description. Alignment of the log clock. Keyword search |
| 4. Fix: change firmware or parameters | The engineer changes them from the judgment | NFU does not change firmware. Its evidence shows where to look | Custom register profile import (planned). Its first version activates base addresses only, and register and command rows stay metadata ([Doc] ADR 0005). Showing register values as names and units is a separate proposal. It needs evidence and an activation contract. Raw Data Analysis (planned, FWU-01) |
| 5. Verify: is it fixed, and are there side effects? | Record again and compare by eye | Open the capture recorded before the change and the one recorded after, one at a time. Each keeps its source and settings identity. For one capture, the same settings and range give the same result, so that result can be reproduced ([Doc] product specification: stable identifiers are deterministic). Replaying one capture cannot show that a changed device stopped the fault | Two captures side by side, synchronized (idea; not designed). A report-rate view (idea) |
| 6. Reply: give the customer an answer with evidence | Take screenshots, write the text, attach files by hand | Export MP4, heatmap PNG, and CSV or JSON, with a manifest ([Doc] product specification) | One-page summary. An evidence bundle. De-identified export (ideas) |
| 7. Accumulate: what makes the next case faster? | Experience stays in people's heads and scattered documents | Not supported. The sidecar keeps the annotations of one capture only | **Log rule files** keep the signature of a solved case as data. A case library (idea; not designed here) |

[Doc] The product specification orders its priorities as replay fidelity, then
exportable evidence, then automated anomaly discovery. That order matches the
table: steps 5 and 6 are strong today, and steps 2 and 3 are mostly the
engineer's own work.

### Where log rule files act

Log rule files act in two steps only.

- Step 3, find the cause. Carmaker log formats differ, so the engineer reads
  each log by hand and matches times in the head. With a rule file, NFU
  recognizes the known lines, shows them as events with a plain description,
  and puts them next to the touch frames. The engineer reads fewer lines and
  keeps the match as evidence.
- Step 7, accumulate. The signature of a solved case is saved as data in a
  rule file. The next package with the same lines shows the same events
  without someone remembering them. A person can review, share and version the
  file.

They do not decide whether a package shows a problem (step 2). They do not
rank causes. Version 1 recognizes single lines only. A signature made of
several lines in a sequence is a later step.

### Five outcomes and how to measure them

| Outcome | What changes | How to measure |
| --- | --- | --- |
| Faster answers | The time from receiving the package to a reply with evidence gets shorter | Record the start and end time of each case |
| Fewer round trips | Video, heatmap and markers go out together, so the customer asks less | Count the follow-up requests per case |
| Not dependent on specific people | Carmaker experience lives in rule files, so a new engineer can follow it | Measure the time a new engineer needs to close a case alone |
| Fixes backed by evidence | Captures recorded before and after the change are compared, and each keeps its source and settings identity | Count how often the same problem is reopened |
| Faster over time | Solved cases stay, so a repeat problem is recognized | Count the share of cases that match an existing rule |

Log rule files feed three of the five: faster answers (step 3), not dependent
on specific people, and faster over time (step 7).

**None of the five outcomes has a baseline.** This draft therefore gives no
time and no percentage. One real past case (Decision 2) gives the first
baseline.

### Synthetic walk-through

This case is invented. A customer reports one ghost point near the top edge
during a 30-minute drive. The package holds one I2C capture and one log in the
invented Acme format used later in this file.

Today [Assumption]: the engineer loads the capture, scans the Review Queue,
marks a suspect frame, then reads the log by hand in a text editor and matches times
mentally.

With a rule file: the engineer selects the Acme rule file for the log. The log
shows `esd detected` and `reset` events. The engineer aligns one log event to
one capture frame, which sets the offset of the log clock (the owner asked for
a configurable time zone and offset, see Decision 3 and "Time"). The events
then appear next to the ghost point. The
engineer exports that range with the log events. The rule file keeps the
signature, so the next package with the same lines shows the same events
without manual reading.

### What would validate this

Decision 2: one real past case. Until then only synthetic cases exist, and
every [Assumption] above stays open.

## Decisions for the owner

Each decision below shows its status. A recommendation is what this draft
follows. It is not an accepted decision, and no silence from the owner accepts
it. A decision that has an owner answer says so, with the date and the
owner's words. PC-Commander relayed those words from the owner's chat. The
answers became part of [ADR 0006](adr/0006-log-rule-files.md) when the owner
approved pull request #131 on GitHub (files under `docs/adr/` are
owner-gated).

Twelve smaller questions from the earlier draft are folded into the first
four. The owner's answer to Decision 1 added a fifth. Items that need no
decision are in [dependencies and follow-ups](#dependencies-and-follow-ups).

### Decision 1: Direction and document form

Status: answered by the owner on 2026-10-29 ("Q7 ok"), as recommended. The
owner added that formats may also differ by project (see Decision 5). The
ADR still needs the owner's approval on GitHub.

Question: Is a data file per carmaker (a log rule file) the way NFU adapts to
different log formats?

- Recommendation (answered: ok): yes. A rule file holds keywords and .NET non-backtracking
  regular expressions. It holds no code. After the owner accepts, a new ADR
  records the decision and reuses the identity rules of ADR 0005 by reference.
  Files under `docs/adr/` are owner-gated.
- The recommendation also covers three details of this draft. The owner
  answered Q7 ("ok") for the whole recommendation. The ADR itself still waits
  for the owner's formal approval on GitHub, so the details below are not yet
  accepted contract text: the regular expression dialect is the .NET non-backtracking
  subset (no lookarounds, no backreferences, the editor lists the limits); a
  failed typed-capture conversion creates the event with an invalid field and a
  warning, so the evidence stays visible; the rule file is a new ADR, not an
  extension of ADR 0005, which covers register profiles only.
- Alternative: a code plug-in per carmaker. This draft does not recommend it,
  because the user could not review the plug-in as data.
- Unblocks: the ADR, and the format work in "Rule file format (version 1)".

### Decision 2: Real evidence

Status: pending owner decision.

Question: Can the owner provide one real past case, de-identified: the
customer's log package, how it was investigated, how long it took, and how the
reply went? The case should include the system log lines that a carmaker
wrote.

- Recommendation: pick one recent ghost-point or false-touch case.
- Until then: every "done today" statement in "User value" stays an assumption.
  This draft adds no feature that is specific to one log family. Ranking
  suspicious moments across a capture and logs (the "judgment" step) is not
  designed.
- Unblocks: the first baseline for the five outcomes, the choice of the first
  log families, and a separate draft for finding suspicious moments.

### Decision 3: Time and the Review Queue in version 1

Status: the clock part is answered by the owner on 2026-10-29. The Review
Queue part is a pending owner decision.

Owner answer, clock (2026-10-29): "Q9 時鐘要可以設定時區和 offset". The time
zone and the offset of the log clock must both be configurable. The answer
says nothing about drift correction or about how the offset is set. Those
two points are not decided. The recommendation below stays a recommendation.

Question: How far does version 1 go on aligning the log clock with the capture
clock, and do log events join the Review Queue?

- Clock, as designed after the owner's answer: the operator sets a time zone
  and an offset for each applied log. This draft recommends one-point
  alignment (pick one log event and one capture frame) as one way to set the
  offset, and recommends no drift correction in version 1. Both are
  recommendations, not owner decisions. The details are in "Time" and stay a
  proposal until the ADR is approved.
- Review Queue, recommendation: log events do not join the Review Queue in
  version 1. Revisit it after the owner has seen real log events.
- Alternative for the clock: two-point alignment (offset and drift). It costs
  more work and needs a real log that drifts.
- Unblocks: the "Time" section and the walk-through in "User value".

### Decision 4: Shared contracts with the register profile work

Status: answered by the owner on 2026-10-29 ("Q10 ok"), as recommended. The
ADR still needs the owner's approval on GitHub.

Question: How do log rule files share contracts with the custom register
profile import?

- Sidecar version. Recommendation: one schema bump for both, planned with the
  register profile import. A newer application loads existing reviews without
  inventing rule bindings (see "Sidecar and exports"). Alternative: two bumps.
  This draft sees no gain in two.
- Canonical hash input. The [register profile schema](register-profile-schema.md)
  prose and its code differ on the final newline (see "Identity and canonical
  form"). The owner's answer to Q10 ("Q10 ok") chose the recommendation for
  the rule file: rule files follow the code. Correcting the register profile
  prose stays separate work. This draft does not change that document.
- Unblocks: the sidecar fields in "Relation to existing events", and the hash
  rule in "Identity and canonical form".

### Decision 5: Carmaker and project scope

Status: pending owner decision. It comes from the owner's answer to Decision 1
on 2026-10-29: "另外車廠可能也有不同 可能需要 by 專案". Even within one carmaker
the log format may differ by project. This draft does not treat any selection
rule as decided.

Question: How does a rule file get its carmaker and project scope, and how is
one rule file chosen for a capture?

- Option A, recommendation: one independent rule file per carmaker and
  project. The `scope` metadata names the carmaker and the project. There is
  no inheritance. Two projects that share most rules use two files, one of
  them a copy. This keeps identity simple: `(profileId, contentSha256)` names
  exactly the rules that ran.
- Option B: a project file that extends a carmaker file. It names the base by
  identity and adds, replaces or disables rules by rule `id`. The identity of
  the result must cover both files, and the merge result must be previewable.
  This needs its own schema design, so it is a later schema version.
- Selection in both options: the operator chooses the rule file explicitly for
  each log. The application never picks a rule file from a log's content, its
  file name or a project name. The choice and its identity are stored in the
  sidecar. A project file in the sense of NFU's own project settings may
  remember the last choice for convenience, and the sidecar stays the record.
- Unblocks: the `scope` field of the format, the picker in the user interface,
  and whether the schema needs an `extends` field in version 1.

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
| `scope` | Opaque metadata: carmaker label, project label, platform and firmware note. Stored and shown. Never executed or compared. The project label comes from the owner's answer to Decision 1. How a rule file is chosen for a project is Decision 5. |
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

- object properties in the fixed order of the schema. An optional property
  that is absent is left out, and an empty `contains`, `captures`, `map` or
  `fields` counts as absent, so adding an optional property in a later
  schema version does not change the hash of an older file. `ignoreCase` is
  written only when it is true;
- strings use the relaxed escaping of System.Text.Json, so characters such as
  `+` and `<` stay readable. The indented form uses two spaces and LF line
  breaks;
- `eventTypes` and `rules` sorted by `id`, `tests` sorted by `name`, `captures`
  sorted by `name`, and the `contains` list sorted, all with ordinal
  comparison;
- every map whose keys the file author chooses is sorted by key with ordinal
  comparison: the `map` of an `enum` capture, `scope` (`carmaker`, `note`,
  `platform`, `project`), and the `fields` map of each self-test expectation
  (`tests[].expect[].fields`). A new map in a later schema version follows the
  same rule;
- the `expect` list of a self-test is sorted by `rule` with ordinal comparison.

Existing ambiguity, not decided here: the
[register profile schema](register-profile-schema.md) prose says canonical
serialization has "one final newline" and that this gives the same content
identity. The code (`NvtRegisterProfileFile.ComputeContentSha256`) hashes the
compact bytes without that newline, while `SerializeCanonical` writes indented
JSON plus a final LF. Rule files follow the code. The owner accepted that recommendation on
2026-10-29 (Decision 4). Correcting the register profile prose is a separate
small change.

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
  [ROADMAP](../ROADMAP.md). `{{` and `}}` write a literal brace. Any other
  brace must belong to a `{name}` placeholder, and `name` must be a capture
  of the rule. The validator rejects any other use of a brace.

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
(see [Decision 1](#decision-1-direction-and-document-form)).

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
allowed. Inline comments `(?#...)` and the `x` option (`(?x)`) are not
allowed either, because comments hide pattern text from the limit check
below. A character class cannot contain an unescaped `[`. Write `\[` for a
literal bracket. This rejects the subtraction form `[a-z-[aeiou]]` and the
`[[:name:]]` form, because the check below must end a class at the same place
as the engine, and those forms change where a class ends.

Guarantee, stated narrowly: for one fixed pattern, matching time is linear in
the length of the line. This does not limit the time to build a pattern or the
total work of many patterns. Version 1 therefore also has these limits, all
checked at validation:

- file size: 1 MiB, for the bytes read and for the canonical saved form. A
  compact file below 1 MiB is refused when its indented saved form is above it;
- rules: 500; event types: 100; tests: 1000;
- pattern length: 1024 characters; capture groups per pattern: 20;
- repeat count in a counted quantifier such as `{n,m}`: 1000; group nesting
  depth: 8; alternations in one pattern: 64;
- line length: 8192 characters. A longer line is not matched. It creates a
  warning diagnostic and the line stays visible as unmatched.

Whether a file is valid never depends on elapsed time, so the same file passes
or fails the same way on any machine. The limits above are counts and lengths
that bound the cost of building a pattern. Building one pattern is a single
synchronous call that cannot be interrupted, so the limits, not a timer, bound
it. The numbers are proposals for version 1. Performance tests confirm them
and are the only place where elapsed time is measured.

Validation runs in the background, not on the interface thread. It builds the
patterns one at a time and checks the cancellation token between patterns. Preview,
test, apply and the command line check the token between patterns and between
lines. A cancelled run writes no output.

### Timestamp

```json
"timestamp": {
  "regex": "^\\[\\s*(?<ts>\\d+\\.\\d+)\\]",
  "format": "secondsSinceBoot"
}
```

The `regex` must declare a named group `ts`. The text of that group is the
value that `format` reads.

`format` is one of a closed list: `secondsSinceBoot`, `epochSeconds`,
`isoOffset`, `timeOfDay`. The list grows only through a schema version. The
value is stored as the time of the log clock. The `timestamp` object may also
carry an optional `timeZone` default (proposal, see "Time"). The first
implementation of the rule file model does not have this property. It
waits until the owner accepts the default. A line without a
readable time still creates events. Such an event has no log time and is shown in log order.

The accepted text of each format is fixed and culture-invariant:

- `secondsSinceBoot` and `epochSeconds`: ASCII digits with one optional
  decimal point. `epochSeconds` is UTC.
- `isoOffset`: ISO 8601 date and time with an explicit offset. A value
  without an offset is a conversion failure.
- `timeOfDay`: `HH:mm:ss` with optional fraction. It carries no date and no
  time zone. The application never applies the local date or the local time
  zone of the PC. Only a time zone that the operator configured applies (see
  "Time").

A time that does not fit its format follows the conversion failure rule in
[Typed captures](#typed-captures): the event is created with no log time and a
warning.

The log clock is not the capture clock. The rule file does not align them.
The operator sets a time zone and an offset for each applied log (see
[Time](#time) and [Decision 3](#decision-3-time-and-the-review-queue-in-version-1)).

### Admission checks of the first implementation

The rule file model checks more than the tables above state. This section
lists every check, so a reader does not have to read the code.

PR #132 listed seven assumptions and asked the owner to confirm them (question
Q11, with the recommendation to keep all seven). The owner answered "Ok" on
2026-10-29 at 18:45 Taipei. PC-Commander read it as confirming all seven. They
are:

1. The `timestamp` `regex` must declare a group named `ts`.
2. A message writes a literal brace as `{{` or `}}`. Any other brace must be a
   `{name}` placeholder of a capture of the rule.
3. A pattern cannot use inline comments `(?#...)` or the `x` option in any
   spelling.
4. An absent optional property is left out of the canonical JSON, and an empty
   list or map counts as absent. This includes the `map` of a capture that is
   not an `enum`. An `enum` capture still needs a non-empty `map`.
5. Strings use the relaxed escaping of System.Text.Json.
6. The first model has no `timeZone` default in the rule file.
7. A rule needs a `message`. IDs, event type IDs and test names use the
   register profile ID pattern. Capture names match
   `[A-Za-z_][A-Za-z0-9_]{0,63}`. Text fields are at most 1024 characters.

The model makes other choices that the "Ok" does not cover. The owner has not
answered these. The recommendation is to keep them as written.

| Check | Needs the owner |
| --- | --- |
| A character class cannot contain an unescaped `[` (see "Regular expression dialect"). | Yes |
| Labels (`displayName`, `scope` values, `map` labels, `message`): not empty, no leading or trailing white space, at most 1024 characters, no control characters. | Yes |
| `contains` entries: not empty, at most 1024 characters, no line break, no duplicates (ordinal). Spaces at the start or end are kept. | Yes |
| `map` keys of an `enum` capture: not empty, at most 1024 characters, no line break. | Yes |
| Empty `eventTypes`, `rules` and `tests` lists are accepted. | Yes |
| A self-test `line` may be empty. It has up to 8192 characters (the line limit), not 1024. The values of expected `fields` have up to 1024 characters and no line break. | Yes |
| A self-test lists a rule at most once in `expect`. | No. It follows from the canonical order. |
| The canonical saved form is at most 1 MiB. Parse, seal and save check it. | No. It restates the file size limit. |
| An admitted document holds immutable copies of its lists and maps. | No. It is a code rule. |

## Evaluation

Two things are kept apart.

- Content evaluation is a pure function of the rule file and the log bytes.
  It returns matches: line number, byte offset, rule ID, captured fields and
  log time. The same inputs always give the same matches in the same order.
- Event projection attaches the evidence reference ID and the evaluated log
  hash to each match. The result is a log event with its
  [event key](#event-key). Two references with identical bytes give identical
  matches and different events.

Determinism of content evaluation does not depend on the culture, the time
zone or the sort order of the PC:

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
5. Matches are ordered by line number, then by rule `id`. Projection keeps
   that order.

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
| Evidence reference ID | The evidence reference |
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
sidecar loader reports a missing file, a hash mismatch, and a file that is too
large or still growing, without blocking the review. Other read or access
errors while hashing are not caught there. They reach the open step, which
reports "Review open failed". A copied value could be missing, or could name other bytes
than the ones evaluated. The reference hash is only an expectation to compare
with.

A binding is the sidecar record that one rule file identity applies to one
evidence reference. It stores the evaluated log hash that the operator accepted.
The check runs on first use and again on every reopen and every reapply. The
expectation is the stored binding hash when a binding exists. Otherwise it is
the reference hash.

| Stored binding | Reference hash | Evaluated hash | Result |
| --- | --- | --- | --- |
| None | Present | Equal to the reference hash | Log events are available |
| None | Absent | Computed | Log events are available and marked "no reference hash". The operator confirms the evaluated hash once, and that creates the binding |
| None | Present | Different from the reference hash | Log events are unavailable. The raw log stays visible. The operator can bind the evaluated hash explicitly (see "Rebinding") |
| Present | Any | Equal to the binding hash | Log events are available. If the reference hash is present and different, the application shows that the binding was set explicitly |
| Present | Any | Different from the binding hash | Log events are unavailable. The raw log stays visible. The operator can rebind |
| Any | Any | Not computable (file missing, unreadable, read error) | Log events are unavailable. The evidence reference, the log path, the expected hash and the failure reason stay visible. The raw log content shows only when the file can be read |

Rebinding is an explicit operator action. It adds a new binding entry with the
new evaluated hash, the operator note and the time. The earlier entry stays in
the history. The evidence reference is not edited, so its hash stays as it was.
Events from before the rebinding keep their keys, because the evaluated hash is
part of the key. They are not renamed.

In every unavailable case the application shows no derived interpretation. It
keeps the evidence reference, the log path, the expected hash and the failure
reason visible. It shows the raw log content only when the file can be read.
It does not fall back to the reference hash.

The "not computable" row is proposed behavior. The existing loader does not
yet turn every read or access error into an unavailable state. Version 1 of
this feature handles them so that log events become unavailable and the
review still opens.

#### Event key

The event key is the full tuple:

```text
(evidenceReferenceId, profileId, contentSha256, evaluatedLogSha256,
 lineByteOffset, ruleId)
```

`evidenceReferenceId` is the ID of the evidence reference that supplied the
log. The sidecar workspace requires reference IDs to be unique, but it does not
require different content. Two references can hold identical bytes, with
different source paths, notes or alignment. Their events have different keys
and are never merged, so each keeps its own provenance and alignment.

`profileId` and `contentSha256` are the rule file identity. Rule IDs are unique
only inside one file, so two rule files, or two revisions of one file, can
produce events with the same `ruleId` on the same line. They have different
keys and are never merged. One rule creates at most one event per line in one
reference, so the key is unique. The line number is stored but is not part of the key, because
the byte offset already names the line.

A short ID may be shown to the operator, for example the first 16 hex
characters of the SHA-256 of the key written as one UTF-8 line. It is only a
display alias. Storage, sidecars and exports use the full key.

### Time

The log clock and the capture clock are different clocks. A boot-relative time
such as `12.345678` has no fixed relation to the time in a capture.
Multi-clock alignment is a deferred capability in the
[product specification](product-spec.md). On 2026-10-29 the owner decided that
the time zone and the offset of the log clock must both be configurable
(Decision 3). This section turns that into a design. The rule file format does
not depend on it, and the details below are a proposal until
[ADR 0006](adr/0006-log-rule-files.md) is approved.

Holds without any further decision:

- No automatic alignment. The application never guesses a time zone or an
  offset.
- Log events are always listed in log order in a log panel, with their log
  time.
- Events with no readable log time are never drawn on the timeline.

Proposal (configurable time zone and offset):

- Time zone. The operator can set a time zone for the applied log. It applies
  to log times that are wall-clock times: `timeOfDay`, and `isoOffset` values
  are already exact and ignore it. `epochSeconds` is UTC and ignores it.
  `secondsSinceBoot` has no wall clock and ignores it. A `timeOfDay` value has
  no date, so the time zone only shifts its time of day and never invents a
  date.
- A rule file may carry a default time zone in the `timestamp` object, for
  example for a carmaker whose logs always use one zone. The operator's choice
  for an applied log overrides it. The default is part of the rule file and its
  hash. The override is part of the sidecar.
- A time zone is written as an IANA zone name or a fixed offset such as
  `+09:00`. The sidecar stores the name and also the UTC offset that applied at
  the moment the operator chose it, because the zone database can differ
  between machines.
- Offset. The operator sets a signed offset between the log clock and the
  capture clock, in milliseconds. One way to set it is to pick one log event and
  one capture frame and state that they are the same moment (one-point
  alignment). The operator can also type the offset.
- Recommendation: no drift correction in version 1. The owner has not decided
  this. Q9 did not mention drift.
- An event is drawn on the timeline only after an offset is set. The time zone,
  the offset and the operator note go into the sidecar, as ADR 0003 says that
  alignment lives in the sidecar.
- The time zone and the offset change where an event is drawn. They do not
  change the event key, because the key uses the byte offset of the line and not
  the time.

### Sidecar and exports

- The sidecar records which rule file, by identity, was applied to which
  evidence reference, together with the evaluated log hash. This needs a new
  sidecar schema version. Opening a newer sidecar in an older application
  already fails safely. ADR 0005 also needs a sidecar version for custom
  register profiles. Both changes should share one version bump.
- Migration of existing reviews. A sidecar of the current schema version 1
  opens in the newer application with no rule binding, no time zone and no
  offset. The application invents none of them. A binding, a time zone or an
  offset exists only after an explicit operator action. The application does not rewrite the opened file.
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
  (see [Decision 3](#decision-3-time-and-the-review-queue-in-version-1)).

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
nvt-replay log-rules apply    <rules.json> <log> [--evidence-reference <id>] [--json <events.json>]
```

`apply` needs the evidence reference ID, because the event key contains it.
The ID is an explicit argument. It is the ID that the review sidecar uses for
that log. With the same rule file, log bytes and reference ID, `apply` writes
the same JSON as the application export. Golden tests compare that file byte
for byte. Without `--evidence-reference`, `apply` writes content matches only.
It writes no event key and says so.

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

## Dependencies and follow-ups

These items need no owner decision of their own. They are the follow-up work
that the four decisions leave behind.

1. Provenance metadata format. ADR 0005 leaves the associated metadata format
   to its implementation. Rule file provenance waits for that format.
2. Where the engine ships. Whether it stays in this application or moves to a
   shared Core module depends on the Launcher direction and is out of scope.
3. Finding suspicious moments. A separate draft, after Decision 2 gives a real
   case.
