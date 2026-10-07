# Core repository checker provenance

Source repository: [Dennis40816/nvt_fw_core](https://github.com/Dennis40816/nvt_fw_core).
Source merge commit: `b32108d104ae8542cbc06e537d2cac73601a9de5`.

Source paths:

- `tools/repo-checks/doc_sync.py`
- `tools/repo-checks/handoff_check.py`
- `tools/repo-checks/README.md`
- `tools/repo-checks/doc-sync.core.json`
- `tests/repo-checks/test_doc_sync.py`
- `tests/repo-checks/test_handoff_check.py`

The two engines, two test files, and Core config are byte-identical copies.
Their copyright and license headers are preserved. The Core README defines
the schema and behavior; NFU usage is documented in [doc-sync.md](../../docs/doc-sync.md).
The copied `doc-sync.core.json` is upstream regression-test data. NFU commands
use `doc-sync.nfu.json`.

Engine, test, and reference-config updates come from Core, not local edits.
Fix shared behavior in Core first, then copy the reviewed files and update
this provenance. NFU policy belongs in `doc-sync.nfu.json`.
