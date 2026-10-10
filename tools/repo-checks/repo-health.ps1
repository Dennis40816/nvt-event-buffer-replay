# Copyright (c) 2026 Dennis Liu. All rights reserved.
#Requires -Version 7.0
<#
H02 syntax ratchet, measurement contract roslyn-physical-v1. No package restore.
The embedded host uses the global.json-selected SDK's Roslyn, in an isolated ALC.
Narrow discovery choices (also in repo-health.md): unresolved async lambdas remain
candidates; blockingWait is syntax only; viewTypeLines uses the 800-line budget;
clock candidates are TimeProvider descendants or Fake/Manual/Test*Clock,
Fake/Manual/Test*TimeProvider, ClockState; workspace lifecycle candidates contain
both Directory.CreateDirectory and Directory.Delete with a Path.GetTempPath call.
Source-read discovery uses ReadText/ReadAllText[Async]/ReadAllLines[Async]/
ReadAllBytes[Async]/OpenText and Contains/DoesNotContain/Matches/DoesNotMatch in
the same file. Discovery is retained everywhere, with a separate plan-filter flag.
No approved-owner exemption until H03 supplies the reviewed seam manifest.
Build outputs (bin, obj, artifacts, .git) are excluded; generated filename suffixes
are NOT excluded. State includes init and positional-record properties; observable
backing fields are counted once, partial properties by symbol once.
H03 replaces the hand baseline validation with csharp/schema.json, supplies full
project/configuration coverage and the build/SARIF/format/provenance stages.
#>
[CmdletBinding()]
param(
    [string]$Mode = 'Verify',
    [string]$Repo = 'core',
    [string]$Root = '.',
    [string]$BaseRef = '',
    [string]$BaselinePath = 'eng/code-health/baseline.json',
    [Alias('OutFile')][string]$OutputPath = '',
    # A diagnostic hook: cannot substitute another parser for the pinned one.
    [string]$ParserDirectory = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Stable output on every host: UTF-8 without BOM, and compiler messages in the invariant culture.
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Globalization.CultureInfo]::CurrentUICulture = [Globalization.CultureInfo]::InvariantCulture
$script:Version = 'roslyn-physical-v1'
$script:Limits = @{ fileLines = 800; methodLines = 80; partialFiles = 8; stateMembers = 30; axamlCodeBehindLines = 150; viewTypeLines = 800 }
$script:SyntaxRules = @('asyncVoid', 'blockingWait', 'suppressions', 'generationFields', 'nativeImportDuplicates', 'fakeClockDuplicates', 'workspaceDuplicates')

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    $lines = @(& $Exe @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "$Exe exited $LASTEXITCODE`: $($lines -join [Environment]::NewLine)" }
    return ($lines -join [Environment]::NewLine)
}

function Read-Baseline([string]$Text, [string]$Label) {
    # H03: replace with the pinned schema, retaining these semantic checks.
    try { $b = ConvertFrom-Json -InputObject $Text -AsHashtable -Depth 100 }
    catch { throw "Bad baseline $Label`: invalid JSON: $($_.Exception.Message)" }
    if ($b -isnot [System.Collections.IDictionary]) { throw "Bad baseline $Label`: expected object" }
    foreach ($key in @('schemaVersion', 'measurementVersion', 'snapshotCommit', 'limits', 'entities', 'findings')) {
        if (-not $b.Contains($key)) { throw "Bad baseline $Label`: missing $key" }
    }
    if (-not (Test-Count $b.schemaVersion) -or $b.schemaVersion -ne 1 -or $b.measurementVersion -cne $script:Version) { throw "Bad baseline $Label`: schema/measurement migration required" }
    if ($b.snapshotCommit -isnot [string] -or $b.snapshotCommit -notmatch '^[0-9a-fA-F]{7,40}$') { throw "Bad baseline $Label`: invalid snapshotCommit" }
    if ($b.limits -isnot [System.Collections.IDictionary] -or $b.entities -isnot [array] -or $b.findings -isnot [array]) { throw "Bad baseline $Label`: limits/entities/findings have wrong types" }
    foreach ($metric in $script:Limits.Keys) {
        if (-not $b.limits.Contains($metric)) {
            if ($metric -eq 'viewTypeLines') { continue }
            throw "Bad baseline $Label`: missing limit $metric"
        }
    }
    foreach ($metric in $b.limits.Keys) {
        if (-not $script:Limits.ContainsKey($metric) -or -not (Test-Count $b.limits[$metric]) -or $b.limits[$metric] -gt $script:Limits[$metric]) { throw "Bad baseline $Label`: invalid/relaxed limit $metric" }
    }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($e in $b.entities) {
        foreach ($key in @('project', 'kind', 'symbol', 'locations', 'ceilings', 'owner', 'issue')) {
            if ($e -isnot [System.Collections.IDictionary] -or -not $e.Contains($key)) { throw "Bad baseline $Label`: entity missing $key" }
        }
        foreach ($key in @('project', 'kind', 'symbol', 'owner', 'issue')) { if ($e[$key] -isnot [string] -or -not $e[$key].Trim()) { throw "Bad baseline $Label`: invalid entity $key" } }
        if ($e.kind -notin @('file', 'type', 'member') -or $e.locations -isnot [array] -or $e.locations.Count -eq 0 -or $e.ceilings -isnot [System.Collections.IDictionary] -or $e.ceilings.Count -eq 0) { throw "Bad baseline $Label`: invalid entity shape" }
        foreach ($path in $e.locations) { if ($path -isnot [string] -or -not $path.Trim()) { throw "Bad baseline $Label`: invalid location" } }
        if ($e.Contains('contentHash') -and ($e.contentHash -isnot [string] -or $e.contentHash -notmatch '^[0-9a-f]{64}$')) { throw "Bad baseline $Label`: invalid contentHash" }
        if (-not $seen.Add((Entity-Key $e) + '|' + ($e.locations -join '|'))) { throw "Bad baseline $Label`: duplicate entity" }
        foreach ($metric in $e.ceilings.Keys) {
            if (-not $script:Limits.ContainsKey($metric) -or -not (Test-Count $e.ceilings[$metric]) -or $e.ceilings[$metric] -le (Get-Limit $b $metric)) { throw "Bad baseline $Label`: ceiling $metric must describe excess above its limit" }
        }
    }
    $seen.Clear()
    foreach ($f in $b.findings) {
        foreach ($key in @('rule', 'project', 'path', 'member', 'symbol', 'syntaxHash', 'count', 'owner', 'removeBy')) {
            if ($f -isnot [System.Collections.IDictionary] -or -not $f.Contains($key)) { throw "Bad baseline $Label`: finding missing $key" }
            if ($key -ne 'count' -and ($f[$key] -isnot [string] -or -not $f[$key].Trim())) { throw "Bad baseline $Label`: invalid finding $key" }
        }
        if (-not (Test-Count $f.count) -or $f.count -eq 0 -or $f.syntaxHash -notmatch '^[0-9a-f]{64}$' -or $f.removeBy -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "Bad baseline $Label`: invalid finding count/hash/date" }
        if (-not $seen.Add((Finding-Key $f))) { throw "Bad baseline $Label`: duplicate finding fingerprint" }
        # Diagnostic fingerprints, including bannedApi/RS0030, use exactly this
        # reader/key. H03 must populate them before enabling complete Verify.
    }
    return $b
}
function Test-Count($Value) { return (($Value -is [int] -or $Value -is [long]) -and $Value -ge 0) }
function Get-Limit($Baseline, [string]$Metric) {
    if ($Baseline.limits.Contains($Metric)) { return $Baseline.limits[$Metric] }
    return $script:Limits[$Metric]
}
function Entity-Key($e) { return "$($e.project)|$($e.kind)|$($e.symbol)" }
function Finding-Key($f) { return "$($f.rule)|$($f.project)|$($f.member)|$($f.symbol)|$($f.syntaxHash)" }

