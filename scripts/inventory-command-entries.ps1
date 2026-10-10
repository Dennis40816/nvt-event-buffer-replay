# Copyright (c) 2026 Dennis Liu. All rights reserved.
#Requires -Version 7.0
[CmdletBinding()]
param([string]$Root = '.', [string]$OutputPath = 'eng/architecture/command-entries.json', [string]$BindingOutputPath = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Sort-Ordinal([object[]]$Items, [scriptblock]$Key) {
    $i = 0
    $keys = [string[]]@($Items | ForEach-Object { (& $Key $_) + '|' + ($i++).ToString('D8') })
    $values = [object[]]@($Items)
    [Array]::Sort([Array]$keys, [Array]$values, [Collections.IComparer][StringComparer]::Ordinal)
    $values
}
$Root = [IO.Path]::GetFullPath($Root)
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE) { throw 'SDK selection failed.' }
$sdkLine = @(& dotnet --list-sdks | Where-Object { $_.StartsWith("$sdk [", [StringComparison]::Ordinal) })[0]
$parser = Join-Path ($sdkLine.Substring($sdk.Length + 2).TrimEnd(']')) "$sdk/Roslyn/bincore"
$context = [Runtime.Loader.AssemblyLoadContext]::new('CommandInventory', $true)
try {
    [void]$context.LoadFromAssemblyPath((Join-Path $parser 'Microsoft.CodeAnalysis.dll'))
    $assembly = $context.LoadFromAssemblyPath((Join-Path $parser 'Microsoft.CodeAnalysis.CSharp.dll'))
    $parse = @($assembly.GetType('Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree').GetMethods() |
        Where-Object { $_.Name -eq 'ParseText' -and $_.GetParameters().Count -eq 5 -and $_.GetParameters()[0].ParameterType -eq [string] })[0]
    $directory = Join-Path $Root 'src/Nvt.Replay.Avalonia'
    $methods = @{}
    $trees = @()
    foreach ($file in @(Sort-Ordinal @(Get-ChildItem $directory -Filter 'MainWindow*.cs' -File) { param($f) $f.Name })) {
        $tree = $parse.Invoke($null, @([IO.File]::ReadAllText($file.FullName), $null, $file.FullName, $null, [Threading.CancellationToken]::None))
        $nodes = @($tree.GetRoot().DescendantNodes())
        $path = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $trees += @{ tree = $tree; nodes = $nodes; path = $path }
        foreach ($node in @($nodes | Where-Object { $_.GetType().Name -eq 'MethodDeclarationSyntax' })) {
            $name = $node.Identifier.ValueText
            $span = $tree.GetLineSpan($node.Span)
            if (-not $methods.ContainsKey($name)) { $methods[$name] = @() }
            $methods[$name] += [ordered]@{ method = $name; file = $path; startLine = $span.StartLinePosition.Line + 1; endLine = $span.EndLinePosition.Line + 1; asyncVoid = ($node.ReturnType.ToString() -eq 'void' -and 'async' -in @($node.Modifiers | ForEach-Object Text)) }
        }
    }
    $entries = [Collections.Generic.List[object]]::new()
    function Add-Entry($kind, $eventName, $handler, $path, $start, $end, $element = '', $inline = $null) {
        if ($methods.ContainsKey($handler)) {
            if ($methods[$handler].Count -ne 1) { throw "Ambiguous handler: $handler" }
            $target = $methods[$handler][0]
        }
        elseif ($null -ne $inline) { $target = $inline }
        else { throw "Unresolved handler: $handler at ${path}:$start" }
        $entries.Add([ordered]@{ kind = $kind; handlerAttribute = $(if ($kind -eq 'axaml') { $eventName } else { $null }); event = $eventName; method = $target.method; file = $target.file; startLine = $target.startLine; endLine = $target.endLine; asyncVoid = $target.asyncVoid; registration = [ordered]@{ file = $path; startLine = $start; endLine = $end; element = $element } })
    }
    $axaml = [Xml.Linq.XDocument]::Load((Join-Path $directory 'MainWindow.axaml'), [Xml.Linq.LoadOptions]::SetLineInfo)
    foreach ($element in $axaml.Descendants()) {
        foreach ($attribute in $element.Attributes()) {
            if ($methods.ContainsKey($attribute.Value) -or $attribute.Value -match '_On\w+$') {
                $line = ([Xml.IXmlLineInfo]$attribute).LineNumber
                Add-Entry 'axaml' $attribute.Name.LocalName $attribute.Value 'src/Nvt.Replay.Avalonia/MainWindow.axaml' $line $line $element.Name.LocalName
            }
        }
    }
    foreach ($item in $trees) {
        foreach ($node in $item.nodes) {
            $type = $node.GetType().Name
            $span = $item.tree.GetLineSpan($node.Span)
            if ($type -eq 'AssignmentExpressionSyntax' -and $node.OperatorToken.Text -eq '+=') {
                $rightType = $node.Right.GetType().Name
                if ($rightType -eq 'IdentifierNameSyntax' -and $methods.ContainsKey($node.Right.ToString())) {
                    Add-Entry 'subscription' $node.Left.ToString() $node.Right.ToString() $item.path ($span.StartLinePosition.Line + 1) ($span.EndLinePosition.Line + 1)
                } elseif ($rightType -match 'LambdaExpressionSyntax$') {
                    $inline = @{ method = '<lambda>'; file = $item.path; startLine = $span.StartLinePosition.Line + 1; endLine = $span.EndLinePosition.Line + 1; asyncVoid = ('async' -in @($node.Right.Modifiers | ForEach-Object Text)) }
                    Add-Entry 'subscription' $node.Left.ToString() '<lambda>' $item.path $inline.startLine $inline.endLine '' $inline
                }
            }
            if ($type -eq 'InvocationExpressionSyntax' -and $node.Expression.ToString() -match '(^|\.)AddHandler$') {
                $args = $node.ArgumentList.Arguments
                Add-Entry 'routed-event' $args[0].Expression.ToString() $args[1].Expression.ToString() $item.path ($span.StartLinePosition.Line + 1) ($span.EndLinePosition.Line + 1)
            }
        }
    }
    foreach ($name in @('OnKeyDown', 'HandleWindowKeyDown')) {
        if ($methods.ContainsKey($name)) { $m = $methods[$name][0]; Add-Entry 'keyboard' 'KeyDown' $name $m.file $m.startLine $m.endLine }
    }
    foreach ($name in @(Sort-Ordinal @($methods.Keys) { param($k) $k })) {
        if ($name -match '_On\w+$' -and $name -notin @($entries | ForEach-Object { $_.method })) {
            $m = $methods[$name][0]
            Add-Entry 'unwired' 'Unregistered' $name $m.file $m.startLine $m.endLine
        }
    }
    $orderedEntries = @(Sort-Ordinal @($entries) { param($e) "$($e.registration.file)|$($e.registration.startLine.ToString('D8'))|$($e.event)|$($e.method)" })
    $result = [ordered]@{ schemaVersion = 1; scope = 'AXAML handlers, event subscriptions, routed events, keyboard override and dispatcher. Lifecycle events are retained for completeness.'; entries = $orderedEntries }
    $destination = [IO.Path]::GetFullPath($OutputPath, $Root)
    [void][IO.Directory]::CreateDirectory((Split-Path $destination -Parent))
    [IO.File]::WriteAllText($destination, ($result | ConvertTo-Json -Depth 20) + "`n")
    $entries | Group-Object { $_.kind } | Sort-Object Name -Culture ([Globalization.CultureInfo]::InvariantCulture) | ForEach-Object { Write-Output "$($_.Name): $($_.Count)" }
    if ($BindingOutputPath) {
        $bindings = @(Sort-Ordinal @(Get-ChildItem (Join-Path $Root 'src') -Recurse -Filter '*.axaml' -File) { param($f) $f.FullName.Replace('\', '/') } | ForEach-Object {
            $text = [IO.File]::ReadAllText($_.FullName)
            $xml = [Xml.Linq.XDocument]::Parse($text)
            $x = [Xml.Linq.XNamespace]::Get('http://schemas.microsoft.com/winfx/2006/xaml')
            [ordered]@{ file = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/'); bindingCount = [regex]::Matches($text, '\{Binding\b').Count; dataTypeCount = @($xml.Descendants().Attributes($x.GetName('DataType'))).Count; rootDataType = ($null -ne $xml.Root.Attribute($x.GetName('DataType'))); compiledBindingCount = [regex]::Matches($text, '\{CompiledBinding\b').Count; compileBindingsDeclarations = @($xml.Descendants().Attributes($x.GetName('CompileBindings')) | ForEach-Object Value) }
        })
        $destination = [IO.Path]::GetFullPath($BindingOutputPath, $Root)
        [void][IO.Directory]::CreateDirectory((Split-Path $destination -Parent))
        [IO.File]::WriteAllText($destination, ([ordered]@{ schemaVersion = 1; files = $bindings } | ConvertTo-Json -Depth 10) + "`n")
    }
} finally { $context.Unload() }
