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

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions ReadOptions = CreateReadOptions();

    private static readonly JsonDocumentOptions DocumentOptions = new()
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
            return Parse(StrictUtf8.GetBytes(json), cancellationToken);
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
            var document = JsonSerializer.Deserialize<LogRuleDocumentV1>(utf8Json, ReadOptions) ??
                throw new LogRuleValidationException("The rule file is empty.");
            Validate(document, cancellationToken);
            return document;
        }
        catch (JsonException exception)
        {
            throw new LogRuleValidationException($"Invalid rule file JSON: {exception.Message}", exception);
        }
    }

    /// <summary>Validates content and verifies that <c>contentSha256</c> matches the canonical content.</summary>
    public static void Validate(LogRuleDocumentV1 document, CancellationToken cancellationToken = default)
    {
        LogRuleValidator.Validate(document, cancellationToken);
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

    /// <summary>Computes the lowercase SHA-256 of the compact canonical bytes without <c>contentSha256</c>. Validates the content first.</summary>
    public static string ComputeContentSha256(LogRuleDocumentV1 document, CancellationToken cancellationToken = default)
    {
        LogRuleValidator.Validate(document, cancellationToken);
        return HashCanonical(document);
    }

    /// <summary>Returns a copy of the document whose <c>contentSha256</c> is computed from its content.</summary>
    public static LogRuleDocumentV1 Seal(LogRuleDocumentV1 document, CancellationToken cancellationToken = default) =>
        document with { ContentSha256 = ComputeContentSha256(document, cancellationToken) };

    /// <summary>Validates the document and returns the saved bytes: indented canonical JSON in UTF-8 without a byte-order mark, LF line breaks and one final LF.</summary>
    public static byte[] SerializeCanonical(LogRuleDocumentV1 document, CancellationToken cancellationToken = default)
    {
        Validate(document, cancellationToken);
        return [.. LogRuleCanonicalWriter.Write(document, includeHash: true, indented: true), (byte)'\n'];
    }

    private static bool IsLowerHex64(string value) =>
        value.Length == 64 && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string HashCanonical(LogRuleDocumentV1 document) =>
        Convert.ToHexStringLower(SHA256.HashData(
            LogRuleCanonicalWriter.Write(document, includeHash: false, indented: false)));

    // The envelope is checked before deserialization so a register profile or a newer file gets a clear error.
    private static void CheckEnvelope(ReadOnlySpan<byte> utf8Json)
    {
        using var parsed = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
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
