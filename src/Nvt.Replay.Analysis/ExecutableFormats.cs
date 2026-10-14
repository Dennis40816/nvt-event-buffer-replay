using Nvt.Replay.Core;
using Nvt.Replay.Formats;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Analysis;

/// <summary>Specifies an Event Buffer version, optional decode profiles, and the target I2C address.</summary>
/// <param name="EventBufferVersion">The requested Event Buffer version text.</param>
/// <param name="RegisterProfile">The requested IC register profile, or null when unspecified.</param>
/// <param name="Desay97Profile">The requested Desay 0x97 profile, or null when unspecified.</param>
/// <param name="TargetI2cAddress">The seven-bit I2C slave address whose Event Buffer records are decoded.</param>
public sealed record FormatDecodeRequest(
    string EventBufferVersion,
    string? RegisterProfile = null,
    string? Desay97Profile = null,
    int TargetI2cAddress = 0x01);

/// <summary>Provides source provenance and diagnostics from a format inspection.</summary>
public interface IFormatInspectionReport
{
    /// <summary>Gets the path of the inspected source capture.</summary>
    string SourcePath { get; }
    /// <summary>Gets the SHA-256 hash of the inspected source capture.</summary>
    string SourceSha256 { get; }
    /// <summary>Gets the diagnostics produced by the format inspection.</summary>
    IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }
}

/// <summary>Defines a selected format and the register profile and I2C address used to decode a capture.</summary>
public abstract class ExecutableFormatSelection
{
    /// <summary>Initializes a format selection with its descriptor, register profile, and target I2C address.</summary>
    protected ExecutableFormatSelection(
        FormatDescriptor descriptor,
        string? registerProfile,
        int targetI2cAddress)
    {
        Descriptor = descriptor;
        RegisterProfile = registerProfile;
        TargetI2cAddress = targetI2cAddress;
    }

    /// <summary>Gets the descriptor for the selected Event Buffer format.</summary>
    public FormatDescriptor Descriptor { get; }
    /// <summary>Gets the selected IC register profile, or null when none is specified.</summary>
    public string? RegisterProfile { get; }
    /// <summary>Gets the target I2C slave address used to select records for decoding.</summary>
    public int TargetI2cAddress { get; }
    /// <summary>Gets the display label identifying the selected format and any format-specific profile.</summary>
    public abstract string DisplayIdentity { get; }

    /// <summary>Decodes a capture with the selected format and produces inspection, replay, and configuration data.</summary>
    public abstract ExecutableFormatDecodeResult Decode(
        CaptureSession capture,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the capture with the selected register profile, reusing it when the profile already matches.</summary>
    protected CaptureSession ConfigureCapture(
        CaptureSession capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return string.Equals(capture.RegisterProfile, RegisterProfile, StringComparison.OrdinalIgnoreCase)
            ? capture
            : capture.WithRegisterProfile(RegisterProfile, cancellationToken);
    }
}

/// <summary>Decodes captures using a selected Common Event Buffer version.</summary>
public sealed class CommonFormatSelection : ExecutableFormatSelection
{
    internal CommonFormatSelection(
        FormatDescriptor descriptor,
        CommonEventBufferVersion version,
        string? registerProfile,
        int targetI2cAddress)
        : base(descriptor, registerProfile, targetI2cAddress) => Version = version;

    /// <summary>Gets the Common Event Buffer version passed to the decoder.</summary>
    public CommonEventBufferVersion Version { get; }
    /// <inheritdoc />
    public override string DisplayIdentity => $"Common {Descriptor.Version}";

    /// <inheritdoc />
    public override ExecutableFormatDecodeResult Decode(
        CaptureSession capture,
        CancellationToken cancellationToken = default)
    {
        var configured = ConfigureCapture(capture, cancellationToken);
        var report = configured.DecodeCommon(Version, TargetI2cAddress, cancellationToken);
        var replay = new CommonReplaySession(report.Frames);
        var eventBufferBase = NvtRegisterCatalog.FindProfile(configured.RegisterProfile)?.EventBufferBase ?? 0;
        var configuration = new ReplayDecodeConfiguration(
            Descriptor.Version,
            null,
            configured.Probe.AdapterId,
            eventBufferBase,
            configured.RegisterProfile,
            TargetI2cAddress);
        return new CommonFormatDecodeResult(this, configured, report, replay, configuration);
    }
}

/// <summary>Decodes Desay 0x97 captures using an explicit profile and IC-specific Event Buffer base.</summary>
public sealed class Desay97FormatSelection : ExecutableFormatSelection
{
    internal Desay97FormatSelection(
        FormatDescriptor descriptor,
        Desay97Profile profile,
        NvtRegisterProfile registerProfile,
        int targetI2cAddress)
        : base(descriptor, registerProfile.IcFamily, targetI2cAddress)
    {
        Profile = profile;
        EventBufferBase = registerProfile.EventBufferBase;
    }

