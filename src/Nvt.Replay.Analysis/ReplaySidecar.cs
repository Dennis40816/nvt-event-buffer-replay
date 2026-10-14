using System.Text.Json;
using System.Text.Json.Serialization;
using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

/// <summary>Classifies the external evidence referenced by a replay marker.</summary>
public enum ReplayEvidenceKind
{
    /// <summary>Identifies evidence classified as a kernel log.</summary>
    KernelLog,
    /// <summary>Identifies evidence classified as a firmware log.</summary>
    FirmwareLog,
    /// <summary>Identifies evidence classified outside the kernel and firmware log categories.</summary>
    Other,
}

/// <summary>Records the Event Buffer format, source adapter, register profile, and target address used for decoding.</summary>
/// <param name="EventBufferVersion">The Event Buffer version identifier used for decoding.</param>
/// <param name="Desay97Profile">The selected Desay 0x97 profile identifier, when supplied.</param>
/// <param name="SourceAdapterId">The identifier of the source adapter used for the capture.</param>
/// <param name="EventBufferBase">The base address used to locate Event Buffer data.</param>
/// <param name="RegisterProfile">The selected IC register profile identifier, when supplied.</param>
/// <param name="TargetI2cAddress">The I2C slave address whose Event Buffer records are decoded.</param>
public sealed record ReplayDecodeConfiguration(
    string EventBufferVersion,
    string? Desay97Profile,
    string SourceAdapterId,
    uint EventBufferBase = 0x99000,
    string? RegisterProfile = null,
    int TargetI2cAddress = 0x01);

/// <summary>Identifies an external evidence file with its kind, path, optional hash, and optional label.</summary>
/// <param name="Id">The identifier assigned to the evidence reference.</param>
/// <param name="Kind">The category of the evidence file.</param>
/// <param name="Path">The evidence file path, resolved relative to the sidecar directory when not absolute.</param>
/// <param name="Sha256">The expected SHA-256 hash, or null when no hash verification is requested.</param>
/// <param name="Label">The optional label assigned to the evidence reference.</param>
public sealed record ReplayEvidenceReference(
    string Id,
    ReplayEvidenceKind Kind,
    string Path,
    string? Sha256 = null,
    string? Label = null);

/// <summary>Stores a labeled logical frame position or range with notes, QA case references, and external evidence.</summary>
/// <param name="Id">The identifier assigned to the replay marker.</param>
/// <param name="Label">The label assigned to the replay marker.</param>
/// <param name="StartLogicalIndex">The logical frame index at the start of the marker.</param>
/// <param name="EndLogicalIndex">The logical frame index at the end of the marker.</param>
/// <param name="CreatedAt">The recorded creation time of the marker.</param>
/// <param name="Note">The optional note attached to the marker.</param>
/// <param name="QaCaseIds">The optional QA case identifiers attached to the marker.</param>
/// <param name="Evidence">The optional external evidence references attached to the marker.</param>
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
    /// <summary>Gets whether the marker's end logical frame index differs from its start index.</summary>
    [JsonIgnore]
    public bool IsRange => EndLogicalIndex != StartLogicalIndex;
}

/// <summary>Stores capture identity, decode settings, markers, review states, visibility preferences, and update time.</summary>
/// <param name="SchemaVersion">The schema version of the sidecar document.</param>
/// <param name="SourceSha256">The SHA-256 hash identifying the source capture.</param>
/// <param name="SourceFileName">The source capture file name stored in the sidecar.</param>
/// <param name="DecodeConfiguration">The decode configuration recorded for the capture.</param>
/// <param name="Markers">The replay markers stored in the sidecar.</param>
/// <param name="ReviewStates">The review state snapshots stored in the sidecar.</param>
/// <param name="VisibilityPreferences">The named visibility settings stored in the sidecar.</param>
/// <param name="UpdatedAt">The recorded update time of the sidecar document.</param>
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
    /// <summary>Defines the replay sidecar schema version supported for reading and writing.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Reports an evidence reference's resolved path, file existence, hash match, and any verification limitation.</summary>
/// <param name="Reference">The evidence reference whose file was resolved.</param>
/// <param name="ResolvedPath">The absolute evidence path resolved from the sidecar directory.</param>
/// <param name="Exists">Whether the resolved evidence file exists.</param>
/// <param name="HashMatches">Whether the file exists and either matches the expected hash or has no expected hash.</param>
/// <param name="NotVerifiedReason">The reason an existing file could not be hashed, when applicable.</param>
public sealed record ReplayEvidenceResolution(
    ReplayEvidenceReference Reference,
    string ResolvedPath,
    bool Exists,
    bool HashMatches,
    string? NotVerifiedReason = null);

