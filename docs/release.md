# Release process

The Windows release is a self-contained `win-x64` archive. Its product version
comes from `VERSION`; its source identity is one exact protected-`main` commit.
NuGet dependencies are restored from committed lock files.
The archive contains both the desktop workstation and headless CLI; neither
requires a machine-wide .NET installation or network access at runtime.

## Local or review preview

From a clean commit, run:

```powershell
$version = (Get-Content ./VERSION -Raw).Trim()
$commit = (git rev-parse HEAD).Trim()
dotnet restore Nvt.EventBufferReplay.sln --locked-mode
./scripts/verify.ps1
./scripts/package.ps1 -Version $version -Commit $commit
./scripts/smoke-release.ps1 -PackagePath "./artifacts/release/NvtEventBufferReplay-v$version-win-x64.zip"
```

`Preview package` performs the same build on demand and retains its GitHub
Actions artifact for three days. It never creates a tag or GitHub Release.

## Stable release

Run `Stable release` from the current `main` workflow and provide that exact
40-character commit SHA. The default `PREPARE_ONLY` mode builds, tests,
packages, verifies a fresh extraction, and uploads an immutable candidate.

To publish, select `PUBLISH`. The candidate must pass first; the `promote` job
then waits on the GitHub `release` environment. That environment should require
an owner review. Promotion receives only the prepared assets and write access:
it does not check out or execute release-source code. It creates an annotated
tag bound to the reviewed SHA and publishes both the ZIP and adjacent SHA-256
file. A final read-only job downloads and verifies the published archive.

## Package contract

The archive has one top-level directory and exactly four files:

- `NvtEventBufferReplay.exe`
- `nvt-replay.exe`
- `RELEASE.json` with version, commit, commit time, runtime, self-contained status, offline defaults, telemetry status, and payload names
- `SHA256SUMS.txt` covering both executables and release identity

The adjacent `.zip.sha256` covers the complete archive. Packaging starts from
empty repository-owned staging directories, rejects dirty worktrees, and fails
if the staged file set differs from this closed allowlist.

`scripts/verify.ps1` is the shared local, preview, and stable-candidate gate. It
performs locked restore, warning-free Release build, tests, handwritten line
budget, and the performance smoke gate. `smoke-release.ps1` verifies the outer
and inner hashes, identity, allowlist, packaged CLI format inventory, and—when
not skipped—a visible desktop window from a fresh temporary extraction.

The desktop manifest declares Windows 10 compatibility and the package is
win-x64 self-contained. Release evidence must include a Windows 11 UI smoke;
Windows 10 remains a manual compatibility smoke on available project hardware
because GitHub-hosted runners are Windows Server rather than client Windows.