function Match-Entities($Old, $New) {
    # Reserve strong matches before using unique symbols. A small partial file
    # must not consume the allowance belonging to a renamed oversized partial.
    $matches = @{}
    $used = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($pass in @('location', 'hash', 'symbol')) {
        for ($i = 0; $i -lt $New.Count; $i++) {
            if ($matches.ContainsKey($i)) { continue }
            $n = $New[$i]
            $candidates = @(for ($j = 0; $j -lt $Old.Count; $j++) {
                if ($used.Contains($j) -or $Old[$j].project -cne $n.project -or $Old[$j].kind -cne $n.kind) { continue }
                $sameSymbol = (Entity-Key $Old[$j]) -ceq (Entity-Key $n)
                if ($pass -eq 'location' -and $sameSymbol -and @($Old[$j].locations | Where-Object { $_ -cin $n.locations }).Count -gt 0) { $j }
                if ($pass -eq 'hash' -and $Old[$j].Contains('contentHash') -and $n.Contains('contentHash') -and $Old[$j].contentHash -ceq $n.contentHash) { $j }
                if ($pass -eq 'symbol' -and $sameSymbol) { $j }
            })
            if ($pass -eq 'symbol') {
                $remaining = @(for ($k=0; $k -lt $New.Count; $k++) { if (-not $matches.ContainsKey($k) -and (Entity-Key $New[$k]) -ceq (Entity-Key $n)) { $k } })
                if ($remaining.Count -ne 1) { continue }
            }
            if ($candidates.Count -eq 1) { $matches[$i] = $candidates[0]; [void]$used.Add($candidates[0]) }
        }
    }
    return $matches
}
function Write-Failure([string]$Code, $Entity, [string]$Metric, $Old, $New, [string]$Reason) {
    $path = if ($Entity.Contains('path')) { $Entity.path } else { $Entity.locations[0] }
    $line = if ($Entity.Contains('line')) { $Entity.line } else { 1 }
    $contributor = $path
    if ($Entity.Contains('locations')) {
        $changed = @($Entity.locations | Where-Object { $_ -cin $script:ChangedPaths })
        if ($changed.Count -gt 0) { $contributor = $changed[0] }
    }
    $contributingLine = $line
    if ($Entity.Contains('locationLines') -and $Entity.locationLines.Contains($contributor)) { $contributingLine = $Entity.locationLines[$contributor] }
    if ($Metric -eq 'suppressions' -and $Entity.Contains('member') -and $Entity.member -eq 'MSBuild') {
        $changedProps = @($script:ChangedPaths | Where-Object { $_.EndsWith('.props', [StringComparison]::OrdinalIgnoreCase) })
        if ($changedProps.Count) { $contributor = $changedProps[0]; $contributingLine = 1 }
    }
    Write-Output "$Code ${path}:${line} $Metric`: $Old -> $New; entity $($Entity.project):$($Entity.symbol); $Reason"
    Write-Output "Contributing change: ${contributor}:${contributingLine}."
    $script:Failures++
}
function Compare-Debt($Allowed, $Current, [string]$ValueField, [bool]$RequireLower) {
    foreach ($metric in $(if ($ValueField -eq 'ceilings') { $script:Limits.Keys } else { @() })) {
        # Compare effective limits: omitting an optional limit restores its default, which can raise it.
        $oldLimit = Get-Limit $Allowed $metric
        $newLimit = Get-Limit $Current $metric
        if ($newLimit -gt $oldLimit) {
            Write-Failure 'HC_STATE' @{ locations = @($BaselinePath); project = $Repo; symbol = 'limits' } $metric $oldLimit $newLimit 'Raised limit is forbidden.'
        }
    }
    $matches = Match-Entities $Allowed.entities $Current.entities
    for ($i = 0; $i -lt $Current.entities.Count; $i++) {
        $e = $Current.entities[$i]
        foreach ($metric in $e[$ValueField].Keys) {
            $limit = Get-Limit $Allowed $metric
            $old = $limit
            if ($matches.ContainsKey($i) -and $Allowed.entities[$matches[$i]].ceilings.Contains($metric)) { $old = $Allowed.entities[$matches[$i]].ceilings[$metric] }
            $value = $e[$ValueField][$metric]
            # Compare excess, so compliant new entities do not consume debt.
            if ([Math]::Max(0, $value - $limit) -gt [Math]::Max(0, $old - $limit)) {
                Write-Failure 'HC_STATE' $e $metric $old $value 'New/growing excess or raised ceiling is forbidden.'
            }
        }
    }
    $currentFindings = @{}
    foreach ($f in $Current.findings) { $currentFindings[(Finding-Key $f)] = $f }
    $oldFindings = @{}
    foreach ($f in $Allowed.findings) { $oldFindings[(Finding-Key $f)] = $f }
    foreach ($f in $Current.findings) {
        $key = Finding-Key $f
        $old = if ($oldFindings.ContainsKey($key)) { $oldFindings[$key].count } else { 0 }
        if ($f.count -gt $old) { Write-Failure 'HC_DIAGNOSTIC' $f $f.rule $old $f.count 'New fingerprint; deleting another finding cannot offset it.' }
    }
    if ($RequireLower) {
        foreach ($old in $Allowed.entities) {
            $idx = @($matches.Keys | Where-Object { $Allowed.entities[$matches[$_]] -eq $old })
            foreach ($metric in $old.ceilings.Keys) {
                $value = if ($idx.Count -eq 1 -and $Current.entities[$idx[0]][$ValueField].Contains($metric)) { $Current.entities[$idx[0]][$ValueField][$metric] } else { 0 }
                if ($value -lt $old.ceilings[$metric]) { Write-Failure 'HC_STATE' $old $metric $old.ceilings[$metric] $value 'Fixes must lower/delete baseline debt in this change; run LowerBaseline.' }
            }
        }
        foreach ($old in $Allowed.findings) {
            $key = Finding-Key $old
            $value = if ($currentFindings.ContainsKey($key)) { $currentFindings[$key].count } else { 0 }
            if ($value -lt $old.count) { Write-Failure 'HC_DIAGNOSTIC' $old $old.rule $old.count $value 'Fixes must lower/delete baseline debt in this change; run LowerBaseline.' }
        }
    }
}

