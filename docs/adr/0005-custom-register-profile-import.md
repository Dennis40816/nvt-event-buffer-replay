# ADR 0005: Custom register profile import

Status: Proposed.

## Context

Operators need register profiles for ICs and customer firmware that are not
built in. Requiring a catalog change and application release for each map
delays analysis and cannot represent independent firmware revisions reliably.

The current selector lists `NvtRegisterCatalog.Profiles` by IC family.
`MainWindow.Capture.cs` applies a profile through
`CaptureSession.WithRegisterProfile`, rebuilding separate register annotations
before decode. Sidecars and analysis/video manifests record the IC family and
Event Buffer base in `ReplayDecodeConfiguration`, not a custom definition.
Opening a review sidecar compares configuration; it does not select a profile
or reconfigure decoding.

The [register profile schema](../register-profile-schema.md) already reserves
validated, versioned JSON. Loading it does not register or activate a profile;
`ToRuntimeProfile` projects only IC family and the three base addresses.
Register and command rows remain metadata. The existing runtime profile hash
covers IC family and bases; the file's `contentSha256` covers its full canonical
definition. These identities are not interchangeable.

Built-in inference from typed address evidence is an existing contract in
[source adapters](../source-adapters.md). This proposal requires explicit
selection for imported profiles and does not extend that inference to them.

## Constraints

- Source bytes, record order, timestamps and locations stay immutable.
  Annotations are a separate projection keyed by source record identity;
  changing a profile never rewrites capture bytes or source records.
- An imported profile must be explicitly selected, never guessed from an
  address, filename, Chip ID or firmware string. Import is not selection.
  Event Buffer Version and Desay Palm profile remain separate explicit choices.
- Preserve the `0x80800` collision guard: 51929/51932 and 51950/51951 have
  independent maps. That address alone leaves both candidates unresolved.
  With a selected profile, absolute Event/Common/History accesses must match
  its map; addresses belonging only to another profile remain raw. The existing
  unknown-page, offset-only decoded-I2C exception retains transport meaning.
- Every derived meaning retains source provenance and the selected profile's
  content identity. Keep supporting document/golden references, evidence hashes
  and review status; a content hash proves integrity, not semantic correctness.
  Unknown or unsupported meanings remain raw or unavailable.
- Profiles are declarative data, with no scripts, callbacks or executable
  decoders. Schema v1 firmware scope is opaque metadata, not an executable
  constraint or an automatic compatibility check.

## Options

| Option | Benefits | Costs and limits |
| --- | --- | --- |
| Explicit versioned JSON file import with schema validation | Reuses the reserved schema, canonical hash and fail-closed parser; portable and independently reviewable. | Needs a selection/approval flow, provenance storage and exact-definition resolution on reopen. |
| A folder of profiles | Convenient reuse and distribution of many validated JSON definitions. | Folder changes, duplicate IDs and discovery order complicate reproducibility; discovery must never activate a profile. |
| Embed the complete profile in each sidecar | Keeps the exact definition with the review; avoids a missing external file. | Duplicates definitions, enlarges sidecars and requires sidecar versioning; opening still needs validation and explicit selection. |
| No import; extend built-ins through reviewed releases | Keeps the current catalog and persistence model simple. | Operators wait for releases, private/customer maps cannot travel independently, and unsupported IC/FW interpretations stay unavailable. |

## Recommendation

Prefer explicit versioned JSON file import, pending owner acceptance. Reuse the
linked v1 schema rather than adding executable plugins or folder discovery.

Validate schema version, unknown properties, required fields, uniqueness,
24-bit address bounds and canonical `contentSha256` before offering a profile.
Show its ID, IC/FW scope, bases, hash and evidence/review status for operator
confirmation. Validation success makes a profile available; only explicit
selection applies its annotation projection before decode. A selection change
requires rebuilding derived results against that exact definition.

Identify custom profiles by `(profileId, contentSha256)`, rather than IC family
alone. Keep built-in identities separate and prevent ID shadowing or silent
replacement. Suggest retaining different hashes as distinct immutable revisions.

Version sidecar/manifest persistence to record the exact custom identity and a
portable reference to the canonical JSON. Carry that file with shared output.
Retain provenance and selection confirmation in associated metadata bound to
the profile hash; v1 has no provenance fields, so do not add unknown properties
to its JSON. Reopening must resolve and verify the exact definition before any
explicit application. Missing, invalid or changed content leaves custom
interpretation unavailable, with raw evidence visible and no built-in fallback.

For the first import slice, recommend activating base addresses only, retaining
register/command rows as metadata until a separately reviewed contract defines
their activation. Firmware applicability requires operator confirmation while
v1 scope stays opaque. This ADR proposes a direction, not an implementation or
an accepted product contract.

## Consequences

New address maps can be reviewed and shared without changing capture evidence
or shipping a new catalog. Exact definitions and hashes make interpretations
reproducible, while explicit selection preserves collision handling.

Later implementation needs identity-aware selection, annotation/cache/decode
invalidation, persistence migration and portable profile packaging. Operators
must retain definitions and evidence; missing profiles can block semantic
replay. Schema validation cannot establish that an IC/FW map is correct, and
firmware-specific value/command semantics still need owner evidence and review.

## Open questions for the owner

| Question | Suggested answer | Effect |
| --- | --- | --- |
| What may the first import activate? | Base addresses only; register/command rows stay metadata. | Broader firmware semantics need a separate reviewed activation contract. |
| Which provenance is required before selection? | Source document/golden reference, evidence SHA-256 and review/confirmation status. | Requires associated metadata bound to the profile hash without changing v1 JSON. |
| How should profiles travel with reviews? | Sidecar reference plus canonical JSON in the shared package. | Keeps sidecars smaller; exact files must remain available, with embedding deferred. |
| What happens when one ID has different content? | Retain distinct immutable hash revisions and require explicit selection. | Prevents silent replacement and permits reproducible reopening. |
| How is firmware applicability confirmed? | Operator confirmation of the stated scope; no v1 range evaluation. | Avoids unsupported compatibility claims and defers a firmware constraint language. |
| Should built-in evidence inference also require manual selection? | Preserve its current contract; make custom selection mandatory. | A broader inference policy change remains a separate owner decision. |
