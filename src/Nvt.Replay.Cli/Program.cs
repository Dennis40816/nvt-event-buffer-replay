using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Rendering;
using Nvt.Replay.Sources;

[assembly: InternalsVisibleTo("Nvt.Replay.Tests")]

return await ReplayCli.RunAsync(args, Console.Out, Console.Error);

internal static class ReplayCli
{
    private const int UsageError = 2;
    private const int InputError = 3;
    private const int DecodeError = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var operands = args.Where(argument => !argument.Equals("--json", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (operands.Length == 0 || operands[0] is "help" or "--help" or "-h")
        {
            await WriteHelpAsync(output);
            return 0;
        }

        try
        {
            switch (operands[0].ToLowerInvariant())
            {
                case "formats":
                    await WriteAsync(output, ExecutableFormatRegistry.Descriptors, json);
                    return 0;
                case "sources":
                    var sources = BuiltInSources.All.Select(source => new { source.Id, source.DisplayName });
                    await WriteAsync(output, sources, json);
                    return 0;
                case "probe" when operands.Length == 2:
                    return await ProbeAsync(operands[1], output, error, json);
                case "inspect":
                    return await InspectAsync(operands[1..], output, error, json);
                case "analyze":
                    return await AnalyzeAsync(operands[1..], output, error, json);
                case "readable":
                    return await ExportReadableLogAsync(operands[1..], output, error, json);
                case "export":
                    return await ExportReplayAsync(operands[1..], output, error, json);
                case "benchmark":
                    return await BenchmarkAsync(operands[1..], output, error, json);
                default:
                    await error.WriteLineAsync("Unknown or incomplete command. Run 'nvt-replay help'.");
                    return UsageError;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SourceSelectionRequiredException or ArgumentException)
        {
            await error.WriteLineAsync($"Input error: {exception.Message}");
            return InputError;
        }
    }

    private static async Task<int> BenchmarkAsync(string[] operands, TextWriter output, TextWriter error, bool json)
    {
        if (operands.Length < 3 || !TryReadOption(operands, "--event-buffer-version", out var versionText))
        {
            await error.WriteLineAsync("Usage: nvt-replay benchmark <file> --event-buffer-version <0x82|0x83|0x84|0x85> [--source-adapter <id>] [--sample-seeks <count>] [--render-frames <count>] [--json]");
            return UsageError;
        }
        if (!File.Exists(operands[0]))
        {
            await error.WriteLineAsync($"Input file does not exist: {operands[0]}");
            return InputError;
        }
        if (!CommonEventBufferDecoder.TryParseVersion(versionText, out var version))
        {
            await error.WriteLineAsync("Benchmark currently supports Common 0x82-0x85 captures.");
            return UsageError;
        }
        if (!TryReadNonNegativeInt(operands, "--sample-seeks", 10_000, out var seekSamples) ||
            !TryReadNonNegativeInt(operands, "--render-frames", 60, out var renderFrames))
        {
            await error.WriteLineAsync("--sample-seeks and --render-frames must be non-negative integers.");
            return UsageError;
        }
        var adapterId = TryReadOption(operands, "--source-adapter", out var sourceText) ? sourceText : null;
        var report = await PerformanceBenchmark.RunCommonAsync(operands[0], version, adapterId, seekSamples, renderFrames);
        if (json) await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions));
        else
        {
            await output.WriteLineAsync($"Input    {report.SourceBytes:N0} bytes · {report.PhysicalRecords:N0} physical records · max offset {report.MaximumByteOffset:N0}");
            await output.WriteLineAsync($"Replay   {report.LogicalFrames:N0} frames · {report.Checkpoints:N0} sparse checkpoints · {report.TimelineDuration:c}");
            await output.WriteLineAsync($"Timing   load {report.LoadMilliseconds:N0} ms · decode/index {report.DecodeAndIndexMilliseconds:N0} ms · {report.SeekSamples:N0} seeks {report.SeekMilliseconds:N0} ms");
            await output.WriteLineAsync($"Render   {report.RenderedFrames:N0} frames · {report.RenderFramesPerSecond:N1} fps");
            await output.WriteLineAsync($"Memory   peak working set {report.PeakWorkingSetBytes / 1_048_576d:N1} MiB");
        }
        return 0;
    }

