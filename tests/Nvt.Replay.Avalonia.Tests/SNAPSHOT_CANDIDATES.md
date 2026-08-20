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

The approved images still do **not** cover these visual states:

- All Break state.
- 1- and 5-contact focused Paint scenes.
- Export loading, progress, completion, failure, and cancellation.
- Other empty, warning, error, and narrow Inspector states.
