namespace Nvt.Replay.Core;

public enum EvidenceStatus
{
    Verified,
    Provisional,
    Todo,
}

public sealed record ConfigurationField(
    string Key,
    string DisplayName,
    bool Required,
    IReadOnlyList<string> Choices,
    string? HelpText = null);

public sealed record FormatDescriptor(
    string Id,
    string Family,
    string Version,
    string DisplayName,
    EvidenceStatus EvidenceStatus,
    IReadOnlyList<ConfigurationField> Configuration);

public sealed record DecodeConfiguration(
    string FormatId,
    IReadOnlyDictionary<string, string> Options);
