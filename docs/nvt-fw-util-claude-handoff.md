# NVT FW UTIL / Raw Data Analysis: Claude Handoff

Updated: 2026-09-29. This document hands over **requirements and work status**; it is not an operating manual claiming that new features are complete. Use [ROADMAP.md](../ROADMAP.md) as the main index of individual tasks; this document adds decision context, design boundaries, risks, and the order of work for the next person taking over. If this document conflicts with new instructions from the user, follow the new instructions and update the task list accordingly.

## 1. What to Know Before Taking Over

- The production C# / Avalonia project is `nvt-event-buffer-replay`, on GitHub at <https://github.com/Dennis40816/nvt-event-buffer-replay>. At the time of writing, the working branch was `0.1.2`, HEAD was `06a525d`, and the local branch was four **documentation** commits ahead of `origin/0.1.2`; the working tree was clean before this document was created. This is only a snapshot of that time. Always rerun `git status -sb` and `git log -5 --oneline` when taking over.
- The existing usable product is still called **NVT Event Buffer Replay**. The rename to **NVT FW UTIL**, a new tool home page, and **Raw Data Analysis** have not yet been implemented. Do not mistake the existing Event Buffer Raw Explorer for the new sensing-matrix Raw Data Analysis tool.
- The existing Raw Explorer handles LA / I²C / Event Buffer transfer records; the new Raw Data Analysis handles per-frame touch-sensing **numeric matrices**. Also, “raw waveform” in the existing architecture means decoding SDA/SCL waveforms into I²C; it is a separate capability from the new tool.
- The runtime requirement is **C# only**. The old Python projects serve only as format, decoder, and golden references; they cannot be runtime dependencies.
- At this stage, verify the specifications and samples first, then choose the smallest vertical slice to implement. **The exact algorithms for NF normalization, CNC / common-mode removal, TPMux2, and Raw Check are not sufficiently defined yet.** Do not invent formulas and label their outputs as correct results.

### Reading Order

1. [Product task list](../ROADMAP.md): FWU-01–FWU-10, confirmed items, and items awaiting confirmation.
2. The “Next product slice: NVT FW UTIL” section of the [Roadmap](../ROADMAP.md): product direction.
3. [README](../README.md) and the [existing product specification](product-spec.md): the Event Buffer baseline that must remain intact, and build / test entry points.
4. [Source adapters](source-adapters.md): the responsibility boundaries of the existing decoded-I²C adapter; sensing matrices need their own data model.
5. When implementing, also read the relevant `src/`, `tests/`, and [performance requirements](performance.md). Do not change existing decoders based on this document alone.

## 2. Product Structure: Confirmed vs. Awaiting Design Review

### Confirmed by the User

1. Rename the product shell to **NVT FW UTIL**.
2. The current Raw Explorer, Decoded Events, Paint, and Output are pages within a single **Event Buffer Analysis** tool. They should not become four parallel tools on the home page.
3. Add a separate **Raw Data Analysis** workspace. Its Load, frame browsing, matrix view, and analysis should not be put into Event Buffer Paint.
4. **Recent captures belongs only to Event Buffer Analysis.** Do not make it a shared, cross-tool list on the home page.
5. Keep the current engineering-tool character and a sufficiently large main view. The user rejected the 「泛用卡片 dashboard」 (generic card dashboard) style and wants a redesigned workstation home page.

### UX Proposals Not Yet Finally Confirmed by the User

- Make the home page a simple tool launcher; keep Paint / Output at full width, without a permanently wide tool sidebar.
- Preserve the Event Buffer session and pause playback when returning to Tools. Opening an Event Buffer file directly should enter Event Buffer Analysis; starting without a file should enter Tools.
- Raw Data Analysis should use a central matrix, per-frame navigation at the bottom, and optional analysis of the current frame on the right.

First build a low-risk shell that supports clicking, switching tools, and returning to the existing workspace without losing the session, then review its visuals. The interactive illustration in the chat was only a concept sketch, **not an approved pixel-perfect baseline**. Verify light and dark themes and narrow windows, but do not shrink the main matrix view just to fit settings.

## 3. Raw Data Analysis: Input, Views, and Responsibility for Numeric Results

### Input Format References

The user specified their private `Dennis40816/nds_helper` project's `frame_extractor` as the format reference, rather than porting Python. Its known entry points / shapes include:

| Reference entry point | Known shape | Still to verify |
| --- | --- | --- |
| `load_csv_diffdata` | `DiffData` CSV with time information; may include separate `Button Data` | Actual fields, time units, matrix dimensions, and malformed rows |
| `load_plain_matrix_log` | Numeric matrix log with timestamps but no `DiffData` marker | Frame boundaries and header variants |
| `load_single_matrix` | A single rectangular numeric matrix, without timestamps / section markers | Numeric types, dimensions, and missing values |

Python's `frame.xy(x,y)` uses zero-based coordinates. It also provides `frame.rc[row,col]`, per-frame views, and statistics helpers. These are sufficient only to identify candidate source formats; they **do not mean** that we have received publicly shareable golden data or a complete C# import contract. Obtain representative inputs that can be shared and their expected matrices before fixing the formats supported by the MVP. When automatic detection is uncertain, show the candidates and reasons; do not silently guess the wrong format. Private repos, real captures, firmware BIN files, and confidential golden data must not be committed directly to this public repo. Synthetic fixtures, format schemas, source notes / SHA-256 hashes, and conclusions from human verification may be committed.

### Minimum Usable View

- After Load, display the **original imported matrix** for each frame, with visible X / Y cell coordinates and values. Provide previous / next frame, direct seek, frame numbers, and source times when source timestamps are available.
- Analysis or visual transformations must not silently modify the original file bytes, frame order, or imported matrices. Derived matrices must be a separate layer, with a way to return to the original values.
- The analysis area on the right is for the **current frame**. Users can select which metrics to calculate / display. Mean, minimum, maximum, and standard deviation are the first candidates; their full formulas are not yet defined. The whole matrix vs. ROI, signedness, invalid cells, whether Button Data participates, units, and precision still need confirmation.
- Keep `TPMux2` as an advanced field, but until its inputs, algorithm, applicability conditions, output meaning, and golden data are provided, it may only display “undefined / unavailable”; it must not produce guessed numbers.
- Large-file loading and indexing should run outside the UI thread. Calculate only the selected metrics for the current frame, and use a bounded frame cache. Measure performance with long captures; do not merely claim support.

## 4. Before / After Diff, NF, and Common-Mode Removal: The Core of This Discussion

The user's key words about the data path were: 「before 到 after 主要就是兩個步驟，一個是感應量正規化，一個是共模雜訊消除」 (The path from before to after mainly consists of two steps: normalizing the sensing values and removing common-mode noise). They want to derive the other three states from any one state. They also stated: 「如果是 Stop FW 的 Before Diff，會先經過 NF，但還不會共模消除」 (For Before Diff from Stop FW, NF has already been applied, but common-mode removal has not). There may be several common-mode removal methods, and users need to adjust them quickly and interactively, such as Stair CNC, whether TPMux2 is present, and the processing order. This is **central work** for the new tool; a single fixed formula plus a menu is not enough.

### Four Logical States

| State ID (for this document, not an existing program enum) | NF normalization | Common-mode removal | Current evidence |
| --- | --- | --- | --- |
| S00 | Off | Off | Exists logically; actual sources and formulas await confirmation |
| S10 | On | Off | **Before Diff from Stop FW = this state, confirmed by the user** |
| S01 | Off | On | Exists logically; operation order and whether it can be derived from other states await confirmation |
| S11 | On | On | Exists logically; which After Diff / outputs it corresponds to awaits confirmation |

This table classifies processing stages; it **does not prove** that all four can be derived from one another without loss. If CNC discards the common-mode component, NF saturates / truncates values, or information is insufficient, some reverse paths may be non-unique or even irreversible. Label derivation results with clear statuses such as “exactly reproducible,” “requires additional parameters / assumptions,” or “cannot be reliably derived,” and explain them to the user instead of producing apparently precise matrices. Even with both NF and CNC on, the **order**, the method used at each step, and its version may change the result. The four state names cannot replace a pipeline recipe.

### Source Scenarios Expected by the User

