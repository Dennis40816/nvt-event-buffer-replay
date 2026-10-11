# C# health enforcement (H02/H03)

PowerShell 7, Git and the installed SDK selected by `global.json` are required.
The syntax parser needs no modules, packages, restore or network access. Complete
enforcement uses the existing locked restore and host NuGet cache; it never restores. The embedded host is
compiled by that SDK and loads its `Roslyn/bincore` assemblies in an isolated
context. Measurement version: `roslyn-physical-v1`.

The checker sets `DOTNET_CLI_UI_LANGUAGE=en`, `VSLANG=1033` and `PreferredUILang=en-US` for every tool it starts, so
compiler and format messages do not change with the host language. It runs `dotnet build` and `dotnet msbuild` with
`-m:4 -nodeReuse:false`, and sets `MSBUILDDISABLENODEREUSE=1` so that `dotnet format` leaves no MSBuild node behind.

```powershell
./tools/repo-checks/repo-health.ps1 -Mode Measure -Repo core -Root . -OutputPath measurement.json
./tools/repo-checks/repo-health.ps1 -Mode Verify -Repo core -Root . -Solution Nvt.Core.sln -BaseRef origin/main
./tools/repo-checks/repo-health.ps1 -Mode LowerBaseline -Repo core -Root . -Solution Nvt.Core.sln -BaseRef origin/main
```

