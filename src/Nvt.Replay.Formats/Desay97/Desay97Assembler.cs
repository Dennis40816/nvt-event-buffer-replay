using System.Globalization;
using Nvt.Replay.Core;

namespace Nvt.Replay.Formats.Desay97;

public sealed class Desay97Assembler
{
    public Desay97Assembler(uint eventBufferBase = 0x99000, int targetI2cAddress = 0x01)
    {
        if (targetI2cAddress is < 0 or > 0x7F)
            throw new ArgumentOutOfRangeException(
                nameof(targetI2cAddress),
                targetI2cAddress,
                "I2C slave address must be a 7-bit value from 0x00 through 0x7F.");
        EventBufferBase = eventBufferBase;
        TargetI2cAddress = targetI2cAddress;
    }

    public uint EventBufferBase { get; }
    public int TargetI2cAddress { get; }

    public Desay97AssemblyResult Assemble(
        IEnumerable<SourceRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var packets = new List<Desay97AssembledPacket>();
        var diagnostics = new List<ReplayDiagnostic>();
        SourceRecord? pending = null;

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isEventRead = IsEventRead(record);
            if (pending is null)
            {
                if (!isEventRead)
                {
                    continue;
                }

                if (record.Data.Count == 1 && record.DeclaredByteCount is null or 1)
                {
                    pending = record;
                }
                else if (record.DeclaredByteCount == 1 || record.Data.Count == 1)
                {
                    diagnostics.Add(Diagnostic(
                        record,
                        "DESAY97_PROBE_LENGTH_MISMATCH",
                        $"Desay 0x97 phase one must declare and contain one byte; declared {Display(record.DeclaredByteCount)}, captured {record.Data.Count}."));
                }
                else
                {
                    diagnostics.Add(Diagnostic(
                        record,
                        "DESAY97_ORPHAN_SECOND_PHASE",
                        $"Desay 0x97 phase two ({record.Data.Count} bytes) arrived without a one-byte phase one probe."));
                }
                continue;
            }

            if (!isEventRead)
            {
                diagnostics.Add(Diagnostic(
                    record,
                    "DESAY97_INTERVENING_TRANSACTION",
                    "Another physical transaction arrived between Desay 0x97 phases; the pending pair was discarded.",
                    new Dictionary<string, string> { ["pending_probe"] = pending.StableId }));
                diagnostics.Add(MissingPhase(pending, "The phase one probe was not followed immediately by phase two."));
                pending = null;
                continue;
            }

            if (record.Data.Count == 1 && record.DeclaredByteCount is null or 1)
            {
                diagnostics.Add(Diagnostic(
                    record,
                    "DESAY97_DUPLICATE_FIRST_PHASE",
                    "A second one-byte probe arrived before phase two; the older probe was discarded.",
                    new Dictionary<string, string> { ["discarded_probe"] = pending.StableId }));
                diagnostics.Add(MissingPhase(pending, "A newer phase one probe replaced this pending probe."));
                pending = record;
                continue;
            }

            var expectedLength = 1 + (5 * (pending.Data[0] & 0x0F)) + 1;
            if (record.Data.Count != expectedLength ||
                record.DeclaredByteCount is { } declared && declared != expectedLength)
            {
                diagnostics.Add(Diagnostic(
                    record,
                    "DESAY97_LENGTH_MISMATCH",
                    $"Desay 0x97 phase two requires {expectedLength} bytes for touch count {pending.Data[0] & 0x0F}; declared {Display(record.DeclaredByteCount)}, captured {record.Data.Count}.",
                    new Dictionary<string, string>
                    {
                        ["expected"] = expectedLength.ToString(CultureInfo.InvariantCulture),
                        ["actual"] = record.Data.Count.ToString(CultureInfo.InvariantCulture),
                        ["declared"] = Display(record.DeclaredByteCount),
                    }));
                pending = null;
                continue;
            }

            if (record.Data[0] != pending.Data[0])
            {
                diagnostics.Add(Diagnostic(
                    record,
                    "DESAY97_HEADER_MISMATCH",
                    $"Phase two repeated header 0x{record.Data[0]:X2}, but phase one captured 0x{pending.Data[0]:X2}."));
                pending = null;
                continue;
            }

            var computedCrc = Crc8Poly1D.Compute(record.Data.Take(record.Data.Count - 1).ToArray());
            if (computedCrc != record.Data[^1])
            {
                diagnostics.Add(Diagnostic(
                    record,
                    "DESAY97_CRC_MISMATCH",
                    $"Desay 0x97 CRC mismatch: expected 0x{computedCrc:X2}, captured 0x{record.Data[^1]:X2}."));
                pending = null;
                continue;
            }

            packets.Add(new Desay97AssembledPacket(
                pending,
                record,
                record.Data.ToArray(),
                record.Data[^1],
                computedCrc));
            pending = null;
        }

        if (pending is not null)
        {
            diagnostics.Add(MissingPhase(pending, "Capture ended before Desay 0x97 phase two arrived."));
        }

        if (packets.Count == 0)
        {
            diagnostics.Add(new ReplayDiagnostic(
                DiagnosticSeverity.Warning,
                "NO_DESAY97_PACKETS",
                "No valid Desay 0x97 two-transaction packet was assembled.",
                string.Empty,
                new SourceLocation(0, 0)));
        }

        return new Desay97AssemblyResult(packets, diagnostics);
    }

    private bool IsEventRead(SourceRecord record)
    {
        if (record.Operation != BusOperation.Read || record.Address != EventBufferBase) return false;
        return record.I2c is { } i2c
            ? i2c.SlaveAddress == TargetI2cAddress
            : TargetI2cAddress == 0x01 &&
              record.Target.Equals("TP", StringComparison.OrdinalIgnoreCase);
    }

    private static ReplayDiagnostic MissingPhase(SourceRecord source, string message) =>
        Diagnostic(source, "DESAY97_MISSING_SECOND_PHASE", message);

    private static string Display(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    private static ReplayDiagnostic Diagnostic(
        SourceRecord source,
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(DiagnosticSeverity.Warning, code, message, source.StableId, source.Location, details);
}
