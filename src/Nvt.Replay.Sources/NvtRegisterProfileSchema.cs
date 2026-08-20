using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nvt.Replay.Sources;

public enum NvtRegisterRegionV1
{
    EventBuffer,
    CommonBuffer,
    History,
}

public sealed record NvtRegisterAddressMapV1(
    uint EventBufferBase,
    uint CommonBufferBase,
    uint HistoryBase);

public sealed record NvtFirmwareScopeV1(string Expression);

public sealed record NvtRegisterDefinitionV1(
    NvtRegisterRegionV1 Region,
    uint Offset,
    string Name,
    string Meaning = "raw_only");

public sealed record NvtCommandDefinitionV1(
    NvtRegisterRegionV1 Region,
    uint RegisterOffset,
    byte Opcode,
    string Name,
    bool Confirmed);

/// <summary>
/// Versioned, declarative register-profile file. The SHA-256 covers the
/// canonical document content except for the <see cref="ContentSha256"/> field
/// itself. It never carries executable code or plugin references.
/// </summary>
public sealed record NvtRegisterProfileDocumentV1(
    int SchemaVersion,
    string ProfileId,
    string DisplayName,
    string IcFamily,
    IReadOnlyList<string> IcScope,
    NvtFirmwareScopeV1? FirmwareScope,
    NvtRegisterAddressMapV1 Addresses,
    IReadOnlyList<NvtRegisterDefinitionV1> Registers,
    IReadOnlyList<NvtCommandDefinitionV1> Commands,
    string ContentSha256)
{
    [JsonIgnore]
    public NvtRegisterProfileIdentity Identity => new(ProfileId, ContentSha256);
}

public sealed class NvtRegisterProfileValidationException(string message, Exception? innerException = null)
    : FormatException(message, innerException);

/// <summary>
/// Creates, validates, and persists the reserved v1 custom-profile schema.
/// Loading a file does not register or activate it in any decoder.
/// </summary>
public static partial class NvtRegisterProfileFile
{
    public const int CurrentSchemaVersion = 1;
    private const uint MaximumNvtAddress = 0xFFFFFF;

    private static readonly JsonSerializerOptions ReadOptions = CreateReadOptions();

    public static NvtRegisterProfileDocumentV1 CreateV1(
        string profileId,
        string displayName,
        string icFamily,
        IReadOnlyList<string> icScope,
        NvtFirmwareScopeV1? firmwareScope,
        NvtRegisterAddressMapV1 addresses,
        IReadOnlyList<NvtRegisterDefinitionV1> registers,
        IReadOnlyList<NvtCommandDefinitionV1> commands)
    {
        var document = new NvtRegisterProfileDocumentV1(
            CurrentSchemaVersion,
            profileId,
            displayName,
            icFamily,
            icScope,
            firmwareScope,
            addresses,
            registers,
            commands,
            string.Empty);
        ValidateContent(document);
        return document with { ContentSha256 = ComputeContentSha256(document) };
    }

    public static NvtRegisterProfileDocumentV1 FromBuiltIn(NvtRegisterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return CreateV1(
            $"builtin.{profile.IcFamily.Replace('/', '-')}",
            profile.IcFamily,
            profile.IcFamily,
            [profile.IcFamily],
            firmwareScope: null,
            new NvtRegisterAddressMapV1(
                profile.EventBufferBase,
                profile.CommonBufferBase,
                profile.HistoryBase),
            BuiltInRegisters,
            BuiltInCommands);
    }

    public static NvtRegisterProfile ToRuntimeProfile(NvtRegisterProfileDocumentV1 document)
    {
        Validate(document);
        return new NvtRegisterProfile(
            document.IcFamily,
            document.Addresses.EventBufferBase,
            document.Addresses.CommonBufferBase,
            document.Addresses.HistoryBase);
    }

    public static string SerializeCanonical(NvtRegisterProfileDocumentV1 document)
    {
        Validate(document);
        var bytes = WriteCanonical(document, includeIdentity: true, indented: true);
        return Encoding.UTF8.GetString(bytes) + "\n";
    }