`Repo` accepts `core`, `nfc`, `nfh`, `nfu`. Measure defaults to JSON on stdout;
`OutFile` aliases `OutputPath`. The default baseline is
`eng/code-health/baseline.json`; override it with `BaselinePath`.
Verify and LowerBaseline require a committed baseline and Solution. Enroll is the
only production enrollment mode: it requires all providers to succeed, refuses any
existing baseline, and writes debt owned by NVT CORE with no due date. It also
generates the marked HealthBaselineWarningIds block in projects.props. No mode
automatically refreshes or raises debt. An absent baseline at the merge base fails
closed: first enrollment must be integrated before checking subsequent PRs against
that base. Measure remains syntax-only; it cannot satisfy the CI gate. Exit codes: **0** pass, **1** debt findings, **2**
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
Baseline entity ownership requires `owner`/`issue`; findings require `owner`.
`removeBy` is optional on a finding (owner decision of 2026-10-11: debt has no deadline). Fixes must
lower/delete debt in the same change. Debt cannot be added and its count cannot grow, with or without a date.
When a finding has a `removeBy`, Verify compares it with today's date (UTC) in both ledgers. A finding is due
on its `removeBy` day and fails from the next day with `HC_STATE`, naming the finding. It lists the first 20
expired findings of a ledger and then one summary line with the count. A `removeBy` that is present and is
not a real calendar date is a bad ledger (exit 2). A finding without `removeBy` never expires. Structural
entities have no date, so they never expire. Core's two ledgers carry no `removeBy`.
LowerBaseline never moves, adds or removes a date. Adding, moving or removing a date is one JSON edit in a dedicated pull
request with an owner decision. The checker does not compare a date with the base ref, so the owner's review
of that pull request is the control.
The bundle file `schema.json` still lists `removeBy` as required. The checker takes it out of that list in
memory when it reads a baseline, because `schema.json` is a distributed file with a pinned hash. The file
changes at the next pin synchronization.

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
| `viewTypeLines` | Aggregate declaration spans of AXAML `x:Class` types across partials; adjacent matching-name `.axaml.cs` is the fallback. The plan omits a threshold: use the file budget of 800. A code-only view (no `.axaml`) is a type that derives, directly or through a base type of the same project, from an Avalonia view base type: `Control`, `UserControl`, `TemplatedControl`, `ContentControl`, `ItemsControl`, `Window`, `Panel`, `Decorator`, `Border`, `Grid`, `StackPanel`, `DockPanel`, `WrapPanel`, `Canvas`, `Button`, `ToggleButton`, `ListBox`, `TextBox`, `ScrollViewer`, `Viewbox`, `ComboBox`, `TabControl`, `Slider`, `ProgressBar`, `CheckBox` or `RadioButton`. The checker has no Avalonia assemblies, so an unresolved base type counts by its simple name, and only in a project whose source mentions Avalonia (a `using` or a qualified name, also with `global::`). A resolved base type must be in the `Avalonia` namespace. The view is measured only when its span is strictly above 800 lines, so a small code-only view adds no entity. The file name decides nothing. Known limits: the name list is closed, and a view that derives from a base type of another project (an unresolved type with a name outside the list) is not found. Add the name to the list when a tool needs it. The 800-line budget of a code-only view is fixed, so a tightened `limits.viewTypeLines` applies only to AXAML views. A project-defined namespace that starts with `Avalonia.` counts as a mention. The branch for a resolved Avalonia base type has no fixture, because the fixtures have no Avalonia assemblies. |
| `suppressions` | SuppressMessage/UnconditionalSuppressMessage attributes (with check ID), each disable pragma plus every disabled ID (`ALL` for no IDs), evaluated NoWarn/WarningsNotAsErrors IDs when props contain those properties. MSBuild evaluates imports/conditions without building; default configuration only in H02. |
| `nativeImportDuplicates` | DllImport/LibraryImport (including resolved attribute aliases): exact DLL string + effective EntryPoint constant, falling back to method name. Report duplicate groups/excess and fingerprints of all participants. Unresolved constants fail. |
| `generationFields` | Mutable int/long/uint/ulong/Int32/Int64 fields whose names contain Generation/Revision/RequestId, ignoring case; increment/adoption semantics need review. |
| `fakeClockDuplicates` | Candidate TimeProvider descendants, ClockState, and Fake/Manual/Test-prefixed Clock/TimeProvider names. The `*.TestSupport` project is the reviewed owner (conventions T2) and is exempt. `*.TestSupport.Tests` and every other project are not. |
| `workspaceDuplicates` | Exact TestWorkspace types, or types directly containing Path.GetTempPath, Directory.CreateDirectory and Directory.Delete calls. Candidates require clone/ownership review. |
| `timeWaitsInTests` | In a test project, a call to `Task.Delay`, `Thread.Sleep` or `SpinWait.SpinUntil`. Use `SignalWait` or `ManualTimeProvider`. Own ledger: `test-debt.json`. |
| `elapsedAssertionsInTests` | In a test project, an `Assert.*` call whose arguments use an identifier containing `Elapsed` or `Stopwatch`. Own ledger. |
| `tempPathInTests` | In a test project, `Path.GetTempPath`, `Path.GetTempFileName` or `Directory.CreateTempSubdirectory`. Use `TestWorkspace`. Own ledger. |
| `sourceTextReadsInTests` | In a test project, a call to `ReadText`, `ReadAllText[Async]`, `ReadAllLines[Async]` or `OpenText`. Files under Architecture, Boundary, Layout or Snapshot names, and `Architecture.Tests` projects, are the sanctioned readers and are skipped. Own ledger. |
| `headlessSessionSetups` | In a test project, `UseHeadless`, `HeadlessUnitTestSession.StartNew` or `GetOrStartForAssembly`, and the `AvaloniaTestApplication` assembly attribute. Each test assembly needs one setup. Own ledger. |
| `childProcessInTests` | In a test project, `new Process`, `new ProcessStartInfo` and `Process.Start`. Use `ChildProcessFixture`. Own ledger. |
| `sameNameFakes` | In a test project, a non-partial type named `Fake`, `Stub`, `Spy`, `Mock`, `Recording` or `Dummy` plus an upper-case letter, declared in two or more files. Every participant is a finding. Own ledger. |
| `sourceTextAssertions` | Discovery: ReadText, ReadAllText/Lines/Bytes (including Async), OpenText; Contains/DoesNotContain/Matches/DoesNotMatch calls in the same file. All locations retained, with a separate Architecture.Tests or Tests+Architecture/Boundary/Layout/Snapshot filter flag. These counts do not prove source dependence. |

Tracked C# source is measured in Git trees; standalone fixture trees use all files.
Only bin/obj/artifacts/.git directories are excluded. Enforcement measures each
tracked source in every evaluated Compile owner, including linked files. It rejects
unowned tracked C# and discovers untracked projects too. Syntax candidate metrics
retain H02's SDK Latest/default preprocessor convention; diagnostic fingerprints
use evaluated Release DefineConstants and ResolveReferences. H02's semantic
candidate metrics still use SDK framework references and same-project source;
they do not prove complete semantic R1–R12 compliance.

