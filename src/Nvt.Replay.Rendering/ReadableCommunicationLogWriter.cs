using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nvt.Core.Csv;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

/// <summary>Contains output file paths and row counts for readable communication log exports.</summary>
/// <param name="CsvPath">The absolute path of the exported CSV communication log.</param>
/// <param name="JsonlPath">The absolute path of the exported JSONL communication log.</param>
/// <param name="RecordCount">The number of source records exported as rows.</param>
/// <param name="AnnotatedRegisterCount">The number of exported rows with a non-null register display name.</param>
/// <param name="AmbiguousRegisterCount">The number of exported rows whose register profile resolution is ambiguous.</param>
public sealed record ReadableCommunicationLogResult(
    string CsvPath,
    string JsonlPath,
    int RecordCount,
    int AnnotatedRegisterCount,
    int AmbiguousRegisterCount);

/// <summary>Exports source records and register annotations as readable CSV and JSONL communication logs.</summary>
public sealed class ReadableCommunicationLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Writes source records to readable CSV and JSONL logs in the specified directory.</summary>
    public async Task<ReadableCommunicationLogResult> WriteAsync(
        string directory,
        IReadOnlyList<SourceRecord> records,
        CancellationToken cancellationToken = default)
        => await WriteAsync(directory, records, null, cancellationToken);

    /// <summary>Writes source records to readable CSV and JSONL logs with an optional register annotation projection.</summary>
    public async Task<ReadableCommunicationLogResult> WriteAsync(
        string directory,
        IReadOnlyList<SourceRecord> records,
        RegisterAnnotationProjection? registerAnnotations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var csvPath = Path.Combine(fullDirectory, "communication-readable.csv");
        var jsonlPath = Path.Combine(fullDirectory, "communication-readable.jsonl");
        var rows = records.Select(record => ToRow(record, registerAnnotations)).ToArray();

        await Nvt.Core.IO.AtomicOutput.WriteAsync(csvPath, async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
            await writer.WriteLineAsync("index,stable_id,pc_time,operation,target,i2c_slave,address,declared_bytes,actual_bytes,register_profile,profile_resolution,region,register,meaning,raw_hex,line_number,byte_offset".AsMemory(), token);
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(CsvRow(row).AsMemory(), token);
            }
            await writer.FlushAsync(token);
        }, cancellationToken);

        await Nvt.Core.IO.AtomicOutput.WriteAsync(jsonlPath, async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions).AsMemory(), token);
            }
            await writer.FlushAsync(token);
        }, cancellationToken);

        return new ReadableCommunicationLogResult(
            csvPath,
            jsonlPath,
            rows.Length,
            rows.Count(row => row.Register is not null),
            rows.Count(row => row.ProfileResolution == "ambiguous"));
    }

    private static ReadableCommunicationRow ToRow(
        SourceRecord record,
        RegisterAnnotationProjection? registerAnnotations)
    {
        var fields = registerAnnotations?.EffectiveFields(record) ?? record.SourceFields;
        return new ReadableCommunicationRow(
            record.Index,
            record.StableId,
            record.Timestamp,
            record.Operation,
            record.Target,
            record.I2c is { } i2c ? $"0x{i2c.SlaveAddress:X2}" : null,
            record.Address is { } address ? $"0x{address:X}" : fields?.GetValueOrDefault("register_address"),
            record.DeclaredByteCount,
            record.Data.Count,
            fields?.GetValueOrDefault("register_profile"),
            fields?.GetValueOrDefault("register_profile_resolution"),
            fields?.GetValueOrDefault("register_region"),
            fields?.GetValueOrDefault("register_display_name"),
            fields?.GetValueOrDefault("register_meaning"),
            string.Join(' ', record.Data.Select(value => value.ToString("X2", CultureInfo.InvariantCulture))),
            record.Location.LineNumber,
            record.Location.ByteOffset);
    }

    private static string CsvRow(ReadableCommunicationRow row) => string.Join(',', new[]
    {
        row.Index.ToString(CultureInfo.InvariantCulture),
        row.StableId,
        row.PcTime?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        row.Operation.ToString(),
        row.Target,
        row.I2cSlave ?? string.Empty,
        row.Address ?? string.Empty,
        row.DeclaredBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        row.ActualBytes.ToString(CultureInfo.InvariantCulture),
        row.RegisterProfile ?? string.Empty,
        row.ProfileResolution ?? string.Empty,
        row.Region ?? string.Empty,
        row.Register ?? string.Empty,
        row.Meaning ?? string.Empty,
        row.RawHex,
        row.LineNumber.ToString(CultureInfo.InvariantCulture),
        row.ByteOffset.ToString(CultureInfo.InvariantCulture),
    }.Select(CsvQuoting.Quote));

    private sealed record ReadableCommunicationRow(
        long Index,
        string StableId,
        DateTimeOffset? PcTime,
        BusOperation Operation,
        string Target,
        string? I2cSlave,
        string? Address,
        int? DeclaredBytes,
        int ActualBytes,
        string? RegisterProfile,
        string? ProfileResolution,
        string? Region,
        string? Register,
        string? Meaning,
        string RawHex,
        int LineNumber,
        long ByteOffset);
}