    public static NvtRegisterProfileDocumentV1 Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new NvtRegisterProfileValidationException("The register-profile root must be a JSON object.");
            if (!parsed.RootElement.TryGetProperty("schemaVersion", out var versionElement) ||
                !versionElement.TryGetInt32(out var schemaVersion))
            {
                throw new NvtRegisterProfileValidationException("schemaVersion is required and must be an integer.");
            }
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new NvtRegisterProfileValidationException(
                    $"Unknown register-profile schemaVersion {schemaVersion}; supported version is {CurrentSchemaVersion}.");
            }

            var document = JsonSerializer.Deserialize<NvtRegisterProfileDocumentV1>(json, ReadOptions) ??
                throw new NvtRegisterProfileValidationException("The register-profile document is empty.");
            Validate(document);
            return document;
        }
        catch (NvtRegisterProfileValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new NvtRegisterProfileValidationException(
                $"Invalid register-profile JSON: {exception.Message}",
                exception);
        }
    }

    public static async Task<NvtRegisterProfileDocumentV1> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(json);
    }

    public static async Task SaveAsync(
        string path,
        NvtRegisterProfileDocumentV1 document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = SerializeCanonical(document);
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
    }

    public static void Validate(NvtRegisterProfileDocumentV1 document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateContent(document);
        if (!Sha256Pattern().IsMatch(document.ContentSha256))
        {
            throw new NvtRegisterProfileValidationException(
                "contentSha256 must be a lowercase 64-character SHA-256 value.");
        }

        var expected = ComputeContentSha256(document);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(document.ContentSha256)))
        {
            throw new NvtRegisterProfileValidationException(
                $"contentSha256 does not match the canonical profile content; expected {expected}.");
        }
    }

    public static string ComputeContentSha256(NvtRegisterProfileDocumentV1 document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateContent(document);
        return Convert.ToHexStringLower(SHA256.HashData(
            WriteCanonical(document, includeIdentity: false, indented: false)));
    }

    private static void ValidateContent(NvtRegisterProfileDocumentV1 document)
    {
        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new NvtRegisterProfileValidationException(
                $"Unknown register-profile schemaVersion {document.SchemaVersion}; supported version is {CurrentSchemaVersion}.");
        }
        ValidateToken(document.ProfileId, nameof(document.ProfileId), ProfileIdPattern());
        ValidateText(document.DisplayName, nameof(document.DisplayName));
        ValidateText(document.IcFamily, nameof(document.IcFamily));
        if (document.IcScope is null)
            throw new NvtRegisterProfileValidationException("icScope is required.");
        if (document.IcScope.Count == 0)
            throw new NvtRegisterProfileValidationException("icScope must contain at least one non-empty target.");
        ValidateUniqueTexts(document.IcScope, "icScope");
        if (document.FirmwareScope is { } firmware)
            ValidateText(firmware.Expression, "firmwareScope.expression");

        if (document.Addresses is null)
            throw new NvtRegisterProfileValidationException("addresses is required.");
        ValidateBase(document.Addresses.EventBufferBase, "addresses.eventBufferBase");
        ValidateBase(document.Addresses.CommonBufferBase, "addresses.commonBufferBase");
        ValidateBase(document.Addresses.HistoryBase, "addresses.historyBase");

        if (document.Registers is null)
            throw new NvtRegisterProfileValidationException("registers is required.");
        if (document.Commands is null)
            throw new NvtRegisterProfileValidationException("commands is required.");
        var registerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registerOffsets = new HashSet<(NvtRegisterRegionV1 Region, uint Offset)>();
        foreach (var register in document.Registers)
        {
            if (register is null)
                throw new NvtRegisterProfileValidationException("registers cannot contain null entries.");
            ValidateText(register.Name, "registers[].name");
            ValidateText(register.Meaning, "registers[].meaning");
            ValidateResolvedAddress(document.Addresses, register.Region, register.Offset, "registers[].offset");
            if (!registerNames.Add(register.Name.Trim()))
                throw new NvtRegisterProfileValidationException($"Duplicate register name '{register.Name}'.");
            if (!registerOffsets.Add((register.Region, register.Offset)))
                throw new NvtRegisterProfileValidationException(
                    $"Duplicate register offset {RegionText(register.Region)}+0x{register.Offset:X}.");
        }

        var commandNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commandKeys = new HashSet<(NvtRegisterRegionV1 Region, uint Offset, byte Opcode)>();
        foreach (var command in document.Commands)
        {
            if (command is null)
                throw new NvtRegisterProfileValidationException("commands cannot contain null entries.");
            ValidateText(command.Name, "commands[].name");
            ValidateResolvedAddress(document.Addresses, command.Region, command.RegisterOffset, "commands[].registerOffset");
            if (!commandNames.Add(command.Name.Trim()))
                throw new NvtRegisterProfileValidationException($"Duplicate command name '{command.Name}'.");
            if (!commandKeys.Add((command.Region, command.RegisterOffset, command.Opcode)))
            {
                throw new NvtRegisterProfileValidationException(
                    $"Duplicate command opcode 0x{command.Opcode:X2} at " +
                    $"{RegionText(command.Region)}+0x{command.RegisterOffset:X}.");
            }
        }
    }

    private static void ValidateToken(string value, string field, Regex pattern)
    {
        ValidateText(value, field);
        if (!pattern.IsMatch(value))
            throw new NvtRegisterProfileValidationException(
                $"{field} must use 1-128 ASCII letters, digits, '.', '_' or '-'.");
    }

    private static void ValidateText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new NvtRegisterProfileValidationException($"{field} cannot be empty.");
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new NvtRegisterProfileValidationException($"{field} cannot have leading or trailing whitespace.");
    }

    private static void ValidateUniqueTexts(IReadOnlyList<string> values, string field)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            ValidateText(value, $"{field}[]");
            if (!unique.Add(value.Trim()))
                throw new NvtRegisterProfileValidationException($"{field} contains duplicate target '{value}'.");
        }
    }

    private static void ValidateBase(uint address, string field)
    {
        if (address > MaximumNvtAddress)
            throw new NvtRegisterProfileValidationException($"{field} exceeds the 24-bit NVT address space.");
    }

    private static void ValidateResolvedAddress(
        NvtRegisterAddressMapV1 addresses,
        NvtRegisterRegionV1 region,
        uint offset,
        string field)
    {
        var baseAddress = region switch
        {
            NvtRegisterRegionV1.EventBuffer => addresses.EventBufferBase,
            NvtRegisterRegionV1.CommonBuffer => addresses.CommonBufferBase,
            NvtRegisterRegionV1.History => addresses.HistoryBase,
            _ => throw new NvtRegisterProfileValidationException($"Unknown register region '{region}'."),
        };
        if (offset > MaximumNvtAddress - baseAddress)
        {
            throw new NvtRegisterProfileValidationException(
                $"{field} resolves outside the 24-bit NVT address space.");
        }
    }

    private static byte[] WriteCanonical(
        NvtRegisterProfileDocumentV1 document,
        bool includeIdentity,
        bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WriteString("profileId", document.ProfileId);
            writer.WriteString("displayName", document.DisplayName);
            writer.WriteString("icFamily", document.IcFamily);
            writer.WritePropertyName("icScope");
            writer.WriteStartArray();
            foreach (var scope in document.IcScope.Order(StringComparer.Ordinal)) writer.WriteStringValue(scope);
            writer.WriteEndArray();
            writer.WritePropertyName("firmwareScope");
            if (document.FirmwareScope is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("expression", document.FirmwareScope.Expression);
                writer.WriteEndObject();
            }
            writer.WritePropertyName("addresses");
            writer.WriteStartObject();
            writer.WriteNumber("eventBufferBase", document.Addresses.EventBufferBase);
            writer.WriteNumber("commonBufferBase", document.Addresses.CommonBufferBase);
            writer.WriteNumber("historyBase", document.Addresses.HistoryBase);
            writer.WriteEndObject();
            writer.WritePropertyName("registers");
            writer.WriteStartArray();
            foreach (var register in document.Registers
                         .OrderBy(item => item.Region)
                         .ThenBy(item => item.Offset)
                         .ThenBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("region", RegionText(register.Region));
                writer.WriteNumber("offset", register.Offset);
                writer.WriteString("name", register.Name);
                writer.WriteString("meaning", register.Meaning);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("commands");
            writer.WriteStartArray();
            foreach (var command in document.Commands
                         .OrderBy(item => item.Region)
                         .ThenBy(item => item.RegisterOffset)
                         .ThenBy(item => item.Opcode)
                         .ThenBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("region", RegionText(command.Region));
                writer.WriteNumber("registerOffset", command.RegisterOffset);
                writer.WriteNumber("opcode", command.Opcode);
                writer.WriteString("name", command.Name);
                writer.WriteBoolean("confirmed", command.Confirmed);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (includeIdentity) writer.WriteString("contentSha256", document.ContentSha256);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static JsonSerializerOptions CreateReadOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static string RegionText(NvtRegisterRegionV1 region) => region switch
    {
        NvtRegisterRegionV1.EventBuffer => "eventBuffer",
        NvtRegisterRegionV1.CommonBuffer => "commonBuffer",
        NvtRegisterRegionV1.History => "history",
        _ => throw new NvtRegisterProfileValidationException($"Unknown register region '{region}'."),
    };

    private static IReadOnlyList<NvtRegisterDefinitionV1> BuiltInRegisters { get; } =
    [
        new(NvtRegisterRegionV1.EventBuffer, 0x00, "Event Buffer"),
        new(NvtRegisterRegionV1.EventBuffer, 0x50, "FW Command"),
        new(NvtRegisterRegionV1.EventBuffer, 0x60, "FW State"),
        new(NvtRegisterRegionV1.EventBuffer, 0x70, "Frame Counter"),
        new(NvtRegisterRegionV1.EventBuffer, 0x76, "DP Version"),
        new(NvtRegisterRegionV1.EventBuffer, 0x78, "TP FW Version"),
        new(NvtRegisterRegionV1.CommonBuffer, 0x00, "Common Buffer"),
        new(NvtRegisterRegionV1.History, 0x00, "History"),
    ];

    private static IReadOnlyList<NvtCommandDefinitionV1> BuiltInCommands { get; } =
    [
        new(NvtRegisterRegionV1.EventBuffer, 0x50, 0x23, "Baseline Reset", Confirmed: true),
    ];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
