// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Text.RegularExpressions;

namespace Nvt.Replay.Sources.LogRules;

/// <summary>Checks the content of a rule file against the version 1 contract. It does not check the content hash.</summary>
internal static partial class LogRuleValidator
{
    private const string TimestampGroup = "ts";

    /// <summary>Validates the document. Cancellation is checked on entry, before the timestamp pattern and before each rule. Nothing is written.</summary>
    public static void Validate(LogRuleDocumentV1 document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateHeader(document);
        var eventTypes = ValidateEventTypes(Require(document.EventTypes, "eventTypes"));
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTimestamp(document.Timestamp);
        var captureNames = ValidateRules(Require(document.Rules, "rules"), eventTypes, cancellationToken);
        ValidateTests(Require(document.Tests, "tests"), captureNames);
    }

    private static void ValidateHeader(LogRuleDocumentV1 document)
    {
        if (document.SchemaVersion != LogRuleFile.CurrentSchemaVersion)
        {
            throw new LogRuleValidationException(
                $"Unknown log-rule schemaVersion {document.SchemaVersion}; supported version is {LogRuleFile.CurrentSchemaVersion}.");
        }
        if (!string.Equals(document.FileKind, LogRuleFile.FileKind, StringComparison.Ordinal))
            throw new LogRuleValidationException($"fileKind must be '{LogRuleFile.FileKind}'.");
        ValidateId(document.ProfileId, "profileId");
        ValidateText(document.DisplayName, "displayName");
        if (!string.Equals(document.LineEncoding, "utf-8", StringComparison.Ordinal))
            throw new LogRuleValidationException("lineEncoding must be 'utf-8' in schema version 1.");
        var scope = Require(document.Scope, "scope");
        ValidateOptionalText(scope.Carmaker, "scope.carmaker");
        ValidateOptionalText(scope.Project, "scope.project");
        ValidateOptionalText(scope.Platform, "scope.platform");
        ValidateOptionalText(scope.Note, "scope.note");
    }

    private static void ValidateTimestamp(LogRuleTimestampV1? timestamp)
    {
        if (timestamp is null) return;
        if (!Enum.IsDefined(timestamp.Format))
            throw new LogRuleValidationException("timestamp.format is not a known format.");
        var regex = LogRulePattern.Compile(timestamp.Pattern, "timestamp.regex");
        if (!LogRulePattern.NamedGroups(regex).Contains(TimestampGroup))
            throw new LogRuleValidationException($"timestamp.regex must declare a group named '{TimestampGroup}'.");
    }