$hostSource = @'
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public static class HealthSyntaxHost
{
    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    static string Normal(SyntaxNode n) => string.Join(" ", n.DescendantTokens().Select(t => t.Text));
    static string Name(SimpleNameSyntax n) => n.Identifier.ValueText;
    static string Call(InvocationExpressionSyntax n) => n.Expression is MemberAccessExpressionSyntax m ? Name(m.Name) : n.Expression is SimpleNameSyntax s ? Name(s) : n.Expression is MemberBindingExpressionSyntax b ? Name(b.Name) : "";
    static bool Modifier(SyntaxTokenList ts, SyntaxKind k) => ts.Any(t => t.IsKind(k));
    static string Qualified(SyntaxNode n)
    {
        var parts = new List<string>();
        foreach (var a in n.AncestorsAndSelf().Reverse())
        {
            if (a is BaseNamespaceDeclarationSyntax ns) parts.Add(ns.Name.ToString().Replace("global::", ""));
            if (a is BaseTypeDeclarationSyntax t)
            {
                int arity = t is TypeDeclarationSyntax td ? td.TypeParameterList?.Parameters.Count ?? 0 : 0;
                parts.Add(t.Identifier.ValueText + (arity == 0 ? "" : "`" + arity));
            }
        }
        return string.Join(".", parts);
    }
    static bool Callable(SyntaxNode n) => n is BaseMethodDeclarationSyntax || n is AccessorDeclarationSyntax || n is LocalFunctionStatementSyntax;
    static string Member(SyntaxNode n)
    {
        var owner = n.Ancestors().FirstOrDefault(Callable);
        string prefix = owner == null ? Qualified(n) : Member(owner);
        string args(ParameterListSyntax p) => "(" + string.Join(",", p.Parameters.Select(x => (x.Modifiers.ToString() + " " + x.Type?.ToString()).Trim())) + ")";
        string id = n switch {
            MethodDeclarationSyntax m => (m.ExplicitInterfaceSpecifier?.ToString() ?? "") + m.Identifier.ValueText + (m.TypeParameterList == null ? "" : "`" + m.TypeParameterList.Parameters.Count) + args(m.ParameterList),
            ConstructorDeclarationSyntax c => (Modifier(c.Modifiers, SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor") + args(c.ParameterList),
            DestructorDeclarationSyntax d => "Finalize()",
            OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text + args(o.ParameterList),
            ConversionOperatorDeclarationSyntax c => c.ImplicitOrExplicitKeyword.Text + " operator " + c.Type + args(c.ParameterList),
            LocalFunctionStatementSyntax l => "local:" + l.Identifier.ValueText + (l.TypeParameterList == null ? "" : "`" + l.TypeParameterList.Parameters.Count) + args(l.ParameterList),
            AccessorDeclarationSyntax a => (a.Parent?.Parent is PropertyDeclarationSyntax p ? p.Identifier.ValueText : a.Parent?.Parent is IndexerDeclarationSyntax i ? "this[" + string.Join(",", i.ParameterList.Parameters.Select(p => p.Type?.ToString())) + "]" : a.Parent?.Parent is EventDeclarationSyntax e ? e.Identifier.ValueText : "accessor") + "." + a.Keyword.Text,
            _ => "<global>"
        };
        return prefix + "." + id;
    }
    static int Line(SyntaxTree t, SyntaxNode n) => t.GetLineSpan(n.Span).StartLinePosition.Line + 1;
    static int SpanLines(SyntaxTree t, SyntaxNode n) => t.GetLineSpan(n.Span).EndLinePosition.Line - t.GetLineSpan(n.Span).StartLinePosition.Line + 1;
    static int Physical(string text) => text.Length == 0 ? 0 : Microsoft.CodeAnalysis.Text.SourceText.From(text).Lines.Count - ((text.EndsWith("\n") || text.EndsWith("\r")) ? 1 : 0);
    sealed class Unit { public string Path, Project; public SyntaxTree Tree; public SemanticModel Model; }
    sealed class Entity
    {
        public string project { get; set; }
        public string kind { get; set; }
        public string symbol { get; set; }
        public List<string> locations { get; set; } = new();
        public int line { get; set; }
        public string contentHash { get; set; }
        public Dictionary<string,int> locationLines { get; set; } = new();
        public Dictionary<string,int> values { get; set; } = new();
    }
    sealed class Finding
    {
        public string rule { get; set; }
        public string project { get; set; }
        public string path { get; set; }
        public int line { get; set; }
        public string member { get; set; }
        public string symbol { get; set; }
        public string syntaxHash { get; set; }
        public int count { get; set; } = 1;
        public bool candidate { get; set; }
        public string note { get; set; }
    }
    static string AttributeName(AttributeSyntax a, Unit u)
    {
        var resolved = (u.Model.GetSymbolInfo(a).Symbol as IMethodSymbol)?.ContainingType.Name;
        return (resolved ?? a.Name.ToString().Split('.').Last()).Replace("Attribute", "");
    }
    static string Constant(ExpressionSyntax e, Unit u)
    {
        var v = u.Model.GetConstantValue(e);
        if (!v.HasValue || !(v.Value is string)) throw new Exception(u.Path + ":" + Line(u.Tree,e) + " unresolved attribute constant");
        return (string)v.Value;
    }
    public static string Measure(string root, string[] paths, string[] projects, string[] referencePaths)
    {
        var units = new List<Unit>();
        var entities = new List<Entity>();
        var findings = new List<Finding>();
        var types = new Dictionary<string,Entity>(StringComparer.Ordinal);
        var state = new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
        var typeText = new Dictionary<string,List<string>>(StringComparer.Ordinal);
        var imports = new Dictionary<string,List<Finding>>(StringComparer.Ordinal);
        var discovery = new List<object>();
        for (int i=0; i<paths.Length; i++)
        {
            if (!paths[i].EndsWith(".cs",StringComparison.OrdinalIgnoreCase)) continue;
            string text = File.ReadAllText(System.IO.Path.Combine(root,paths[i]));
            var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse), paths[i]);
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new Exception(string.Join("\n", errors.Select(d => paths[i] + ":" + (d.Location.GetLineSpan().StartLinePosition.Line+1) + " parse error " + d.Id + ": " + d.GetMessage())));
            units.Add(new Unit { Path=paths[i], Project=projects[i], Tree=tree });
        }
        var references = referencePaths.Select(p => MetadataReference.CreateFromFile(p)).ToArray();
        foreach (var group in units.GroupBy(u => u.Project))
        {
            var compilation = CSharpCompilation.Create("Health_" + group.Key, group.Select(u => u.Tree), references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe:true));
            foreach (var u in group) u.Model = compilation.GetSemanticModel(u.Tree, ignoreAccessibility:true);
        }
        foreach (var u in units)
        {
            var syntax = u.Tree.GetRoot();
            var nodes = syntax.DescendantNodes().ToArray();
            var declared = nodes.OfType<BaseTypeDeclarationSyntax>().Select(Qualified).Concat(nodes.OfType<DelegateDeclarationSyntax>().Select(d => Qualified(d) + "." + d.Identifier.ValueText + (d.TypeParameterList == null ? "" : "`" + d.TypeParameterList.Parameters.Count))).Distinct().OrderBy(x => x,StringComparer.Ordinal);
            int physical = Physical(u.Tree.GetText().ToString());
            string fileSymbol = string.Join(";",declared);
            var file = new Entity { project=u.Project, kind="file", symbol="file:" + (fileSymbol.Length==0 ? u.Path : fileSymbol), locations=new(){u.Path}, line=1, contentHash=Hash(u.Tree.GetText().ToString()), values=new(){{"fileLines",physical}} };
            if (u.Path.EndsWith(".axaml.cs",StringComparison.OrdinalIgnoreCase)) file.values["axamlCodeBehindLines"] = physical;
            entities.Add(file);
            void Find(string rule, SyntaxNode node, string symbol, string fingerprint=null, bool candidate=false, string note="")
            {
                var context = node is PragmaWarningDirectiveTriviaSyntax ? syntax.FindToken(node.Span.End).Parent : node;
                var owner = context.AncestorsAndSelf().FirstOrDefault(Callable);
                string responsible = owner == null ? Qualified(context) : Member(owner);
                if (responsible.Length==0) responsible = file.symbol;
                if (owner == null && context.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault() is PropertyDeclarationSyntax property) responsible += "." + property.Identifier.ValueText;
                if (owner == null && context.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault() is FieldDeclarationSyntax field) responsible += ".fields:" + string.Join(",",field.Declaration.Variables.Select(v => v.Identifier.ValueText));
                findings.Add(new Finding { rule=rule, project=u.Project, path=u.Path, line=Line(u.Tree,node), member=responsible, symbol=symbol, syntaxHash=Hash(fingerprint ?? Normal(node)), candidate=candidate, note=note });
            }
            foreach (var t in nodes.OfType<BaseTypeDeclarationSyntax>())
            {
                string symbol = Qualified(t), key = u.Project + "|" + symbol;
                if (!types.TryGetValue(key,out var e))
                {
                    e = new Entity { project=u.Project, kind="type", symbol=symbol, line=Line(u.Tree,t), values=new(){{"stateMembers",0},{"partialFiles",0}} };
                    types[key] = e; state[key] = new(StringComparer.Ordinal); typeText[key] = new(); entities.Add(e);
                }
                if (!e.locations.Contains(u.Path)) { e.locations.Add(u.Path); e.locationLines[u.Path] = Line(u.Tree,t); e.values["partialFiles"]++; }
                typeText[key].Add(Normal(t));
                // Sum declaration spans, not whole files or nested sibling types.
                e.values["viewTypeLines"] = e.values.GetValueOrDefault("viewTypeLines") + SpanLines(u.Tree,t);
                foreach (var f in t.ChildNodes().OfType<FieldDeclarationSyntax>())
                {
                    if (Modifier(f.Modifiers,SyntaxKind.ReadOnlyKeyword) || Modifier(f.Modifiers,SyntaxKind.ConstKeyword)) continue;
                    foreach (var v in f.Declaration.Variables)
                    {
                        state[key].Add("field:" + v.Identifier.ValueText);
                        string type = f.Declaration.Type.ToString().Replace("global::", "").Replace("System.", "");
                        string name = v.Identifier.ValueText;
                        if (new[]{"int","long","uint","ulong","Int32","Int64"}.Contains(type) && new[]{"Generation","Revision","RequestId"}.Any(s => name.IndexOf(s,StringComparison.OrdinalIgnoreCase)>=0))
                            Find("generationFields",v,symbol + "." + name,type + " " + name,false,"Increment/adoption semantics require review.");
                    }
                }
                foreach (var p in t.ChildNodes().OfType<PropertyDeclarationSyntax>())
                {
                    if (p.AccessorList != null && p.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)) && (Modifier(p.Modifiers,SyntaxKind.PartialKeyword) || p.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null)))
                        state[key].Add("property:" + p.ExplicitInterfaceSpecifier?.ToString() + p.Identifier.ValueText);
                }
                if (t is RecordDeclarationSyntax r && r.ParameterList != null && !(r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) && Modifier(r.Modifiers,SyntaxKind.ReadOnlyKeyword)))
                    foreach (var p in r.ParameterList.Parameters) state[key].Add("property:" + p.Identifier.ValueText);
                string nameT = t.Identifier.ValueText;
                bool provider = t is TypeDeclarationSyntax td && td.BaseList != null && td.BaseList.Types.Any(b => b.Type.ToString().Split('.').Last() == "TimeProvider" || (u.Model.GetTypeInfo(b.Type).Type as INamedTypeSymbol)?.ToDisplayString() == "System.TimeProvider");
                for (var ancestor = (u.Model.GetDeclaredSymbol(t) as INamedTypeSymbol)?.BaseType; ancestor != null; ancestor = ancestor.BaseType)
                    if (ancestor.ToDisplayString()=="System.TimeProvider") provider = true;
                bool knownClock = nameT == "ClockState" || new[]{"Fake","Manual","Test"}.Any(p => nameT.StartsWith(p,StringComparison.Ordinal) && (nameT.EndsWith("Clock",StringComparison.Ordinal) || nameT.EndsWith("TimeProvider",StringComparison.Ordinal)));
                if (provider || knownClock) Find("fakeClockDuplicates",t,symbol,Normal(t),true,"Candidate outside reviewed owner; ClockState is not necessarily a TimeProvider.");
                var ownedCalls = t.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => c.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault() == t).ToArray();
                bool directoryCall(string type, string method) => ownedCalls.Any(c => Call(c)==method && c.Expression is MemberAccessExpressionSyntax m && m.Expression.ToString().Split('.').Last()==type);
                bool lifecycle = directoryCall("Path","GetTempPath") && directoryCall("Directory","CreateDirectory") && directoryCall("Directory","Delete");
                if (nameT == "TestWorkspace" || lifecycle) Find("workspaceDuplicates",t,symbol,Normal(t),true,nameT == "TestWorkspace" ? "TestWorkspace implementation." : "Temp-directory lifecycle clone candidate.");
            }
            foreach (var n in nodes.Where(Callable))
            {
                string member = Member(n);
                entities.Add(new Entity { project=u.Project,kind="member",symbol=member,locations=new(){u.Path},line=Line(u.Tree,n),contentHash=Hash(Normal(n)),values=new(){{"methodLines",SpanLines(u.Tree,n)}} });
                SyntaxTokenList mods = n is MethodDeclarationSyntax m ? m.Modifiers : n is LocalFunctionStatementSyntax l ? l.Modifiers : default;
                TypeSyntax ret = n is MethodDeclarationSyntax mm ? mm.ReturnType : n is LocalFunctionStatementSyntax ll ? ll.ReturnType : null;
                if (Modifier(mods,SyntaxKind.AsyncKeyword) && ret is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.VoidKeyword))
                {
                    string signature = n is MethodDeclarationSyntax x ? Normal(x.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)) : Normal(((LocalFunctionStatementSyntax)n).WithBody(null).WithExpressionBody(null).WithSemicolonToken(default));
                    Find("asyncVoid",n,member,signature);
                }
            }
            foreach (var lambda in nodes.OfType<AnonymousFunctionExpressionSyntax>().Where(l => l.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)))
            {
                var converted = u.Model.GetTypeInfo(lambda).ConvertedType as INamedTypeSymbol;
                if (converted?.DelegateInvokeMethod?.ReturnsVoid == true || converted?.DelegateInvokeMethod == null)
                    Find("asyncVoid",lambda,"async-lambda:" + (converted?.ToDisplayString() ?? "unresolved-void-delegate"),null,converted?.DelegateInvokeMethod == null,"Unresolved conversions are retained as candidates; H03 resolves project references.");
            }
            foreach (var access in nodes.OfType<MemberAccessExpressionSyntax>().Where(m => Name(m.Name)=="Result")) Find("blockingWait",access,"Result",null,true,"Syntax candidate; domain Result access may need classification.");
            foreach (var access in nodes.OfType<MemberBindingExpressionSyntax>().Where(m => Name(m.Name)=="Result")) Find("blockingWait",access,"Result",null,true,"Conditional Result syntax candidate.");
            var calls = nodes.OfType<InvocationExpressionSyntax>().ToArray();
            foreach (var c in calls)
            {
                string name = Call(c);
                if (new[]{"Wait","WaitAll","WaitAny"}.Contains(name) || (name=="GetResult" && c.Expression is MemberAccessExpressionSyntax m && m.Expression is InvocationExpressionSyntax inner && Call(inner)=="GetAwaiter"))
                    Find("blockingWait",c,name,null,true,"Syntax candidate; Task/ValueTask and dedicated-thread approval are H03/review classifications.");
            }
            foreach (var pragma in syntax.DescendantTrivia(descendIntoTrivia:true).Select(t => t.GetStructure()).OfType<PragmaWarningDirectiveTriviaSyntax>().Where(p => p.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword)))
            {
                string[] ids = pragma.ErrorCodes.Select(c => c.ToString()).ToArray();
                Find("suppressions",pragma,"pragma:directive", "disable:" + string.Join(",",ids));
                foreach (string id in ids.Length==0 ? new[]{"ALL"} : ids) Find("suppressions",pragma,"pragma:" + id,"disable:" + id);
            }
            foreach (var a in nodes.OfType<AttributeSyntax>())
            {
                string name = AttributeName(a,u);
                if (name=="SuppressMessage" || name=="UnconditionalSuppressMessage") {
                    string id = a.ArgumentList?.Arguments.Count >= 2 ? Constant(a.ArgumentList.Arguments[1].Expression,u) : "ALL";
                    Find("suppressions",a,name + ":" + id);
                }
                if (name!="DllImport" && name!="LibraryImport") continue;
                if (a.ArgumentList == null || a.ArgumentList.Arguments.Count==0) throw new Exception(u.Path + ":" + Line(u.Tree,a) + " native import missing DLL");
                var method = a.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (method == null) throw new Exception(u.Path + ":" + Line(u.Tree,a) + " native import missing method");
                string dll = Constant(a.ArgumentList.Arguments[0].Expression,u);
                var entry = a.ArgumentList.Arguments.FirstOrDefault(x => x.NameEquals?.Name.Identifier.ValueText == "EntryPoint");
                string ep = entry == null ? method.Identifier.ValueText : Constant(entry.Expression,u);
                string key = dll + "|" + ep;
                if (!imports.ContainsKey(key)) imports[key] = new();
                imports[key].Add(new Finding { rule="nativeImportDuplicates",project=u.Project,path=u.Path,line=Line(u.Tree,a),member=Member(method),symbol=key,syntaxHash=Hash(key),note="Duplicate DLL + effective EntryPoint participant; group metric counts excess declarations." });
            }
            int reads = calls.Count(c => new[]{"ReadText","ReadAllText","ReadAllTextAsync","ReadAllLines","ReadAllLinesAsync","ReadAllBytes","ReadAllBytesAsync","OpenText"}.Contains(Call(c)));
            int assertions = reads==0 ? 0 : calls.Count(c => new[]{"Contains","DoesNotContain","Matches","DoesNotMatch"}.Contains(Call(c)));
            bool filtered = u.Project.Contains("Architecture.Tests",StringComparison.OrdinalIgnoreCase) || (u.Project.Contains("Tests",StringComparison.OrdinalIgnoreCase) && new[]{"Architecture","Boundary","Layout","Snapshot"}.Any(s => u.Path.Contains(s,StringComparison.OrdinalIgnoreCase)));
            if (reads>0) discovery.Add(new { project=u.Project,path=u.Path,readCalls=reads,assertions=assertions,planFilter=filtered });
        }
        // Associate by AXAML x:Class, with adjacent .axaml.cs as a fallback.
        var views = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in units.Where(u => u.Path.EndsWith(".axaml.cs",StringComparison.OrdinalIgnoreCase)))
            foreach (var t in u.Tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => t.Identifier.ValueText == Path.GetFileName(u.Path)[..^9] && !t.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any())) views.Add(u.Project + "|" + Qualified(t));
        for (int i=0; i<paths.Length; i++)
        {
            if (!paths[i].EndsWith(".axaml",StringComparison.OrdinalIgnoreCase)) continue;
            var xml = System.Xml.Linq.XDocument.Load(Path.Combine(root,paths[i]));
            string className = xml.Root?.Attributes().FirstOrDefault(a => a.Name.LocalName=="Class" && a.Name.NamespaceName=="http://schemas.microsoft.com/winfx/2006/xaml")?.Value;
            if (className != null) foreach (var e in types.Values.Where(e => e.symbol==className && e.project==projects[i])) views.Add(e.project + "|" + e.symbol);
        }
        foreach (var pair in types)
        {
            var e = pair.Value; e.values["stateMembers"] = state[pair.Key].Count;
            e.contentHash = Hash(string.Join("\n",typeText[pair.Key].OrderBy(s => s,StringComparer.Ordinal)));
            if (!views.Contains(pair.Key)) e.values.Remove("viewTypeLines");
        }
        int nativeExcess = 0, nativeGroups = 0;
        foreach (var group in imports.Values.Where(g => g.Count>1)) { nativeGroups++; nativeExcess += group.Count-1; findings.AddRange(group); }
        // Count repeated fingerprints, never collapse unrelated members into totals.
        findings = findings.GroupBy(f => f.rule+"|"+f.project+"|"+f.member+"|"+f.symbol+"|"+f.syntaxHash).Select(g => { var f=g.First(); f.count=g.Count(); return f; }).ToList();
        return JsonSerializer.Serialize(new { entities, findings, sourceTextAssertions=new { candidates=discovery }, nativeImportDuplicates=new { groups=nativeGroups,excess=nativeExcess } });
    }
}
'@

