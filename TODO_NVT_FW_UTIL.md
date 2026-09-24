# NVT FW UTIL — workstation and raw-data backlog

Status: user requirements recorded; visual concept awaits review; **not implemented**.
Updated 2026-09-24.

This file records the user's decisions for the next conversation. The current
Event Buffer Replay application remains the working baseline. Do not treat the
interactive concept mockup as a parser specification or as evidence that Raw
Data Analysis already works.

## Confirmed product and navigation requirements

- Rename the product shell to **NVT FW UTIL**. The current Raw Explorer,
  Decoded Events, Paint, and Output pages form one tool named **Event Buffer
  Analysis**; they are not four peer tools on the home screen.
- Redesign the home screen as a restrained tool launcher, with Event Buffer
  Analysis and the proposed Raw Data Analysis as separate workspaces. Avoid the
  previous generic workstation/card-dashboard look. Keep the existing
  engineering palette and legible controls.
- **Recent captures belong inside Event Buffer Analysis**, not on the global
  home screen. Do not mix recent files from unrelated tools.

Proposed behavior to confirm during implementation: preserve the full-width
Paint/Output surfaces without a permanently wide tool sidebar; pause playback
but retain session state when returning to Tools; route direct Event Buffer file
open to Event Buffer Analysis while no-file startup opens Tools.

## Raw Data Analysis workspace

| ID | Requirement / acceptance direction | Status |
| --- | --- | --- |
| FWU-01 | Add Raw Data Analysis as a separate NVT FW UTIL tool with its own Load flow and source summary. Do not place raw-matrix controls in Event Buffer Paint. | Planned |
| FWU-02 | Use the user's existing `nds_helper.frame_extractor` formats as the reference for input probing and frame semantics. Candidate shapes documented there include timestamped `DiffData` CSV with optional Button Data, timestamped plain-matrix logs, and a single rectangular matrix. Confirm exact MVP formats against representative files before implementation. Runtime implementation remains **C# only**; Python is a reference, not an embedded dependency. | Reference identified; format validation pending |
| FWU-03 | After load, show one original matrix frame at a time, with visible X/Y cell coordinates and values. Provide Paint-like frame navigation: previous/next, direct seek, frame number, and source timestamp when available. Source bytes and frame order remain unchanged. | Planned |
| FWU-04 | Put **current-frame** analysis in a right inspector. Operators choose which calculations are visible; initial basic candidates are average, minimum, maximum, and standard deviation. Define scope (whole matrix versus ROI), signedness, invalid-cell handling, and units before locking the calculation contract. | Planned; metric details to confirm |
| FWU-05 | Reserve an advanced calculation slot for **TPMux2**, but do not implement or display a numeric result until the user supplies its input fields, formula/algorithm, applicability, and golden examples. An unavailable calculation should be visibly unavailable, never silently estimated. | Definition pending from user |
| FWU-06 | Distinguish **Before Diff** and **After Diff** at import. Confirmed Stop FW semantics: its **Before Diff has already passed NF normalization but has not had common-mode noise removed**. Treat this as normalized=on / common-mode-removal=off. Use the NF table and selected CNC method to derive other states while preserving the imported matrix and recording method, order, parameters, and provenance. Do not guess arithmetic, sign, scale, saturation, or whether the reverse direction is mathematically valid. | Before Diff state clarified; remaining state mapping pending |
| FWU-07 | Keep frame navigation and selected-metric state responsive for large captures: parse/index off the UI thread, cache a bounded set of frames, and calculate only enabled metrics for the selected frame. Verify with long captures before claiming performance. | Planned |
| FWU-08 | Review the launcher and raw workspace concept with the user, then verify the chosen design in dark/light and narrow layouts; avoid shrinking the matrix to make room for low-priority controls. | Design review pending |
| FWU-09 | Make the NF-table reconstruction an **operator-editable, ordered step pipeline** rather than one fixed formula. Provide reusable blocks for common operations; allow drag-and-drop reordering; allow the operator to add a custom step using placeholders such as `{col}`. Show the chosen order and parameters, and retain them with the derived output so the result can be reproduced. The original input remains untouched. | User requirement recorded; block catalog and expression contract pending |
| FWU-10 | Add **Raw Check** analysis to the Raw Data Analysis tool. The user expects this to be a substantial analysis area; record it for planning, but do not infer checks or results from the name alone. | Requested; scope and rules pending |

### Processing-state model (current understanding)

Two independent stages create four logical states: NF normalization on/off ×
common-mode noise removal on/off. The user has clarified that Stop FW
`Before Diff` is the **NF on / common-mode removal off** state. The exact mapping
of `After Diff`, whether a separately labeled “normalized Before” matrix is a
distinct input, and how the other two states are derived remain to be confirmed.
When both stages are enabled, method and order remain part of the recipe because
changing order can produce a different result.

## Evidence and decisions still needed

1. The specific raw-data files and format variants to support first; a parser
   probe must report uncertainty instead of silently choosing the wrong shape.
2. Remaining Before Diff / After Diff state mapping, whether the separately
   mentioned “normalized Before” is a distinct input, and which conversion
   directions are required. For NF table: source format, dimensions/index
   mapping, value type, arithmetic, rounding/clamping, version dependence, and
   at least one input→expected-output golden. Until confirmed, only the imported
   values may be shown as factual; derived values must be disabled or explicitly
   marked unavailable.
3. The exact TPMux2 definition, input sections and frame relationships, output
   meaning/unit, failure behavior, and a golden example.
4. Metric scope and missing/invalid-cell rules, including whether Button Data
   is a parallel section or participates in any calculation.
5. Reconstruction editor contract: the first set of built-in function blocks;
   whether each step operates on one cell, one column, one frame, or an entire
   capture; and the exact meaning and allowed companions of `{col}` (for
   example, whether it denotes a column index, current cell value, or a column
   vector). Also confirm expression syntax, evaluation order, error handling,
   numeric precision, and a golden case where step order changes the result.
   Custom steps should use a validated, deterministic expression language,
   not unrestricted code execution; this is a proposed safety boundary to
   review with the user.
6. Raw Check contract: what input/state it consumes, which checks and
   conditions it runs, how results are displayed, and representative expected
   results/golden evidence. Keep this separate from generic matrix statistics
   until the user defines how they relate.

## Reference boundary

`nds_helper.frame_extractor` currently documents `load_csv_diffdata`,
`load_plain_matrix_log`, `load_single_matrix`, zero-based `frame.xy(x, y)`,
per-frame views, and statistical helpers. Use these to understand source shapes
and expected frame behavior, then design a native C# import and analysis layer.
Do not vendor the private Python repository, its private fixtures, or real
capture/golden data into this public repository.

The design concept shown in the prior chat is an exploratory reference, not
an approved pixel-perfect baseline. The next conversation should first confirm
the exact raw input/NF/TPMux2 contracts, then decide an implementation slice.
