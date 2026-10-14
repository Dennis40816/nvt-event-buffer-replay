namespace Nvt.Replay.Core;

/// <summary>Indicates the evidence status associated with a format or profile.</summary>
public enum EvidenceStatus
{
    /// <summary>Marks format or profile evidence as verified.</summary>
    Verified,
    /// <summary>Marks format or profile evidence as provisional.</summary>
    Provisional,
    /// <summary>Marks format or profile evidence as awaiting work.</summary>
    Todo,
}

/// <summary>Describes a configuration option, its available choices, and guidance for setting it.</summary>
/// <param name="Key">The key that identifies the configuration option.</param>
/// <param name="DisplayName">The label used to display the configuration field.</param>
/// <param name="Required">Whether the configuration field requires a value.</param>
/// <param name="Choices">The values offered for the configuration field.</param>
/// <param name="HelpText">Optional guidance for setting the configuration field.</param>
public sealed record ConfigurationField(
    string Key,
    string DisplayName,
    bool Required,
    IReadOnlyList<string> Choices,
    string? HelpText = null);

/// <summary>Describes an Event Buffer format, its evidence status, and its configuration fields.</summary>
/// <param name="Id">The identifier used to select the format.</param>
/// <param name="Family">The Event Buffer family to which the format belongs.</param>
/// <param name="Version">The Event Buffer version represented by the format.</param>
/// <param name="DisplayName">The format name for display.</param>
/// <param name="EvidenceStatus">The evidence status associated with the format.</param>
/// <param name="Configuration">The configuration fields associated with the format.</param>
public sealed record FormatDescriptor(
    string Id,
    string Family,
    string Version,
    string DisplayName,
    EvidenceStatus EvidenceStatus,
    IReadOnlyList<ConfigurationField> Configuration);

/// <summary>Holds the selected format identifier and option values for decoding.</summary>
/// <param name="FormatId">The identifier of the format selected for decoding.</param>
/// <param name="Options">The configuration option values indexed by option key.</param>
public sealed record DecodeConfiguration(
    string FormatId,
    IReadOnlyDictionary<string, string> Options);
