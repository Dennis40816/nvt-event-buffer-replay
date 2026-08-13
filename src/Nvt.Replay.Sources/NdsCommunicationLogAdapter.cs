using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed partial class NdsCommunicationLogAdapter : ISourceAdapter
{
    private const int ProbeLineLimit = 40;

    public string Id => "nds-communication-log";

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
            ? [$"Matched {matched} NDS Read/Write record(s) in the first {inspected} non-empty lines."]
            : ["No NDS timestamp + Read/Write + target + address header was found."];

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

        PendingRecord? pending = null;
        long recordIndex = 0;
        var lineNumber = 0;
        long byteOffset = 0;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            var lineOffset = byteOffset;
            byteOffset += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

            var match = HeaderPattern().Match(line);
            if (match.Success)
            {
                if (pending is not null)
                {
                    yield return pending.ToSourceRecord(recordIndex++, context.SourceId);
                }

                pending = PendingRecord.FromHeader(match, line, lineNumber, lineOffset);
                if (pending.IsComplete)
                {
                    yield return pending.ToSourceRecord(recordIndex++, context.SourceId);
                    pending = null;
                }

                continue;
            }

            if (pending is not null && ByteTokenPattern().IsMatch(line))
            {
                pending.Append(line);
                if (pending.IsComplete)
                {
                    yield return pending.ToSourceRecord(recordIndex++, context.SourceId);
                    pending = null;
                }
            }
        }

        if (pending is not null)
        {
            yield return pending.ToSourceRecord(recordIndex, context.SourceId);
        }
    }

    [GeneratedRegex(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}:\d{3})\s+(?<operation>Read|Write)\s+(?<target>\S+)\s+(?<address>0x[0-9A-Fa-f]+)\s+(?<count>\d+)\s*(?<data>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"0x[0-9A-Fa-f]{2}", RegexOptions.CultureInvariant)]
    private static partial Regex ByteTokenPattern();

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

        public bool IsComplete => data.Count >= DeclaredByteCount;

        public static PendingRecord FromHeader(Match match, string line, int lineNumber, long byteOffset)
        {
            var timestamp = DateTimeOffset.ParseExact(
                match.Groups["timestamp"].Value,
                "yyyy-MM-dd HH:mm:ss:fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal);
            var operation = Enum.Parse<BusOperation>(match.Groups["operation"].Value, ignoreCase: true);
            var address = uint.Parse(match.Groups["address"].Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
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
                if (data.Count < DeclaredByteCount)
                {
                    data.Add(byte.Parse(match.ValueSpan[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                }
            }
        }

        public SourceRecord ToSourceRecord(long index, string sourceId)
        {
            return new SourceRecord(
                index,
                $"{sourceId}:L{LineNumber}",
                Timestamp,
                Operation,
                Target,
                Address,
                DeclaredByteCount,
                data.ToArray(),
                rawText.ToString(),
                new SourceLocation(ByteOffset, LineNumber));
        }
    }
}