Verify/LowerBaseline/Enroll validate the local schema, bundle provenance, managed
EditorConfig and evaluated project settings, create artifacts/code-health, remove
stale named outputs, then run:

```text
dotnet build <solution> -c Release --no-restore --no-incremental -p:HealthCollectDiagnostics=true
dotnet format <solution> --verify-no-changes --no-restore --severity warn --report artifacts/code-health/format
dotnet format style <test.csproj> --verify-no-changes --no-restore --severity warn --diagnostics IDE0005 --report artifacts/code-health/format-style-<project>
```

Every project must produce fresh, readable SARIF 2.1.0. A failed build, analyzer
crash, invalid location, failed project load or missing report fails. SARIF warning
results enter the ledger; notes do not. RS0030's quoted symbol is parsed regardless
of UI language, then resolved with SDK Roslyn to a banned documentation ID. Reports
print HC_DIAGNOSTIC file:line, symbol, member and old/new counts. Build error rules
(including VSTHRD100 and CS4014) cannot be grandfathered or suppressed by generated
warning IDs. Apps must migrate their async void handlers before build/enrollment;
a syntax baseline cannot exempt an analyzer error. HealthPublicApi is set for
Core's four published projects in eng/code-health/projects.props (H07d). Each
project keeps its own PublicAPI.Shipped.txt and PublicAPI.Unshipped.txt.

Analyzer warnings repeated by format after prospective edits reuse fresh SARIF
occurrences in source order, independently gated by the build ledger. They are not
counted twice. Actual format changes use rule FORMAT:<DiagnosticId>; their symbol is the diagnostic ID.
Exit 2 is accepted only with a valid nonempty report whose complete findings are
within the ledger (or the explicit first Enroll). Exit 0 still requires a valid
report. Other exits, project-load failures, missing/malformed/empty nonzero reports
fail. Diagnostic and format fingerprints group equal identities and preserve their
occurrence count; unrelated removals never offset additions.

Diagnostic syntaxHash v1 uses the token at the 1-based diagnostic line and UTF-16
column. Select its nearest StatementSyntax excluding BlockSyntax, else its nearest
MemberDeclarationSyntax, else the compilation unit. Join DescendantTokens().Text
in source order with one ASCII space, excluding trivia. Hash UTF-8 without BOM
with SHA-256, encoded as lowercase 64-digit hex. Before hashing, every identity
hash (token text, file contentHash, entity and duplicate hashes) replaces CRLF and
a lone CR with LF, so a CRLF and an LF checkout of the same source give the same
fingerprints. Member identity uses H02's Roslyn
qualified type, callable signature, nested/local member, property or field group.
Locationless project warnings use member MSBuild and the trimmed invariant message
as hash input. Syntax metrics retain roslyn-physical-v1's recognized-node tokens.
Pragma/attribute covered statements additionally use rule suppressionScopes with
the same statement-token hash. A wider span, added or substituted covered code
fails per fingerprint; narrowing/removal lowers debt. Protected error suppressions,
all-warning disables and whole-file RS0030 disables fail.
Path and line are display-only; rule/project/member/symbol/syntaxHash plus count
form the debt identity, so a file rename retains its allowance.

The versioned seam wrapper is `{ "schemaVersion": 1, "entries": [] }`. Entries use
the plan's path/member/symbol/owner/reason/kind/review fields. Match exact path and
symbol plus exact member; member `*` grants that symbol to a reviewed file owner.
Directories and symbol globs never match. kind is permanent-seam. Authorized
RS0030 counts remain separate from debt; Core starts with zero entries. Adding
existing debt as a permanent seam requires owner review, not mechanical migration.

