# UI interaction and accessibility

The desktop workstation defaults to the high-contrast dark palette and can toggle a reviewed light palette from the header or command palette. Semantic brushes adapt the main surfaces; legacy literal evidence colors are contrast-adjusted in light mode while the Paint canvas remains dark for stable visual comparison.

Press `Ctrl+K` to open the command palette. Global shortcuts are:

- `Ctrl+O` load a capture; `Ctrl+S` save review; `Ctrl+E` export analysis;
- `Space` play/pause; `Left`/`Right` step; `Home`/`End` seek bounds;
- `I`/`O` set loop bounds; `M` marks the current frame or loop range;
- `Escape` closes the command palette.

Primary controls expose explicit UI Automation names. Review rows always print `Alarm`, `Error`, `Warning`, or `Info`; ASIL and QA states are textual and never rely on color alone. Status changes are a polite live region. Standard Avalonia focus order, keyboard activation, high-DPI scaling, and Windows text rendering remain enabled. The window is usable at its 1040×700 minimum; Review Queue collapses automatically below 1280 pixels and Inspector below 1180 pixels unless the operator explicitly chooses a rail state.

For reproducible desktop QA, a capture may be opened with an explicit decoder choice: `Nvt.Replay.Avalonia capture.txt --event-version 0x83`. Desay 0x97 additionally requires `--palm-profile Standard` or `--palm-profile Benz-Palm`. These options never guess semantic configuration.

Manual release UI checks cover dark and light palettes, command palette focus/dismissal, keyboard replay, decoder selection, and a full capture/decode/Paint round trip. No telemetry, update checker, upload, or network client exists in the application.
