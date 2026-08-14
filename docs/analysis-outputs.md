# Analysis outputs

The Analysis workspace and `nvt-replay analyze` call the same C# analyzer and
output writer. For the same source hash, decoder configuration, and logical
range they produce equivalent data.

The output directory contains:

- `analysis-report.json`: complete schema-versioned analysis model;
- `events.json` and `events.csv`: parsed Reported Frame events and contacts;
- `diagnostics.json` and `diagnostics.csv`: protocol/source findings with
  source location and linked event IDs;
- `manifest.json`: source SHA-256, adapter, format evidence status, selected
  range, captured/frame clock summary, and heatmap transform;
- `heatmap.png`: deterministic touch-density image.

Diagnostic aggregates retain diagnostic IDs, event IDs, and physical source
record IDs. They cover all decoder and source findings, including byte-count,
CRC, Desay assembly, counter, timing, and source-quality diagnostics whenever
those findings occur in the selected evidence. Unknown fields remain raw
detail values or bitmaps; the analyzer does not assign speculative labels.

The heatmap counts only valid, active contacts from Reported Frames. It never
uses Host State to fill gaps and never invents coordinates for rejected
frames. The origin is top-left and the linear coordinate extent is stored in
the manifest alongside the PNG dimensions.

Example:

```powershell
dotnet run --project src/Nvt.Replay.Cli -- analyze capture.txt `
  --event-buffer-version 0x83 `
  --output ./analysis `
  --range 1:500 `
  --heatmap-size 1280x720
```

The CLI range is one-based and inclusive. Without `--range`, all decoded
frames are included. Each file is written through same-directory temporary
storage and atomically replaces its prior version only after a successful
flush; cancellation preserves the prior valid file.