The lock is `{ "schemaVersion": 1, "coreCommit": "<40 lowercase hex>", "files":
{ "<each of the six filenames>": "<SHA-256 lowercase hex>" } }`. No policy.json is
needed: versions, valid layers, limits and package pins are fixed in the checker
and shared schema. Lock hashes cover raw LF UTF-8 bytes, without Git filters. Git
blob identity additionally checks all six files against coreCommit's canonical
files. Core also checks its working canonical tree. With BaseRef, the committed
merge-base pin must agree; without it, the committed HEAD pin must agree. An app
supplies -CoreRoot <local approved Core checkout> for the canonical object lookup.
Without -CoreRoot (an application whose CI does not fetch Core) the checker runs in
lock-only mode: it checks LF/UTF-8 and that each eng/core-health file equals the SHA-256
in the lock. With BaseRef it also requires every lock hash to equal the merge-base
lock, because the pin alone cannot detect a pull request that edits a bundle file and
its lock hash together. Lock-only mode has no canonical Core objects, so a copy of the
checker kept in the application repository is governed by code review, not by a
self-check. Application CI must always pass -BaseRef: without it, a bundle file and its
lock hash edited together are accepted. Core itself always compares with its own canonical tree.
No network operation occurs. A pin change is a separately reviewed synchronization;
first integrate the approved pin before using it as the base for adoption changes.

The managed root block uses `# BEGIN CORE HEALTH MANAGED BLOCK` and
`# END CORE HEALTH MANAGED BLOCK`. Its interior must exactly equal the canonical
EditorConfig bytes, including LF. We conservatively reject changed protected
non-severity settings in descendant/root-tail configuration and accept only equal
or stronger protected severities. The rule is conservative on purpose: a protected
`[*.cs]` key such as indent_size with a different value is rejected in ANY section,
even one that cannot match `.cs` (for example `[*.json]`); move or delete such lines.
Outside the managed block (every descendant file, and the root file's tail after the
block) an EditorConfig must also not contain a generated_code key, a
dotnet_analyzer_diagnostic.* key, a dotnet_code_quality.* option (it narrows analysis
without changing a severity), or a dotnet_diagnostic.<ID>.severity of none, silent or
suggestion; warning and error stay allowed. The line reader follows Roslyn: it drops a
leading BOM, accepts `=` or `:` as the separator, and ignores a trailing `#` or `;`
comment. File names are matched case-insensitively. Only Core skips
`tools/repo-checks/csharp/.editorconfig`, because only Core compares it with the pin. Per-project Debug, Release and other declared
configurations must retain the shared settings, SDK WarningsAsErrors IDs, exact
ledger warning exemptions and approved analyzer package references/assets/versions.
NoWarn must equal the suppression ledger in every evaluated configuration.
Multiple target frameworks currently fail closed because the bundle ErrorLog
contract has only one path per project; apps must add distinct per-TFM outputs
before multi-target adoption.

Enroll once after the owner has approved the current-tree evidence:

```powershell
./tools/repo-checks/repo-health.ps1 -Mode Enroll -Repo core -Root . -Solution Nvt.Core.sln
```

### Test-duplication ledger

The seven test rules above (`timeWaitsInTests` to `sameNameFakes`) answer to the testing
conventions in `docs/core/testing.md`. They use their own ledger,
`eng/code-health/test-debt.json` (`-TestDebtPath` changes the path), and never touch
`baseline.json`. Clock classes and temp-directory lifecycles stay with
`fakeClockDuplicates` and `workspaceDuplicates`.

A file is a test file when its path has a `tests` or `test` folder or a folder ending in
`.Tests`/`.Test`, or when its project name ends in `Tests`. A folder or project ending in
`.TestSupport` is exempt: it is the one place where these helpers are built.

- `-Mode EnrollTestDebt -Solution <sln>` writes the ledger once. It evaluates the projects
  (so a file linked into two test projects counts for each, as in Verify) but does not build.
  Without `-Solution` it attributes by the nearest project file, which can disagree with
  Verify. It refuses to overwrite a ledger. The owner follows Enroll. The ledger has no `removeBy`.
- Verify fails with `New test duplication` when a fingerprint appears or its count grows.
  It also fails with `run LowerBaseline` when a recorded fingerprint shrinks and the ledger
  was not lowered. LowerBaseline lowers the ledger and never adds to it.
- The ledger of the base ref is the ceiling. A ledger that grows, or is deleted, against
  the base ref fails. When the base ref has no ledger, the change is the first enrollment.
- When the ledger is missing and a test rule has findings, Verify fails and names
  `EnrollTestDebt`.
