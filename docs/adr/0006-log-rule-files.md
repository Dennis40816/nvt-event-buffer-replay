# ADR 0006: Log rule files

Status: Draft. Proposed, not accepted. Nothing in this file is a decision until the owner writes an answer in the answer fields below and the status line changes to Accepted.

## Context

Customers send NFU a capture and, often, the system log of the host that ran the touch controller (a Linux or Android kernel log, a firmware log or another text file). Each carmaker writes a different log format. An engineer reads these logs by hand to find the lines that matter, such as a driver warning or a reset, and to see when they happened.

[`docs/log-rule-files.md`](../log-rule-files.md) proposes a data file per carmaker, called a log rule file. It holds keywords and regular expressions, and no code. NFU applies the file to a raw log and shows the matching lines as log events next to the capture. That document is a proposal. It was reviewed and merged as a draft design (pull requests #126 and #130) and it states that it is not a contract.

The owner asked for this work on 2026-10-28: editable keywords that adapt NFU to the log of each carmaker.

This ADR is the place to fix the direction before any code under `src/` exists. Files under `docs/adr/` and every change under `src/` need the owner's approval.

## Constraints

These come from ADRs that are already accepted. They are not open.

- Source bytes, record order, timestamps and locations stay immutable ([ADR 0003](0003-replay-time-and-evidence.md)). A log event is a derived result. It points back to a log line and never rewrites it.
- Physical, logical and evidence time stay distinct ([ADR 0003](0003-replay-time-and-evidence.md)). A log clock is not the capture clock until the operator aligns them.
- A meaning the owner has not defined is shown as unknown or unavailable. A missing, invalid or changed rule file leaves log events unavailable. There is no fallback to another rule file.
- Rule files are declarative data. They contain no scripts, callbacks or executable decoders ([ADR 0005](0005-custom-register-profile-import.md) sets the same rule for register profiles).
- The product runs on C# and .NET 10 alone ([ADR 0001](0001-csharp-native-product.md)). A regular expression engine comes from .NET.
- Private logs, firmware and customer data stay out of this public repository. Samples in the repository are synthetic.

## Decisions

Each decision has a question, the recommendation of the design document, and an answer field for the owner. A recommendation is not an answer. An empty answer field means the decision is open.

### Decision 1: Direction and document form

Question: Is a data file per carmaker (a log rule file) the way NFU adapts to different log formats?

Recommendation: Yes. A rule file holds keywords and .NET non-backtracking regular expressions, and no code. This ADR then records the decision and reuses the identity rules of ADR 0005 by reference. The same answer settles three details:

- The regular expression dialect is the .NET non-backtracking subset (no lookarounds, no backreferences). The editor lists the limits.
- A failed typed-capture conversion creates the event with an invalid field and a warning, so the evidence stays visible.
- The rule file is described by this new ADR. It does not extend ADR 0005, which covers register profiles only.

Alternative: a code plug-in per carmaker. The design document does not recommend it, because the operator could not review a plug-in as data.

Owner answer: _(empty)_

### Decision 2: Real evidence

Question: Can the owner provide one real past case, de-identified: the customer's log package, how it was investigated, how long it took, and how the reply went? The case should include the system log lines that a carmaker wrote.

Recommendation: Pick one recent ghost-point or false-touch case. Until it exists, every statement about how the work is done today is an assumption, and no feature specific to one log family is added. Samples stay synthetic.

Owner answer: _(empty)_

### Decision 3: Time and the Review Queue in version 1

Question: How far does version 1 go on aligning the log clock with the capture clock, and do log events join the Review Queue?

Recommendation: The engineer aligns one log event to one capture frame by hand (one-point offset, no drift correction). Log events do not join the Review Queue in version 1. Revisit both after the owner has seen real log events.

Alternative: two-point alignment (offset and drift). It costs more work and needs a real log that drifts.

Owner answer: _(empty)_

### Decision 4: Shared contracts with the register profile work

Question: How do log rule files share contracts with the custom register profile import (ADR 0005)?

Recommendation:

- Sidecar version: one schema bump for both, planned together. A newer application loads existing reviews without inventing rule bindings. Alternative: two bumps.
- Canonical hash input: the register profile schema prose and its code differ on the final newline. Rule files follow the code. A separate small change corrects the register profile prose.

Owner answer: _(empty)_

## Consequences

If the owner accepts the recommendations:

- Version 1 needs: a rule file model and validator, an evaluation engine, a command line (`validate`, `test`, `apply`), event projection with hash binding, a sidecar version bump, export, and a user interface for import, preview and self-tests.
- The work is split into several pull requests, each under `src/` and each approved by the owner. The first code pull request is the rule file model in `Nvt.Replay.Sources`.
- The sidecar version bump waits for Decision 4 and is shared with the register profile import.
- Real log families wait for Decision 2.

If the owner rejects or changes a decision, this ADR is edited before any code is written.

This Draft is documentation only. It adds no code and accepts no part of the design document.