    private static bool TryReadNonNegativeInt(string[] operands, string name, int fallback, out int value)
    {
        if (!TryReadOption(operands, name, out var text))
        {
            value = fallback;
            return true;
        }
        return int.TryParse(text, out value) && value >= 0;
    }

    private static async Task<int> ExportReplayAsync(
        string[] operands,
        TextWriter output,
        TextWriter error,
        bool json)
    {
        if (operands.Length < 5 ||
            !TryReadOption(operands, "--event-buffer-version", out var versionText) ||
            !TryReadOption(operands, "--output", out var outputPath))
        {
            await error.WriteLineAsync("Usage: nvt-replay export <file> --event-buffer-version <version> --output <file.mp4> [--i2c-address <0x00-0x7F>] [--range <first:last>] [--size <width>x<height>] [--fps <1-240>] [--speed <value>] [--clock <recorded|frame>] [--ffmpeg <path>] [--review <sidecar>] [--source-adapter <id>] [--register-profile <family>] [--desay97-profile <standard|benz-palm>] [--json]");
            return UsageError;
        }
        var path = operands[0];
        if (!File.Exists(path))
        {
            await error.WriteLineAsync($"Input file does not exist: {path}");
            return InputError;
        }
        var adapterId = TryReadOption(operands, "--source-adapter", out var sourceText) ? sourceText : null;
        if (!TryReadRegisterProfile(operands, out var registerProfile, out var registerProfileError))
        {
            await error.WriteLineAsync(registerProfileError);
            return UsageError;
        }
        if (!TryReadI2cAddress(operands, out var targetI2cAddress, out var i2cAddressError))
        {
            await error.WriteLineAsync(i2cAddressError);
            return UsageError;
        }
        var palmProfile = TryReadOption(operands, "--desay97-profile", out var selectedPalm) ? selectedPalm : null;
        var loadedCapture = await CaptureSession.LoadAsync(path, adapterId: adapterId);
        if (!ExecutableFormatRegistry.TryResolve(
                loadedCapture,
                new FormatDecodeRequest(versionText, registerProfile, palmProfile, targetI2cAddress),
                out var formatSelection,
                out _,
                out var formatError))
        {
            await error.WriteLineAsync(formatError);
            return UsageError;
        }
        var decodedFormat = formatSelection!.Decode(loadedCapture);
        var capture = decodedFormat.Capture;
        var replay = decodedFormat.Replay;
        var diagnostics = decodedFormat.Diagnostics;
        var configuration = decodedFormat.Configuration;
        if (replay.Count == 0)
        {
            await error.WriteLineAsync("No decoded frames are available for export.");
            return DecodeError;
        }

        AnalysisRange range = new(0, replay.Count - 1);
        AnalysisRange? selectedRange = null;
        if (TryReadOption(operands, "--range", out var rangeText) && !TryParseRange(rangeText, replay.Count, out selectedRange))
        {
            await error.WriteLineAsync($"--range must be a 1-based inclusive FIRST:LAST within 1:{replay.Count}.");
            return UsageError;
        }
        else if (selectedRange is not null) range = selectedRange;
        var width = 1280;
        var height = 720;
        if (TryReadOption(operands, "--size", out var sizeText) && !TryParseSize(sizeText, out width, out height))
        {
            await error.WriteLineAsync("--size must be an even WIDTHxHEIGHT, at least 320x180.");
            return UsageError;
        }
        if (width < 320 || height < 180 || width % 2 != 0 || height % 2 != 0)
        {
            await error.WriteLineAsync("--size must be an even WIDTHxHEIGHT, at least 320x180.");
            return UsageError;
        }
        var frameRate = 30;
        if (TryReadOption(operands, "--fps", out var fpsText) && !TryParseExportFrameRate(fpsText, out frameRate))
        {
            await error.WriteLineAsync($"--fps must be between 1 and {ReplayFramePlan.MaximumFrameRate}.");
            return UsageError;
        }
        var speed = 1d;
        if (TryReadOption(operands, "--speed", out var speedText) && (!double.TryParse(speedText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out speed) || speed is <= 0 or > 100))
        {
            await error.WriteLineAsync("--speed must be greater than 0 and no more than 100.");
            return UsageError;
        }
        var clock = ReplayExportClock.Recorded;
        if (TryReadOption(operands, "--clock", out var clockText))
        {
            clock = clockText.ToLowerInvariant() switch
            {
                "recorded" => ReplayExportClock.Recorded,
                "frame" => ReplayExportClock.Frame,
                _ => (ReplayExportClock)(-1),
            };
            if ((int)clock < 0)
            {
                await error.WriteLineAsync("--clock must be recorded or frame.");
                return UsageError;
            }
        }

        IReadOnlyList<ReplayMarker> markers = [];
        if (TryReadOption(operands, "--review", out var reviewPath))
        {
            var sidecar = await new ReplaySidecarStore().LoadAsync(reviewPath, capture.SourceSha256, configuration);
            if (sidecar.RequiresExplicitConfirmation)
            {
                await error.WriteLineAsync("Review sidecar source/configuration mismatch; headless export will not apply it.");
                return InputError;
            }
            markers = sidecar.Document.Markers;
        }
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        var ffmpeg = TryReadOption(operands, "--ffmpeg", out var ffmpegPath) ? ffmpegPath : null;
        var options = new ReplayExportOptions(
            outputPath, range, width, height, frameRate, speed, clock, ReplayRenderMode.Compare, ffmpeg,
            Path.GetFileName(capture.SourcePath), capture.SourceSha256, configuration);
        var result = await new ReplayRangeExporter().ExportAsync(
            replay,
            index => ReplaySceneFactory.Create(replay.Seek(index), replay.Count, extent, diagnostics, markers),
            options);
        if (json)
            await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
        else
        {
            await output.WriteLineAsync($"Export   {result.Kind} · {result.OutputPath}");
            await output.WriteLineAsync($"Frames   {result.OutputFrameCount:N0} · {result.Duration.TotalSeconds:0.###}s · {width}x{height} @ {frameRate} fps");
            await output.WriteLineAsync($"Manifest {result.ManifestPath}");
            if (result.Warning is not null) await error.WriteLineAsync($"WARNING  {result.Warning}");
        }
        return 0;
    }

