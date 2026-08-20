# UI snapshot candidates

The 16-state matrix renders Paint, MP4 Output, Heatmap Output, and Settings at
1920 × 1080 and 1180 × 720 in both dark and light themes. It uses the repository
fixture `tests/fixtures/kingstvis-common-0x83.csv`; no private golden, absolute
path, or user metadata is captured.

The matrix is deliberately **not an approved visual baseline yet**. Candidate
images are written to the git-ignored `artifacts/ui-snapshot-candidates`
directory only when explicitly requested:

```powershell
.\scripts\capture-ui-snapshot-candidates.ps1 -Capture
.\scripts\verify-ui-snapshot-candidates.ps1 -Runs 2
```

Verification uses exact PNG equality. A mismatch fails and writes the actual
image, a high-contrast diff, and pixel metrics. It never widens a threshold to
hide missing content.

Open visual gates before promotion to approved baselines:

- At 1180 × 720, Paint transport status, clocks, Loop text, and counts collide
  near the bottom-right edge.
- At narrow width, disabled Save hides its only label and becomes an empty gray
  square instead of retaining a meaningful icon.

After both responsive defects are fixed, regenerate and review all 16 states.
Only then should the candidate workflow be promoted to an always-on approved
snapshot gate.
