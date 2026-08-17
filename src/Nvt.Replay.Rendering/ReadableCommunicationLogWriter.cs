using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public sealed record ReadableCommunicationLogResult(
    string CsvPath,
    string JsonlPath,
    int RecordCount,
    int AnnotatedRegisterCount,
    int AmbiguousRegisterCount);

public sealed class ReadableCommunicationLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<ReadableCommunicationLogResult> WriteAsync(
        string directory,
        IReadOnlyList<SourceRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var csvPath = Path.Combine(fullDirectory, "communication-readable.csv");
        var jsonlPath = Path.Combine(fullDirectory, "communication-readable.jsonl");
        var rows = records.Select(ToRow).ToArray();

        await AtomicOutput.WriteAsync(csvPath, async (stream, token) =>
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

        await AtomicOutput.WriteAsync(jsonlPath, async (stream, token) =>
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

    private static ReadableCommunicationRow ToRow(SourceRecord record)
    {
        var fields = record.SourceFields;
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
    }.Select(Csv));

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";

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
