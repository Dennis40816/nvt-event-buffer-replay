// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nvt.Replay.Sources.LogRules;

/// <summary>
/// Parses, validates, hashes and serializes version 1 log rule files. These functions read no file and
/// run no rule. Reading the bytes of a file is the job of the caller.
/// </summary>
internal static class LogRuleFile
{
    /// <summary>The only supported schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The value of <c>fileKind</c>. A register profile has no such property, so each loader rejects the other kind.</summary>
    public const string FileKind = "logRules";

    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions _readOptions = CreateReadOptions();

    private static readonly JsonDocumentOptions _documentOptions = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = 32,
    };

    /// <summary>Parses rule file JSON text and validates its schema, content and SHA-256.</summary>
    public static LogRuleDocumentV1 Parse(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return Parse(_strictUtf8.GetBytes(json), cancellationToken);
        }
        catch (EncoderFallbackException exception)
        {
            throw new LogRuleValidationException("The rule file text is not valid Unicode.", exception);
        }
    }

    /// <summary>Parses rule file bytes and validates its schema, content and SHA-256. A byte-order mark is not accepted.</summary>
    public static LogRuleDocumentV1 Parse(ReadOnlySpan<byte> utf8Json, CancellationToken cancellationToken = default)
    {
        if (utf8Json.Length > LogRuleLimits.MaxFileBytes)
            throw new LogRuleValidationException($"The rule file is larger than {LogRuleLimits.MaxFileBytes} bytes.");
        try
        {
            CheckEnvelope(utf8Json);
            var document = JsonSerializer.Deserialize<LogRuleDocumentV1>(utf8Json, _readOptions) ??
                throw new LogRuleValidationException("The rule file is empty.");
            var admitted = LogRuleAdmission.Freeze(document);
            ValidateFrozen(admitted, cancellationToken);
            return admitted;
        }
        catch (JsonException exception)
        {
            throw new LogRuleValidationException($"Invalid rule file JSON: {exception.Message}", exception);
        }
    }

    /// <summary>Validates content, checks the size of the saved form and verifies that <c>contentSha256</c> matches the canonical content.</summary>
    public static void Validate(LogRuleDocumentV1 document, CancellationToken cancellationToken = default) =>
        ValidateFrozen(LogRuleAdmission.Freeze(document), cancellationToken);

    /// <summary>Computes the lowercase SHA-256 of the compact canonical bytes without <c>contentSha256</c>. Validates the content first.</summary>
    public static string ComputeContentSha256(LogRuleDocumentV1 document, CancellationToken cancellationToken = default) =>
        ComputeHash(LogRuleAdmission.Freeze(document), cancellationToken);

    /// <summary>
    /// Returns a copy of the document whose <c>contentSha256</c> is computed from its content. The copy owns
    /// immutable collections, so later changes to the source collections cannot change the sealed content.
    /// </summary>
    public static LogRuleDocumentV1 Seal(LogRuleDocumentV1 document, CancellationToken cancellationToken = default)
    {
        var snapshot = LogRuleAdmission.Freeze(document);
        return snapshot with { ContentSha256 = ComputeHash(snapshot, cancellationToken) };
    }

    /// <summary>Validates the document and returns the saved bytes: indented canonical JSON in UTF-8 without a byte-order mark, LF line breaks and one final LF.</summary>
    public static byte[] SerializeCanonical(LogRuleDocumentV1 document, CancellationToken cancellationToken = default)
    {
        var snapshot = LogRuleAdmission.Freeze(document);
        ValidateFrozen(snapshot, cancellationToken);
        return [.. LogRuleCanonicalWriter.Write(snapshot, includeHash: true, indented: true), (byte)'\n'];
    }

    private static void ValidateFrozen(LogRuleDocumentV1 document, CancellationToken cancellationToken)
    {
        LogRuleValidator.Validate(document, cancellationToken);
        CheckSavedSize(document);
        if (document.ContentSha256 is null || !IsLowerHex64(document.ContentSha256))
            throw new LogRuleValidationException("contentSha256 must be a lowercase 64-character SHA-256 value.");

        var expected = HashCanonical(document);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(document.ContentSha256)))
        {
            throw new LogRuleValidationException(
                $"contentSha256 does not match the canonical content; expected {expected}.");
        }
    }

    private static string ComputeHash(LogRuleDocumentV1 document, CancellationToken cancellationToken)
    {
        LogRuleValidator.Validate(document, cancellationToken);
        CheckSavedSize(document);
        return HashCanonical(document);
    }

    // Parse refuses a file above the limit, so a document must not be admitted or returned when its saved
    // form is above it. Indentation can make the saved form much larger than the compact input.
    private static void CheckSavedSize(LogRuleDocumentV1 document)
    {
        var sized = document.ContentSha256 is { } hash && IsLowerHex64(hash)
            ? document
            : document with { ContentSha256 = new string('0', 64) };
        var length = LogRuleCanonicalWriter.Write(sized, includeHash: true, indented: true).Length + 1;
        if (length > LogRuleLimits.MaxFileBytes)
        {
            throw new LogRuleValidationException(
                $"The rule file would be {length} bytes once saved in canonical form; the limit is {LogRuleLimits.MaxFileBytes} bytes.");
        }
    }

    private static bool IsLowerHex64(string value) =>
        value.Length == 64 && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string HashCanonical(LogRuleDocumentV1 document) =>
        Convert.ToHexStringLower(SHA256.HashData(
            LogRuleCanonicalWriter.Write(document, includeHash: false, indented: false)));

    // The envelope is checked before deserialization so a register profile or a newer file gets a clear error.
    private static void CheckEnvelope(ReadOnlySpan<byte> utf8Json)
    {
        using var parsed = JsonDocument.Parse(utf8Json.ToArray(), _documentOptions);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new LogRuleValidationException("The rule file root must be a JSON object.");
        if (!root.TryGetProperty("fileKind", out var kind) ||
            kind.ValueKind != JsonValueKind.String ||
            !string.Equals(kind.GetString(), FileKind, StringComparison.Ordinal))
        {
            throw new LogRuleValidationException(
                $"fileKind must be '{FileKind}'. This is not a log rule file.");
        }
        if (!root.TryGetProperty("schemaVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var version))
        {
            throw new LogRuleValidationException("schemaVersion is required and must be an integer.");
        }
        if (version != CurrentSchemaVersion)
        {
            throw new LogRuleValidationException(
                $"Unknown log-rule schemaVersion {version}; supported version is {CurrentSchemaVersion}.");
        }
    }

    private static JsonSerializerOptions CreateReadOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            RespectNullableAnnotations = true,
            MaxDepth = 32,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
