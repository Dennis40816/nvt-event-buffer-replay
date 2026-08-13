# ADR 0004: MVP boundary and code budget

Status: accepted.

MVP ships one vertical path: load, probe, configure, parse/index, replay Paint
and Host State, inspect timeline/raw/transport, review ASIL and diagnostics,
mark a range, and export MP4 plus machine results.

Handwritten production C#/XAML targets 18,000-22,000 lines. Crossing 25,000
requires architecture review; 30,000 is a hard cap. This budget is a ceiling,
not a reason to pull deferred capabilities forward.