function Measure-Syntax {
    $sdkVersion = Invoke-Checked 'dotnet' @('--version')
    if ($sdkVersion -notmatch '^\d+\.\d+\.\d+[^\r\n]*$') { throw 'Parser cannot be loaded: invalid SDK selection' }
    $sdkLines = (Invoke-Checked 'dotnet' @('--list-sdks')) -split '\r?\n'
    $sdkLine = @($sdkLines | Where-Object { $_.StartsWith("$sdkVersion [", [StringComparison]::Ordinal) })
    if ($sdkLine.Count -ne 1) { throw "Parser cannot be loaded: selected SDK $sdkVersion is not installed" }
    $sdkHome = Join-Path ($sdkLine[0].Substring($sdkVersion.Length + 2).TrimEnd(']')) $sdkVersion
    $parser = Join-Path $sdkHome 'Roslyn/bincore'
    if ($ParserDirectory) {
        if (-not (Test-Path -LiteralPath $ParserDirectory -PathType Container)) { throw "Parser cannot be loaded: missing $ParserDirectory" }
        if ([IO.Path]::GetFullPath($ParserDirectory) -ne [IO.Path]::GetFullPath($parser)) { throw 'Parser cannot be loaded: only the selected SDK parser is permitted' }
    }
    foreach ($assembly in @('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll', 'csc.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $parser $assembly) -PathType Leaf)) { throw "Parser cannot be loaded: missing $parser/$assembly" }
    }
    $major = $sdkVersion.Split('.')[0]
    $packs = Join-Path (Split-Path (Split-Path $sdkHome -Parent) -Parent) 'packs/Microsoft.NETCore.App.Ref'
    $pack = @(Get-ChildItem -LiteralPath $packs -Directory | Where-Object { $_.Name -match "^$major\." } | Sort-Object { [version]$_.Name } -Descending)[0]
    $refs = @(Get-ChildItem -LiteralPath (Join-Path $pack.FullName "ref/net$major.0") -Filter '*.dll' | ForEach-Object { $_.FullName })
    if ($refs.Count -eq 0) { throw 'Parser cannot be loaded: missing SDK reference pack' }
    $csFiles = @($script:Inventory | Where-Object { $_.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase) })
    if ($csFiles.Count -eq 0) { throw 'No tracked C# input in the measurement root (untracked fixture files must be staged in a Git tree)' }
    $inputFiles = @($csFiles) + @($script:Inventory | Where-Object { $_.EndsWith('.axaml', [StringComparison]::OrdinalIgnoreCase) })
    $projects = @($script:Inventory | Where-Object { $_.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase) })
    $projectNames = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $inputFiles) {
        $directory = Split-Path (Join-Path $Root $file) -Parent
        $name = '<root>'
        while ($directory.Length -ge $Root.Length) {
            $near = @($projects | Where-Object { (Split-Path (Join-Path $Root $_) -Parent) -eq $directory })
            if ($near.Count -gt 1) { throw "Ambiguous project ownership for $file; H03 project coverage is required" }
            if ($near.Count -eq 1) { $name = [IO.Path]::GetFileNameWithoutExtension($near[0]); break }
            if ($directory -eq $Root) { break }
            $directory = Split-Path $directory -Parent
        }
        $projectNames.Add($name)
    }
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $temporary = Join-Path $temporaryRoot ('repo-health-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($temporary)
    $context = $null
    try {
        $source = Join-Path $temporary 'Host.cs'
        $dll = Join-Path $temporary 'Host.dll'
        [IO.File]::WriteAllText($source, $hostSource)
        $response = @('-nologo', '-target:library', '-langversion:latest', ('-out:"{0}"' -f $dll))
        $response += @($refs | ForEach-Object { '-r:"{0}"' -f $_ })
        $response += @((('-r:"{0}"' -f (Join-Path $parser 'Microsoft.CodeAnalysis.dll'))), (('-r:"{0}"' -f (Join-Path $parser 'Microsoft.CodeAnalysis.CSharp.dll'))), ('"{0}"' -f $source))
        $rsp = Join-Path $temporary 'host.rsp'
        [IO.File]::WriteAllLines($rsp, [string[]]$response)
        [void](Invoke-Checked 'dotnet' @('exec', (Join-Path $parser 'csc.dll'), "@$rsp"))
        $context = [Runtime.Loader.AssemblyLoadContext]::new('RepoHealth-' + [guid]::NewGuid(), $true)
        [void]$context.LoadFromAssemblyPath((Join-Path $parser 'Microsoft.CodeAnalysis.dll'))
        [void]$context.LoadFromAssemblyPath((Join-Path $parser 'Microsoft.CodeAnalysis.CSharp.dll'))
        $stream = [IO.MemoryStream]::new([IO.File]::ReadAllBytes($dll))
        try { $hostAssembly = $context.LoadFromStream($stream) } finally { $stream.Dispose() }
        $json = $hostAssembly.GetType('HealthSyntaxHost').GetMethod('Measure').Invoke($null, [object[]]@($Root, [string[]]$inputFiles, $projectNames.ToArray(), [string[]]$refs))
        $m = ConvertFrom-Json -InputObject $json -AsHashtable -Depth 100
    }
    finally {
        if ($null -ne $context) { $context.Unload() }
        # Only the fresh GUID directory directly under the checked temp root.
        if ([IO.Path]::GetFullPath((Split-Path $temporary -Parent)).TrimEnd([IO.Path]::DirectorySeparatorChar) -eq $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)) { [IO.Directory]::Delete($temporary, $true) }
    }
    # Evaluation only, never a build/restore. Imports and conditions are MSBuild's.
    # H03 adds all configuration/TFM combinations and attribution from its map.
    $props = @($script:Inventory | Where-Object { $_.EndsWith('.props', [StringComparison]::OrdinalIgnoreCase) })
    # Always evaluate tracked projects: a NoWarn that lives only in a .csproj must be seen too.
    if ($projects.Count -or $props.Count) {
        $evaluate = if ($projects.Count) { $projects } else { $props }
        # The SDK adds its own default NoWarn IDs. Evaluate a pristine project once and ignore those IDs.
        $pristineDirectory = Join-Path ([IO.Path]::GetTempPath()) ('repo-health-sdk-' + [guid]::NewGuid().ToString('N'))
        $sdkDefaults = @{ NoWarn = @(); WarningsNotAsErrors = @() }
        try {
            [void][IO.Directory]::CreateDirectory($pristineDirectory)
            $pristineProject = Join-Path $pristineDirectory 'Pristine.csproj'
            [IO.File]::WriteAllText($pristineProject, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
            Copy-Item -LiteralPath (Join-Path $global 'global.json') -Destination (Join-Path $pristineDirectory 'global.json')
            $pristine = ConvertFrom-Json -AsHashtable -InputObject (Invoke-Checked 'dotnet' @('msbuild', $pristineProject, '-nologo', '-getProperty:NoWarn,WarningsNotAsErrors'))
            foreach ($property in $sdkDefaults.Keys.Clone()) { $sdkDefaults[$property] = @($pristine.Properties[$property] -split '[;,\s]+' | Where-Object { $_ }) }
        }
        finally {
            if ([IO.Path]::GetFullPath((Split-Path $pristineDirectory -Parent)).TrimEnd([IO.Path]::DirectorySeparatorChar) -eq [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) -and (Test-Path -LiteralPath $pristineDirectory)) { [IO.Directory]::Delete($pristineDirectory, $true) }
        }
        foreach ($project in $evaluate) {
            $values = ConvertFrom-Json -AsHashtable -InputObject (Invoke-Checked 'dotnet' @('msbuild', (Join-Path $Root $project), '-nologo', '-getProperty:NoWarn,WarningsNotAsErrors'))
            foreach ($property in @('NoWarn', 'WarningsNotAsErrors')) {
                foreach ($id in @($values.Properties[$property] -split '[;,\s]+' | Where-Object { $_ -and $_ -notin $sdkDefaults[$property] } | Sort-Object -Unique)) {
                    if ($id -match '\$\(') { throw "Unresolved evaluated $property in $project" }
                    $m.findings += @{ rule = 'suppressions'; project = [IO.Path]::GetFileNameWithoutExtension($project); path = $project; line = 1; member = 'MSBuild'; symbol = "$property`:$id"; syntaxHash = Get-Hash "$property`:$id"; count = 1; candidate = $false; note = 'Evaluated warning exemption from project/props imports.' }
                }
            }
        }
    }
    $summary = @{}
    foreach ($metric in $script:Limits.Keys) {
        $values = @($m.entities | Where-Object { $_.values.Contains($metric) } | ForEach-Object { $_.values[$metric] })
        $summary[$metric] = @{ total = ($values | Measure-Object -Sum).Sum; max = ($values | Measure-Object -Maximum).Maximum; overLimit = @($values | Where-Object { $_ -gt $script:Limits[$metric] }).Count; excess = (@($values | ForEach-Object { [Math]::Max(0, $_ - $script:Limits[$metric]) }) | Measure-Object -Sum).Sum }
    }
    foreach ($rule in $script:SyntaxRules) { $summary[$rule] = (@($m.findings | Where-Object { $_.rule -eq $rule } | ForEach-Object { $_.count }) | Measure-Object -Sum).Sum }
    $summary.nativeImportDuplicates = $m.nativeImportDuplicates
    $summary.methodLines['splitPlanRequired'] = @($m.entities | Where-Object { $_.values.Contains('methodLines') -and $_.values.methodLines -gt 150 }).Count
    $m.sourceTextAssertions['readCalls'] = (@($m.sourceTextAssertions.candidates | ForEach-Object { $_.readCalls }) | Measure-Object -Sum).Sum
    $m.sourceTextAssertions['assertions'] = (@($m.sourceTextAssertions.candidates | ForEach-Object { $_.assertions }) | Measure-Object -Sum).Sum
    $m['summary'] = $summary
    $m['schemaVersion'] = 1
    $m['measurementVersion'] = $script:Version
    $m['snapshotCommit'] = $script:Snapshot
    $m['limits'] = $script:Limits.Clone()
    $m['repo'] = $Repo
    $m['extensions'] = @{ build = 'not implemented here (H03)'; sarif = 'not implemented here (H03)'; format = 'not implemented here (H03)'; bannedApi = $null; diagnosticFingerprints = $null; projectCoverage = 'nearest declaring project; H03 supplies evaluated coverage/configurations' }
    return $m
}
function Get-Hash([string]$Text) { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant() }