- Actual inputs will mostly be `Before Diff`, `After Diff`, or a **single Before Diff matrix captured with Stop FW, already NF-normalized but without common-mode removal**. Whether the user's “normalized Before” also refers to a separate input label needs confirmation; do not invent a fifth processing state.
- The user previously requested 「算回」 (calculate back) between Before / After Diff using an NF table. However, the NF table's file format, row / column indexing, formulas, signs, precision, rounding / clamp behavior, version binding, and golden data remain undefined. At present, only the input values can be displayed as facts.
- CNC may require Stair CNC, TPMux2, and ordering settings. The actual FW operation order, parameters, and golden data have not yet been obtained. Do not hard-code these as universal rules for all ICs.

### Operator-Adjustable Reconstruction Workflow

The user explicitly requires reconstruction to support more than a single fixed formula:

1. Create reusable blocks for common operations (**the specific list is still undecided**).
2. Allow steps to be reordered by dragging, with immediate comparison of the derived results from different recipes; the input matrix remains unchanged.
3. Allow custom key steps expressed with placeholders such as `{col}`. However, whether `{col}` means a column index, the current cell, a column vector, or something else **has not been confirmed**. Do not define syntax first and then force the data to fit it.
4. Save and display step order, methods, parameters, NF table version / hash, source frame / file, output status, and applicability so that results are reproducible and accountable.

The engineering recommendation is a validated, deterministic expression language with limited capabilities, rather than arbitrary C# / Python code execution. This is a **safety design proposal**; the functional requirements still need confirmation with the user. Obtain at least one validation case where changing the order changes the golden output before implementing recalculation after dragging.

### Suggested Guardrails for the Derived Pipeline (Not Approved Algorithms)

- Model the original snapshot, state interpretation, NF table, recipe, and derived matrix separately.
- Each operation block should expose its input / output states, scope (cell, column, frame, capture), parameter validation, and reversibility. Explicitly disable unsupported conversions.
- Use background work and cancellation / version tokens to handle rapid adjustments; results from an old recipe must not overwrite results from a new one. Large matrices may be cached by stage; verify correctness before optimizing.
- Preserve calculation provenance and provide difference views. All comparisons must use the same cell axes, dimensions, and coordinate semantics.

## 5. Raw Check: Proposed, with No Algorithm Authorized Yet

The user also wants **Raw Check** analysis and explicitly said 「其實不簡單，但可以先記錄」 (It is actually not simple, but we can record it first). It is listed as `FWU-10`; do not equate Raw Check with min/max, SNR, simple thresholds, or existing QA. Confirm the accepted input types / which of the states above it accepts, check rules and thresholds, whether it spans frames, output details and severity, whether parameters are adjustable, representative positive and negative golden examples, and its relationship to TPMux2. Before the rules are defined, the UI should provide only a clearly unavailable entry point or a task-list item; do not display fabricated pass / fail results.

## 6. Integration Points in the Existing Code and the Baseline That Must Be Preserved

- `src/Nvt.Replay.Avalonia/App.axaml.cs` currently starts `MainWindow`. If a capture is supplied as a command-line argument, it is loaded when the window opens. Switching to a tool home page must preserve direct file opening and handle startup without a file.
- `src/Nvt.Replay.Avalonia/MainWindow.axaml` currently contains the header, Event Buffer tabs, Paint, Output, Inspector, and transport. `MainWindow.Shell.cs` handles tab / shell behavior. When refactoring the shell, confine Event Buffer chrome to its workspace; do not simply insert a “home tab” while leaving the global Paint transport visible on the home page.
- `src/Nvt.Replay.Sources/` contains the existing I²C / LA adapters; `src/Nvt.Replay.Analysis/` contains the replay workspace / playback; `src/Nvt.Replay.Rendering/` provides the rendering foundation. Clearly separate the responsibilities for new raw-matrix import, frame snapshots, metrics, and pipelines; do not force matrices into the existing I²C `SourceRecord`.
- Existing Event Buffer `0x82`–`0x85` / Desay `0x97` decoders, Raw Explorer, Paint, Output, review, ASIL, MP4, and other features already have behavior contracts and tests. The new tool must not break them; see [README](../README.md) for the specific product baseline.
- The repo currently uses the .NET 10 solution `Nvt.EventBufferReplay.sln`, containing Core, Sources, Formats, Analysis, Rendering, Cli, Avalonia, and test projects. When taking over, check the SDK / `dotnet build` / existing tests before beginning a refactor; use the build commands in README.

## 7. Questions to Confirm with the User First

These are ordered by how much they block correctness / architecture. They need not all be asked at once; samples can support interactive confirmation.

