using System.Text.Json;
using System.Text.Json.Serialization;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Rendering;
using Nvt.Replay.Sources;

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
                    await WriteAsync(output, BuiltInFormats.All, json);
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
            await error.WriteLineAsync("Usage: nvt-replay analyze <file> --event-buffer-version <0x82|0x83|0x84|0x85|0x97> --output <directory> [--range <first:last>] [--heatmap-size <width>x<height>] [--source-adapter <id>] [--desay97-profile <standard|benz-palm>] [--json]");
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

        var capture = await CaptureSession.LoadAsync(path, adapterId: adapterId);
        ITouchReplaySession replay;
        IReadOnlyList<ReplayDiagnostic> diagnostics;
        ReplayDecodeConfiguration configuration;
        EvidenceStatus evidenceStatus;
        if (versionText.Equals("0x97", StringComparison.OrdinalIgnoreCase) || versionText.Equals("97", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadOption(operands, "--desay97-profile", out var profileText) || !TryParseDesay97Profile(profileText, out var profile))
            {
                await error.WriteLineAsync("Desay 0x97 requires --desay97-profile <standard|benz-palm>; it is never auto-detected.");
                return UsageError;
            }
            var decoded = capture.DecodeDesay97(profile);
            replay = new Desay97ReplaySession(decoded.Frames);
            diagnostics = decoded.Diagnostics.Concat(replay.Diagnostics).ToArray();
            configuration = new ReplayDecodeConfiguration("0x97", ProfileText(profile), capture.Probe.AdapterId);
            evidenceStatus = EvidenceStatus.Provisional;
        }
        else
        {
            if (!CommonEventBufferDecoder.TryParseVersion(versionText, out var version))
            {
                await error.WriteLineAsync($"Unsupported Event Buffer Version: {versionText}");
                return UsageError;
            }
            var decoded = capture.DecodeCommon(version);
            replay = new CommonReplaySession(decoded.Frames);
            diagnostics = decoded.Diagnostics.Concat(replay.Diagnostics).ToArray();
            var canonicalVersion = VersionText(version);
            configuration = new ReplayDecodeConfiguration(canonicalVersion, null, capture.Probe.AdapterId);
            evidenceStatus = canonicalVersion is "0x83" or "0x84" ? EvidenceStatus.Verified : EvidenceStatus.Provisional;
        }
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
        var result = await new AnalysisOutputWriter().WriteAsync(outputDirectory, report);
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
            await output.WriteLineAsync("Outputs  analysis-report.json, events.json/csv, diagnostics.json/csv, manifest.json, heatmap.png");
        }
        return 0;
    }

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
            await error.WriteLineAsync("Usage: nvt-replay inspect <file> --event-buffer-version <0x82|0x83|0x84|0x85|0x97> [--source-adapter <id>] [--desay97-profile <standard|benz-palm>] [--json]");
            return UsageError;
        }

        var path = operands[0];
        var sourceAdapterId = TryReadOption(operands, "--source-adapter", out var selectedSource) ? selectedSource : null;
        if (!File.Exists(path))
        {
            await error.WriteLineAsync($"Input file does not exist: {path}");
            return InputError;
        }

        if (versionText.Equals("0x97", StringComparison.OrdinalIgnoreCase) ||
            versionText.Equals("97", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadOption(operands, "--desay97-profile", out var profileText) ||
                !TryParseDesay97Profile(profileText, out var profile))
            {
                await error.WriteLineAsync("Desay 0x97 requires --desay97-profile <standard|benz-palm>; it is never auto-detected.");
                return UsageError;
            }

            var desayReport = await new Desay97NdsInspector().InspectAsync(path, profile, sourceAdapterId: sourceAdapterId);
            if (json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(desayReport, JsonOptions));
            }
            else
            {
                await WriteDesay97InspectionAsync(desayReport, output, error);
            }
            return desayReport.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                ? DecodeError
                : 0;
        }

        if (!CommonEventBufferDecoder.TryParseVersion(versionText, out var version))
        {
            await error.WriteLineAsync($"Unsupported Event Buffer Version: {versionText}");
            return UsageError;
        }

        var report = await new CommonNdsInspector().InspectAsync(path, version, sourceAdapterId);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            await WriteInspectionAsync(report, output, error);
        }

        return report.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            ? DecodeError
            : 0;
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

    private static bool TryParseDesay97Profile(string value, out Desay97Profile profile)
    {
        var normalized = value.Trim().Replace('_', '-').ToLowerInvariant();
        profile = normalized switch
        {
            "standard" => Desay97Profile.Standard,
            "benz" or "benz-palm" => Desay97Profile.BenzPalm,
            _ => default,
        };
        return normalized is "standard" or "benz" or "benz-palm";
    }

    private static string ProfileText(Desay97Profile profile) => profile switch
    {
        Desay97Profile.Standard => "Standard",
        Desay97Profile.BenzPalm => "Benz Palm",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

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
        await output.WriteLineAsync($"Format  Common {VersionText(report.Version)} (operator selected)");
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

    private static string VersionText(CommonEventBufferVersion version) => version switch
    {
        CommonEventBufferVersion.V82 => "0x82",
        CommonEventBufferVersion.V83 => "0x83",
        CommonEventBufferVersion.V84 => "0x84",
        CommonEventBufferVersion.V85 => "0x85",
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

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
                                 [--source-adapter <id>] [--json]
                                           Decode Common records from any supported source
              nvt-replay inspect <file> --event-buffer-version 0x97
                                 --desay97-profile <standard|benz-palm>
                                 [--source-adapter <id>] [--json]
                                           Assemble and decode Desay reads
              nvt-replay analyze <file> --event-buffer-version <version>
                                 --output <directory> [--range <first:last>]
                                 [--heatmap-size <width>x<height>]
                                 [--source-adapter <id>]
                                 [--desay97-profile <standard|benz-palm>] [--json]
                                           Export deterministic analysis JSON/CSV/PNG

            Source detection is ranked and may require --source-adapter when ambiguous.
            Event Buffer Version and Benz Palm remain explicit operator choices.
            """);
    }
}
