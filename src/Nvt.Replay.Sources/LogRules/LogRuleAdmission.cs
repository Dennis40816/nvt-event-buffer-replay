// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Collections.Immutable;

namespace Nvt.Replay.Sources.LogRules;

/// <summary>
/// Copies a document into collections nobody else can change. A rule file is admitted by
/// <see cref="LogRuleFile"/> only after this copy, so the content that was validated and hashed is the
/// content the caller holds. A <c>null</c> list or entry stays <c>null</c> so the validator reports it.
/// </summary>
internal static class LogRuleAdmission
{
    /// <summary>Returns a copy of the document whose lists and maps are immutable, all the way down.</summary>
    public static LogRuleDocumentV1 Freeze(LogRuleDocumentV1 document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with
        {
            EventTypes = document.EventTypes is { } eventTypes ? CopyItems(eventTypes, null) : null!,
            Rules = document.Rules is { } rules ? CopyItems(rules, FreezeRule) : null!,
            Tests = document.Tests is { } tests ? CopyItems(tests, FreezeTest) : null!,
        };
    }

    private static LogRuleV1 FreezeRule(LogRuleV1 rule) =>
        rule with
        {
            Keywords = rule.Keywords is { } keywords ? CopyItems(keywords, null) : null,
            Captures = rule.Captures is { } captures ? CopyItems(captures, FreezeCapture) : null,
        };

    private static LogRuleCaptureV1 FreezeCapture(LogRuleCaptureV1 capture) =>
        capture with { Map = capture.Map is { } map ? CopyMap(map) : null };

    private static LogRuleTestV1 FreezeTest(LogRuleTestV1 test) =>
        test with { Expect = test.Expect is { } expect ? CopyItems(expect, FreezeExpectation) : null! };

    private static LogRuleExpectationV1 FreezeExpectation(LogRuleExpectationV1 expectation) =>
        expectation with { Fields = expectation.Fields is { } fields ? CopyMap(fields) : null };

    // A null entry stays null, and the validator rejects it before any code reads it.
    private static ImmutableArray<T> CopyItems<T>(IReadOnlyList<T> source, Func<T, T>? freeze)
        where T : class
    {
        var builder = ImmutableArray.CreateBuilder<T>(source.Count);
        foreach (var item in source)
            builder.Add(freeze is null || item is null ? item! : freeze(item));
        return builder.MoveToImmutable();
    }

    private static ImmutableDictionary<string, string> CopyMap(IReadOnlyDictionary<string, string> source) =>
        source.ToImmutableDictionary(StringComparer.Ordinal);
}
