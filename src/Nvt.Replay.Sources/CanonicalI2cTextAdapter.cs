using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed class CanonicalI2cTextAdapter : ISourceAdapter
{
    public const string Magic = "# NVT-I2C-TXT 1";
    public string Id => "canonical-i2c-txt";
    public string DisplayName => "Canonical NVT I2C TXT";

    public async ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return None("File extension is not .txt.");
        using var reader = File.OpenText(path);
        var first = await reader.ReadLineAsync(cancellationToken);
        return first == Magic
            ? new(Id, DisplayName, ProbeConfidence.High, ["Matched the versioned NVT-I2C-TXT 1 magic header."])
            : None("Canonical NVT-I2C-TXT magic header was not found.");
    }

    public async IAsyncEnumerable<SourceRecord> ReadAsync(
        SourceOpenContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(context.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var layout = TextFileLayout.Detect(context.Path);
        var header = await reader.ReadLineAsync(cancellationToken);
        if (header != Magic)
        {
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "CANONICAL_INVALID_HEADER", "Canonical I2C TXT magic header is missing or unsupported.", 1);
            yield break;
        }

        var tracker = new NvtRegisterTracker();
        var lineNumber = 1;
        long byteOffset = layout.FirstLineOffset + layout.Advance(header);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var lineOffset = byteOffset;
            byteOffset += layout.Advance(line);
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            SourceRecord? record;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var index = root.GetProperty("index").GetInt64();
                var timestamp = ReadTimestamp(root, out var timestampMissing);
                if (timestampMissing)
                {
                    SourceDiagnostics.Report(context, DiagnosticSeverity.Warning, "CANONICAL_TIMESTAMP_MISSING", "Canonical record has no timestamp; replay will use the synthetic Frame clock.", lineNumber, lineOffset);
                }
                var operation = ReadOperation(root);
                var dataHex = root.TryGetProperty("read_data", out var readData)
                    ? readData.GetString()
                    : root.TryGetProperty("data", out var data) ? data.GetString() : null;
                if (string.IsNullOrWhiteSpace(dataHex)) throw new FormatException("read_data is missing");
                var bytes = Convert.FromHexString(dataHex.Replace(" ", string.Empty, StringComparison.Ordinal));
                var sourceMeta = root.TryGetProperty("source_meta", out var meta) && meta.ValueKind == JsonValueKind.Object ? meta : default;
                var slave = ReadInteger(root, "i2c_slave_address") ?? ReadInteger(sourceMeta, "i2c_address") ?? 1;
                var address = ReadInteger(root, "address") ?? ReadInteger(sourceMeta, "register_address");
                var commands = ReadCommands(root, sourceMeta);
                var acks = ReadAcks(root, sourceMeta);
                var fields = FlattenFields(sourceMeta);
                fields["adapter"] = "canonical";
                record = new SourceRecord(
                    index,
                    $"{context.SourceId}:L{lineNumber}",
                    timestamp,
                    operation,
                    slave == 1 ? "TP" : $"I2C 0x{slave:X2}",
                    address is null ? null : checked((uint)address.Value),
                    bytes.Length,
                    bytes,
                    line,
                    new SourceLocation(lineOffset, lineNumber),
                    new I2cTransport(slave, commands, acks, null),
                    fields);
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
            {
                tracker.ResetEvidence();
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "CANONICAL_MALFORMED_RECORD", $"Invalid canonical record at line {lineNumber}: {exception.Message}", lineNumber, lineOffset);
                continue;
            }
            yield return tracker.Observe(record);
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root, out bool missing)
    {
        missing = !root.TryGetProperty("timestamp_s", out var value) || value.ValueKind == JsonValueKind.Null;
        if (missing) return null;
        var seconds = value.GetDouble();
        if (!double.IsFinite(seconds)) throw new FormatException("timestamp_s is not finite");
        return DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(seconds);
    }

    private static BusOperation ReadOperation(JsonElement root)
    {
        var value = root.TryGetProperty("operation", out var operation) ? operation.GetString() : "Read";
        return value?.ToUpperInvariant() switch
        {
            "R" or "READ" => BusOperation.Read,
            "W" or "WRITE" => BusOperation.Write,
            _ => throw new FormatException($"unsupported operation '{value}'"),
        };
    }

    private static int? ReadInteger(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetInt32();
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(text, CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<IReadOnlyList<byte>> ReadCommands(JsonElement root, JsonElement meta)
    {
        var result = new List<IReadOnlyList<byte>>();
        if (root.TryGetProperty("write_commands", out var commands) && commands.ValueKind == JsonValueKind.Array)
        {
            result.AddRange(commands.EnumerateArray().Select(item => (IReadOnlyList<byte>)Convert.FromHexString((item.GetString() ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal))));
        }
        else if (meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("write_data", out var writeData) && writeData.ValueKind == JsonValueKind.String)
        {
            result.Add(Convert.FromHexString((writeData.GetString() ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal)));
        }
        return result;
    }

    private static IReadOnlyList<bool> ReadAcks(JsonElement root, JsonElement meta)
    {
        var source = root.TryGetProperty("acks", out var direct) ? direct :
            meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("acks", out var nested) ? nested : default;
        if (source.ValueKind != JsonValueKind.Array) return [];
        return source.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.True || item.ValueKind == JsonValueKind.Number && item.GetInt32() == 0).ToArray();
    }

    private static Dictionary<string, string> FlattenFields(JsonElement meta)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (meta.ValueKind != JsonValueKind.Object) return fields;
        foreach (var property in meta.EnumerateObject())
        {
            fields[$"source_meta.{property.Name}"] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }
        return fields;
    }

    private SourceProbeResult None(string reason) => new(Id, DisplayName, ProbeConfidence.None, [reason]);
}
