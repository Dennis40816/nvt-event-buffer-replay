# Domain context

- **Source record**: one normalized record emitted by a source adapter, with
  original bytes/text and source location intact.
- **Physical transaction**: one captured I2C read/write or equivalent NDS
  operation.
- **Logical event**: one complete event-buffer packet. Common is normally one
  physical transaction; Desay 0x97 is assembled from two transactions.
- **Reported frame**: the touch information present in the current logical
  event.
- **Host State**: persistent contacts maintained by applying valid Enter,
  Move, Break, and All Break events as a vehicle head unit would.
- **Evidence event**: diagnostic, device-state transition, behavior finding,
  QA result, or human annotation placed on the shared timeline.
- **Project Preset**: reusable IC, format, panel, transform, address, and policy
  configuration.
- **Replay sidecar**: portable analysis state for one capture, including
  markers, dispositions, alignment, and source hashes. It never modifies the
  capture.
- **Evidence claim**: a scoped statement from a specification, FW-owner
  override, golden, QA observation, or heuristic. Scope and provenance are
  retained; observations do not silently become universal rules.