    /// <summary>Gets the Desay 0x97 profile passed to the decoder.</summary>
    public Desay97Profile Profile { get; }
    /// <summary>Gets the Event Buffer base address from the selected IC register profile.</summary>
    public uint EventBufferBase { get; }
    /// <summary>Gets Standard or Benz Palm as the display name for the selected Desay 0x97 profile.</summary>
    public string ProfileDisplayName => Profile == Desay97Profile.Standard ? "Standard" : "Benz Palm";
    /// <inheritdoc />
    public override string DisplayIdentity => $"Desay 0x97 / {ProfileDisplayName}";

    /// <inheritdoc />
    public override ExecutableFormatDecodeResult Decode(
        CaptureSession capture,
        CancellationToken cancellationToken = default)
    {
        var configured = ConfigureCapture(capture, cancellationToken);
        var report = configured.DecodeDesay97(Profile, EventBufferBase, TargetI2cAddress, cancellationToken);
        var replay = new Desay97ReplaySession(report.Frames);
        var configuration = new ReplayDecodeConfiguration(
            "0x97",
            ProfileDisplayName,
            configured.Probe.AdapterId,
            EventBufferBase,
            configured.RegisterProfile,
            TargetI2cAddress);
        return new Desay97FormatDecodeResult(this, configured, report, replay, configuration);
    }
}

/// <summary>Holds the selected format, configured capture, inspection report, replay, and combined diagnostics.</summary>
public abstract class ExecutableFormatDecodeResult
{
    /// <summary>Stores the decode outputs and combines inspection diagnostics with replay diagnostics.</summary>
    protected ExecutableFormatDecodeResult(
        ExecutableFormatSelection selection,
        CaptureSession capture,
        IFormatInspectionReport inspectionReport,
        ITouchReplaySession replay,
        ReplayDecodeConfiguration configuration)
    {
        Selection = selection;
        Capture = capture;
        InspectionReport = inspectionReport;
        Replay = replay;
        Configuration = configuration;
        Diagnostics = inspectionReport.Diagnostics.Concat(replay.Diagnostics).ToArray();
    }

    /// <summary>Gets the executable format selection used to decode the capture.</summary>
    public ExecutableFormatSelection Selection { get; }
    /// <summary>Gets the format descriptor from the decode selection.</summary>
    public FormatDescriptor Descriptor => Selection.Descriptor;
    /// <summary>Gets the display label from the decode selection.</summary>
    public string DisplayIdentity => Selection.DisplayIdentity;
    /// <summary>Gets the capture configured with the register profile used for decoding.</summary>
    public CaptureSession Capture { get; }
    /// <summary>Gets the inspection report containing source provenance and decode diagnostics.</summary>
    public IFormatInspectionReport InspectionReport { get; }
    /// <summary>Gets the touch replay session created from the decoded frames.</summary>
    public ITouchReplaySession Replay { get; }
    /// <summary>Gets the format, profile, adapter, and address configuration recorded for the decode.</summary>
    public ReplayDecodeConfiguration Configuration { get; }
    /// <summary>Gets inspection diagnostics followed by replay diagnostics.</summary>
    public IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }
    /// <summary>Gets whether the inspection report contains any error-severity diagnostic.</summary>
    public bool HasInspectionErrors => InspectionReport.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

/// <summary>Holds the Common format selection, inspection report, and replay session for a decoded capture.</summary>
public sealed class CommonFormatDecodeResult : ExecutableFormatDecodeResult
{
    internal CommonFormatDecodeResult(
        CommonFormatSelection selection,
        CaptureSession capture,
        CommonInspectionReport report,
        CommonReplaySession replay,
        ReplayDecodeConfiguration configuration)
        : base(selection, capture, report, replay, configuration)
    {
        CommonSelection = selection;
        Report = report;
        CommonReplay = replay;
    }

    /// <summary>Gets the Common format selection used for this decode.</summary>
    public CommonFormatSelection CommonSelection { get; }
    /// <summary>Gets the Common format inspection report produced by this decode.</summary>
    public CommonInspectionReport Report { get; }
    /// <summary>Gets the Common replay session created from the decoded frames.</summary>
    public CommonReplaySession CommonReplay { get; }
}

/// <summary>Holds the Desay 0x97 format selection, inspection report, and replay session for a decoded capture.</summary>
public sealed class Desay97FormatDecodeResult : ExecutableFormatDecodeResult
{
    internal Desay97FormatDecodeResult(
        Desay97FormatSelection selection,
        CaptureSession capture,
        Desay97InspectionReport report,
        Desay97ReplaySession replay,
        ReplayDecodeConfiguration configuration)
        : base(selection, capture, report, replay, configuration)
    {
        DesaySelection = selection;
        Report = report;
        DesayReplay = replay;
    }

    /// <summary>Gets the Desay 0x97 format selection used for this decode.</summary>
    public Desay97FormatSelection DesaySelection { get; }
    /// <summary>Gets the Desay 0x97 inspection report produced by this decode.</summary>
    public Desay97InspectionReport Report { get; }
    /// <summary>Gets the Desay 0x97 replay session created from the decoded frames.</summary>
    public Desay97ReplaySession DesayReplay { get; }
}

/// <summary>Lists built-in formats and resolves decode requests into executable format selections.</summary>
public static class ExecutableFormatRegistry
{
    /// <summary>Gets the descriptors for all built-in Event Buffer formats.</summary>
    public static IReadOnlyList<FormatDescriptor> Descriptors => BuiltInFormats.All;

