// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Text;
using System.Text.Json.Nodes;
using Nvt.Replay.Sources.LogRules;

namespace Nvt.Replay.Tests.LogRules;

public sealed class LogRuleParseTests
{
    public static TheoryData<string, string, string, string> TextEdits() => new()
    {
        { "unknown property", "\"fileKind\": \"logRules\",", "\"fileKind\": \"logRules\",\n  \"extra\": 1,", "extra" },
        { "duplicate property", "\"profileId\": \"acme.example.v1\",", "\"profileId\": \"acme.example.v1\",\n  \"profileId\": \"other\",", "profileId" },
        { "missing displayName", "  \"displayName\": \"Acme Motors example (synthetic)\",\n", string.Empty, "isplayName" },
        { "null displayName", "\"displayName\": \"Acme Motors example (synthetic)\"", "\"displayName\": null", "isplayName" },
        { "integer category", "\"category\": \"deviceState\"", "\"category\": 1", "category" },
        { "unknown category", "\"category\": \"deviceState\"", "\"category\": \"other\"", "category" },
        { "unknown severity", "\"severity\": \"Warning\"", "\"severity\": \"Fatal\"", "severity" },
        { "unknown capture type", "\"type\": \"integer\"", "\"type\": \"float\"", "type" },
        { "unknown timestamp format", "\"format\": \"secondsSinceBoot\"", "\"format\": \"julian\"", "format" },
        { "trailing comma", "\"format\": \"secondsSinceBoot\"", "\"format\": \"secondsSinceBoot\",", "JSON" },
        { "comment", "\"lineEncoding\": \"utf-8\",", "\"lineEncoding\": \"utf-8\", // note", "JSON" },
        { "property case", "\"profileId\"", "\"ProfileId\"", "ProfileId" },
    };

    public static TheoryData<string> SchemaVersionsThatAreNotIntegers() => new()
    {
        "\"1\"",
        "null",
        "true",
        "[1]",
        "{}",
        "1.5",
    };

    [Fact]
    public void ParseAcceptsTheGoldenText()
    {
        var parsed = LogRuleFile.Parse(AcmeRuleFile.CanonicalText);

        Assert.Equal(AcmeRuleFile.ContentSha256, parsed.ContentSha256);
    }

    [Fact]
    public void ParseAcceptsAnyPropertyOrderWhenTheHashMatchesTheContent()
    {
        var root = JsonNode.Parse(AcmeRuleFile.CanonicalText)!.AsObject();
        var reversed = new JsonObject(root.Reverse().Select(property =>
            KeyValuePair.Create(property.Key, property.Value?.DeepClone())));

        var parsed = LogRuleFile.Parse(reversed.ToJsonString());

        Assert.Equal(AcmeRuleFile.ContentSha256, parsed.ContentSha256);
    }

    [Theory]
    [MemberData(nameof(TextEdits))]
    public void ParseRejectsAnEditedGoldenText(string name, string find, string replace, string fragment)
    {
        var edited = AcmeRuleFile.CanonicalText.Replace(find, replace, StringComparison.Ordinal);

        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(edited));

        Assert.NotEqual(AcmeRuleFile.CanonicalText, edited);
        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public void ParseRejectsARegisterProfileWithAClearMessage()
    {
        const string registerProfile = """{"schemaVersion":1,"profileId":"x","displayName":"x"}""";

        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(registerProfile));

        Assert.Contains("fileKind must be 'logRules'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsAnUnknownSchemaVersionBeforeDeserializing()
    {
        const string newer = """{"schemaVersion":2,"fileKind":"logRules","somethingNew":true}""";

        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(newer));

        Assert.Contains("Unknown log-rule schemaVersion 2", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SchemaVersionsThatAreNotIntegers))]
    public void ParseReportsASchemaVersionThatIsNotAnInteger(string value)
    {
        var json = $$"""{"schemaVersion":{{value}},"fileKind":"logRules"}""";

        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(json));

        Assert.Contains("schemaVersion is required and must be an integer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseReportsAMissingSchemaVersion()
    {
        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse("""{"fileKind":"logRules"}"""));

        Assert.Contains("schemaVersion is required and must be an integer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsARootThatIsNotAnObject()
    {
        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse("[]"));

        Assert.Contains("root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsAnEmptyDocument()
    {
        Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(string.Empty));
    }

    [Fact]
    public void ParseRejectsAByteOrderMark()
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(AcmeRuleFile.CanonicalText))
            .ToArray();

        Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(bytes));
    }

    [Fact]
    public void ParseRejectsBytesThatAreNotUtf8()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"fileKind\":\"logRules\",\"profileId\":\""), 0xFF, 0xFE, .. Encoding.UTF8.GetBytes("\"}")];

        Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(bytes));
    }

    [Fact]
    public void ParseRejectsTextWithALoneSurrogate()
    {
        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse("{\"x\":\"\ud800\"}"));

        Assert.Contains("Unicode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsAFileLargerThanOneMebibyte()
    {
        var bytes = new byte[LogRuleLimits.MaxFileBytes + 1];

        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(bytes));

        Assert.Contains("larger than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsAFileWhoseCanonicalFormIsOverTheLimitEvenWhenTheInputIsNot()
    {
        var document = AcmeRuleFile.WithManyKeywords(60_000) with { ContentSha256 = new string('0', 64) };
        var compact = LogRuleCanonicalWriter.Write(document, includeHash: true, indented: false);

        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(compact));

        Assert.InRange(compact.Length, 1, LogRuleLimits.MaxFileBytes);
        Assert.Contains("once saved in canonical form", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsAHashThatDoesNotMatchTheContent()
    {
        var edited = AcmeRuleFile.CanonicalText.Replace("Acme Motors example (synthetic)", "Acme Motors example (changed)", StringComparison.Ordinal);

        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(edited));

        Assert.Contains("does not match the canonical content", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsAnUppercaseHash()
    {
        var edited = AcmeRuleFile.CanonicalText.Replace(AcmeRuleFile.ContentSha256, AcmeRuleFile.ContentSha256.ToUpperInvariant(), StringComparison.Ordinal);

        var error = Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(edited));

        Assert.Contains("lowercase 64-character", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsAnEmptyHash()
    {
        var edited = AcmeRuleFile.CanonicalText.Replace(AcmeRuleFile.ContentSha256, string.Empty, StringComparison.Ordinal);

        Assert.Throws<LogRuleValidationException>(() => LogRuleFile.Parse(edited));
    }

    [Fact]
    public void ParseStopsWhenCancellationIsAlreadyRequested()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => LogRuleFile.Parse(AcmeRuleFile.CanonicalText, source.Token));
    }
}
