// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Nvt.Replay.Sources.LogRules;

namespace Nvt.Replay.Tests.LogRules;

public sealed class LogRuleAdmissionTests
{
    [Fact]
    public void SealKeepsItsContentWhenTheSourceListChanges()
    {
        var rules = new List<LogRuleV1>(AcmeRuleFile.Unsealed().Rules);
        var sealedDocument = LogRuleFile.Seal(AcmeRuleFile.Unsealed() with { Rules = rules });

        rules.Clear();

        Assert.Equal(3, sealedDocument.Rules.Count);
        Assert.Equal(AcmeRuleFile.ContentSha256, LogRuleFile.ComputeContentSha256(sealedDocument));
    }

    [Fact]
    public void SealKeepsItsContentWhenANestedSourceListChanges()
    {
        var keywords = new List<string> { "tp_drv:", "frame lost" };
        var document = AcmeRuleFile.WithRule(AcmeRuleFile.Unsealed(), "frame-lost", rule => rule with { Keywords = keywords });
        var sealedDocument = LogRuleFile.Seal(document);

        keywords.Add("extra");

        Assert.Equal(2, sealedDocument.Rules.Single(rule => rule.Id == "frame-lost").Keywords!.Count);
    }

    [Fact]
    public void SealKeepsItsContentWhenASourceMapChanges()
    {
        var map = new Dictionary<string, string> { ["0x01"] = "power-on", ["0x04"] = "watchdog" };
        var document = AcmeRuleFile.WithRule(
            AcmeRuleFile.Unsealed(),
            "reset-reason",
            rule => rule with { Captures = [new("code", LogCaptureType.Enum, map)] });
        var sealedDocument = LogRuleFile.Seal(document);

        map["0x09"] = "other";

        Assert.Equal(AcmeRuleFile.ContentSha256, LogRuleFile.ComputeContentSha256(sealedDocument));
    }

    [Fact]
    public void SealKeepsItsContentWhenAnExpectedFieldMapChanges()
    {
        var fields = new Dictionary<string, string> { ["cnt"] = "3" };
        var document = AcmeRuleFile.Unsealed() with { Tests = [new("t1", "line", [new("esd-detected", fields)])] };
        var sealedDocument = LogRuleFile.Seal(document);

        fields["cnt"] = "4";

        Assert.Equal("3", sealedDocument.Tests[0].Expect[0].Fields!["cnt"]);
    }

    [Fact]
    public void ParsedListsCannotBeChangedThroughTheirInterfaces()
    {
        var parsed = LogRuleFile.Parse(AcmeRuleFile.CanonicalText);

        Assert.Throws<NotSupportedException>(() => ((IList<LogEventTypeV1>)parsed.EventTypes).Add(parsed.EventTypes[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<LogRuleV1>)parsed.Rules).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<LogRuleTestV1>)parsed.Tests).Clear());
    }

    [Fact]
    public void ParsedNestedListsAndMapsCannotBeChangedThroughTheirInterfaces()
    {
        var parsed = LogRuleFile.Parse(AcmeRuleFile.CanonicalText);
        var rule = parsed.Rules.Single(item => item.Id == "reset-reason");
        var expectation = parsed.Tests.Single(test => test.Name == "esd-basic").Expect[0];

        Assert.Throws<NotSupportedException>(() => ((IList<string>)rule.Keywords!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<LogRuleCaptureV1>)rule.Captures!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)rule.Captures![0].Map!).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<LogRuleExpectationV1>)parsed.Tests[0].Expect).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)expectation.Fields!).Clear());
    }

    [Fact]
    public void SealedListsCannotBeChangedThroughTheirInterfaces()
    {
        var document = AcmeRuleFile.Create();

        Assert.Throws<NotSupportedException>(() => ((IList<LogRuleV1>)document.Rules).Clear());
    }
}
