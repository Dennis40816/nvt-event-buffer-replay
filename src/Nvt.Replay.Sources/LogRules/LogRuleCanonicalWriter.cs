// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Nvt.Replay.Sources.LogRules;

/// <summary>
/// Writes the canonical JSON of a rule file. Properties follow the schema order. Lists and maps whose
/// order carries no meaning are sorted with ordinal comparison. Absent optional values are left out,
/// so a later optional property does not change the hash of an older file. Strings use the relaxed
/// System.Text.Json escaping, which keeps ASCII punctuation such as '+' and '&lt;' readable.
/// </summary>
internal static class LogRuleCanonicalWriter
{
    /// <summary>Writes the document as UTF-8 without a byte-order mark. A compact write is the hash input. An indented write has LF line breaks and no final newline.</summary>
    public static byte[] Write(LogRuleDocumentV1 document, bool includeHash, bool indented)
    {
        using var stream = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Indented = indented,
            IndentCharacter = ' ',
            IndentSize = 2,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WriteString("fileKind", document.FileKind);
            writer.WriteString("profileId", document.ProfileId);
            writer.WriteString("displayName", document.DisplayName);
            WriteScope(writer, document.Scope);
            writer.WriteString("lineEncoding", document.LineEncoding);
            WriteTimestamp(writer, document.Timestamp);
            WriteEventTypes(writer, document.EventTypes);
            WriteRules(writer, document.Rules);
            WriteTests(writer, document.Tests);
            if (includeHash) writer.WriteString("contentSha256", document.ContentSha256);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteScope(Utf8JsonWriter writer, LogRuleScopeV1 scope)
    {
        writer.WriteStartObject("scope");
        // The keys of scope are chosen by the schema, not by the file author, but the contract sorts them
        // with ordinal comparison like every other map, so a new key slots in without a rule.
        WriteOptionalString(writer, "carmaker", scope.Carmaker);
        WriteOptionalString(writer, "note", scope.Note);
        WriteOptionalString(writer, "platform", scope.Platform);
        WriteOptionalString(writer, "project", scope.Project);
        writer.WriteEndObject();
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, LogRuleTimestampV1? timestamp)
    {
        if (timestamp is null) return;
        writer.WriteStartObject("timestamp");
        writer.WriteString("regex", timestamp.Pattern);
        writer.WriteString("format", CamelCase(timestamp.Format));
        writer.WriteEndObject();
    }

    private static void WriteEventTypes(Utf8JsonWriter writer, IReadOnlyList<LogEventTypeV1> eventTypes)
    {
        writer.WriteStartArray("eventTypes");
        foreach (var eventType in eventTypes.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", eventType.Id);
            writer.WriteString("displayName", eventType.DisplayName);
            writer.WriteString("category", CamelCase(eventType.Category));
            writer.WriteString("severity", eventType.Severity.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteRules(Utf8JsonWriter writer, IReadOnlyList<LogRuleV1> rules)
    {
        writer.WriteStartArray("rules");
        foreach (var rule in rules.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", rule.Id);
            writer.WriteString("eventType", rule.EventType);
            if (rule.Keywords is { Count: > 0 })
            {
                writer.WriteStartArray("contains");
                foreach (var keyword in rule.Keywords.Order(StringComparer.Ordinal)) writer.WriteStringValue(keyword);
                writer.WriteEndArray();
            }
            if (rule.IgnoreCase) writer.WriteBoolean("ignoreCase", true);
            WriteOptionalString(writer, "regex", rule.Pattern);
            WriteCaptures(writer, rule.Captures);
            if (rule.Severity is { } severity) writer.WriteString("severity", severity.ToString());
            writer.WriteString("message", rule.Message);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteCaptures(Utf8JsonWriter writer, IReadOnlyList<LogRuleCaptureV1>? captures)
    {
        if (captures is not { Count: > 0 }) return;
        writer.WriteStartArray("captures");
        foreach (var capture in captures.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", capture.Name);
            writer.WriteString("type", CamelCase(capture.Type));
            WriteMap(writer, "map", capture.Map);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteTests(Utf8JsonWriter writer, IReadOnlyList<LogRuleTestV1> tests)
    {
        writer.WriteStartArray("tests");
        foreach (var test in tests.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", test.Name);
            writer.WriteString("line", test.Line);
            writer.WriteStartArray("expect");
            foreach (var expectation in test.Expect.OrderBy(item => item.Rule, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("rule", expectation.Rule);
                WriteMap(writer, "fields", expectation.Fields);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteMap(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string>? map)
    {
        if (map is not { Count: > 0 }) return;
        writer.WriteStartObject(name);
        foreach (var (key, value) in map.OrderBy(item => item.Key, StringComparer.Ordinal))
            writer.WriteString(key, value);
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null) writer.WriteString(name, value);
    }

    private static string CamelCase<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
