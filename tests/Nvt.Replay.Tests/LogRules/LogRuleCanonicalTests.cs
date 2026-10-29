// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Text;
using Nvt.Replay.Core;
using Nvt.Replay.Sources.LogRules;

namespace Nvt.Replay.Tests.LogRules;

public sealed class LogRuleCanonicalTests
{
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // The document type is internal, so a public theory cannot take it. The theory takes the case name.
    private static readonly Dictionary<string, Func<LogRuleDocumentV1, LogRuleDocumentV1>> _contentChangeCases = new()
    {
        ["profileId"] = d => d with { ProfileId = "acme.example.v2" },
        ["displayName"] = d => d with { DisplayName = "Acme Motors example (other)" },
        ["scope.carmaker"] = d => d with { Scope = d.Scope with { Carmaker = "Other" } },
        ["scope.project"] = d => d with { Scope = d.Scope with { Project = "P1" } },
        ["scope.platform"] = d => d with { Scope = d.Scope with { Platform = "other" } },
        ["scope.note"] = d => d with { Scope = d.Scope with { Note = "other" } },
        ["timestamp.removed"] = d => d with { Timestamp = null },
        ["timestamp.regex"] = d => d with { Timestamp = d.Timestamp! with { Pattern = "^(?<ts>[0-9.]+) " } },
        ["timestamp.format"] = d => d with { Timestamp = d.Timestamp! with { Format = LogTimestampFormat.EpochSeconds } },
        ["eventType.added"] = d => d with { EventTypes = [.. d.EventTypes, new("touch.extra", "Extra", LogEventCategory.Annotation, DiagnosticSeverity.Info)] },
        ["eventType.displayName"] = d => d with { EventTypes = [d.EventTypes[0] with { DisplayName = "Other" }, .. d.EventTypes.Skip(1)] },
        ["eventType.category"] = d => d with { EventTypes = [d.EventTypes[0] with { Category = LogEventCategory.QaResult }, .. d.EventTypes.Skip(1)] },
        ["eventType.severity"] = d => d with { EventTypes = [d.EventTypes[0] with { Severity = DiagnosticSeverity.Error }, .. d.EventTypes.Skip(1)] },
        ["rule.id"] = d => AcmeRuleFile.WithRule(d, "frame-lost", rule => rule with { Id = "frame-lost2" }),
        ["rule.eventType"] = d => AcmeRuleFile.WithRule(d, "frame-lost", rule => rule with { EventType = "touch.esd" }),
        ["rule.message"] = d => AcmeRuleFile.WithRule(d, "frame-lost", rule => rule with { Message = "Lost {seq}" }),
        ["rule.contains"] = d => AcmeRuleFile.WithRule(d, "frame-lost", rule => rule with { Keywords = ["frame lost"] }),
        ["rule.ignoreCase"] = d => AcmeRuleFile.WithRule(d, "frame-lost", rule => rule with { IgnoreCase = true }),
        ["rule.regex"] = d => AcmeRuleFile.WithRule(d, "frame-lost", rule => rule with { Pattern = "frame lost seq=(?<seq>[0-9]+)" }),
        ["rule.severity"] = d => AcmeRuleFile.WithRule(d, "frame-lost", rule => rule with { Severity = DiagnosticSeverity.Alarm }),
        ["capture.type"] = d => AcmeRuleFile.WithRule(d, "frame-lost", rule => rule with { Captures = [new("seq", LogCaptureType.String)] }),
        ["capture.mapLabel"] = d => AcmeRuleFile.WithRule(d, "reset-reason", rule => rule with { Captures = [new("code", LogCaptureType.Enum, new Dictionary<string, string> { ["0x01"] = "power", ["0x04"] = "watchdog" })] }),
        ["capture.mapKey"] = d => AcmeRuleFile.WithRule(d, "reset-reason", rule => rule with { Captures = [new("code", LogCaptureType.Enum, new Dictionary<string, string> { ["0x02"] = "power-on", ["0x04"] = "watchdog" })] }),
        ["test.name"] = d => d with { Tests = [d.Tests[0] with { Name = "other-name" }, d.Tests[1]] },
        ["test.line"] = d => d with { Tests = [d.Tests[0] with { Line = "other line" }, d.Tests[1]] },
        ["test.expectRule"] = d => d with { Tests = [d.Tests[0], d.Tests[1] with { Expect = [new("esd-detected")] }] },
        ["test.expectField"] = d => d with { Tests = [d.Tests[0], d.Tests[1] with { Expect = [new("esd-detected", new Dictionary<string, string> { ["cnt"] = "4" })] }] },
    };

    public static TheoryData<string> ContentChanges()
    {
        var data = new TheoryData<string>();
        foreach (var name in _contentChangeCases.Keys) data.Add(name);
        return data;
    }

    [Fact]
    public void SerializeCanonicalMatchesTheGoldenText()
    {
        var text = _strictUtf8.GetString(LogRuleFile.SerializeCanonical(AcmeRuleFile.Create()));

        Assert.Equal(AcmeRuleFile.CanonicalText, text);
    }

