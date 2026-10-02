# Contributing

## Branch model

- `main` holds released versions and receives release merges only.
- A version branch such as `0.1.2` is the integration trunk of that version.
  The owner decides when a trunk is created and when it is released.
- `feature/<version>/<topic>` carries one logical change and merges into its
  version trunk.
- `VERSION` changes in the release-preparation commit, so on a trunk it names
  the previous release until then.

Owner decision, 2026-10-02: `0.1.2` finishes the repository template adoption
(agent documents, a check that enforces the approval rule below, and CI
alignment) with no product behavior change, and keeps the checks that
`ci.yml` and `scripts/verify.ps1` already run. NVT FW UTIL product work uses
a new `0.2.0` trunk.

## Change sequence

1. Branch from the trunk head.
2. Implement one logical change and run the affected tests. Commit with a
   Conventional Commit title and a body that states the reason.
3. At the integration boundary run `./scripts/verify.ps1` outside any sandbox.
4. Rebase onto the trunk head, then open the pull request against the trunk.
5. Obtain an independent review of the exact head and post its record on the
   pull request. After any later push, review again and post a new record.
6. Merge under the approval rule below with
   `gh pr merge <number> --merge --match-head-commit <head>`.

## Review record

A review record is posted as a pull request review (not an issue comment).
Its first non-empty line is `Review record: <full head SHA> <verdict>`;
the record gives:

- the verdict: `accept` or `reject`;
- findings, each with a severity, `path:line`, the failing scenario and a fix;
- the limits of what was checked.

Severities: **P0** ships wrong results, loses data or leaks private material;
**P1** breaks a contract or required behavior, or misdirects an agent;
**P2** should be fixed soon; **P3** is optional polish.
An `accept` verdict requires zero P0 and P1 findings.

The latest pull request review from the owner or the GitHub App whose body
contains the letters "review record", in any case and with any spacing or
punctuation between the two words, is the record in force. Write that first
line exactly: any other form, including a review that only mentions a review
record, reads as a malformed record and blocks the merge until a well-formed
record follows. A record posted as a change request, or later dismissed, does
not accept. Issue comments and inline comments are not read.

## Who approves

Owner-gated paths are everything that decides what is built, tested, checked,
released or approved. A pull request that touches one is owner-gated as a
whole. Every pattern below matches at any depth of the repository, ignoring
case. The exact pattern list is in
[`approval-policy.json`](.github/approval-policy.json).

- Product and build graph: `src/**`, `*.sln`, `*.slnx`, `*.slnf`, `*.csproj`,
  `packages.lock.json`, `*.props`, `*.targets`, `*.rsp`, `global.json`,
  `nuget.config`, `.editorconfig`, `.globalconfig`.
- Existing tests: any status other than `A` under `tests/**`, including
  approved snapshots and fixtures. GitHub's changed-file list supplies the
  status (`added` means `A`) and detects renames. A rename or copy into
  `tests/**` is owner-gated even when its destination is new.
- CI, gates and release: `.github/**`, `scripts/**`, `eng/**`, `VERSION`,
  `docs/release.md`, `CODEOWNERS`.
- Decisions and contracts: `docs/adr/**`, `docs/product-spec.md`.
- Rules and agent configuration: `AGENTS.md`, `AGENTS.override.md`,
  `CLAUDE.md`, `CONTRIBUTING.md`, `.agents/**`, `.claude/**`, `.codex/**`,
  `.mcp.json`.
- Repository hygiene: `.gitignore`, `.gitattributes`.
- Any merge into `main`.

Everything else is review-gated: other documents and new files under
`tests/**`. A new file under `tests/**` can still change how existing tests
run (for example an assembly-level attribute, a module initializer, a global
using, a type that shadows one the tests use, or a runner configuration
file); the reviewer checks for that first.

Review-gated merge condition: the head contains the current head of the base
branch, the latest review record is for that exact head with verdict `accept`
and zero P0 and P1 findings, and the required check has completed green on it.

Owner-gated merge condition: all review-gated conditions, plus the owner's
latest review decision being an approval on that same head. A later change
request or dismissal blocks it.

The required build check is the `build-and-test` job of
`.github/workflows/ci.yml`. The `approval` workflow (job
`governance / approval`) evaluates the approval rule on pull requests and
reports a commit status; its script does not read `build-and-test`. The
repository ruleset must require `build-and-test` and the commit status
`governance/approval-rule`, pinned to the GitHub Actions source, plus Code
Owner review with stale approval dismissal and branches up to date. The
GitHub App must hold no `workflows` permission or commit-status write
permission.

GitHub reads `approval.yml` from the pull request's merge ref, which the pull
request can change. The workflow checks out the base branch's script and
policy, but that checkout does not make the workflow definition trusted.
Several `governance / approval` check runs accumulate on one head; a stale
red run remains after approval. A commit status belongs to a commit, so two
open pull requests with the same head share one status and the latest run
wins. The ruleset must require the commit status `governance/approval-rule`
and `build-and-test`, not the check run `governance / approval`. Pull requests
from forks cannot post the status with a read-only token, so they stay
blocked until a maintainer handles them.

The merging agent confirms each part of the condition for the exact head
immediately before merging: `git fetch origin <base>` followed by
`git merge-base --is-ancestor origin/<base> <head>`, then the check conclusion
and the owner's review as GitHub reports them. A head behind its base branch,
a pending or failed check, an open P0 or P1 finding, or an approval of an
earlier commit blocks the merge.
From a checkout of `origin/<base>`, the merging agent also runs
`python scripts/approval_check.py` with `--repository`, `--pull-request`,
`--expected-head <head>` and `--checked-out-base` set to that checkout's
`git rev-parse HEAD`, and requires exit 0 before merging. While a base
branch has no checker yet (the pull request that introduces it, and the
first release merge into `main`), the check stays red and this step cannot
run: the merging agent confirms the other parts and the owner merges.

Every merge moves the base branch, so the other open pull requests then need a
rebase, a new review and a new check run. Ask for the owner's approval after
the final rebase, and merge one pull request at a time.

Agents open pull requests through the GitHub App, so the owner's account
remains free to approve them. GitHub forbids approval of one's own pull
request. An owner-authored owner-gated pull request leaves the approval check
and status red; the owner merges it personally, through the ruleset bypass
once a ruleset requires the status.

Releases follow [`docs/release.md`](docs/release.md). Publishing a release is
the owner's action, or an agent's on the owner's instruction for that version.

## GitHub access for agents

Agents write to GitHub (pull requests, comments, merges) through the GitHub
App wrapper the owner provisions for this repository. The owner supplies its
setup values in the session. Credential files, `git credential` commands,
credential helpers, remotes, rulesets, environments and the default branch are
the owner's to change. Until the App covers this repository, agent branches
stay local.

## Temporary files

Tests use the system temporary path. The gate and packaging scripts write
under the ignored `artifacts/` folder and reset their own subfolders there, so
run one of them at a time per checkout. Put any other scratch output in the
ignored `out/` folder or outside the repository.
