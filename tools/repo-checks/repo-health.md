# C# syntax health ratchet (H02)

PowerShell 7, Git and the installed SDK selected by `global.json` are required.
No modules, packages, restore or network access are needed. The embedded host is
compiled by that SDK and loads its `Roslyn/bincore` assemblies in an isolated
context. Measurement version: `roslyn-physical-v1`.

```powershell
./tools/repo-checks/repo-health.ps1 -Mode Measure -Repo core -Root . -OutputPath measurement.json
./tools/repo-checks/repo-health.ps1 -Mode Verify -Repo core -Root . -BaseRef origin/main
./tools/repo-checks/repo-health.ps1 -Mode LowerBaseline -Repo core -Root . -BaseRef origin/main
```

`Repo` accepts `core`, `nfc`, `nfh`, `nfu`. Measure defaults to JSON on stdout;
`OutFile` aliases `OutputPath`. The default baseline is
`eng/code-health/baseline.json`; override it with `BaselinePath`.
Verify and LowerBaseline require a committed baseline. There is no enrollment
or automatic refresh mode. Exit codes: **0** pass, **1** debt findings, **2**
tool/input error (including parse errors and unavailable stages).

Baseline fields are exactly `schemaVersion`, `measurementVersion`,
`snapshotCommit`, `limits`, `entities`, `findings`. Entity `ceilings` store measured
values; enforcement compares excess above each limit. Measurement entities expose
`values`, locations, line and `contentHash`; size summaries contain total, maximum,
oversized count and excess. Findings use rule/project/member/symbol/`syntaxHash`
and count, preserving identity across path renames. Symbol+location matches and
unique content hashes are reserved before remaining unique symbols; locations
disambiguate partial files with the same symbols. Ambiguous identities fail closed.
The example baseline may omit `viewTypeLines`; its effective limit is 800 without
adding a field during LowerBaseline.
Baseline entity ownership requires `owner`/`issue`; findings require
`owner`/`removeBy`. Fixes must lower/delete debt in the same change.

The allowed baseline comes from `git show <merge-base>:<path>`, where the checker
computes the merge base of HEAD and BaseRef. Without BaseRef it reads HEAD's
committed baseline. It checks edited allowances against that baseline before
checking source. LowerBaseline refuses new debt before writing, then only removes
entries or lowers existing ceilings/counts; it preserves snapshotCommit.
Failures print `HC_STATE` or `HC_DIAGNOSTIC`, `file:line`, old/new values, entity
and contributing path. They never offset each other through repository totals.

| Metric | Recognizer |
| --- | --- |
| `asyncVoid` | Async void methods/local functions; semantic void-delegate conversions of async lambdas/anonymous methods, using SDK framework references and same-project source. Unresolved conversions remain candidates. |
| `blockingWait` | Syntax candidates: `.Result`/`?.Result`, `Wait`, `WaitAll`, `WaitAny`, and `GetAwaiter().GetResult()`. Domain results, signal waits and bridges need classification. |
| `stateMembers` | Mutable non-const/non-readonly field variables and auto/partial properties with set/init, per qualified type across partials. Positional record properties count; readonly record structs do not. Observable backing is counted once; partial properties deduplicate by name. Manual properties and readonly collections remain review items. |
| `fileLines` | Physical lines, including blank/comment/literal lines; limit 800. Final newline does not create an extra line. |
| `methodLines` | Full declaration span, including attributes, of methods, constructors, destructors, operators, conversions, accessors and local functions; limit 80, with the >150 split-plan review retained in the size summary. |
| `partialFiles` | Distinct files declaring each project-qualified type, including nested/generic types; limit 8. |
| `axamlCodeBehindLines` | Physical `.axaml.cs` lines; limit 150. |
| `viewTypeLines` | Aggregate declaration spans of AXAML `x:Class` types across partials; adjacent matching-name `.axaml.cs` is the fallback. The plan omits a threshold: use the file budget of 800. |
| `suppressions` | SuppressMessage/UnconditionalSuppressMessage attributes (with check ID), each disable pragma plus every disabled ID (`ALL` for no IDs), evaluated NoWarn/WarningsNotAsErrors IDs when props contain those properties. MSBuild evaluates imports/conditions without building; default configuration only in H02. |
| `nativeImportDuplicates` | DllImport/LibraryImport (including resolved attribute aliases): exact DLL string + effective EntryPoint constant, falling back to method name. Report duplicate groups/excess and fingerprints of all participants. Unresolved constants fail. |
| `generationFields` | Mutable int/long/uint/ulong/Int32/Int64 fields whose names contain Generation/Revision/RequestId, ignoring case; increment/adoption semantics need review. |
| `fakeClockDuplicates` | Candidate TimeProvider descendants, ClockState, and Fake/Manual/Test-prefixed Clock/TimeProvider names. Owner classification awaits H03's reviewed seam manifest; no blanket exemption. |
| `workspaceDuplicates` | Exact TestWorkspace types, or types directly containing Path.GetTempPath, Directory.CreateDirectory and Directory.Delete calls. Candidates require clone/ownership review. |
| `sourceTextAssertions` | Discovery: ReadText, ReadAllText/Lines/Bytes (including Async), OpenText; Contains/DoesNotContain/Matches/DoesNotMatch calls in the same file. All locations retained, with a separate Architecture.Tests or Tests+Architecture/Boundary/Layout/Snapshot filter flag. These counts do not prove source dependence. |

Tracked C# source is measured in Git trees; standalone fixture trees use all files.
Only bin/obj/artifacts/.git directories are excluded, including during discovery;
generated suffixes and tracked generated directories remain in scope. Project
ownership is the nearest single csproj. Parser configuration is SDK Latest syntax
with default preprocessor symbols; H03 supplies evaluated configurations, linked
files, project references and complete coverage. No semantic correctness claim is
made for candidates.

H03 must add the schema, evaluated coverage, build/SARIF/format providers, pinned
bundle drift/provenance checks, seam classifications, workflow and first baseline.
`Build`, `Sarif`, `Format` return `HC_NOT_IMPLEMENTED`/2. Diagnostic findings,
including `bannedApi`/RS0030, have reader/fingerprint slots; Verify/LowerBaseline
refuse to discard them until H03 supplies their measurement provider. A successful
Verify explicitly reports H02 syntax scope. `ParserDirectory` is a troubleshooting
hook that rejects substitution of another SDK's parser.

```text
python -B -m unittest discover -s tests/repo-checks -p test_repo_health.py -v
```