    [Fact]
    public void SerializeCanonicalUsesLfOnlyAndNoByteOrderMark()
    {
        var bytes = LogRuleFile.SerializeCanonical(AcmeRuleFile.Create());

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public void SerializeCanonicalEndsWithExactlyOneLf()
    {
        var bytes = LogRuleFile.SerializeCanonical(AcmeRuleFile.Create());

        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Equal((byte)'}', bytes[^2]);
    }

    [Fact]
    public void HashInputMatchesTheGoldenCompactBytes()
    {
        var bytes = LogRuleCanonicalWriter.Write(AcmeRuleFile.Unsealed(), includeHash: false, indented: false);

        Assert.Equal(AcmeRuleFile.CompactHashInput, _strictUtf8.GetString(bytes));
    }

    [Fact]
    public void ContentHashEqualsTheIndependentlyComputedValue()
    {
        var hash = LogRuleFile.ComputeContentSha256(AcmeRuleFile.Unsealed());

        Assert.Equal(AcmeRuleFile.ContentSha256, hash);
    }

    [Fact]
    public void HashIgnoresTheStoredContentSha256()
    {
        var other = AcmeRuleFile.Unsealed() with { ContentSha256 = "not a hash" };

        Assert.Equal(AcmeRuleFile.ContentSha256, LogRuleFile.ComputeContentSha256(other));
    }

    [Fact]
    public void HashAndBytesDoNotDependOnListOrderOrMapOrder()
    {
        var first = AcmeRuleFile.Create();
        var reordered = AcmeRuleFile.Unsealed() with
        {
            EventTypes = [.. first.EventTypes.Reverse()],
            Rules = [.. first.Rules.Reverse().Select(rule => rule with { Keywords = rule.Keywords?.Reverse().ToArray() })],
            Tests = [.. first.Tests.Reverse()],
        };
        reordered = LogRuleFile.Seal(AcmeRuleFile.WithRule(reordered, "reset-reason", rule => rule with
        {
            Captures = [new("code", LogCaptureType.Enum, new Dictionary<string, string> { ["0x01"] = "power-on", ["0x04"] = "watchdog" })],
        }));

        Assert.Equal(first.ContentSha256, reordered.ContentSha256);
        Assert.Equal(LogRuleFile.SerializeCanonical(first), LogRuleFile.SerializeCanonical(reordered));
    }

    [Fact]
    public void AbsentAndEmptyOptionalListsGiveTheSameHash()
    {
        var absent = AcmeRuleFile.WithRule(AcmeRuleFile.Unsealed(), "frame-lost", rule => rule with { Keywords = null });
        var empty = AcmeRuleFile.WithRule(AcmeRuleFile.Unsealed(), "frame-lost", rule => rule with { Keywords = [] });

        Assert.Equal(LogRuleFile.ComputeContentSha256(absent), LogRuleFile.ComputeContentSha256(empty));
    }

    [Theory]
    [MemberData(nameof(ContentChanges))]
    public void EveryContentFieldChangesTheHash(string field)
    {
        var changed = _contentChangeCases[field](AcmeRuleFile.Unsealed());

        Assert.NotEqual(AcmeRuleFile.ContentSha256, LogRuleFile.ComputeContentSha256(changed));
    }

    [Fact]
    public void ParseOfTheCanonicalBytesRoundTripsToTheSameBytes()
    {
        var bytes = LogRuleFile.SerializeCanonical(AcmeRuleFile.Create());

        var parsed = LogRuleFile.Parse(bytes);

        Assert.Equal(bytes, LogRuleFile.SerializeCanonical(parsed));
        Assert.Equal(new LogRuleIdentity("acme.example.v1", AcmeRuleFile.ContentSha256), parsed.Identity);
    }

    [Fact]
    public void RelaxedEscapingKeepsRegexPunctuationReadable()
    {
        var document = AcmeRuleFile.WithRule(
            AcmeRuleFile.Unsealed(),
            "frame-lost",
            rule => rule with { Pattern = "frame lost seq=(?<seq>[0-9]+)<'&>" });

        var text = _strictUtf8.GetString(LogRuleFile.SerializeCanonical(LogRuleFile.Seal(document)));

        Assert.Contains("[0-9]+)<'&>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NonAsciiTextSurvivesARoundTrip()
    {
        var document = LogRuleFile.Seal(AcmeRuleFile.Unsealed() with { DisplayName = "測試 規則檔" });

        var parsed = LogRuleFile.Parse(LogRuleFile.SerializeCanonical(document));

        Assert.Equal("測試 規則檔", parsed.DisplayName);
    }

    [Fact]
    public void OptionalValuesAreLeftOutOfTheCanonicalJson()
    {
        var document = AcmeRuleFile.Unsealed() with
        {
            Scope = new LogRuleScopeV1(),
            Timestamp = null,
            Rules = [new("only-keyword", "touch.esd", "Seen", Keywords: ["tp_drv:"])],
            Tests = [],
        };

        var text = _strictUtf8.GetString(LogRuleFile.SerializeCanonical(LogRuleFile.Seal(document)));

        Assert.Contains("\"scope\": {}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("timestamp", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ignoreCase", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"regex\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("captures", text, StringComparison.Ordinal);
    }
}
