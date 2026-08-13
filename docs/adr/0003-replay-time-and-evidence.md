# ADR 0003: Preserve physical, logical, and evidence timelines

Status: accepted.

The domain keeps physical transactions, assembled logical events, and evidence
events distinct. Recorded and uniform Frame clocks are both supported. Source
timestamps and bytes remain immutable; alignment and human analysis live in a
sidecar.

Paint maintains Reported Frame and Host State separately. Protocol-invalid
input does not update Host State. Every derived result retains deterministic
provenance back to source offsets.
