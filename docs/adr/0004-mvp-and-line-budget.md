# ADR 0004: MVP boundary and code budget

Status: accepted.

## Decision 2026-10-02

The MVP shipped in v0.1.1. Handwritten production `.cs` and `.axaml` files
under `src/` now have an 800-line ceiling. The six files already above 800
lines are listed in `eng/file-size-baseline.json`; each is capped at its
measured size and may only shrink. The check reports total and per-project
line counts without applying a total limit. The owner approves every change
under `src/` under the repository approval policy.

The former total cap now blocks agreed new scope rather than controlling file
size. The 25,000-line architecture review threshold was crossed in v0.1.0
without a recorded review. One window class holds 31% of production code,
which the total cap did not prevent. The per-file ceiling directly limits
further concentration while the owner reviews each source change.

## Superseded 2026-10-02

MVP ships one vertical path: load, probe, configure, parse/index, replay Paint
and Host State, inspect timeline/raw/transport, review ASIL and diagnostics,
mark a range, and export MP4 plus machine results.

Handwritten production C#/XAML targets 18,000-22,000 lines. Crossing 25,000
requires architecture review; 30,000 is a hard cap. This budget is a ceiling,
not a reason to pull deferred capabilities forward.
