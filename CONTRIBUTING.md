# Contributing

## Branch model

- `main` holds released versions and receives release merges only.
- A version branch such as `0.1.2` is the integration trunk of that version.
- `feature/<version>/<topic>` carries one logical change and merges into its
  version trunk.
- `VERSION` changes in the release-preparation commit, so on a trunk it names
  the previous release until then.

Owner decision, 2026-10-02: `0.1.2` finishes the repository template adoption
(agent documents and CI alignment, no product behavior change); NVT FW UTIL
product work uses a new `0.2.0` trunk.

## Change sequence

1. Branch from the trunk head. One writer per branch.
2. Implement one logical change and run the affected tests. Commit with a
   Conventional Commit title and a body that states the reason.
3. At the integration boundary run `./scripts/verify.ps1` outside any sandbox.
4. Rebase onto the trunk head, then open the pull request against the trunk.
5. Obtain an independent review of the exact head and post its record on the
   pull request. After any later push, review again and post a new record.
6. Merge under the approval rule below with
   `gh pr merge <number> --merge --match-head-commit <head>`.

## Review record

A review record names the full head SHA it reviewed and gives:

- the verdict: `accept` or `reject`;
- findings, each with a severity, `path:line`, the failing scenario and a fix;
- the limits of what was checked.

Severities: **P0** ships wrong results, loses data or leaks private material;
**P1** breaks a contract or required behavior; **P2** should be fixed soon;
**P3** is optional polish.

## Who approves

A pull request that touches any owner-gated path is owner-gated as a whole.

| Gate | Paths | Merge condition |
| --- | --- | --- |
| Owner-gated | `src/**`; `.github/**`; `VERSION`, `global.json`, `Directory.Build.props`; `eng/**`; the scripts that CI and the release workflows execute (`scripts/verify.ps1`, `verify-release-identity.ps1`, `check-line-budget.ps1`, `performance-gate.ps1`, `package.ps1`, `smoke-release.ps1`, `install-ffmpeg.ps1`); `AGENTS.md`, `CONTRIBUTING.md`; agent permission settings (`.claude/settings*.json`, `.codex/**`) | The review-gated condition plus the owner's GitHub approval of the last push |
| Review-gated | Everything else: documents, tests, other scripts, agent skills | Review verdict `accept` with zero P0 and P1 findings on the exact head, and every required check green |

A failing required check or an open P0 or P1 finding blocks every merge.
Releases follow [`docs/release.md`](docs/release.md) and wait on the GitHub
`release` environment.

## GitHub access for agents

Agents write to GitHub (pull requests, comments, merges) through the GitHub
App wrapper the owner provisions for this repository. The owner supplies its
setup values in the session. Credential files, `git credential` commands,
credential helpers, remotes, rulesets, environments and the default branch are
the owner's to change. Until the App covers this repository, agent branches
stay local.

## Temporary files

Tests and scripts create uniquely named directories under the system temporary
path and remove them. Put any other scratch output in the ignored `artifacts/`
or `out/` folders, or outside the repository.
