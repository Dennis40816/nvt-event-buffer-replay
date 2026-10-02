# ADR 0004: MVP boundary and code budget

Status: accepted.

MVP ships one vertical path: load, probe, configure, parse/index, replay Paint
and Host State, inspect timeline/raw/transport, review ASIL and diagnostics,
mark a range, and export MP4 plus machine results.

## Decision 2026-10-02

The MVP shipped in v0.1.1. Every handwritten production `.cs` or `.axaml`
file under `src/` that is not in `eng/file-size-baseline.json` has an 800-line
ceiling. The six grandfathered files are capped at their recorded sizes. A
shrink requires lowering its recorded ceiling; an entry is removed when its
file reaches 800 lines or is deleted. The check reports total and per-project
line counts without applying a total limit. The owner approves every change
under `src/` under the repository approval policy.

The former total cap blocked owner-agreed new scope without constraining
file concentration. The 25,000-line architecture review threshold was
crossed in v0.1.0 without a recorded review. MainWindow code and styles hold
about 31% of production code, which the total cap did not prevent. The
per-file ceiling directly limits further concentration while the owner
reviews each source change.

## Superseded 2026-10-02

Handwritten production C#/XAML targets 18,000-22,000 lines. Crossing 25,000
requires architecture review; 30,000 is a hard cap. This budget is a ceiling,
not a reason to pull deferred capabilities forward.