    private static async Task<int> AnalyzeAsync(
        string[] operands,
        TextWriter output,
        TextWriter error,
        bool json)
    {
        if (operands.Length < 5 ||
            !TryReadOption(operands, "--event-buffer-version", out var versionText) ||
            !TryReadOption(operands, "--output", out var outputDirectory))
        {
            await error.WriteLineAsync("Usage: nvt-replay analyze <file> --event-buffer-version <0x82|0x83|0x84|0x85|0x97> --output <directory> [--i2c-address <0x00-0x7F>] [--range <first:last>] [--heatmap-size <width>x<height>] [--source-adapter <id>] [--register-profile <family>] [--desay97-profile <standard|benz-palm>] [--json]");
            return UsageError;
        }
        var path = operands[0];
        if (!File.Exists(path))
        {
            await error.WriteLineAsync($"Input file does not exist: {path}");
            return InputError;
        }
        var adapterId = TryReadOption(operands, "--source-adapter", out var selectedSource) ? selectedSource : null;
        var heatmapWidth = 640;
        var heatmapHeight = 360;
        if (TryReadOption(operands, "--heatmap-size", out var sizeText) && !TryParseSize(sizeText, out heatmapWidth, out heatmapHeight))
        {
            await error.WriteLineAsync("--heatmap-size must be WIDTHxHEIGHT with positive integers.");
            return UsageError;
        }

        if (!TryReadRegisterProfile(operands, out var registerProfile, out var registerProfileError))
        {
            await error.WriteLineAsync(registerProfileError);
            return UsageError;
        }
        if (!TryReadI2cAddress(operands, out var targetI2cAddress, out var i2cAddressError))
        {
            await error.WriteLineAsync(i2cAddressError);
            return UsageError;
        }
        var palmProfile = TryReadOption(operands, "--desay97-profile", out var selectedPalm) ? selectedPalm : null;
        var loadedCapture = await CaptureSession.LoadAsync(path, adapterId: adapterId);
        if (!ExecutableFormatRegistry.TryResolve(
                loadedCapture,
                new FormatDecodeRequest(versionText, registerProfile, palmProfile, targetI2cAddress),
                out var formatSelection,
                out _,
                out var formatError))
        {
            await error.WriteLineAsync(formatError);
            return UsageError;
        }
        var decodedFormat = formatSelection!.Decode(loadedCapture);
        var capture = decodedFormat.Capture;
        var replay = decodedFormat.Replay;
        var diagnostics = decodedFormat.Diagnostics;
        var configuration = decodedFormat.Configuration;
        var evidenceStatus = decodedFormat.Descriptor.EvidenceStatus;
        if (replay.Count == 0)
        {
            await error.WriteLineAsync("No decoded frames are available for analysis.");
            return DecodeError;
        }

        AnalysisRange? range = null;
        if (TryReadOption(operands, "--range", out var rangeText))
        {
            if (!TryParseRange(rangeText, replay.Count, out range))
            {
                await error.WriteLineAsync($"--range must be a 1-based inclusive FIRST:LAST within 1:{replay.Count}.");
                return UsageError;
            }
        }
        var sourceIndices = Enumerable.Range(0, replay.Count)
            .SelectMany(index => replay.Seek(index).PhysicalRecords.Select(item => (item.StableId, Index: index)))
            .GroupBy(item => item.StableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Index, StringComparer.Ordinal);
        var review = new ReviewSession(diagnostics, sourceIndices);
        var report = new CaptureAnalyzer().Analyze(
            capture.SourcePath,
            capture.SourceSha256,
            configuration,
            evidenceStatus,
            replay,
            diagnostics,
            review,
            range,
            heatmapPixelWidth: heatmapWidth,
            heatmapPixelHeight: heatmapHeight);
        var result = await new AnalysisOutputWriter().WriteAsync(outputDirectory, report, sourceRecords: capture.Records);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                result.Directory,
                Frames = report.Events.Count,
                Diagnostics = report.Diagnostics.Count,
                HotspotSamples = report.Hotspot.SampleCount,
                report.Asil,
                Files = result,
            }, JsonOptions));
        }
        else
        {
            await output.WriteLineAsync($"Analysis {result.Directory}");
            await output.WriteLineAsync($"Frames   {report.Events.Count:N0} ({report.Manifest.Clock.Domain})");
            await output.WriteLineAsync($"Findings {report.Diagnostics.Count:N0} · {report.DiagnosticAggregates.Count:N0} groups");
            await output.WriteLineAsync($"ASIL     {report.Asil.Assertions} assert · {report.Asil.Clears} clear · {report.Asil.UnresolvedAlarmGroups} unresolved");
            await output.WriteLineAsync($"Hotspot  {report.Hotspot.SampleCount:N0} samples · {report.Manifest.HeatmapPixelWidth}x{report.Manifest.HeatmapPixelHeight} PNG");
            await output.WriteLineAsync("Outputs  analysis-report.json, events.json/csv, diagnostics.json/csv, communication-readable.csv/jsonl, manifest.json, heatmap.png");
        }
        return 0;
    }

    internal static bool TryParseExportFrameRate(string text, out int frameRate) =>
        int.TryParse(text, out frameRate) && frameRate is >= 1 and <= ReplayFramePlan.MaximumFrameRate;

    private static bool TryParseRange(string value, int count, out AnalysisRange? range)
    {
        range = null;
        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var first) || !int.TryParse(parts[1], out var last) || first < 1 || last < first || last > count)
            return false;
        range = new AnalysisRange(first - 1, last - 1);
        return true;
    }

    private static bool TryParseSize(string value, out int width, out int height)
    {
        width = height = 0;
        var parts = value.ToLowerInvariant().Split('x', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height) && width > 0 && height > 0;
    }

    private static async Task<int> InspectAsync(
        string[] operands,
        TextWriter output,
        TextWriter error,
        bool json)
    {
        if (operands.Length < 3 || !TryReadOption(operands, "--event-buffer-version", out var versionText))
        {
            await error.WriteLineAsync("Usage: nvt-replay inspect <file> --event-buffer-version <0x82|0x83|0x84|0x85|0x97> [--i2c-address <0x00-0x7F>] [--source-adapter <id>] [--register-profile <family>] [--desay97-profile <standard|benz-palm>] [--json]");
            return UsageError;
        }

        var path = operands[0];
        var sourceAdapterId = TryReadOption(operands, "--source-adapter", out var selectedSource) ? selectedSource : null;
        if (!File.Exists(path))
        {
            await error.WriteLineAsync($"Input file does not exist: {path}");
            return InputError;
        }
        if (!TryReadRegisterProfile(operands, out var registerProfile, out var registerProfileError))
        {
            await error.WriteLineAsync(registerProfileError);
            return UsageError;
        }
        if (!TryReadI2cAddress(operands, out var targetI2cAddress, out var i2cAddressError))
        {
            await error.WriteLineAsync(i2cAddressError);
            return UsageError;
        }
        var palmProfile = TryReadOption(operands, "--desay97-profile", out var selectedPalm) ? selectedPalm : null;
        var capture = await CaptureSession.LoadAsync(path, adapterId: sourceAdapterId);
        if (!ExecutableFormatRegistry.TryResolve(
                capture,
                new FormatDecodeRequest(versionText, registerProfile, palmProfile, targetI2cAddress),
                out var formatSelection,
                out _,
                out var formatError))
        {
            await error.WriteLineAsync(formatError);
            return UsageError;
        }
        var decodedFormat = formatSelection!.Decode(capture);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(
                decodedFormat.InspectionReport,
                decodedFormat.InspectionReport.GetType(),
                JsonOptions));
        }
        else
        {
            switch (decodedFormat)
            {
                case CommonFormatDecodeResult common:
                    await WriteInspectionAsync(common.Report, output, error);
                    break;
                case Desay97FormatDecodeResult desay:
                    await WriteDesay97InspectionAsync(desay.Report, output, error);
                    break;
                default:
                    throw new InvalidOperationException($"No CLI inspection presenter is registered for {decodedFormat.DisplayIdentity}.");
            }
        }
        return decodedFormat.HasInspectionErrors ? DecodeError : 0;
    }

    private static async Task WriteDesay97InspectionAsync(
        Desay97InspectionReport report,
        TextWriter output,
        TextWriter error)
    {
        await output.WriteLineAsync($"Source  {report.SourcePath}");
        await output.WriteLineAsync($"SHA-256 {report.SourceSha256}");
        await output.WriteLineAsync($"Format  Desay 0x97 / {ProfileText(report.Profile)} (operator selected)");
        await output.WriteLineAsync($"Base    0x{report.EventBufferBase:X}");
        await output.WriteLineAsync($"Frames  {report.Frames.Count}");

        foreach (var frame in report.Frames)
        {
            var timestamp = frame.Source.Timestamp?.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) ?? "--:--:--.---";
            var flags = new List<string>();
            if (frame.AllBreak) flags.Add("ALL BREAK");
            if (frame.Short) flags.Add("SHORT");
            if (frame.Open) flags.Add("OPEN");
            if (frame.TpAsilError) flags.Add("TP ASIL");
            var suffix = flags.Count > 0 ? $"  [{string.Join(", ", flags)}]" : string.Empty;
            await output.WriteLineAsync(
                $"#{frame.Source.Index,6}  {timestamp}  touches={frame.NumTouches,2}  crc=  OK  {frame.Packet.StableId}{suffix}");
            await output.WriteLineAsync(
                $"          physical={frame.Packet.Probe.StableId}, {frame.Packet.PayloadRead.StableId}");
            foreach (var finger in frame.Fingers)
            {
                var semantic = finger.Invalid ? "INVALID" : finger.Palm ? "PALM" : "FINGER";
                await output.WriteLineAsync(
                    $"          id={finger.Id,2}  {semantic,-8} {finger.Status,-8} x={finger.X,5} y={finger.Y,5}");
            }
        }

        foreach (var diagnostic in report.Diagnostics)
        {
            await error.WriteLineAsync(
                $"{diagnostic.Severity.ToString().ToUpperInvariant(),-7} {diagnostic.Code} L{diagnostic.Location.LineNumber}: {diagnostic.Message}");
        }
    }

    private static bool TryReadOption(string[] operands, string name, out string value)
    {
        var index = Array.FindIndex(operands, operand => operand.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < operands.Length)
        {
            value = operands[index + 1];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string ProfileText(Desay97Profile profile) => profile switch
    {
        Desay97Profile.Standard => "Standard",
        Desay97Profile.BenzPalm => "Benz Palm",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    private static bool TryReadRegisterProfile(string[] operands, out string? profile, out string error)
    {
        profile = null;
        error = string.Empty;
        if (!TryReadOption(operands, "--register-profile", out var text)) return true;
        var resolved = NvtRegisterCatalog.FindProfile(text);
        if (resolved is not null)
        {
            profile = resolved.IcFamily;
            return true;
        }

        error = $"--register-profile must be one of: {string.Join(", ", NvtRegisterCatalog.Profiles.Select(item => item.IcFamily))}.";
        return false;
    }

    internal static bool TryReadI2cAddress(
        string[] operands,
        out int address,
        out string error)
    {
        address = 0x01;
        error = string.Empty;
        if (!TryReadOption(operands, "--i2c-address", out var text)) return true;
        var trimmed = text.Trim();
        var style = System.Globalization.NumberStyles.Integer;
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
            style = System.Globalization.NumberStyles.AllowHexSpecifier;
        }
        if (int.TryParse(trimmed, style, System.Globalization.CultureInfo.InvariantCulture, out address) &&
            address is >= 0 and <= 0x7F)
        {
            return true;
        }

        error = "--i2c-address must be a 7-bit value from 0x00 through 0x7F (default 0x01).";
        return false;
    }

    private static async Task<int> ExportReadableLogAsync(
        string[] operands,
        TextWriter output,
        TextWriter error,
        bool json)
    {
        if (operands.Length < 3 || !TryReadOption(operands, "--output", out var outputDirectory))
        {
            await error.WriteLineAsync("Usage: nvt-replay readable <file> --output <directory> [--source-adapter <id>] [--register-profile <family>] [--json]");
            return UsageError;
        }
        var path = operands[0];
        if (!File.Exists(path))
        {
            await error.WriteLineAsync($"Input file does not exist: {path}");
            return InputError;
        }
        if (!TryReadRegisterProfile(operands, out var registerProfile, out var registerProfileError))
        {
            await error.WriteLineAsync(registerProfileError);
            return UsageError;
        }

        var adapterId = TryReadOption(operands, "--source-adapter", out var selectedSource) ? selectedSource : null;
        var capture = (await CaptureSession.LoadAsync(path, adapterId: adapterId)).WithRegisterProfile(registerProfile);
        var result = await new ReadableCommunicationLogWriter().WriteAsync(outputDirectory, capture.Records);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Source = capture.SourcePath,
                capture.SourceSha256,
                RegisterProfile = registerProfile,
                Result = result,
            }, JsonOptions));
        }
        else
        {
            await output.WriteLineAsync($"Readable {result.RecordCount:N0} records · {result.AnnotatedRegisterCount:N0} register annotations · {result.AmbiguousRegisterCount:N0} ambiguous");
            await output.WriteLineAsync($"CSV      {result.CsvPath}");
            await output.WriteLineAsync($"JSONL    {result.JsonlPath}");
        }
        return 0;
    }

    private static async Task<int> ProbeAsync(string path, TextWriter output, TextWriter error, bool json)
    {
        if (!File.Exists(path))
        {
            await error.WriteLineAsync($"Input file does not exist: {path}");
            return InputError;
        }

        var results = new List<object>();
        foreach (var source in BuiltInSources.All)
        {
            var result = await source.ProbeAsync(path);
            results.Add(result);
        }

        await WriteAsync(output, results, json);
        return 0;
    }

    private static async Task WriteAsync<T>(TextWriter output, T value, bool json)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
            return;
        }

        switch (value)
        {
            case IEnumerable<Nvt.Replay.Core.FormatDescriptor> formats:
                foreach (var format in formats)
                {
                    await output.WriteLineAsync($"{format.Id,-12} {format.DisplayName} [{format.EvidenceStatus}]");
                }

                break;
            case IEnumerable<object> items:
                foreach (var item in items)
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(item, JsonOptions));
                }

                break;
            default:
                await output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
                break;
        }
    }

    private static async Task WriteInspectionAsync(
        CommonInspectionReport report,
        TextWriter output,
        TextWriter error)
    {
        await output.WriteLineAsync($"Source  {report.SourcePath}");
        await output.WriteLineAsync($"SHA-256 {report.SourceSha256}");
        await output.WriteLineAsync($"Format  Common {ExecutableFormatRegistry.CommonVersionText(report.Version)} (operator selected)");
        await output.WriteLineAsync($"Frames  {report.Frames.Count}");

        foreach (var frame in report.Frames)
        {
            var timestamp = frame.Source.Timestamp?.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) ?? "--:--:--.---";
            var flags = new List<string>();
            if (frame.AllBreak)
            {
                flags.Add("ALL BREAK");
            }

            if (frame.Asil.Alarm || frame.TpAsilError)
            {
                flags.Add($"ASIL=0x{frame.Asil.Raw:X2}");
            }

            if (!frame.CrcValid)
            {
                flags.Add("CRC FAIL");
            }

            var suffix = flags.Count > 0 ? $"  [{string.Join(", ", flags)}]" : string.Empty;
            await output.WriteLineAsync(
                $"#{frame.Source.Index,6}  {timestamp}  touches={frame.NumTouches,2}  crc={(frame.CrcValid ? "OK" : "FAIL"),4}  {frame.Source.StableId}{suffix}");

            foreach (var finger in frame.Fingers.Where(IsReportedFinger))
            {
                await output.WriteLineAsync(
                    $"          id={finger.Id,2}  {finger.Type,-8} {finger.Status,-8} x={finger.X,5} y={finger.Y,5}");
            }
        }

        foreach (var diagnostic in report.Diagnostics)
        {
            await error.WriteLineAsync(
                $"{diagnostic.Severity.ToString().ToUpperInvariant(),-7} {diagnostic.Code} L{diagnostic.Location.LineNumber}: {diagnostic.Message}");
        }
    }

    private static bool IsReportedFinger(CommonFinger finger) =>
        !IsAllFfSlot(finger) &&
        (finger.Status != TouchStatus.NoFinger || finger.Type == TouchType.Palm);

    private static bool IsAllFfSlot(CommonFinger finger) =>
        finger.RawMeta == 0xFF &&
        finger.X == 0xFFFF &&
        finger.Y == 0xFFFF &&
        finger.Reserved.All(value => value == 0xFF);

    private static Task WriteHelpAsync(TextWriter output)
    {
        return output.WriteLineAsync(
            """
            NVT Event Buffer Replay CLI

            Usage:
              nvt-replay formats [--json]   List supported Event Buffer formats
              nvt-replay sources [--json]   List available input adapters
              nvt-replay probe <file> [--json]
                                           Suggest a source adapter without selecting a format
              nvt-replay inspect <file> --event-buffer-version <0x82|0x83|0x84|0x85>
                                 [--i2c-address <0x00-0x7F>]
                                 [--source-adapter <id>] [--register-profile <family>] [--json]
                                           Decode Common records from any supported source
              nvt-replay inspect <file> --event-buffer-version 0x97
                                 --desay97-profile <standard|benz-palm>
                                 --register-profile <family>
                                 [--i2c-address <0x00-0x7F>]
                                 [--source-adapter <id>] [--json]
                                           Assemble and decode Desay reads
              nvt-replay analyze <file> --event-buffer-version <version>
                                 --output <directory> [--range <first:last>]
                                 [--heatmap-size <width>x<height>]
                                 [--i2c-address <0x00-0x7F>]
                                 [--source-adapter <id>]
                                 [--register-profile <family>]
                                 [--desay97-profile <standard|benz-palm>] [--json]
                                           Export deterministic analysis and readable communication logs
              nvt-replay readable <file> --output <directory>
                                  [--source-adapter <id>]
                                  [--register-profile <family>] [--json]
                                           Export a paired readable CSV/JSONL without decoding events
              nvt-replay export <file> --event-buffer-version <version>
                                --output <file.mp4> [--range <first:last>]
                                [--size <width>x<height>] [--fps <1-240>]
                                [--speed <value>] [--clock <recorded|frame>]
                                [--ffmpeg <path>] [--review <sidecar>]
                                [--i2c-address <0x00-0x7F>]
                                [--source-adapter <id>]
                                [--register-profile <family>]
                                [--desay97-profile <standard|benz-palm>] [--json]
                                           Export MP4 or atomic PNG fallback
              nvt-replay benchmark <file> --event-buffer-version <0x82|0x83|0x84|0x85>
                                   [--source-adapter <id>] [--sample-seeks <count>]
                                   [--render-frames <count>] [--json]
                                           Measure load, decode/index, seek, render, and peak memory

            Source detection is ranked and may require --source-adapter when ambiguous.
            Event Buffer Version, Benz Palm, and collision-prone IC register profiles remain explicit operator choices.
            Desay 0x97 always requires both --desay97-profile and --register-profile.
            """);
    }
}
