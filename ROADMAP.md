# Roadmap

This is the only plan file of the repository. It has three lines of work. Every item has a target version, a status and a link. Older plan files (`TODO_0.0.3.md`, `TODO_NVT_FW_UTIL.md`, `UI_TODO.md`) were merged into this file and removed. Git history keeps them.

## Branches and versions

Owner version rules (2026-10-07):

- Only a version that goes to customers gets a formal tag and a GitHub Release. The number is chosen at release time. Customer-visible numbers are continuous, with no gaps.
- A minor version needs a large set of features. A small set raises the patch number.
- Internal development codes match the next customer version. No internal number is larger than the next customer version.
- To release, cut a release branch with the same name from the trunk. It is frozen and takes fixes only. The trunk keeps taking features and Core adoption, in the order they finish.
- A hotfix branches from the latest customer tag and merges back after the release.
- An internal tag marks one handoff group, and only after the whole group is merged into the trunk. Format: `dev/<trunk>/g<number>-<name>`. It has no Release and does not go through the Launcher.
- A customer who skips versions must still be able to update: the updater installs required intermediate versions first, settings and user data migrate directly from every customer version, and release notes list the accumulated changes.

Current state:

- Latest customer version: `v0.1.1`. The next customer version is `1.0.0`. The 0.x to 1.0.0 step is a normal major upgrade, not a skipped number.
- Trunk: branch `1.0.x` (was `0.2.0`). `VERSION` stays `0.1.1` until the release-preparation commit sets `1.0.0`.
- Hotfix line: branch `hotfix/<version>` from the latest customer tag, for example `hotfix/0.1.2` from `v0.1.1`. After the release, merge the hotfix branch back into the trunk.
- Core integration work on the trunk must not block a hotfix.
- Feature branches are named `feature/1.0.x/<topic>`. Branches merged before the rename keep their old names.

Handoff groups before `1.0.0`:

| Group | Content | Status | Internal tag |
| --- | --- | --- | --- |
| g1 | Core module adoption: build-time download, CI download, AtomicOutput, CsvQuoting, SourceFileNavigation, Core 0.2.0 package ([#33](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/33) to [#40](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/40)) | Done | `dev/1.0.x/g1-core-modules` |
| g2 | Hygiene and guards: ROADMAP, SuppressEvents, English handoff, README.zh-TW, ADR 0005, snapshot gaps, doc-sync (#41, #43 to #49) | In progress | `dev/1.0.x/g2-hygiene` |
| g3 | Core look and Core 1.0.0: theme look ([#42](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/42)), Core 1.0.0 upgrade, regenerated approved snapshots | Waiting for the NFC look pull request and the Core 1.0.0 release | `dev/1.0.x/g3-core-1.0-look` |

`1.0.0` ships after g1 to g3. Product features (the FWU items, Inspector layout, density, view split, custom register profile import from ADR 0005) go to `1.1.0` and later. IDs such as FWU-01 are historical and do not mean a version.

## Line 1: Hotfix

No open hotfix. Defects go to GitHub Issues with the `bug` label. A defect that hurts a released version gets a `hotfix/<version>` branch from the release tag.

| Item | Target | Status | Link |
| --- | --- | --- | --- |
| Register profile guard flag is not reset when the selection assignment throws (low risk, no known trigger) | 1.0.0 | Done. The fix is part of the `SuppressEvents` item in line 3 | [#43](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/43), closes [#39](https://github.com/Dennis40816/nvt-event-buffer-replay/issues/39) |

## Line 2: Product

### Next product slice: NVT FW UTIL

The shell becomes NVT FW UTIL (target 1.1.0 or later, planned, no link yet). Event Buffer Analysis (Raw Explorer, Decoded Events, Paint, Output) is one tool inside it, and recent captures live inside that tool. Raw Data Analysis is a second tool. The work waits for owner evidence (file formats, formulas, golden data). Until the owner gives it, derived values stay disabled or marked unavailable. The runtime is C# only. `nds_helper.frame_extractor` is a format reference only. Private repositories, fixtures and real captures never enter this public repository.

| ID | Item | Target | Status | Link |
| --- | --- | --- | --- | --- |
| FWU-01 | Raw Data Analysis as a separate tool with its own Load flow and source summary | 1.1.0 | Planned | none yet |
| FWU-02 | Input probing for the first supported raw formats (timestamped `DiffData` CSV with optional Button Data, timestamped plain-matrix logs, single rectangular matrix). The probe must report uncertainty | 1.1.0 | Waiting for representative files | none yet |
| FWU-03 | One original matrix frame at a time, with X/Y cell coordinates, values, frame navigation and source timestamp. Source bytes and frame order stay unchanged | 1.1.0 | Planned | none yet |
| FWU-04 | Current-frame analysis in a right inspector. The operator picks the metrics (average, minimum, maximum, standard deviation first). Scope, signedness and invalid-cell rules need a contract | 1.1.0 | Waiting for the metric contract | none yet |
| FWU-05 | TPMux2 slot. No numeric result until the owner gives inputs, formula, applicability and golden examples | after 1.1.0 | Waiting for the definition | none yet |
| FWU-06 | Before Diff and After Diff at import. Confirmed: Stop FW Before Diff is NF-normalized with common-mode removal not applied. The other states and the arithmetic need evidence | 1.1.0 | Waiting for evidence | none yet |
| FWU-07 | Responsive large captures: parse and index off the UI thread, bounded frame cache, calculate only enabled metrics. Verify with long captures | 1.1.0 | Planned | none yet |
| FWU-08 | Review the launcher and raw workspace design with the owner. Verify dark, light and narrow layouts | 1.1.0 | Design review pending | none yet |
| FWU-09 | NF-table reconstruction as an operator-editable ordered pipeline with reusable blocks, drag reorder and validated custom steps (placeholders such as `{col}`). Deterministic expression language, no unrestricted code. Original data stays untouched | 1.1.0 | Contract pending | none yet |
| FWU-10 | Raw Check analysis. Scope and rules are not defined. Do not infer checks from the name | after 1.1.0 | Waiting for scope | none yet |

Version route: customer `1.0.0` first, then `1.1.0` for these items, then `2.0.0`, which starts sharing the Core architecture (owner's provisional target, 2026-10-02).

### Register-aware communication log

| Item | Target | Status | Link |
| --- | --- | --- | --- |
| Custom register profile import (versioned JSON with validation and provenance) | 1.1.0 | Planned | none yet |
| Remaining FW data: register contracts, FW Command table, Common Buffer handshake, History layout, reset sequences, golden evidence | unscheduled (version set when the data arrives) | Waiting for FW and project owners | [register knowledge](docs/register-knowledge.md) |

### UI and quality

| Item | Target | Status | Link |
| --- | --- | --- | --- |
| Reorganize the Inspector Protocol, Raw and Review layers: remove repeated fields, fix the hierarchy, stay readable at 320, 380 and 520 px | 1.1.0 | Planned | none yet |
| Comfortable and Compact density, or 100%, 110% and 125% UI scale, with verification and approved snapshots | 1.1.0 | Planned | none yet |
| Approved snapshots added: Raw and Decoded at 1180 px with both rails open, Output loading and cancel, Data package PNG | 1.0.0 | Done | [#47](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/47) |
| Split the Paint, Output, Review and Inspector XAML trees into separate views (code-behind is already split into partial classes) | 1.1.0 | Planned | none yet |
| Fixed 11-slot array for the host state checkpoint (micro-optimization; the 100k and long-loop gates show no bottleneck) | unscheduled (version set if profiling shows a need) | Deferred | none yet |

### Evidence gates and open questions

Every item waits for owner evidence or a decision. The target is unscheduled; a version is set when the evidence arrives.

| Item | Target | Status | Link |
| --- | --- | --- | --- |
| Acute real-export validation with continuation-row evidence | unscheduled | Waiting for evidence | none yet |
| Raw waveform exports, real Linux and Android kernel log capture, scoped QA records for rule conversion | unscheduled | Waiting for evidence | none yet |
| Multi-source clock alignment and comparison workflows | unscheduled | Waiting for design | none yet |
| Complete register maps and FW command dictionaries | unscheduled | Waiting for FW and project owners | [register knowledge](docs/register-knowledge.md) |
| Private Desay Standard and Benz Palm golden, needed before a formal release | unscheduled | Waiting for the owner to provide it | none yet |
| Tools launcher behavior to confirm during FWU-01: keep full-width Paint and Output surfaces; pause playback but keep the session when returning to Tools; route direct Event Buffer file open to Event Buffer Analysis and a no-file start to Tools | 1.0 | Open question | none yet |
| Raw calculation details to confirm: whether a separately labeled "normalized Before" matrix is a distinct input and which conversion directions are required; NF table format, dimensions and index mapping, value type, rounding and clamping, version dependence and one golden; units of the basic metrics (average, minimum, maximum, standard deviation); the first set of built-in function blocks and which placeholders may be used together with `{col}`; TPMux2 frame relationships, output meaning, output unit and failure behavior; whether Button Data takes part in any calculation; rebuild step scope (cell, column, frame or capture), the exact meaning of `{col}`, expression syntax, error handling, precision and a golden where step order changes the result; Raw Check inputs, presentation and golden | 1.0 | Open questions for FWU-02, FWU-04, FWU-05, FWU-06, FWU-09 and FWU-10 | none yet |
| Approved Paint PNG built from a public, reproducible fixture (a private KingstVIS long golden was only checked by hand; no private capture enters the repository) | 1.1.0 | Open | none yet |
| Side-by-side check of the approved reference images (control position and size within 4 px, font size within 1 px, only existing design tokens) | 1.1.0 | Open | none yet |
| Real-time measurement artifacts of every playback speed combination on the private capture (deterministic tests and the 21,600-advance gate cover them today) | unscheduled | Open | none yet |
| Formal `v0.0.3` tag and GitHub release (gate from the 0.0.3 plan). `v0.1.0` and `v0.1.1` superseded it; confirm with the owner that it can be dropped | 1.0.0 | Decision needed | none yet |

## Line 3: Core integration

NFU takes shared modules from the Core repository `Dennis40816/nvt_fw_core`. Owner target: Core 1.0.0 in all three tools by 2026-10-15. Packages are downloaded at build time (`core-packages.json`, `scripts/fetch_core_packages.py`). No package file is committed.

| Item | Target | Status | Link |
| --- | --- | --- | --- |
| Core 0.1.0 as a build-time download | 1.0.0 | Done | [#33](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/33) |
| CI downloads Core packages before restore | 1.0.0 | Done | [#38](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/38) |
| AtomicOutput from Core | 1.0.0 | Done | [#34](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/34) |
| CsvQuoting from Core | 1.0.0 | Done | [#35](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/35) |
| SourceFileNavigation from Core | 1.0.0 | Done | [#36](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/36) |
| Core 0.2.0 package upgrade, zero difference (step one of the theme adoption) | 1.0.0 | Done | [#40](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/40) |
| Theme look, step two: shared palette, buttons and scrollbars from `Nvt.Core.Avalonia`, in one pull request with before and after images for dark and light. Order of the three tools: NFC, then NFU, then NFH | 1.0.0 | In progress. The pull request goes to the owner after the NFC one is in review | none yet |
| `SuppressEvents()` helper that replaces the `configuring*` and `synchronizing*` guard flags in `MainWindow` | 1.0.0 | Done | [#43](https://github.com/Dennis40816/nvt-event-buffer-replay/pull/43) |
| Core 1.0.0 adoption (version bump and verification) | 1.0.0 (owner target for Core 1.0.0 is 2026-10-15) | Waiting for the Core 1.0.0 release | none yet |
| Move the `MainWindow` internal state into a ViewModel with bindings | after Core 1.0.0 | Planned | none yet |
| Later Core modules (for example Locale, ReportList) when NFU needs them | unscheduled (version set when a module is needed) | Not started | none yet |

## Completed milestones

- M1 C# parity core: domain records, probing and format adapters, 0x82 to 0x85 decoding, Desay 0x97, `nvt-replay` CLI, frozen Python parity observation.
- M2 Replay slice: Load with auto decode configuration, Reported Frame and Host State Paint, recorded and frame-paced clocks, logical stepping, multi-track timeline.
- M3 Analysis slice: diagnostics and ASIL lifecycle, Review Queue, structured Inspector, markers with `.nvtreplay.json` round trip. See [Review Queue](docs/review-queue.md).
- M4 Export slice: shared ReplayScene, Output workspace (MP4, heatmap, data package), exact MP4 preview, JSON and CSV, source manifest.
- M5 Hardening: one-GB and eight-hour performance gate, cancellation and recovery journal, atomic output, Settings, accessibility checks, portable Windows package and release gates.
- M6 Register-aware communication log: register catalog, built-in IC profiles, evidence-typed IC inference, FW Command parser, readable CSV and JSONL export, activity strip. Reference data: [register knowledge](docs/register-knowledge.md).
- 0.0.3 stabilization: `MainWindow` split into partial classes, immutable replay-frame cache, 29 approved screenshots, UI polish items UI-01 to UI-19, UI-21 and UI-22.