$exitCode = 2
try {
    if ($Mode -in @('Build', 'Sarif', 'Format')) { throw "HC_NOT_IMPLEMENTED: $Mode is not implemented here; H03 extension stage (exit 2)" }
    if ($Mode -notin @('Measure', 'Verify', 'LowerBaseline') -or $Repo -notin @('core', 'nfc', 'nfh', 'nfu')) { throw 'Invalid Mode or Repo' }
    $Root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Missing root $Root" }
    Push-Location -LiteralPath $Root
    try {
        $global = $Root
        while (-not (Test-Path -LiteralPath (Join-Path $global 'global.json') -PathType Leaf)) {
            $parent = Split-Path $global -Parent
            if (-not $parent -or $parent -eq $global) { throw 'Parser cannot be loaded: global.json is required' }
            $global = $parent
        }
        $script:GitRoot = ''
        $gitProbe = @(& git -C $Root rev-parse --show-toplevel 2>&1)
        if ($LASTEXITCODE -eq 0) { $script:GitRoot = [IO.Path]::GetFullPath(($gitProbe -join '').Trim()) }
        $script:Snapshot = if ($script:GitRoot) { Invoke-Checked 'git' @('-C', $Root, 'rev-parse', 'HEAD') } else { 'fixture' }
        if ($script:GitRoot) {
            $all = (Invoke-Checked 'git' @('-C', $script:GitRoot, 'ls-files', '-z', '--cached')) -split [char]0
            $script:Inventory = @($all | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $script:GitRoot $_)) } | Where-Object { $_.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { [IO.Path]::GetRelativePath($Root, $_).Replace('\', '/') })
        }
        else { $script:Inventory = @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | ForEach-Object { [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/') }) }
        $script:Inventory = @($script:Inventory | Where-Object { -not (@($_ -split '/' | Where-Object { $_ -in @('bin', 'obj', 'artifacts', '.git') }).Count) } | Sort-Object -Unique)
        foreach ($path in $script:Inventory) { if (-not (Test-Path -LiteralPath (Join-Path $Root $path) -PathType Leaf)) { throw "Missing tracked file $path" } }
        $script:ChangedPaths = @()
        $script:Failures = 0
        $baseline = $null
        if ($Mode -ne 'Measure') {
            if (-not $script:GitRoot) { throw 'Verify/LowerBaseline require a Git repository with a committed baseline' }
            $baselineFull = [IO.Path]::GetFullPath((Join-Path $Root $BaselinePath))
            if (-not $baselineFull.StartsWith($script:GitRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'BaselinePath must be within the Git repository' }
            $baselineGit = [IO.Path]::GetRelativePath($script:GitRoot, $baselineFull).Replace('\','/')
            $allowedCommit = if ($BaseRef.Trim()) { Invoke-Checked 'git' @('-C', $script:GitRoot, 'merge-base', 'HEAD', $BaseRef) } else { $script:Snapshot }
            $allowed = Read-Baseline (Invoke-Checked 'git' @('-C', $script:GitRoot, 'show', "${allowedCommit}:$baselineGit")) "${allowedCommit}:$baselineGit"
            $baseline = Read-Baseline ([IO.File]::ReadAllText($baselineFull)) $BaselinePath
            $script:ChangedPaths = @((Invoke-Checked 'git' @('-C', $Root, 'diff', '--name-only', '--relative', $allowedCommit, '--')) -split '\r?\n')
            Compare-Debt $allowed $baseline 'ceilings' $false
            if (@(@($allowed.findings) + @($baseline.findings) | Where-Object { $_.rule -notin $script:SyntaxRules }).Count -gt 0) { throw 'HC_NOT_IMPLEMENTED: diagnostic fingerprints/bannedApi require the H03 measurement provider; refusing to discard unmeasured findings' }
        }
        $measurement = Measure-Syntax
        if ($Mode -eq 'Measure') {
            $json = ConvertTo-Json -InputObject $measurement -Depth 100
            if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json + [Environment]::NewLine) } else { Write-Output $json }
            $exitCode = 0
        }
        else {
            Compare-Debt $baseline $measurement 'values' ($Mode -eq 'Verify')
            if ($script:Failures -gt 0) { $exitCode = 1 }
            elseif ($Mode -eq 'LowerBaseline') {
                $matches = Match-Entities $baseline.entities $measurement.entities
                $reduced = @()
                for ($i=0; $i -lt $baseline.entities.Count; $i++) {
                    $e = $baseline.entities[$i]
                    $idx = @($matches.Keys | Where-Object { $matches[$_] -eq $i })
                    foreach ($metric in @($e.ceilings.Keys)) {
                        $value = if ($idx.Count -eq 1 -and $measurement.entities[$idx[0]].values.Contains($metric)) { $measurement.entities[$idx[0]].values[$metric] } else { 0 }
                        if ($value -le (Get-Limit $baseline $metric)) { $e.ceilings.Remove($metric) } else { $e.ceilings[$metric] = $value }
                    }
                    if ($e.ceilings.Count) { $reduced += $e }
                }
                $baseline.entities = $reduced
                $remaining = @{}
                foreach ($f in $measurement.findings) { $remaining[(Finding-Key $f)] = $f.count }
                $baseline.findings = @($baseline.findings | Where-Object { $remaining.ContainsKey((Finding-Key $_)) } | ForEach-Object { $_.count = $remaining[(Finding-Key $_)]; $_ })
                [IO.File]::WriteAllText($baselineFull, (ConvertTo-Json -InputObject $baseline -Depth 100) + [Environment]::NewLine)
                Write-Output 'HC_STATE baseline:1 LowerBaseline passed; only existing debt was deleted or lowered.'
                $exitCode = 0
            }
            else { Write-Output 'HC_STATE syntax:1 Verify passed (H02 syntax scope; H03 build/SARIF/format/provenance stages are pending).'; $exitCode = 0 }
        }
    }
    finally { Pop-Location }
}
catch {
    Write-Output "HC_DIAGNOSTIC repo-health:1 old=n/a new=n/a entity=tool/input; $($_.Exception.GetBaseException().Message)"
    Write-Output 'Contributing change: checker input/toolchain. Tool or input error; exit 2.'
    $exitCode = 2
}
exit $exitCode
