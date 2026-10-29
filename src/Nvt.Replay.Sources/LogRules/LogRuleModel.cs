// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Text.Json.Serialization;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources.LogRules;

/// <summary>Classifies a log event. The values match the Review Queue categories, which live in a project this one cannot reference.</summary>
internal enum LogEventCategory
{
    ProtocolDiagnostic,
    DeviceState,
    BehaviorFinding,
    QaResult,
    Annotation,
}

/// <summary>Names how a timestamp capture is read. The list is closed and grows only through a schema version.</summary>
internal enum LogTimestampFormat
{
    SecondsSinceBoot,
    EpochSeconds,
    IsoOffset,
    TimeOfDay,
}

/// <summary>Names how the text of a regular expression capture is converted.</summary>
internal enum LogCaptureType
{
    String,
    Integer,
    Hex,
    Enum,
}

/// <summary>Holds operator-facing labels that are stored and shown but never executed or compared.</summary>
internal sealed record LogRuleScopeV1(
    string? Carmaker = null,
    string? Project = null,
    string? Platform = null,
    string? Note = null);

/// <summary>Describes how to read the time of a log line. <paramref name="Pattern"/> must declare a group named ts.</summary>
internal sealed record LogRuleTimestampV1(
    [property: JsonPropertyName("regex")] string Pattern,
    LogTimestampFormat Format);

/// <summary>Declares one kind of event a rule file may create.</summary>
internal sealed record LogEventTypeV1(
    string Id,
    string DisplayName,
    LogEventCategory Category,
    DiagnosticSeverity Severity);

/// <summary>Maps a named group of a rule pattern to a typed event field. <paramref name="Map"/> is only for <see cref="LogCaptureType.Enum"/>.</summary>
internal sealed record LogRuleCaptureV1(
    string Name,
    LogCaptureType Type,
    IReadOnlyDictionary<string, string>? Map = null);

/// <summary>Defines what a line must contain, which fields to capture and which event type to create.</summary>
internal sealed record LogRuleV1(
    string Id,
    string EventType,
    string Message,
    [property: JsonPropertyName("contains")] IReadOnlyList<string>? Keywords = null,
    bool IgnoreCase = false,
    [property: JsonPropertyName("regex")] string? Pattern = null,
    IReadOnlyList<LogRuleCaptureV1>? Captures = null,
    DiagnosticSeverity? Severity = null);

/// <summary>States that one rule matches a self-test line and which raw capture text it produces.</summary>
internal sealed record LogRuleExpectationV1(
    string Rule,
    IReadOnlyDictionary<string, string>? Fields = null);

/// <summary>Holds a sample line and the rules expected to match it. An empty list means no rule matches.</summary>
internal sealed record LogRuleTestV1(
    string Name,
    string Line,
    IReadOnlyList<LogRuleExpectationV1> Expect);

/// <summary>Names the revision of a rule file: its profile ID and the SHA-256 of its canonical content.</summary>
internal sealed record LogRuleIdentity(string ProfileId, string ContentSha256);

/// <summary>
/// Versioned, declarative log rule file. The SHA-256 covers the canonical content except for
/// <see cref="ContentSha256"/>. It carries no script, callback, plugin or URI.
/// </summary>
internal sealed record LogRuleDocumentV1(
    int SchemaVersion,
    string FileKind,
    string ProfileId,
    string DisplayName,
    LogRuleScopeV1 Scope,
    string LineEncoding,
    IReadOnlyList<LogEventTypeV1> EventTypes,
    IReadOnlyList<LogRuleV1> Rules,
    IReadOnlyList<LogRuleTestV1> Tests,
    string ContentSha256,
    LogRuleTimestampV1? Timestamp = null)
{
    /// <summary>Gets the identity formed from the profile ID and the content SHA-256.</summary>
    [JsonIgnore]
    public LogRuleIdentity Identity => new(ProfileId, ContentSha256);
}

/// <summary>Reports invalid log rule content or malformed rule file JSON.</summary>
internal sealed class LogRuleValidationException(string message, Exception? innerException = null)
    : FormatException(message, innerException);