    private static HashSet<string> ValidateEventTypes(IReadOnlyList<LogEventTypeV1> eventTypes)
    {
        if (eventTypes.Count > LogRuleLimits.MaxEventTypes)
            throw new LogRuleValidationException($"eventTypes has more than {LogRuleLimits.MaxEventTypes} entries.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var eventType in eventTypes)
        {
            if (eventType is null)
                throw new LogRuleValidationException("eventTypes cannot contain null entries.");
            ValidateId(eventType.Id, "eventTypes[].id");
            ValidateText(eventType.DisplayName, "eventTypes[].displayName");
            if (!Enum.IsDefined(eventType.Category))
                throw new LogRuleValidationException($"eventTypes[].category of '{eventType.Id}' is not a known category.");
            if (!Enum.IsDefined(eventType.Severity))
                throw new LogRuleValidationException($"eventTypes[].severity of '{eventType.Id}' is not a known severity.");
            if (!ids.Add(eventType.Id))
                throw new LogRuleValidationException($"Duplicate event type id '{eventType.Id}'.");
        }
        return ids;
    }

    // Returns the capture names of every rule, so that self-tests can check their field names.
    private static Dictionary<string, HashSet<string>> ValidateRules(
        IReadOnlyList<LogRuleV1> rules,
        HashSet<string> eventTypes,
        CancellationToken cancellationToken)
    {
        if (rules.Count > LogRuleLimits.MaxRules)
            throw new LogRuleValidationException($"rules has more than {LogRuleLimits.MaxRules} entries.");
        var captureNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule is null)
                throw new LogRuleValidationException("rules cannot contain null entries.");
            ValidateId(rule.Id, "rules[].id");
            if (captureNames.ContainsKey(rule.Id))
                throw new LogRuleValidationException($"Duplicate rule id '{rule.Id}'.");
            if (!eventTypes.Contains(rule.EventType ?? string.Empty))
                throw new LogRuleValidationException($"Rule '{rule.Id}' names unknown event type '{rule.EventType}'.");
            if (rule.Severity is { } severity && !Enum.IsDefined(severity))
                throw new LogRuleValidationException($"Rule '{rule.Id}' has an unknown severity.");
            ValidateKeywords(rule);
            captureNames[rule.Id] = ValidatePatternAndCaptures(rule);
            ValidateMessage(rule, captureNames[rule.Id]);
        }
        return captureNames;
    }

    private static void ValidateKeywords(LogRuleV1 rule)
    {
        var keywords = rule.Keywords ?? [];
        var field = $"Rule '{rule.Id}' contains";
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keyword in keywords)
        {
            ValidateLineText(keyword, field, allowEmpty: false);
            if (!unique.Add(keyword))
                throw new LogRuleValidationException($"{field} lists '{keyword}' twice.");
        }
        if (keywords.Count == 0 && rule.Pattern is null)
            throw new LogRuleValidationException($"Rule '{rule.Id}' needs at least one of contains and regex.");
    }

    private static HashSet<string> ValidatePatternAndCaptures(LogRuleV1 rule)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var captures = rule.Captures ?? [];
        if (rule.Pattern is null)
        {
            if (captures.Count > 0)
                throw new LogRuleValidationException($"Rule '{rule.Id}' has captures but no regex.");
            return names;
        }

        var groups = LogRulePattern.NamedGroups(LogRulePattern.Compile(rule.Pattern, $"Rule '{rule.Id}' regex"));
        foreach (var capture in captures)
        {
            if (capture is null)
                throw new LogRuleValidationException($"Rule '{rule.Id}' captures cannot contain null entries.");
            ValidateCapture(rule, capture, groups);
            if (!names.Add(capture.Name))
                throw new LogRuleValidationException($"Rule '{rule.Id}' captures '{capture.Name}' twice.");
        }
        return names;
    }

    private static void ValidateCapture(LogRuleV1 rule, LogRuleCaptureV1 capture, IReadOnlySet<string> groups)
    {
        var field = $"Rule '{rule.Id}' capture '{capture.Name}'";
        if (capture.Name is null || !CaptureNamePattern().IsMatch(capture.Name))
        {
            throw new LogRuleValidationException(
                $"Rule '{rule.Id}' capture name must use 1-64 ASCII letters, digits or '_' and start with a letter or '_'.");
        }
        if (!groups.Contains(capture.Name))
            throw new LogRuleValidationException($"{field} is not a named group of the regex.");
        if (!Enum.IsDefined(capture.Type))
            throw new LogRuleValidationException($"{field} has an unknown type.");
        if (capture.Type != LogCaptureType.Enum)
        {
            if (capture.Map is { Count: > 0 })
                throw new LogRuleValidationException($"{field} has a map, but only enum captures have one.");
            return;
        }

        if (capture.Map is null || capture.Map.Count == 0)
            throw new LogRuleValidationException($"{field} is an enum and needs a non-empty map.");
        foreach (var (key, label) in capture.Map)
        {
            ValidateLineText(key, $"{field} map key", allowEmpty: false);
            ValidateText(label, $"{field} map label");
        }
    }

    private static void ValidateMessage(LogRuleV1 rule, HashSet<string> captureNames)
    {
        var field = $"Rule '{rule.Id}' message";
        ValidateText(rule.Message, field);
        foreach (var name in LogRuleMessageTemplate.PlaceholderNames(rule.Message, field))
        {
            if (!captureNames.Contains(name))
                throw new LogRuleValidationException($"{field} uses placeholder '{{{name}}}' that is not a capture.");
        }
    }

    private static void ValidateTests(
        IReadOnlyList<LogRuleTestV1> tests,
        Dictionary<string, HashSet<string>> captureNames)
    {
        if (tests.Count > LogRuleLimits.MaxTests)
            throw new LogRuleValidationException($"tests has more than {LogRuleLimits.MaxTests} entries.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in tests)
        {
            if (test is null)
                throw new LogRuleValidationException("tests cannot contain null entries.");
            ValidateId(test.Name, "tests[].name");
            if (!names.Add(test.Name))
                throw new LogRuleValidationException($"Duplicate test name '{test.Name}'.");
            ValidateLineText(test.Line, $"Test '{test.Name}' line", allowEmpty: true, LogRuleLimits.MaxLineLength);
            ValidateExpectations(test, captureNames);
        }
    }

    private static void ValidateExpectations(
        LogRuleTestV1 test,
        Dictionary<string, HashSet<string>> captureNames)
    {
        var rules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expectation in Require(test.Expect, $"Test '{test.Name}' expect"))
        {
            if (expectation is null)
                throw new LogRuleValidationException($"Test '{test.Name}' expect cannot contain null entries.");
            if (expectation.Rule is null || !captureNames.TryGetValue(expectation.Rule, out var fields))
                throw new LogRuleValidationException($"Test '{test.Name}' expects unknown rule '{expectation.Rule}'.");
            if (!rules.Add(expectation.Rule))
                throw new LogRuleValidationException($"Test '{test.Name}' expects rule '{expectation.Rule}' twice.");
            foreach (var (name, value) in expectation.Fields ?? new Dictionary<string, string>())
            {
                if (!fields.Contains(name))
                    throw new LogRuleValidationException($"Test '{test.Name}' expects field '{name}' that rule '{expectation.Rule}' does not capture.");
                ValidateLineText(value, $"Test '{test.Name}' field '{name}'", allowEmpty: true);
            }
        }
    }

    private static T Require<T>(T? value, string field)
        where T : class =>
        value ?? throw new LogRuleValidationException($"{field} is required.");

    private static void ValidateId(string? value, string field)
    {
        if (value is null || !IdPattern().IsMatch(value))
        {
            throw new LogRuleValidationException(
                $"{field} must use 1-128 ASCII letters, digits, '.', '_' or '-', and start with a letter or digit.");
        }
    }

    private static void ValidateOptionalText(string? value, string field)
    {
        if (value is not null) ValidateText(value, field);
    }

    private static void ValidateText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new LogRuleValidationException($"{field} cannot be empty.");
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new LogRuleValidationException($"{field} cannot have leading or trailing whitespace.");
        if (value.Length > LogRuleLimits.MaxTextLength)
            throw new LogRuleValidationException($"{field} is longer than {LogRuleLimits.MaxTextLength} characters.");
        if (value.Any(char.IsControl))
            throw new LogRuleValidationException($"{field} cannot contain control characters.");
    }

    // Text compared with a log line keeps its spaces. A log line never contains a line break.
    private static void ValidateLineText(
        string? value,
        string field,
        bool allowEmpty,
        int maxLength = LogRuleLimits.MaxTextLength)
    {
        if (value is null || (!allowEmpty && value.Length == 0))
            throw new LogRuleValidationException($"{field} cannot be empty.");
        if (value.Length > maxLength)
            throw new LogRuleValidationException($"{field} is longer than {maxLength} characters.");
        if (value.AsSpan().ContainsAny('\r', '\n'))
            throw new LogRuleValidationException($"{field} cannot contain a line break.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CaptureNamePattern();
}