1. **Actual files and labels**: Which `nds_helper` formats should be supported first? Does a file contain one matrix or multiple frames? What do the headers for `Before Diff`, `After Diff`, and Stop FW look like? Can the same frame contain data from multiple stages? Can publicly shareable synthetic golden data be provided?
2. **Four-state mapping / reversibility**: Which of S00 / S10 / S01 / S11 do ordinary `Before Diff` and `After Diff` correspond to? Is “normalized Before” simply an alias for Stop FW Before Diff? If CNC removes information, will the user accept “cannot calculate back exactly” or an estimate with assumptions?
3. **NF table**: Source files, row / col axes, dimensions, per-cell / column mapping, numeric ranges, and signedness; forward and inverse formulas, operation order, integer / floating-point arithmetic, rounding, saturation, and IC / FW versions; at least one golden case with an expected matrix at every step.
4. **CNC / TPMux2**: The exact Stair CNC operations and parameters, TPMux2's position and definition, whether multiple FW methods exist, and how to identify them from the source. If the actual FW method is unknown, how should the UI present candidates and comparisons without claiming a single correct answer?
5. **Editable steps**: The initial built-in blocks, each step's scope, the precise meaning of `{col}`, allowed operators / functions and error display, and the default recipe; whether recipes need to be saved / shared.
6. **Current-frame analysis and Raw Check**: Do statistics cover the entire matrix, a selected region, or a selectable scope? Is Button Data shown in a separate column? What anomalies should Raw Check actually find, and what is the golden data?
7. **Visual flow**: Finalize the home page / Raw workspace after reviewing the prototype, especially large-matrix readability, the width of the analysis area on the right, and how source versions and the current state are displayed.

## 8. Suggested Delivery Stages and Acceptance Gates

These are handoff recommendations, not user authorization to implement everything at once. If the person taking over is only discussing the work in this round, answer questions / update specifications first. If development is requested, proceed in the smallest verifiable slices.

| Stage | Deliverables | Completion criteria |
| --- | --- | --- |
| A: Specifications and samples | Source format list, state mappings, NF / CNC / TPMux2 evidence matrix, synthetic golden plan | Every fact has a source; unclear conversions are marked as gates |
| B: Workstation shell | NVT FW UTIL launcher, Event Buffer Analysis retaining existing functionality, Recent capture only within that tool | No regressions in startup, direct capture opening, returning to tools, Paint / Output, or existing tests |
| C: Raw read-only slice | A C# importer for one chosen format with golden data, original matrices and per-frame browsing, basic source location | Values, X/Y, frames / timestamps match the golden data; the source remains unchanged |
| D: Optional basic analysis | Current-frame metrics on the right, with enable / disable controls | The user confirms rules for precision, invalid values, ROI, etc., and golden data exists |
| E: Derived pipeline | Four-state display, editable NF / CNC recipes, reversibility / unavailability display, provenance | Forward and reverse operations are enabled only with algorithms and golden data; reordered steps have verifiable outputs |
| F: Advanced | TPMux2, Raw Check, performance with larger datasets, and output | Each has an explicit contract, golden data, failure messages, and measured performance |

At every stage, unavailable numeric features must explicitly state “definition / evidence pending,” without silent fallback. Visually derived values must not be presented as actual firmware outputs. Include only synthetic data or contracts and tests with sensitive information removed in the public repo.

## 9. Starting Instructions for Claude

1. Recheck the branch, HEAD, working tree, and whether subsequent implementation already exists; the Git status in this document will become stale.
2. Read the documents listed in section 1 and any accessible format documentation for `nds_helper`. If access is unavailable, explicitly identify the missing source; do not guess formats from names or copy private content.
3. Record “user-confirmed,” “engineering recommendation,” and “awaiting golden data” separately. In particular, preserve **Stop FW Before Diff = NF on / CNC off**; do not map ordinary Before / After to the same rule without confirmation.
4. Before working on algorithms, ask the user for the first essential samples and NF / CNC contracts in section 7. A shell or read-only import slice that does not depend on unknown formulas can be built first, but it must have corresponding acceptance criteria.
5. For every slice, verify the existing Event Buffer workflow, source immutability, golden data, and UI readability. Update the status in `ROADMAP.md` and record gates that still require human review.