/// <summary>Reports a loaded sidecar, capture identity and decode checks, resolved evidence, and warnings.</summary>
/// <param name="Document">The sidecar document loaded from JSON.</param>
/// <param name="SourceHashMatches">Whether the stored capture hash matches the expected hash, ignoring case.</param>
/// <param name="DecodeConfigurationMatches">Whether the stored decode configuration equals the expected configuration.</param>
/// <param name="Evidence">The resolution and verification results for marker evidence references.</param>
/// <param name="Warnings">The warnings about capture mismatches and missing, unverified, or hash-mismatched evidence.</param>
public sealed record ReplaySidecarOpenResult(
    ReplaySidecarDocument Document,
    bool SourceHashMatches,
    bool DecodeConfigurationMatches,
    IReadOnlyList<ReplayEvidenceResolution> Evidence,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Gets whether the capture hash or decode configuration mismatch requires explicit operator confirmation.</summary>
    public bool RequiresExplicitConfirmation => !SourceHashMatches || !DecodeConfigurationMatches;
}

/// <summary>Signals that a replay sidecar uses a schema version newer than the supported version.</summary>
public sealed class UnsupportedReplaySidecarVersionException(int version)
    : Exception($"Replay sidecar schema version {version} is newer than supported version {ReplaySidecarDocument.CurrentSchemaVersion}.")
{
    /// <summary>Gets the unsupported replay sidecar schema version that caused the exception.</summary>
    public int Version { get; } = version;
}

/// <summary>Saves and loads replay sidecars with capture identity checks and external evidence verification.</summary>
public sealed class ReplaySidecarStore
{
    private readonly Func<string, CancellationToken, Task<string>> hashFile;

    /// <summary>Creates a sidecar store that computes evidence hashes with the file SHA-256 hasher.</summary>
    public ReplaySidecarStore() : this((path, token) => FileHasher.ComputeSha256Async(path, token))
    {
    }

    // Tests inject the hash step to use a small size limit or a file that changes during the read.
    internal ReplaySidecarStore(Func<string, CancellationToken, Task<string>> hashFile)
    {
        this.hashFile = hashFile;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Atomically writes a current-schema sidecar as JSON with evidence paths relative to the sidecar directory.</summary>
    public async Task SaveAsync(
        string path,
        ReplaySidecarDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != ReplaySidecarDocument.CurrentSchemaVersion)
            throw new ArgumentException($"Only schema version {ReplaySidecarDocument.CurrentSchemaVersion} can be written.", nameof(document));
        var portable = MakeEvidencePathsPortable(Path.GetFullPath(path), document);
        await Nvt.Core.IO.AtomicOutput.WriteAsync(
            path,
            (stream, token) => JsonSerializer.SerializeAsync(stream, portable, JsonOptions, token),
            cancellationToken);
    }

    /// <summary>Loads a sidecar, checks its schema and capture identity, and resolves and verifies referenced evidence.</summary>
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
        warnings.AddRange(evidence.Where(item => item.Exists && item.NotVerifiedReason is not null).Select(item => $"Evidence not verified: {item.Reference.Path} ({item.NotVerifiedReason})"));
        warnings.AddRange(evidence.Where(item => item.Exists && item.NotVerifiedReason is null && !item.HashMatches).Select(item => $"Evidence SHA-256 mismatch: {item.Reference.Path}"));
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

    private async Task<IReadOnlyList<ReplayEvidenceResolution>> ResolveEvidenceAsync(
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
            var hashMatches = exists && reference.Sha256 is null;
            string? notVerifiedReason = null;
            if (exists && reference.Sha256 is not null)
            {
                try
                {
                    hashMatches = string.Equals(
                        reference.Sha256,
                        await hashFile(resolved, cancellationToken),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch (FileNotHashableException exception)
                {
                    // An evidence file that is too large or still growing must not block the review from opening.
                    notVerifiedReason = exception.Reason;
                }
            }
            result.Add(new ReplayEvidenceResolution(reference, resolved, exists, hashMatches, notVerifiedReason));
        }
        return result;
    }
}
