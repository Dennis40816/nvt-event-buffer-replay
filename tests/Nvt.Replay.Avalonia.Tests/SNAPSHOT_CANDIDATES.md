# UI snapshot baselines and candidates

The 24-state base matrix renders Paint, MP4 Output, Heatmap Output, Reported
Points Output, Data Package Output, and Settings at
1920 × 1080 and 1180 × 720 in both dark and light themes. It uses the repository
fixture `tests/fixtures/kingstvis-common-0x83.csv`; no private golden, absolute
path, or user metadata is captured.

The 24 reviewed base images plus 4 focused Review/Inspector images in
`ApprovedSnapshots` are always-on visual gates. Every normal Avalonia test run
compares them with exact PNG equality.
This approval is intentionally narrow: it establishes the primary workspace
composition, themes, and responsive widths; it does not declare the whole UI
or Phase 7 complete.

Candidate images for an intentional visual change are written to the
git-ignored `artifacts/ui-snapshot-candidates` directory only when explicitly
requested:

```powershell
.\scripts\capture-ui-snapshot-candidates.ps1 -Capture
.\scripts\verify-ui-snapshot-candidates.ps1 -Runs 2
```

Verification uses exact PNG equality. A mismatch fails and writes the actual
image, a high-contrast diff, and pixel metrics. It never widens a threshold to
hide missing content. Candidate images must be reviewed before their PNGs
replace the approved files.

`GapSnapshotTests` adds these covered states to both candidate scripts and the
normal exact-PNG gates. Every state uses the same repository fixture at
1180 × 720. Raw Explorer and Decoded Events need separate images because they
are separate workspace tabs. Both their Review and Inspector rails are open.

| State | Snapshot file |
| --- | --- |
| Raw Explorer, both rails, dark | `raw-rails-1180x720-dark.png` |
| Raw Explorer, both rails, light | `raw-rails-1180x720-light.png` |
| Decoded Events, both rails, dark | `decoded-rails-1180x720-dark.png` |
| Decoded Events, both rails, light | `decoded-rails-1180x720-light.png` |
| Output, running MP4 preparation, dark | `output-loading-1180x720-dark.png` |
| Output, cancel requested while still active, dark | `output-cancel-1180x720-dark.png` |
| Data package, included heatmap PNG, dark | `package-png-1180x720-dark.png` |

The Output tests hold an injected export operation through the existing
`StartOutputExportJob` seam. No ffmpeg process or export file is created.
They report determinate `0/120` progress to keep preparation visible without
a moving progress highlight. The cancel snapshot uses the real cancel button
and holds the job until capture finishes. Data package always includes
`heatmap.png`; this UI has no separate PNG toggle.

These seven approved PNGs are in `ApprovedSnapshots` and run as always-on gates.
A normal test run compares them with exact PNG equality. The theme look pull
request regenerates them with the new look. A missing image fails with
`Missing approved UI snapshot` and the file name; `ProcessSnapshot` never skips it.

Snapshot tests still do **not** cover these visual states:

- All Break state.
- 1- and 5-contact focused Paint scenes.
- Export progress after preparation, completion, and failure.
- Other empty, warning, error, and narrow Inspector states.
