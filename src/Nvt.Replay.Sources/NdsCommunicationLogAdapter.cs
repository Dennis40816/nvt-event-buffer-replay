using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed partial class NdsCommunicationLogAdapter : ISourceAdapter
{
    public const string AdapterId = "nds-communication-log";
    public const string AddressSemanticsField = "address_semantics";
    public const string AbsoluteRegisterAddress = "absolute_register";
    public const string I2cSlaveAddress = "i2c_slave";
    public const string AccessSemanticsField = "access_semantics";
    public const string EventBufferRead = "event_buffer_read";
    public const string RegisterRead = "register_read";
    public const string RegisterWrite = "register_write";
    private const int ProbeLineLimit = 40;

    public string Id => AdapterId;

    public string DisplayName => "NDS communication log";

    public async ValueTask<SourceProbeResult> ProbeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var matched = 0;
        var inspected = 0;
        using var reader = File.OpenText(path);
        while (inspected < ProbeLineLimit && await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            inspected++;
            if (HeaderPattern().IsMatch(line))
            {
                matched++;
            }
        }

        var confidence = matched switch
        {
            >= 3 => ProbeConfidence.High,
            >= 1 => ProbeConfidence.Medium,
            _ => ProbeConfidence.None,
        };

        IReadOnlyList<string> reasons = matched > 0
            ? [$"Matched {matched} NDS Paint/Read/Write record(s) in the first {inspected} non-empty lines."]
            : ["No NDS timestamp + Paint/Read/Write + target + address header was found."];

        return new SourceProbeResult(Id, DisplayName, confidence, reasons);
    }

    public async IAsyncEnumerable<SourceRecord> ReadAsync(
        SourceOpenContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var stream = new FileStream(
            context.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var layout = TextFileLayout.Detect(context.Path);

        PendingRecord? pending = null;
        long recordIndex = 0;
        var lineNumber = 0;
        long byteOffset = layout.FirstLineOffset;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var lineOffset = byteOffset;
            byteOffset += layout.Advance(line);

            var match = HeaderPattern().Match(line);
            if (match.Success)
            {
                if (pending is not null)
                {
                    yield return pending.ToSourceRecord(recordIndex++, context.SourceId);
                }

                try
                {
                    pending = PendingRecord.FromHeader(match, line, lineNumber, lineOffset);
                }
                catch (Exception exception) when (exception is FormatException or OverflowException)
                {
                    pending = null;
                    SourceDiagnostics.Report(
                        context,
                        DiagnosticSeverity.Warning,
                        "NDS_MALFORMED_RECORD",
                        $"Invalid NDS record at line {lineNumber}: {exception.Message}",
                        lineNumber,
                        lineOffset);
                }
                continue;
            }

            if (HeaderCandidatePattern().IsMatch(line))
            {
                if (pending is not null)
                {
                    yield return pending.ToSourceRecord(recordIndex++, context.SourceId);
                    pending = null;
                }
                SourceDiagnostics.Report(
                    context,
                    DiagnosticSeverity.Warning,
                    "NDS_MALFORMED_RECORD",
                    $"Invalid NDS record header at line {lineNumber}; the row was discarded.",
                    lineNumber,
                    lineOffset);
                continue;
            }

            if (pending is not null && ContinuationPattern().IsMatch(line))
            {
                pending.Append(line);
            }
        }

        if (pending is not null)
        {
            yield return pending.ToSourceRecord(recordIndex, context.SourceId);
        }
    }

    [GeneratedRegex(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}:\d{3})\s+(?<operation>Paint|Read|Write)\s+(?<target>\S+)\s+(?<address>0x[0-9A-Fa-f]+)\s+(?<count>\d+)\s*(?<data>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(
        @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}:\d{3}\s+(?:Paint|Read|Write)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderCandidatePattern();

    [GeneratedRegex(@"0x[0-9A-Fa-f]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex ByteTokenPattern();

    [GeneratedRegex(
        @"^\s*0x[0-9A-Fa-f]{2}(?:\s+0x[0-9A-Fa-f]{2})*\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationPattern();

    private sealed class PendingRecord
    {
        private readonly List<byte> data = [];
        private readonly StringBuilder rawText = new();

        private PendingRecord(
            DateTimeOffset timestamp,
            BusOperation operation,
            string target,
            uint address,
            int declaredByteCount,
            int lineNumber,
            long byteOffset)
        {
            Timestamp = timestamp;
            Operation = operation;
            Target = target;
            Address = address;
            DeclaredByteCount = declaredByteCount;
            LineNumber = lineNumber;
            ByteOffset = byteOffset;
        }

        public DateTimeOffset Timestamp { get; }

        public BusOperation Operation { get; }

        public string Target { get; }

        public uint Address { get; }

        public int DeclaredByteCount { get; }

        public int LineNumber { get; }

        public long ByteOffset { get; }

        public static PendingRecord FromHeader(Match match, string line, int lineNumber, long byteOffset)
        {
            var timestamp = DateTimeOffset.ParseExact(
                match.Groups["timestamp"].Value,
                "yyyy-MM-dd HH:mm:ss:fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal);
            var operation = Enum.Parse<BusOperation>(match.Groups["operation"].Value, ignoreCase: true);
            var address = uint.Parse(match.Groups["address"].Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (operation == BusOperation.Paint && address is > 0x7F and < 0x100)
            {
                throw new FormatException(
                    $"Paint address 0x{address:X2} is neither a 7-bit I2C slave address nor an absolute register address (>= 0x100).");
            }
            var count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
            var pending = new PendingRecord(timestamp, operation, match.Groups["target"].Value, address, count, lineNumber, byteOffset);
            pending.rawText.Append(line);
            pending.AppendBytes(match.Groups["data"].Value);
            return pending;
        }

        public void Append(string line)
        {
            if (rawText.Length > 0)
            {
                rawText.AppendLine();
            }

            rawText.Append(line);
            AppendBytes(line);
        }

        private void AppendBytes(string text)
        {
            foreach (Match match in ByteTokenPattern().Matches(text))
            {
                data.Add(byte.Parse(match.ValueSpan[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }

        public SourceRecord ToSourceRecord(long index, string sourceId)
        {
            var paintCarriesI2cAddress = Operation == BusOperation.Paint && Address <= 0x7F;
            var accessSemantics = Operation switch
            {
                BusOperation.Paint when paintCarriesI2cAddress => EventBufferRead,
                BusOperation.Paint or BusOperation.Read => RegisterRead,
                BusOperation.Write => RegisterWrite,
                _ => throw new InvalidOperationException($"Unsupported NDS operation {Operation}."),
            };
            var sourceFields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["adapter"] = AdapterId,
                ["dialect"] = "nds-communication-log",
                ["raw_address"] = paintCarriesI2cAddress ? $"0x{Address:X2}" : $"0x{Address:X}",
                [AddressSemanticsField] = paintCarriesI2cAddress ? I2cSlaveAddress : AbsoluteRegisterAddress,
                [AccessSemanticsField] = accessSemantics,
            };
            var transport = paintCarriesI2cAddress
                ? new I2cTransport(checked((int)Address), [], [], null)
                : null;
            return NvtRegisterCatalog.Annotate(new SourceRecord(
                index,
                $"{sourceId}:L{LineNumber}",
                Timestamp,
                Operation,
                Target,
                paintCarriesI2cAddress ? null : Address,
                DeclaredByteCount,
                data.ToArray(),
                rawText.ToString(),
                new SourceLocation(ByteOffset, LineNumber),
                transport,
                sourceFields));
        }
    }
}
