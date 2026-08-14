using System.Text.Json;
using System.Text.Json.Serialization;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
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
            default:
                await error.WriteLineAsync("Unknown or incomplete command. Run 'nvt-replay help'.");
                return UsageError;
        }
    }

    private static async Task<int> InspectAsync(
        string[] operands,
        TextWriter output,
        TextWriter error,
        bool json)
    {
        if (operands.Length < 3 || !TryReadOption(operands, "--event-buffer-version", out var versionText))
        {
            await error.WriteLineAsync("Usage: nvt-replay inspect <file> --event-buffer-version <0x82|0x83|0x84|0x85|0x97> [--desay97-profile <standard|benz-palm>] [--json]");
            return UsageError;
        }

        var path = operands[0];
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

            var desayReport = await new Desay97NdsInspector().InspectAsync(path, profile);
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

        var report = await new CommonNdsInspector().InspectAsync(path, version);
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
              nvt-replay inspect <file> --event-buffer-version <0x82|0x83|0x84|0x85> [--json]
                                           Decode Common NDS Paint records
              nvt-replay inspect <file> --event-buffer-version 0x97
                                 --desay97-profile <standard|benz-palm> [--json]
                                           Assemble and decode Desay NDS reads

            Source detection is advisory. Event Buffer Version and Benz Palm remain explicit choices.
            """);
    }
}
