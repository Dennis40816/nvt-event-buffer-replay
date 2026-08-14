using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public sealed record AnalysisOutputResult(
    string Directory,
    string ReportJson,
    string EventsJson,
    string EventsCsv,
    string DiagnosticsJson,
    string DiagnosticsCsv,
    string ManifestJson,
    string HeatmapPng);

public sealed class AnalysisOutputWriter
{
    private const string JournalFileName = "analysis-journal.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<AnalysisOutputResult> WriteAsync(
        string directory,
        CaptureAnalysisReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var result = new AnalysisOutputResult(
            fullDirectory,
            Path.Combine(fullDirectory, "analysis-report.json"),
            Path.Combine(fullDirectory, "events.json"),
            Path.Combine(fullDirectory, "events.csv"),
            Path.Combine(fullDirectory, "diagnostics.json"),
            Path.Combine(fullDirectory, "diagnostics.csv"),
            Path.Combine(fullDirectory, "manifest.json"),
            Path.Combine(fullDirectory, "heatmap.png"));

        var journalPath = Path.Combine(fullDirectory, JournalFileName);
        var previousManifestHash = File.Exists(result.ManifestJson)
            ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(result.ManifestJson, cancellationToken)))
            : null;
        await WriteJsonAsync(journalPath, new AnalysisRecoveryJournal(
            "1.0",
            DateTimeOffset.UtcNow,
            report.Manifest.SourceSha256,
            previousManifestHash,
            ["analysis-report.json", "events.json", "events.csv", "diagnostics.json", "diagnostics.csv", "heatmap.png", "manifest.json"]), cancellationToken);

        await WriteJsonAsync(result.ReportJson, report, cancellationToken);
        await WriteJsonAsync(result.EventsJson, report.Events, cancellationToken);
        await WriteTextAsync(result.EventsCsv, EventsCsv(report.Events), cancellationToken);
        await WriteJsonAsync(result.DiagnosticsJson, report.Diagnostics, cancellationToken);
        await WriteTextAsync(result.DiagnosticsCsv, DiagnosticsCsv(report.Diagnostics), cancellationToken);
        await AtomicOutput.WriteAsync(
            result.HeatmapPng,
            (stream, token) => DeterministicPng.WriteHeatmapAsync(
                stream,
                report.Hotspot,
                report.Manifest.HeatmapPixelWidth,
                report.Manifest.HeatmapPixelHeight,
                token),
            cancellationToken);
        await WriteJsonAsync(result.ManifestJson, report.Manifest, cancellationToken);
        File.Delete(journalPath);
        return result;
    }

    private static Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        AtomicOutput.WriteAsync(
            path,
            (stream, token) => JsonSerializer.SerializeAsync(stream, value, JsonOptions, token),
            cancellationToken);

    private static Task WriteTextAsync(string path, string value, CancellationToken cancellationToken) =>
        AtomicOutput.WriteAsync(
            path,
            async (stream, token) =>
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
                await writer.WriteAsync(value.AsMemory(), token);
                await writer.FlushAsync(token);
            },
            cancellationToken);

    private static string EventsCsv(IEnumerable<AnalysisEvent> events)
    {
        var result = new StringBuilder("event_id,logical_index,source_record_id,physical_record_ids,captured_time,frame_time,timestamp_synthetic,host_state_eligible,global_palm,contact_id,contact_type,contact_status,x,y,invalid\n");
        foreach (var item in events)
        {
            var contacts = item.ReportedContacts.Count == 0 ? new AnalysisContact?[] { null } : item.ReportedContacts.Cast<AnalysisContact?>();
            foreach (var contact in contacts)
            {
                AppendRow(result,
                    item.Id,
                    item.LogicalIndex.ToString(CultureInfo.InvariantCulture),
                    item.SourceRecordId,
                    string.Join(';', item.PhysicalRecordIds),
                    item.CapturedTime.ToString("c", CultureInfo.InvariantCulture),
                    item.FrameTime.ToString("c", CultureInfo.InvariantCulture),
                    item.TimestampSynthetic.ToString(CultureInfo.InvariantCulture),
                    item.HostStateEligible.ToString(CultureInfo.InvariantCulture),
                    item.GlobalPalm.ToString(CultureInfo.InvariantCulture),
                    contact?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    contact?.Type.ToString() ?? string.Empty,
                    contact?.Status.ToString() ?? string.Empty,
                    contact?.X.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    contact?.Y.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    contact?.Invalid.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }
        return result.ToString();
    }

    private static string DiagnosticsCsv(IEnumerable<AnalysisDiagnostic> diagnostics)
    {
        var result = new StringBuilder("diagnostic_id,severity,code,message,source_record_id,line_number,byte_offset,event_ids,details_json\n");
        foreach (var item in diagnostics)
        {
            AppendRow(result,
                item.Id,
                item.Severity.ToString(),
                item.Code,
                item.Message,
                item.SourceRecordId,
                item.Location.LineNumber.ToString(CultureInfo.InvariantCulture),
                item.Location.ByteOffset.ToString(CultureInfo.InvariantCulture),
                string.Join(';', item.EventIds),
                JsonSerializer.Serialize(item.Details, JsonOptions));
        }
        return result.ToString();
    }

    private static void AppendRow(StringBuilder result, params string[] values)
    {
        result.AppendJoin(',', values.Select(Csv));
        result.Append('\n');
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
}

internal sealed record AnalysisRecoveryJournal(
    string SchemaVersion,
    DateTimeOffset StartedUtc,
    string SourceSha256,
    string? PreviousManifestSha256,
    IReadOnlyList<string> TargetFiles);
