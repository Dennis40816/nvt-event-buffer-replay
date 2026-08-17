# Replay review sidecar

Replay review work is stored beside an immutable capture as a versioned
`*.nvtreplay.json` sidecar. The source capture is never modified.

Schema version 1 records:

- the capture SHA-256 and file name;
- the selected source adapter, Event Buffer Version, Desay profile, Event
  Buffer base address, and explicit IC register profile;
- point or range markers with optional QA case IDs;
- raw Kernel/FW/other evidence references with optional SHA-256;
- Review Queue acknowledgement, lifecycle, disposition, and resolution state;
- replay visibility preferences.

## Operator workflow

1. Load and decode the capture, then use `MARK` for the current frame or the
   active In/Out range.
2. Select the marker in Review Queue. Add comma-separated QA case IDs or attach
   a raw log from the Inspector.
3. Use `Save review`. The application writes a temporary file in the target
   directory, flushes it, and atomically replaces the destination.
4. Use `Open review` after loading and decoding the same capture. A source hash
   or decode-configuration mismatch is shown and remains unapplied until the
   operator explicitly chooses `Apply mismatch`.

Evidence paths are made relative to the sidecar where possible. On open, the
application reports missing evidence and SHA-256 mismatches without hiding the
reference. A sidecar from a newer schema version is rejected safely instead of
being partially interpreted.

Raw log attachment is intentionally evidence-only in the MVP. Semantic Kernel
and FW log parsing is a later adapter layer; attaching a file does not imply
that its contents were validated or parsed.