- A repository that pins this script must seed its own ledger in its gate pull request.
  `baseline.json` and the bundle schema do not change.

### First enrollment (seeding)

Enroll builds the solution. A build with TreatWarningsAsErrors=true fails on every
analyzer that has no ledger entry yet, so the first Enroll of a repository needs a
seed. Build once with `-p:TreatWarningsAsErrors=false`, collect the diagnostic IDs
from the SARIF output, and write them into the generated `HealthBaselineWarningIds`
block of `eng/code-health/projects.props`. Then run Enroll, which regenerates that
block from the ledger. Core's own enrollment used the seed
`CA1031;EnableGenerateDocumentationFile;IDE1006;RS0030`.

`-Owner <name>` sets the owner written to entities and findings. Without it, Core
uses `NVT CORE` and any other `-Repo` uses its upper-case name (nfc becomes NFC).
The issue stays shared (`health-refactor/2026-10-31`). Enroll writes no `removeBy`.
Verify checks owner and issue only as non-empty strings, so a hand edit is not
rejected, but `-Owner` is the supported way.

`asyncVoid` counts async void methods and local functions and also async lambdas or
anonymous methods that are converted to void delegates. Only VSTHRD100 (async void
methods) is an error in the bundle `.editorconfig`. Lambdas converted to void
delegates are counted in the ledger but do not fail the build.

The ledger stores H02 structural excess and syntax findings plus analyzer and
format debt. Entities use issue health-refactor/2026-10-31; findings have no removeBy,
all with the Enroll owner (NVT CORE for Core). Verify checks evaluated HealthBaselineWarningIds
against build findings in the ledger, per project. LowerBaseline lowers both the
ledger and generated warning-ID block; it never enrolls. Tooling/function-only
loading supports provider tests, and never executes enrollment or verification.

## Known limitations

An independent review of the first gate found six gaps. The gate does not close them yet. Each has an Issue.

1. Dennis40816/nvt_fw_core#163: analyzer configuration outside files named `.editorconfig` (`EditorConfigFiles`, `GlobalAnalyzerConfigFiles`, removed `Analyzer` items) is not checked.
2. Dennis40816/nvt_fw_core#164: sources and EditorConfig files under any `bin`, `obj` or `artifacts` folder escape coverage.
3. Dennis40816/nvt_fw_core#165: a baseline entity can be re-pointed, so one structural allowance can offset another.
4. Dennis40816/nvt_fw_core#166: `eng/code-health/seam-owners.json` and the project `HealthLayer` are not compared with the merge base.
5. Dennis40816/nvt_fw_core#167: suppressed analyzer results leave no fingerprint.
6. Dennis40816/nvt_fw_core#168: the format ledger depends on the checkout line endings (see "Checkout line endings").

Policy-change rule until these are fixed: a pull request that changes any of the files below is a policy change. The reviewer reads the change as policy, not as routine code, and the pull request says so.

- `eng/code-health/seam-owners.json`, `eng/code-health/projects.props` (including `HealthLayer`), `eng/code-health/baseline.json`
- `eng/core-health/*`, `eng/core-health.lock.json`, and the managed block in `.editorconfig`
- any project, `Directory.Build.*` or `.editorconfig` change that adds analyzer, EditorConfig or suppression inputs
- a tracked file under a `bin`, `obj` or `artifacts` folder

### Checkout line endings

Identity hashes ignore line endings, but the `dotnet format whitespace` ledger does not. Core enrolled the baseline on a CRLF checkout, which is the Git for Windows default and what a Windows runner produces. A pure LF checkout of the same commit reports different WHITESPACE counts (187 format fingerprints differ in this repository). Run Verify on the same checkout style that enrolled the baseline. A repository that enrolls on Windows and verifies on Linux must pin the checkout style first.

```text
HEALTH_SKIP_HOST_TESTS=1 python -B -m unittest discover -s tests/repo-checks -p test*.py -v
python -B -m unittest discover -s tests/repo-checks -p test_health_enforcement.py -k HostMutationTests -v
```

The fast suite has a 600-second process limit in host checks. The host mutation
test requires restored assets: whole Verify must exit 0, then a banned call in a
copy must exit 1. It never restores, changes branches or writes the shared index.