    /// <summary>
    /// Resolves a format against immutable capture evidence. When no register profile was supplied,
    /// a unique Switch Page + Event Buffer match is applied; ambiguous or conflicting evidence is
    /// returned to the caller instead of being guessed.
    /// </summary>
    public static bool TryResolve(
        CaptureSession capture,
        FormatDecodeRequest request,
        out ExecutableFormatSelection? selection,
        out NvtRegisterProfileInferenceResult inference,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(request);

        inference = string.IsNullOrWhiteSpace(request.RegisterProfile)
            ? NvtRegisterProfileInference.Infer(capture.Records, request.TargetI2cAddress)
            : new NvtRegisterProfileInferenceResult(NvtRegisterProfileInferenceStatus.None, [], []);

        var effectiveRequest = request;
        switch (inference.Status)
        {
            case NvtRegisterProfileInferenceStatus.Unique:
                effectiveRequest = request with { RegisterProfile = inference.UniqueProfile!.IcFamily };
                break;
            case NvtRegisterProfileInferenceStatus.Ambiguous:
                selection = null;
                error = $"IC profile is ambiguous from Switch Page + Event Buffer evidence " +
                        $"({string.Join(", ", inference.Candidates.Select(item => item.IcFamily))}). " +
                        "Select an IC profile explicitly (--register-profile in CLI).";
                return false;
            case NvtRegisterProfileInferenceStatus.Conflicting:
                selection = null;
                error = $"Switch Page + Event Buffer evidence conflicts across IC profiles " +
                        $"({string.Join(", ", inference.Candidates.Select(item => item.IcFamily))}). " +
                        "Select an IC profile explicitly (--register-profile in CLI).";
                return false;
        }

        return TryResolve(effectiveRequest, out selection, out error);
    }

    /// <summary>Validates a decode request and resolves its format, profiles, and I2C address into an executable selection.</summary>
    public static bool TryResolve(
        FormatDecodeRequest request,
        out ExecutableFormatSelection? selection,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(request);
        selection = null;
        if (request.TargetI2cAddress is < 0 or > 0x7F)
        {
            error = "--i2c-address must be a 7-bit value from 0x00 through 0x7F.";
            return false;
        }
        var registerProfile = ResolveRegisterProfile(request.RegisterProfile, out error);
        if (!string.IsNullOrWhiteSpace(request.RegisterProfile) && registerProfile is null) return false;

        if (CommonEventBufferDecoder.TryParseVersion(request.EventBufferVersion, out var commonVersion))
        {
            var canonical = CommonVersionText(commonVersion);
            var descriptor = Descriptors.Single(item => item.Family == "Common" && item.Version == canonical);
            selection = new CommonFormatSelection(
                descriptor,
                commonVersion,
                registerProfile?.IcFamily,
                request.TargetI2cAddress);
            error = string.Empty;
            return true;
        }

        if (!IsDesay97Version(request.EventBufferVersion))
        {
            error = $"Unsupported Event Buffer Version: {request.EventBufferVersion}";
            return false;
        }
        if (registerProfile is null)
        {
            error = "Desay 0x97 requires --register-profile <family>; its Event Buffer base is IC-specific.";
            return false;
        }
        if (!TryParseDesay97Profile(request.Desay97Profile, out var desayProfile))
        {
            error = "Desay 0x97 requires --desay97-profile <standard|benz-palm>; it is never auto-detected.";
            return false;
        }

        selection = new Desay97FormatSelection(
            Descriptors.Single(item => item.Family == "Desay" && item.Version == "0x97"),
            desayProfile,
            registerProfile,
            request.TargetI2cAddress);
        error = string.Empty;
        return true;
    }

    /// <summary>Converts a supported Common version to its canonical hexadecimal version text.</summary>
    public static string CommonVersionText(CommonEventBufferVersion version) => version switch
    {
        CommonEventBufferVersion.V82 => "0x82",
        CommonEventBufferVersion.V83 => "0x83",
        CommonEventBufferVersion.V84 => "0x84",
        CommonEventBufferVersion.V85 => "0x85",
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    private static NvtRegisterProfile? ResolveRegisterProfile(string? value, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = string.Empty;
            return null;
        }
        var profile = NvtRegisterCatalog.FindProfile(value);
        error = profile is null
            ? $"--register-profile must be one of: {string.Join(", ", NvtRegisterCatalog.Profiles.Select(item => item.IcFamily))}."
            : string.Empty;
        return profile;
    }

    private static bool TryParseDesay97Profile(string? value, out Desay97Profile profile)
    {
        var normalized = value?.Trim().Replace('_', '-').ToLowerInvariant();
        profile = normalized switch
        {
            "standard" => Desay97Profile.Standard,
            "benz" or "benz-palm" => Desay97Profile.BenzPalm,
            _ => default,
        };
        return normalized is "standard" or "benz" or "benz-palm";
    }

    private static bool IsDesay97Version(string value) =>
        value.Equals("0x97", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("97", StringComparison.OrdinalIgnoreCase);
}
