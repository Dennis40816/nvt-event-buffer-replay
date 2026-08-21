using System.Text.Json;
using System.Text.Json.Serialization;
using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

public enum ReplayEvidenceKind
{
    KernelLog,
    FirmwareLog,
    Other,
}

public sealed record ReplayDecodeConfiguration(
    string EventBufferVersion,
    string? Desay97Profile,
    string SourceAdapterId,
    uint EventBufferBase = 0x99000,
    string? RegisterProfile = null,
    int TargetI2cAddress = 0x01);

public sealed record ReplayEvidenceReference(
    string Id,
    ReplayEvidenceKind Kind,
    string Path,
    string? Sha256 = null,
    string? Label = null);

public sealed record ReplayMarker(
    string Id,
    string Label,
    int StartLogicalIndex,
    int EndLogicalIndex,
    DateTimeOffset CreatedAt,
    string? Note = null,
    IReadOnlyList<string>? QaCaseIds = null,
    IReadOnlyList<ReplayEvidenceReference>? Evidence = null)
{
    [JsonIgnore]
    public bool IsRange => EndLogicalIndex != StartLogicalIndex;
}

public sealed record ReplaySidecarDocument(
    int SchemaVersion,
    string SourceSha256,
    string SourceFileName,
    ReplayDecodeConfiguration DecodeConfiguration,
    IReadOnlyList<ReplayMarker> Markers,
    IReadOnlyList<ReviewStateSnapshot> ReviewStates,
    IReadOnlyDictionary<string, bool> VisibilityPreferences,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ReplayEvidenceResolution(
    ReplayEvidenceReference Reference,
    string ResolvedPath,
    bool Exists,
    bool HashMatches);

public sealed record ReplaySidecarOpenResult(
    ReplaySidecarDocument Document,
    bool SourceHashMatches,
    bool DecodeConfigurationMatches,
    IReadOnlyList<ReplayEvidenceResolution> Evidence,
    IReadOnlyList<string> Warnings)
{
    public bool RequiresExplicitConfirmation => !SourceHashMatches || !DecodeConfigurationMatches;
}

public sealed class UnsupportedReplaySidecarVersionException(int version)
    : Exception($"Replay sidecar schema version {version} is newer than supported version {ReplaySidecarDocument.CurrentSchemaVersion}.")
{
    public int Version { get; } = version;
}

public sealed class ReplaySidecarStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task SaveAsync(
        string path,
        ReplaySidecarDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != ReplaySidecarDocument.CurrentSchemaVersion)
            throw new ArgumentException($"Only schema version {ReplaySidecarDocument.CurrentSchemaVersion} can be written.", nameof(document));
        var portable = MakeEvidencePathsPortable(Path.GetFullPath(path), document);
        await AtomicOutput.WriteAsync(
            path,
            (stream, token) => JsonSerializer.SerializeAsync(stream, portable, JsonOptions, token),
            cancellationToken);
    }

    public async Task<ReplaySidecarOpenResult> LoadAsync(
        string path,
        string expectedSourceSha256,
        ReplayDecodeConfiguration expectedConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceSha256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("schemaVersion", out var versionElement) || !versionElement.TryGetInt32(out var version))
            throw new InvalidDataException("Replay sidecar is missing an integer schemaVersion.");
        if (version > ReplaySidecarDocument.CurrentSchemaVersion)
            throw new UnsupportedReplaySidecarVersionException(version);
        if (version < 1) throw new InvalidDataException($"Replay sidecar schema version {version} is invalid.");
        var document = json.Deserialize<ReplaySidecarDocument>(JsonOptions) ?? throw new InvalidDataException("Replay sidecar contains no document.");
        var hashMatches = document.SourceSha256.Equals(expectedSourceSha256, StringComparison.OrdinalIgnoreCase);
        var configurationMatches = document.DecodeConfiguration == expectedConfiguration;
        var evidence = await ResolveEvidenceAsync(Path.GetFullPath(path), document.Markers, cancellationToken);
        var warnings = new List<string>();
        if (!hashMatches) warnings.Add("Capture SHA-256 does not match the sidecar; explicit operator confirmation is required.");
        if (!configurationMatches) warnings.Add("Decode configuration does not match the sidecar; explicit operator confirmation is required.");
        warnings.AddRange(evidence.Where(item => !item.Exists).Select(item => $"Evidence is missing: {item.Reference.Path}"));
        warnings.AddRange(evidence.Where(item => item.Exists && !item.HashMatches).Select(item => $"Evidence SHA-256 mismatch: {item.Reference.Path}"));
        return new ReplaySidecarOpenResult(document, hashMatches, configurationMatches, evidence, warnings);
    }

    private static ReplaySidecarDocument MakeEvidencePathsPortable(string sidecarPath, ReplaySidecarDocument document)
    {
        var directory = Path.GetDirectoryName(sidecarPath)!;
        var markers = document.Markers.Select(marker => marker with
        {
            Evidence = marker.Evidence?.Select(reference => reference with
            {
                Path = Path.IsPathFullyQualified(reference.Path)
                    ? Path.GetRelativePath(directory, reference.Path)
                    : reference.Path,
            }).ToArray(),
        }).ToArray();
        return document with { Markers = markers };
    }

    private static async Task<IReadOnlyList<ReplayEvidenceResolution>> ResolveEvidenceAsync(
        string sidecarPath,
        IEnumerable<ReplayMarker> markers,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(sidecarPath)!;
        var result = new List<ReplayEvidenceResolution>();
        foreach (var reference in markers.SelectMany(marker => marker.Evidence ?? []))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = Path.GetFullPath(reference.Path, directory);
            var exists = File.Exists(resolved);
            var hashMatches = exists && (reference.Sha256 is null ||
                string.Equals(reference.Sha256, await ComputeSha256Async(resolved, cancellationToken), StringComparison.OrdinalIgnoreCase));
            result.Add(new ReplayEvidenceResolution(reference, resolved, exists, hashMatches));
        }
        return result;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken));
    }
}
