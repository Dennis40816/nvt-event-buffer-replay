# Documentation sync and handoff checks

These development checks use Python 3.10 or later, its standard library, and Git.
Run them from the repository root. Their [provenance](../tools/repo-checks/PROVENANCE.md)
records the Core source.

```text
python -B tools/repo-checks/doc_sync.py --repo . --config tools/repo-checks/doc-sync.nfu.json --base origin/1.0.x --mode warn
```

Choose the target branch as `--base`. The check compares `base...head` with
rename detection. `--head <ref>` defaults to `HEAD`; uncommitted changes are
not checked. Add `--pr-body-file <path>` to supply a UTF-8 PR body, or
`--all-links` to check all tracked Markdown links.

Warn mode prints warnings and exits 0 when findings exist. Enforce mode
(`--mode enforce`) prints errors and exits 1 when findings exist. Both modes
exit 2 for usage, config, file, or Git errors. The owner chose one week of
warnings before enforcement. CI wiring and rollout timing are a separate step.

The [NFU config](../tools/repo-checks/doc-sync.nfu.json) pairs `README.md` and
`README.zh-TW.md`, assuming the translated README is added by its separate PR.
Other current documents have no Chinese companion and are excluded from the
bilingual check. No existing rule requires a specific document to change with
every source change in a feature area, so `mappings` is empty. There is no
required module-list convention, so both `moduleLists` arrays are empty.
The engine's Markdown link checks remain active.

A PR body may contain this single line with a nonempty reason:

```text
Docs: none — <reason>
```

It exempts only source-to-document mappings. It does not exempt bilingual or
link checks. An empty reason produces a finding. With the current empty
mapping policy, this line does not suppress any active NFU check.

Run the handoff report with a WIP file; replace `<wip-file>` with its path:

```text
python -B tools/repo-checks/handoff_check.py --wip <wip-file> --repo . --branch HEAD --utc-offset +08:00
```

The report checks the first heading containing `以本節為準`, its latest
recognized timestamp, and the selected head SHA. Optionally pass
`--open-prs <file>` with the JSON array from `gh pr list --json number,url`
to check full PR URLs in that section. It is report-only and is not wired into
CI. A completed report exits 0 even with findings; usage, file, and Git errors
exit 2.

Run the shared regression tests with:

```text
python -B -m unittest discover -s tests/repo-checks -v
```
