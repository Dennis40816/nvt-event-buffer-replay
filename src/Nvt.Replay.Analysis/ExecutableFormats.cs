using Nvt.Replay.Core;
using Nvt.Replay.Formats;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Analysis;

public sealed record FormatDecodeRequest(
    string EventBufferVersion,
    string? RegisterProfile = null,
    string? Desay97Profile = null);

public interface IFormatInspectionReport
{
    string SourcePath { get; }
    string SourceSha256 { get; }
    IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }
}

public abstract class ExecutableFormatSelection
{
    protected ExecutableFormatSelection(FormatDescriptor descriptor, string? registerProfile)
    {
        Descriptor = descriptor;
        RegisterProfile = registerProfile;
    }

    public FormatDescriptor Descriptor { get; }
    public string? RegisterProfile { get; }
    public abstract string DisplayIdentity { get; }

    public abstract ExecutableFormatDecodeResult Decode(
        CaptureSession capture,
        CancellationToken cancellationToken = default);

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

public sealed class CommonFormatSelection : ExecutableFormatSelection
{
    internal CommonFormatSelection(
        FormatDescriptor descriptor,
        CommonEventBufferVersion version,
        string? registerProfile)
        : base(descriptor, registerProfile) => Version = version;

    public CommonEventBufferVersion Version { get; }
    public override string DisplayIdentity => $"Common {Descriptor.Version}";

    public override ExecutableFormatDecodeResult Decode(
        CaptureSession capture,
        CancellationToken cancellationToken = default)
    {
        var configured = ConfigureCapture(capture, cancellationToken);
        var report = configured.DecodeCommon(Version, cancellationToken);
        var replay = new CommonReplaySession(report.Frames);
        var eventBufferBase = NvtRegisterCatalog.FindProfile(configured.RegisterProfile)?.EventBufferBase ?? 0;
        var configuration = new ReplayDecodeConfiguration(
            Descriptor.Version,
            null,
            configured.Probe.AdapterId,
            eventBufferBase,
            configured.RegisterProfile);
        return new CommonFormatDecodeResult(this, configured, report, replay, configuration);
    }
}

public sealed class Desay97FormatSelection : ExecutableFormatSelection
{
    internal Desay97FormatSelection(
        FormatDescriptor descriptor,
        Desay97Profile profile,
        NvtRegisterProfile registerProfile)
        : base(descriptor, registerProfile.IcFamily)
    {
        Profile = profile;
        EventBufferBase = registerProfile.EventBufferBase;
    }

    public Desay97Profile Profile { get; }
    public uint EventBufferBase { get; }
    public string ProfileDisplayName => Profile == Desay97Profile.Standard ? "Standard" : "Benz Palm";
    public override string DisplayIdentity => $"Desay 0x97 / {ProfileDisplayName}";

    public override ExecutableFormatDecodeResult Decode(
        CaptureSession capture,
        CancellationToken cancellationToken = default)
    {
        var configured = ConfigureCapture(capture, cancellationToken);
        var report = configured.DecodeDesay97(Profile, EventBufferBase, cancellationToken);
        var replay = new Desay97ReplaySession(report.Frames);
        var configuration = new ReplayDecodeConfiguration(
            "0x97",
            ProfileDisplayName,
            configured.Probe.AdapterId,
            EventBufferBase,
            configured.RegisterProfile);
        return new Desay97FormatDecodeResult(this, configured, report, replay, configuration);
    }
}

public abstract class ExecutableFormatDecodeResult
{
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

    public ExecutableFormatSelection Selection { get; }
    public FormatDescriptor Descriptor => Selection.Descriptor;
    public string DisplayIdentity => Selection.DisplayIdentity;
    public CaptureSession Capture { get; }
    public IFormatInspectionReport InspectionReport { get; }
    public ITouchReplaySession Replay { get; }
    public ReplayDecodeConfiguration Configuration { get; }
    public IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }
    public bool HasInspectionErrors => InspectionReport.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

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

    public CommonFormatSelection CommonSelection { get; }
    public CommonInspectionReport Report { get; }
    public CommonReplaySession CommonReplay { get; }
}

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

    public Desay97FormatSelection DesaySelection { get; }
    public Desay97InspectionReport Report { get; }
    public Desay97ReplaySession DesayReplay { get; }
}

public static class ExecutableFormatRegistry
{
    public static IReadOnlyList<FormatDescriptor> Descriptors => BuiltInFormats.All;

    public static bool TryResolve(
        FormatDecodeRequest request,
        out ExecutableFormatSelection? selection,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(request);
        selection = null;
        var registerProfile = ResolveRegisterProfile(request.RegisterProfile, out error);
        if (!string.IsNullOrWhiteSpace(request.RegisterProfile) && registerProfile is null) return false;

        if (CommonEventBufferDecoder.TryParseVersion(request.EventBufferVersion, out var commonVersion))
        {
            var canonical = CommonVersionText(commonVersion);
            var descriptor = Descriptors.Single(item => item.Family == "Common" && item.Version == canonical);
            selection = new CommonFormatSelection(descriptor, commonVersion, registerProfile?.IcFamily);
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
            registerProfile);
        error = string.Empty;
        return true;
    }

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
