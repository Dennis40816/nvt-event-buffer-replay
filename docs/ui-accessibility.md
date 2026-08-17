# UI interaction and accessibility

The desktop workstation defaults to the high-contrast dark palette and can toggle a reviewed light palette from the header or command palette. Semantic brushes adapt the main surfaces; legacy literal evidence colors are contrast-adjusted in light mode while the Paint canvas remains dark for stable visual comparison.

Press `Ctrl+K` to open the command palette. Global shortcuts are:

- `Ctrl+O` load a capture; `Ctrl+S` save review; `Ctrl+E` export analysis;
- `Space` play/pause; `Left`/`Right` step; `Shift+Left`/`Shift+Right` jump 10 logical frames; `Home`/`End` seek bounds;
- `[`/`]` decrease/increase replay speed; `1` returns to `1×`;
- `I`/`O` remain keyboard shortcuts for loop bounds; `M` marks the current frame or loop range; `L` shows or hides the Paint legend;
- `Escape` closes the command palette.

The bottom `Keys` button opens the same catalog. Keyboard matching, transport
tooltips, and the four palette modules all use `ReplayShortcutCatalog` as one
source of truth.

Primary controls expose explicit UI Automation names. Review rows always print `Alarm`, `Error`, `Warning`, or `Info`; ASIL and QA states are textual and never rely on color alone. Status changes are a polite live region. Standard Avalonia focus order, keyboard activation, high-DPI scaling, and Windows text rendering remain enabled. The window is usable at its 1040×700 minimum, and startup caps its height to the active screen working area with a small safety margin so the transport never opens behind the taskbar. Review Queue collapses automatically below 1400 pixels and Inspector below 1240 pixels unless the operator explicitly chooses a rail state.

Paint keeps every contact's point, coordinate label, and trajectory in one stable color. Labels choose among eight anchors, avoid every visible point and prior label when possible, use a leader line, and split ID/state from X/Y coordinates. Finger, Glove, Palm, and Reserved glyphs remain textual in the legend so type is not communicated by shape alone. Legend Auto mode scores contact density across the complete capture, chooses the least occupied corner once, and remains stable while frames advance; operators can pin a corner, click the legend to compact it, or use `L` and Canvas Settings to show or hide it. `Recent` exposes a 2–120 frame history length, `Until Break` clears a contact after its Break, and `Persistent` keeps completed gestures without joining a later reuse of the same ID. Canvas overlays expose independent X/Y reversal, subtle/strong coordinate grid, zoom, Fit, and a view-only Clear action.

The replay timeline uses a thin neutral track that strengthens on hover, a white playhead, lime logical/loop state, muted physical progress, and amber evidence. Loop bounds are direct draggable brackets with a shaded selected range; the `Loop` toggle creates a one-second default range and `Clear` removes it. No default platform slider thumb is exposed.

For reproducible desktop QA, a capture may be opened with an explicit decoder choice: `Nvt.Replay.Avalonia capture.txt --event-version 0x83`. Desay 0x97 additionally requires both `--register-profile <family>` and `--palm-profile Standard` or `--palm-profile Benz-Palm`. These options never guess semantic configuration or assume NT51927's Event Buffer base.

Manual release UI checks cover dark and light palettes, command palette focus/dismissal, keyboard replay, all trajectory modes, decoder selection, and a full capture/decode/Paint round trip. No telemetry, update checker, upload, or network client exists in the application.
