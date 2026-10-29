// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Nvt.Replay.Core;
using Nvt.Replay.Sources.LogRules;

namespace Nvt.Replay.Tests.LogRules;

public sealed class LogRuleValidationTests
{
    public static TheoryData<string, Func<LogRuleDocumentV1, LogRuleDocumentV1>, string> InvalidDocuments() => new()
    {
        { "schema version", d => d with { SchemaVersion = 2 }, "schemaVersion" },
        { "file kind", d => d with { FileKind = "registerProfile" }, "fileKind" },
        { "profile id empty", d => d with { ProfileId = string.Empty }, "profileId" },
        { "profile id space", d => d with { ProfileId = "bad id" }, "profileId" },
        { "profile id trailing line break", d => d with { ProfileId = "acme\n" }, "profileId" },
        { "profile id non-ascii", d => d with { ProfileId = "acme-é" }, "profileId" },
        { "profile id too long", d => d with { ProfileId = new string('a', 129) }, "profileId" },
        { "display name padded", d => d with { DisplayName = " Acme" }, "displayName" },
        { "display name control", d => d with { DisplayName = "Ac\tme" }, "displayName" },
        { "line encoding", d => d with { LineEncoding = "utf-16" }, "lineEncoding" },
        { "scope null", d => d with { Scope = null! }, "scope is required" },
        { "scope too long", d => d with { Scope = d.Scope with { Note = new string('n', 1025) } }, "scope.note" },
        { "scope empty label", d => d with { Scope = d.Scope with { Project = string.Empty } }, "scope.project" },
        { "timestamp without ts group", d => d with { Timestamp = d.Timestamp! with { Pattern = "^(?<time>[0-9.]+)" } }, "group named 'ts'" },
        { "timestamp bad pattern", d => d with { Timestamp = d.Timestamp! with { Pattern = "^(?<ts>" } }, "timestamp.regex" },
        { "timestamp format", d => d with { Timestamp = d.Timestamp! with { Format = (LogTimestampFormat)99 } }, "timestamp.format" },
        { "event types null", d => d with { EventTypes = null! }, "eventTypes is required" },
        { "event type null entry", d => d with { EventTypes = [null!] }, "null entries" },
        { "event type duplicate", d => d with { EventTypes = [.. d.EventTypes, d.EventTypes[0]] }, "Duplicate event type" },
        { "event type id", d => d with { EventTypes = [d.EventTypes[0] with { Id = "-x" }, .. d.EventTypes.Skip(1)] }, "eventTypes[].id" },
        { "event type name", d => d with { EventTypes = [d.EventTypes[0] with { DisplayName = string.Empty }, .. d.EventTypes.Skip(1)] }, "eventTypes[].displayName" },
        { "event type category", d => d with { EventTypes = [d.EventTypes[0] with { Category = (LogEventCategory)99 }, .. d.EventTypes.Skip(1)] }, "category" },
        { "event type severity", d => d with { EventTypes = [d.EventTypes[0] with { Severity = (DiagnosticSeverity)99 }, .. d.EventTypes.Skip(1)] }, "severity" },
        { "rules null", d => d with { Rules = null! }, "rules is required" },
        { "rule null entry", d => d with { Rules = [null!] }, "null entries" },
        { "rule duplicate", d => d with { Rules = [.. d.Rules, d.Rules[0]] }, "Duplicate rule id" },
        { "rule id", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Id = "frame lost" }), "rules[].id" },
        { "rule event type unknown", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { EventType = "touch.none" }), "unknown event type" },
        { "rule severity", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Severity = (DiagnosticSeverity)99 }), "unknown severity" },
        { "rule without contains and regex", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Keywords = null, Pattern = null, Captures = null, Message = "Lost" }), "at least one of contains and regex" },
        { "rule empty keyword", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Keywords = [string.Empty] }), "cannot be empty" },
        { "rule duplicate keyword", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Keywords = ["a", "a"] }), "twice" },
        { "rule keyword line break", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Keywords = ["a\nb"] }), "line break" },
        { "rule keyword too long", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Keywords = [new string('k', 1025)] }), "longer than" },
        { "rule captures without regex", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Pattern = null, Message = "Lost" }), "captures but no regex" },
        { "rule regex lookahead", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Pattern = "(?<seq>a)(?=b)" }), "non-backtracking" },
        { "rule regex lookbehind", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Pattern = "(?<=a)(?<seq>b)" }), "non-backtracking" },
        { "rule regex backreference", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Pattern = "(?<seq>a)\\k<seq>" }), "non-backtracking" },
        { "rule regex atomic group", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Pattern = "(?>(?<seq>a))" }), "non-backtracking" },
        { "rule regex syntax", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Pattern = "(?<seq>a" }), "non-backtracking" },
        { "rule regex empty", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Pattern = string.Empty }), "cannot be empty" },
        { "rule capture null entry", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Captures = [null!] }), "null entries" },
        { "rule capture not a group", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Captures = [new("other", LogCaptureType.Integer)] }), "not a named group" },
        { "rule capture numbered group", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Captures = [new("1", LogCaptureType.Integer)], Message = "Lost" }), "capture name" },
        { "rule capture duplicate", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Captures = [new("seq", LogCaptureType.Integer), new("seq", LogCaptureType.String)] }), "twice" },
        { "rule capture type", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Captures = [new("seq", (LogCaptureType)99)] }), "unknown type" },
        { "rule capture map on integer", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Captures = [new("seq", LogCaptureType.Integer, new Dictionary<string, string> { ["1"] = "one" })] }), "only enum captures" },
        { "rule enum without map", d => AcmeRuleFile.WithRule(d, "reset-reason", r => r with { Captures = [new("code", LogCaptureType.Enum)] }), "non-empty map" },
        { "rule enum with empty map", d => AcmeRuleFile.WithRule(d, "reset-reason", r => r with { Captures = [new("code", LogCaptureType.Enum, new Dictionary<string, string>())] }), "non-empty map" },
        { "rule enum empty key", d => AcmeRuleFile.WithRule(d, "reset-reason", r => r with { Captures = [new("code", LogCaptureType.Enum, new Dictionary<string, string> { [string.Empty] = "x" })] }), "map key" },
        { "rule enum empty label", d => AcmeRuleFile.WithRule(d, "reset-reason", r => r with { Captures = [new("code", LogCaptureType.Enum, new Dictionary<string, string> { ["0x01"] = string.Empty })] }), "map label" },
        { "rule message empty", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Message = string.Empty }), "message" },
        { "rule message unknown placeholder", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Message = "Lost {other}" }), "placeholder" },
        { "rule message empty placeholder", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Message = "Lost {}" }), "placeholder" },
        { "rule message open brace", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Message = "Lost {seq" }), "closing" },
        { "rule message close brace", d => AcmeRuleFile.WithRule(d, "frame-lost", r => r with { Message = "Lost }" }), "opening" },
        { "tests null", d => d with { Tests = null! }, "tests is required" },
        { "test null entry", d => d with { Tests = [null!] }, "null entries" },
        { "test duplicate name", d => d with { Tests = [.. d.Tests, d.Tests[0]] }, "Duplicate test name" },
        { "test name", d => d with { Tests = [d.Tests[0] with { Name = "bad name" }] }, "tests[].name" },
        { "test line break", d => d with { Tests = [d.Tests[0] with { Line = "a\nb" }] }, "line break" },
        { "test line too long", d => d with { Tests = [d.Tests[0] with { Line = new string('x', 8193) }] }, "longer than 8192" },
        { "test expect null", d => d with { Tests = [d.Tests[0] with { Expect = null! }] }, "expect is required" },
        { "test expect null entry", d => d with { Tests = [d.Tests[0] with { Expect = [null!] }] }, "null entries" },
        { "test unknown rule", d => d with { Tests = [d.Tests[0] with { Expect = [new("missing")] }] }, "unknown rule" },
        { "test duplicate rule", d => d with { Tests = [d.Tests[0] with { Expect = [new("esd-detected"), new("esd-detected")] }] }, "twice" },
        { "test unknown field", d => d with { Tests = [d.Tests[0] with { Expect = [new("esd-detected", new Dictionary<string, string> { ["other"] = "1" })] }] }, "does not capture" },
        { "test field line break", d => d with { Tests = [d.Tests[0] with { Expect = [new("esd-detected", new Dictionary<string, string> { ["cnt"] = "1\n" })] }] }, "line break" },
    };

    public static TheoryData<string, string> AcceptedPatterns() => new()
    {
        { "counted quantifier at the limit", "a{1000}" },
        { "counted range at the limit", "a{1,1000}" },
        { "open range at the limit", "a{1000,}" },
        { "brace without a number", "a{,5000}" },
        { "brace with text", "a{x}" },
        { "large number inside a class", "[{5000}]" },
        { "escaped brace", @"a\{5000}" },
        { "class with leading bracket", "[]{5000}]" },
        { "negated class with leading bracket", "[^]{5000}]" },
        { "literal bracket in a class", "[[{5000}]" },
        { "class subtraction", "[a-z-[aeiou]]{5}" },
        { "parentheses in a class", "[((((((((((]" },
        { "escaped parentheses", @"\(\(\(\(\(\(\(\(\(\(\)" },
        { "group depth at the limit", "((((((((a))))))))" },
        { "option group is not a group", "(?i)((((((((a))))))))" },
        { "alternations in a class", "[" + new string('|', 200) + "]" },
        { "alternations at the limit", string.Join('|', Enumerable.Repeat("a", 65)) },
        { "capture groups at the limit", string.Concat(Enumerable.Repeat("(a)", 20)) },
    };

    public static TheoryData<string, string, string> RejectedPatterns() => new()
    {
        { "counted quantifier over the limit", "a{1001}", "repeats more than" },
        { "counted range over the limit", "a{1,1001}", "repeats more than" },
        { "open range over the limit", "a{1001,}", "repeats more than" },
        { "huge counted quantifier", "a{99999999999999999999}", "repeats more than" },
        { "group depth over the limit", "(((((((((a)))))))))", "nests groups" },
        { "alternations over the limit", string.Join('|', Enumerable.Repeat("a", 66)), "alternations" },
        { "capture groups over the limit", string.Concat(Enumerable.Repeat("(a)", 21)), "capture groups" },
        { "inline comment", "a(?#((((((((((((((((()b", "inline comments" },
        { "enabled x option", "(?x)a#((((((((((((", "x option" },
        { "enabled x option with another", "(?ix)a", "x option" },
        { "enabled x option in a group", "(?x:a)", "x option" },
        { "pattern too long", new string('a', 1025), "longer than 1024" },
        { "quantifier after a class with a literal bracket", "[[]{5000}", "repeats more than" },
        { "groups after a class with an escaped hyphen", @"[a\-[b]((((((((((a", "nests groups" },
        { "disabled x option", "(?-x)a", "x option" },
        { "uppercase x option", "(?X)a", "x option" },
        { "x option with a plus", "(?+x)a", "x option" },
        { "unterminated class", "[a((((((((((", "without a closing bracket" },
        { "quantifier after a class subtraction", "[a-z-[aeiou]]{5000}", "repeats more than" },
        { "quantifier after a class with a leading bracket", "[]a]{5000}", "repeats more than" },
        { "group after an escaped parenthesis", @"\((((((((((a", "nests groups" },
    };

    [Fact]
    public void AcmeRuleFileIsValid()
    {
        var document = AcmeRuleFile.Create();

        LogRuleFile.Validate(document);

        Assert.Equal("acme.example.v1", document.ProfileId);
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void ValidateRejectsAnInvalidDocument(
        string name,
        Func<LogRuleDocumentV1, LogRuleDocumentV1> change,
        string fragment)
    {
        var document = change(AcmeRuleFile.Create());

        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Validate(document));

        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Theory]
    [MemberData(nameof(AcceptedPatterns))]
    public void PatternLimitsAcceptAPatternInsideTheLimits(string name, string pattern)
    {
        var regex = LogRulePattern.Compile(pattern, "test");

        Assert.NotNull(regex);
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Theory]
    [MemberData(nameof(RejectedPatterns))]
    public void PatternLimitsRejectAPatternOutsideTheLimits(string name, string pattern, string fragment)
    {
        var error = Assert.Throws<LogRuleValidationException>(() => LogRulePattern.Compile(pattern, "test"));

        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public void ValidateAcceptsTheLargestAllowedRuleCount()
    {
        var document = WithManyRules(LogRuleLimits.MaxRules);

        LogRuleFile.Validate(LogRuleFile.Seal(document));

        Assert.Equal(LogRuleLimits.MaxRules, document.Rules.Count);
    }

    [Fact]
    public void ValidateRejectsOneRuleOverTheLimit()
    {
        var error = Assert.Throws<LogRuleValidationException>(() =>
            LogRuleFile.Validate(WithManyRules(LogRuleLimits.MaxRules + 1)));

        Assert.Contains("rules has more than 500", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAcceptsTheLargestAllowedEventTypeCount()
    {
        var document = WithManyEventTypes(LogRuleLimits.MaxEventTypes);

        LogRuleFile.Validate(LogRuleFile.Seal(document));

        Assert.Equal(LogRuleLimits.MaxEventTypes, document.EventTypes.Count);
    }

    [Fact]
    public void ValidateRejectsOneEventTypeOverTheLimit()
    {
        var error = Assert.Throws<LogRuleValidationException>(() =>
            LogRuleFile.Validate(WithManyEventTypes(LogRuleLimits.MaxEventTypes + 1)));

        Assert.Contains("eventTypes has more than 100", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAcceptsTheLargestAllowedTestCount()
    {
        var document = WithManyTests(LogRuleLimits.MaxTests);

        LogRuleFile.Validate(LogRuleFile.Seal(document));

        Assert.Equal(LogRuleLimits.MaxTests, document.Tests.Count);
    }

    [Fact]
    public void ValidateRejectsOneTestOverTheLimit()
    {
        var error = Assert.Throws<LogRuleValidationException>(() =>
            LogRuleFile.Validate(WithManyTests(LogRuleLimits.MaxTests + 1)));

        Assert.Contains("tests has more than 1000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAcceptsAMessageWithEscapedBraces()
    {
        var document = AcmeRuleFile.WithRule(
            AcmeRuleFile.Unsealed(),
            "frame-lost",
            rule => rule with { Message = "Lost {{{seq}}}" });

        LogRuleFile.Validate(LogRuleFile.Seal(document));

        Assert.Contains("{{{seq}}}", document.Rules.Single(rule => rule.Id == "frame-lost").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAcceptsKeywordsThatKeepTheirSpaces()
    {
        var document = AcmeRuleFile.WithRule(
            AcmeRuleFile.Unsealed(),
            "frame-lost",
            rule => rule with { Keywords = [" frame lost ", "\ttp_drv:"] });

        LogRuleFile.Validate(LogRuleFile.Seal(document));

        Assert.Equal(" frame lost ", document.Rules.Single(rule => rule.Id == "frame-lost").Keywords![0]);
    }

    [Fact]
    public void ValidateAcceptsARuleWithOnlyAKeyword()
    {
        var document = AcmeRuleFile.Unsealed() with
        {
            Rules = [new("only-keyword", "touch.esd", "Seen", Keywords: ["tp_drv:"])],
            Tests = [],
        };

        LogRuleFile.Validate(LogRuleFile.Seal(document));

        Assert.Single(document.Rules);
    }

    [Fact]
    public void ValidateAcceptsARuleWithOnlyARegex()
    {
        var document = AcmeRuleFile.Unsealed() with
        {
            Rules = [new("only-regex", "touch.esd", "Seen {n}", Pattern: "n=(?<n>[0-9]+)", Captures: [new("n", LogCaptureType.Hex)])],
            Tests = [],
        };

        LogRuleFile.Validate(LogRuleFile.Seal(document));

        Assert.Single(document.Rules);
    }

    [Fact]
    public void ValidateStopsBetweenPatternsWhenCancelled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => LogRuleFile.Validate(AcmeRuleFile.Create(), source.Token));
    }

    private static LogRuleDocumentV1 WithManyRules(int count) => AcmeRuleFile.Unsealed() with
    {
        Rules = [.. Enumerable.Range(0, count).Select(index => new LogRuleV1($"rule-{index}", "touch.esd", "Seen", Keywords: [$"key{index}"]))],
        Tests = [],
    };

    private static LogRuleDocumentV1 WithManyEventTypes(int count) => AcmeRuleFile.Unsealed() with
    {
        EventTypes = [.. Enumerable.Range(0, count).Select(index => new LogEventTypeV1($"type-{index}", "Type", LogEventCategory.Annotation, DiagnosticSeverity.Info))],
        Rules = [],
        Tests = [],
    };

    private static LogRuleDocumentV1 WithManyTests(int count) => AcmeRuleFile.Unsealed() with
    {
        Tests = [.. Enumerable.Range(0, count).Select(index => new LogRuleTestV1($"test-{index}", "line", []))],
    };
}
