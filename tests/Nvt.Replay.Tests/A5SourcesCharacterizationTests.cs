using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

public sealed class A5SourcesCharacterizationTests : IDisposable
{
    private const string SourceId = "a5-synthetic";
    private const string SaleaeHeader = "Time [s],Packet ID,Address,Data,Read/Write,ACK/NAK\n";
    private const string KingstHeader = "Time[s],Packet ID,Address,Read/Write,Data\n";
    private const string KingstLegacyHeader = "Time [s],Packet ID,Address,Data,Read/Write,ACK\n";
    private const string DslHeader = "Id,Time[ns],1:I²C: Address/Data\n";
    private const string AcuteHeader = "Timestamp,Status,Address(8b),D0,D1\n";
    private const string NdsGood = "2026-07-29 14:52:41:241 Read TP 0x99060 1 0xA5";
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"a5-sources-{Guid.NewGuid():N}");

    public A5SourcesCharacterizationTests() => Directory.CreateDirectory(directory);

    // Contract: docs/source-adapters.md, Saleae NAK boundaries and malformed-row discard;
    // Selection contract, surviving records retain source locations and original evidence.
    [Theory]
    [InlineData("1,2,0x01,0xEE,Read,UNKNOWN")]
    [InlineData("1,2,0x01,0xEE,Read")]
    public async Task Saleae_bad_row_discards_pending_bytes_and_recovers_at_a_complete_transaction(string badRow)
    {
        const string pending = "1,1,0x01,0xDD,Read,ACK\n";
        const string good = "2,3,0x2A,0xA5,Read,NAK";
        var text = SaleaeHeader + pending + badRow + "\n" + good;
        var (records, diagnostics) = await Read(new SaleaeDecodedI2cAdapter(), Write(".csv", text));

        AssertDiagnostic(diagnostics, "SALEAE_MALFORMED_ROW", DiagnosticSeverity.Error, text, 3);
        var record = Assert.Single(records);
        AssertRecord(record, text, 4, "L4-L4", good, [0xA5]);
        Assert.Equal(0x2A, record.I2c?.SlaveAddress);
        Assert.Equal("I2C 0x2A", record.Target);
        Assert.Equal([false], record.I2c?.Acked);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2), record.Timestamp);
    }

    // Contract: docs/source-adapters.md, Saleae transactions terminate at NAK;
    // partial transactions are diagnosed/discarded without altering completed evidence.
    [Fact]
    public async Task Saleae_EOF_without_NAK_keeps_only_the_prior_completed_transaction()
    {
        const string good = "1,1,0x01,0xA5,Read,NAK";
        var text = SaleaeHeader + good + "\n2,2,0x01,0xEE,Read,ACK";
        var (records, diagnostics) = await Read(new SaleaeDecodedI2cAdapter(), Write(".csv", text));

        AssertRecord(Assert.Single(records), text, 2, "L2-L2", good, [0xA5]);
        AssertDiagnostic(diagnostics, "SALEAE_INCOMPLETE_TRANSACTION", DiagnosticSeverity.Error, text, 3);
    }

    // Width ambiguity: production accepts 0xE as 0x0E; docs use 0xNN without defining strict width.
    // Use an empty 0x token below as the unambiguous truncation boundary.
    // Contract: docs/source-adapters.md, KingstVIS complete transaction rows contain
    // space-separated bytes; malformed rows are diagnosed/discarded, never repaired.
    [Theory]
    [InlineData("1,-1,0x03,Read,0xEE")]
    [InlineData("1,2,0x03,Read,0x")]
    [InlineData("1,2,0x03,Read,")]
    public async Task KingstVis_bad_transaction_row_keeps_surrounding_evidence(string badRow)
    {
        const string first = "0,1,0x03,Read,0xA5";
        const string last = "2,3,0x55,Read,0x5A 0x97";
        var text = KingstHeader + first + "\n" + badRow + "\n" + last;
        var (records, diagnostics) = await Read(new KingstVisDecodedI2cAdapter(), Write(".csv", text));

        Assert.Equal(2, records.Count);
        AssertRecord(records[0], text, 2, "L2", first, [0xA5]);
        AssertRecord(records[1], text, 4, "L4", last, [0x5A, 0x97]);
        AssertDiagnostic(diagnostics, "KINGSTVIS_MALFORMED_ROW", DiagnosticSeverity.Error, text, 3);
        Assert.Equal(0x2A, records[1].I2c?.SlaveAddress);
        Assert.Empty(records[1].I2c!.Acked);
        Assert.Null(records[1].I2c!.AddressAcknowledged);
        Assert.Equal("unavailable", records[1].SourceFields?["ack_evidence"]);
    }

    // Contract: docs/source-adapters.md, KingstVIS legacy rows group by Packet ID
    // and validate direction; an invalid final packet is discarded with a diagnostic.
    [Fact]
    public async Task KingstVis_legacy_mixed_direction_packet_at_EOF_is_discarded()
    {
        const string good = "0,1,0x03,0xA5,Read,NAK";
        var text = KingstLegacyHeader + good + "\n1,2,0x03,0xDD,Read,ACK\n1,2,0x03,0xEE,Write,NAK";
        var (records, diagnostics) = await Read(new KingstVisDecodedI2cAdapter(), Write(".csv", text));

        AssertRecord(Assert.Single(records), text, 2, "L2-L2", good, [0xA5]);
        AssertDiagnostic(diagnostics, "KINGSTVIS_INVALID_PACKET", DiagnosticSeverity.Error, text, 3);
    }

    // Contract: docs/source-adapters.md, DSL Start/Stop assembly and partial discard;
    // Selection contract retains address ACK, per-byte ACK, timestamp and raw rows.
    [Fact]
    public async Task Dsl_nested_Start_discards_the_partial_transaction_and_retains_the_new_one()
    {
        const string good = "2,2000000000,Start\n2,2000000000,Address read: 2A\n2,2000000000,ACK\n" +
            "2,2000000000,Data read: A5\n2,2000000000,NACK\n2,2000000000,Stop";
        var text = DslHeader + "1,0,Start\n1,0,Address read: 01\n" + good;
        var (records, diagnostics) = await Read(new DslDecodedI2cAdapter(), Write(".csv", text));

        AssertDiagnostic(diagnostics, "DSL_NESTED_START", DiagnosticSeverity.Error, text, 4);
        var record = Assert.Single(records);
        AssertRecord(record, text, 4, "L4-L9", good.Replace("\n", Environment.NewLine), [0xA5]);
        Assert.Equal(0x2A, record.I2c?.SlaveAddress);
        Assert.True(record.I2c?.AddressAcknowledged);
        Assert.Equal([false], record.I2c?.Acked);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2), record.Timestamp);
        Assert.Equal("9", record.SourceFields?["row_end"]);
    }

    // Contract: docs/source-adapters.md, DSL ACK/NAK assembly and malformed/partial
    // transaction diagnostics. A Stop cannot complete read data with missing ACK evidence.
    [Fact]
    public async Task Dsl_Stop_with_missing_data_ACK_is_rejected()
    {
        var text = DslHeader + "1,0,Start\n1,0,Address read: 01\n1,0,ACK\n1,0,Data read: EE\n1,0,Stop";
        var (records, diagnostics) = await Read(new DslDecodedI2cAdapter(), Write(".csv", text));

        Assert.Empty(records);
        AssertDiagnostic(diagnostics, "DSL_INVALID_TRANSACTION", DiagnosticSeverity.Error, text, 6);
        Assert.Contains("data/ACK count mismatch", Assert.Single(diagnostics).Message, StringComparison.Ordinal);
    }

    // Contract: docs/source-adapters.md, DSL transactions require Stop; truncated
    // input must not publish a partial payload or damage a previous complete record.
    [Fact]
    public async Task Dsl_EOF_after_data_NACK_without_Stop_discards_only_the_partial_suffix()
    {
        const string good = "1,0,Start\n1,0,Address read: 01\n1,0,ACK\n1,0,Data read: A5\n1,0,NACK\n1,0,Stop";
        var text = DslHeader + good + "\n2,1,Start\n2,1,Address read: 01\n2,1,ACK\n2,1,Data read: EE\n2,1,NACK";
        var (records, diagnostics) = await Read(new DslDecodedI2cAdapter(), Write(".csv", text));

        AssertRecord(Assert.Single(records), text, 2, "L2-L7", good.Replace("\n", Environment.NewLine), [0xA5]);
        AssertDiagnostic(diagnostics, "DSL_INCOMPLETE_TRANSACTION", DiagnosticSeverity.Error, text, 8);
    }

    // Contract: docs/source-adapters.md, Acute continuation rows fail closed and
    // 8-bit addresses validate direction; malformed rows cannot contaminate later bytes.
    [Theory]
    [InlineData(",ACK,,EE,DD")]
    [InlineData("1,ACK,Rd 02,EE,DD")]
    public async Task Acute_ambiguous_or_conflicting_row_is_diagnosed_before_recovery(string badRow)
    {
        const string good = "2,ACK,Rd 55,A5 ACK,5A NACK";
        var text = AcuteHeader + badRow + "\n" + good;
        var (records, diagnostics) = await Read(new AcuteDecodedI2cAdapter(), Write(".csv", text));

        AssertDiagnostic(diagnostics, "ACUTE_MALFORMED_ROW", DiagnosticSeverity.Error, text, 2);
        var record = Assert.Single(records);
        AssertRecord(record, text, 3, "L3", good, [0xA5, 0x5A]);
        Assert.Equal(0x2A, record.I2c?.SlaveAddress);
        Assert.Equal([true, false], record.I2c?.Acked);
        Assert.Equal("8b", record.SourceFields?["address_mode"]);
        Assert.Equal("2", record.SourceFields?["raw_timestamp"]);
        Assert.Null(record.Address);
        Assert.Empty(record.I2c!.WriteCommands);
    }

    // Contract: docs/source-adapters.md, Acute rows represent complete transactions;
    // a truncated row without data is diagnosed/discarded, with previous evidence intact.
    [Fact]
    public async Task Acute_EOF_before_data_columns_is_diagnosed()
    {
        const string good = "0,ACK,Rd 03,A5 NACK,";
        var text = AcuteHeader + good + "\n1,ACK,Rd 03";
        var (records, diagnostics) = await Read(new AcuteDecodedI2cAdapter(), Write(".csv", text));

        AssertRecord(Assert.Single(records), text, 2, "L2", good, [0xA5]);
        AssertDiagnostic(diagnostics, "ACUTE_MALFORMED_ROW", DiagnosticSeverity.Error, text, 3);
    }

    // Contract: docs/source-adapters.md, Excel transaction columns, byte counts,
    // malformed-row discard and original row fields. This tests no absolute-address projection.
    // Known discrepancy: the documented 0x99000 mapping is currently unknown in production.
    [Theory]
    [InlineData("3", "03 EE")]
    [InlineData("1", "03")]
    [InlineData("2", "03 E")]
    public async Task Excel_inconsistent_or_truncated_payload_row_is_diagnosed_and_neighbors_survive(
        string declared, string bytes)
    {
        var path = WriteExcel(["8", "1.5", "I2C", declared, bytes]);
        var (records, diagnostics) = await Read(new ExcelDecodedI2cAdapter(), path);

        Assert.Equal(2, records.Count);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("EXCEL_MALFORMED_ROW", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal($"{SourceId}:L3", diagnostic.SourceRecordId);
        Assert.Equal(new SourceLocation(0, 3), diagnostic.Location);
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var row = index == 0 ? 2 : 4;
            Assert.Equal($"{SourceId}:R{row}", record.StableId);
            Assert.Equal(new SourceLocation(0, row), record.Location);
            Assert.Equal([0xA5, 0x5A], record.Data);
            Assert.Equal(2, record.DeclaredByteCount);
            Assert.Equal(1, record.I2c?.SlaveAddress);
            Assert.Equal(index == 0 ? "7 | 1.25 | I2C | 3 | 03 A5 5A" : "9 | 1.75 | I2C | 3 | 03 A5 5A", record.RawText);
            Assert.Equal("3", record.SourceFields?["byte_count"]);
            Assert.Equal("0x03", record.SourceFields?["register_address_byte"]);
        }
    }

    // Contract: docs/source-adapters.md, Excel inputs are Open XML workbooks and
    // explicitly selected adapters must still accept the schema; truncated ZIP is rejected.
    [Fact]
    public async Task Excel_truncated_ZIP_is_not_probed_as_a_workbook_and_read_is_rejected()
    {
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.xlsx");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04]);
        var adapter = new ExcelDecodedI2cAdapter();

        Assert.Equal(ProbeConfidence.None, (await adapter.ProbeAsync(path)).Confidence);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(adapter, path));
    }

    // Contract: docs/source-adapters.md, canonical JSON Lines and malformed-record
    // discard. A truncated final JSON object does not erase an earlier valid record.
    [Fact]
    public async Task Canonical_truncated_JSON_at_EOF_is_diagnosed_at_its_exact_source_line()
    {
        const string good = "{\"index\":7,\"timestamp_s\":1.25,\"read_data\":\"A55A\",\"i2c_slave_address\":42}";
        var text = CanonicalI2cTextAdapter.Magic + "\n# synthetic evidence\n\n" + good + "\n{\"index\":8,\"read_data\":\"EE";
        var (records, diagnostics) = await Read(new CanonicalI2cTextAdapter(), Write(".txt", text));

        var record = Assert.Single(records);
        AssertRecord(record, text, 4, "L4", good, [0xA5, 0x5A]);
        Assert.Equal(7, record.Index);
        Assert.Equal(42, record.I2c?.SlaveAddress);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1.25), record.Timestamp);
        AssertDiagnostic(diagnostics, "CANONICAL_MALFORMED_RECORD", DiagnosticSeverity.Error, text, 5);
    }

    // Contract: docs/source-adapters.md, missing canonical timestamps are retained
    // with a warning. Malformed hex is discarded and cannot contribute page/write evidence.
    [Fact]
    public async Task Canonical_bad_hex_resets_transport_evidence_and_null_timestamp_remains_unknown()
    {
        const string page = "{\"index\":1,\"timestamp_s\":0,\"operation\":\"Write\",\"data\":\"FF099000\"}";
        const string bad = "{\"index\":2,\"timestamp_s\":1,\"read_data\":\"E\"}";
        const string good = "{\"index\":3,\"timestamp_s\":null,\"read_data\":\"A55A\",\"write_commands\":[\"00\"],\"acks\":[true,false],\"source_meta\":{\"note\":\"synthetic\"}}";
        var text = CanonicalI2cTextAdapter.Magic + "\n" + page + "\n" + bad + "\n" + good;
        var (records, diagnostics) = await Read(new CanonicalI2cTextAdapter(), Write(".txt", text));

        Assert.Equal(2, records.Count);
        AssertRecord(records[0], text, 2, "L2", page, [0xFF, 0x09, 0x90, 0x00]);
        AssertRecord(records[1], text, 4, "L4", good, [0xA5, 0x5A]);
        AssertDiagnostic(diagnostics, "CANONICAL_MALFORMED_RECORD", DiagnosticSeverity.Error, text, 3);
        AssertDiagnostic(diagnostics, "CANONICAL_TIMESTAMP_MISSING", DiagnosticSeverity.Warning, text, 4);
        Assert.Equal(2, diagnostics.Count);
        Assert.Null(records[1].Timestamp);
        Assert.Null(records[1].Address);
        Assert.Equal([0x00], Assert.Single(records[1].I2c!.WriteCommands));
        Assert.Equal([true, false], records[1].I2c!.Acked);
        Assert.Equal("false", records[1].SourceFields?["register_page_known"]);
        Assert.Equal("synthetic", records[1].SourceFields?["source_meta.note"]);
    }

    // Contract: docs/source-adapters.md, NDS malformed rows are warned/discarded;
    // a truncated header cannot absorb continuation bytes or alter the preceding record.
    [Fact]
    public async Task Nds_truncated_header_is_warned_and_orphan_continuation_is_discarded()
    {
        var text = NdsGood + "\n2026-07-29 14:52:41:243 Read TP 0x99061\n  0xEE";
        var (records, diagnostics) = await Read(new NdsCommunicationLogAdapter(), Write(".txt", text));

        var record = Assert.Single(records);
        AssertRecord(record, text, 1, "L1", NdsGood, [0xA5]);
        Assert.Equal(0x99060u, record.Address);
        Assert.Equal(BusOperation.Read, record.Operation);
        AssertDiagnostic(diagnostics, "NDS_MALFORMED_RECORD", DiagnosticSeverity.Warning, text, 2);
    }

    // Contract: docs/source-adapters.md, malformed NDS rows are warned/discarded;
    // overflowing declared counts cannot produce a record or consume continuation bytes.
    [Fact]
    public async Task Nds_declared_count_overflow_is_warned_before_a_valid_EOF_record()
    {
        var text = "2026-07-29 14:52:41:239 Read TP 0x99061 2147483648 0xEE\n  0xDD\n" + NdsGood;
        var (records, diagnostics) = await Read(new NdsCommunicationLogAdapter(), Write(".txt", text));

        AssertRecord(Assert.Single(records), text, 3, "L3", NdsGood, [0xA5]);
        AssertDiagnostic(diagnostics, "NDS_MALFORMED_RECORD", DiagnosticSeverity.Warning, text, 1);
    }

    // Contract: docs/source-adapters.md, partial transactions create diagnostics and
    // are discarded. Production currently emits the short record without a diagnostic.
    // Keep the intended contract explicit rather than asserting the discrepancy as correct.
    [Fact(Skip = "Contract discrepancy: NDS EOF payload shorter than declared count is retained without a diagnostic; production changes are outside A5 scope.")]
    public async Task Nds_EOF_before_declared_payload_completes_must_be_diagnosed_and_discarded()
    {
        var text = "2026-07-29 14:52:41:241 Read TP 0x99060 3 0xA5\n  0x5A";
        var (records, diagnostics) = await Read(new NdsCommunicationLogAdapter(), Write(".txt", text));

        Assert.Empty(records);
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error &&
            diagnostic.Location == new SourceLocation(0, 1));
    }

    // Contract: docs/source-adapters.md, source normalization/probing never selects
    // Common 0x82..0x85, Desay 0x97 or its Palm profile; only transport grammar is detected.
    [Theory]
    [InlineData("saleae-decoded-i2c")]
    [InlineData("kingstvis-decoded-i2c")]
    [InlineData("dsl-decoded-i2c")]
    [InlineData("acute-decoded-i2c")]
    [InlineData("excel-decoded-i2c")]
    [InlineData("canonical-i2c-txt")]
    [InlineData("nds-communication-log")]
    public async Task Probing_is_identical_for_semantic_version_markers_and_loading_preserves_opaque_bytes(string adapterId)
    {
        var adapter = Assert.Single(BuiltInSources.All, candidate => candidate.Id == adapterId);
        SourceProbeResult? firstProbe = null;
        foreach (var marker in new byte[] { 0x82, 0x83, 0x84, 0x85, 0x97 })
        {
            var payload = new byte[] { marker, 0xA5, 0x5A };
            var path = WriteProbeInput(adapterId, payload);
            var original = File.ReadAllBytes(path);
            var probe = await adapter.ProbeAsync(path);
            Assert.NotEqual(ProbeConfidence.None, probe.Confidence);
            if (firstProbe is not null)
            {
                Assert.Equal(firstProbe.AdapterId, probe.AdapterId);
                Assert.Equal(firstProbe.DisplayName, probe.DisplayName);
                Assert.Equal(firstProbe.Confidence, probe.Confidence);
                Assert.Equal(firstProbe.Reasons, probe.Reasons);
            }
            firstProbe = probe;

            var session = await CaptureSession.LoadAsync(path);
            Assert.Equal(adapterId, session.Probe.AdapterId);
            Assert.Null(session.RegisterProfile);
            Assert.Empty(session.SourceDiagnostics);
            var read = Assert.Single(session.Records, record => record.Operation is BusOperation.Read or BusOperation.Paint);
            Assert.Equal(payload, read.Data);
            Assert.Equal(payload.Length, read.DeclaredByteCount);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.DoesNotContain(read.SourceFields!.Keys, key => key is "event_buffer_version" or "event_buffer_family" or "palm_profile");
        }
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string extension, string text)
    {
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static async Task<(List<SourceRecord> Records, List<ReplayDiagnostic> Diagnostics)> Read(ISourceAdapter adapter, string path)
    {
        var records = new List<SourceRecord>();
        var diagnostics = new List<ReplayDiagnostic>();
        await foreach (var record in adapter.ReadAsync(new SourceOpenContext(path, SourceId, diagnostics.Add)))
            records.Add(record);
        return (records, diagnostics);
    }

    private static void AssertRecord(SourceRecord record, string text, int line, string idSuffix, string raw, byte[] data)
    {
        Assert.Equal($"{SourceId}:{idSuffix}", record.StableId);
        Assert.Equal(new SourceLocation(Offset(text, line), line), record.Location);
        Assert.Equal(raw, record.RawText);
        Assert.Equal(data, record.Data);
        Assert.Equal(data.Length, record.DeclaredByteCount);
    }

    private static void AssertDiagnostic(List<ReplayDiagnostic> diagnostics, string code, DiagnosticSeverity severity, string text, int line)
    {
        var diagnostic = Assert.Single(diagnostics, item => item.Code == code);
        Assert.Equal(severity, diagnostic.Severity);
        Assert.Equal($"{SourceId}:L{line}", diagnostic.SourceRecordId);
        Assert.Equal(new SourceLocation(Offset(text, line), line), diagnostic.Location);
        Assert.NotEmpty(diagnostic.Message);
    }

    private static long Offset(string text, int line) => Encoding.UTF8.GetByteCount(
        string.Concat(text.Split('\n').Take(line - 1).Select(value => value + "\n")));

    private string WriteExcel(string[] badRow)
    {
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.xlsx");
        DecodedI2cSimulator.WriteExcel(path, Enumerable.Range(0, 3).Select(index =>
            new SyntheticI2cTransaction(7 + index, 1.25 + index * 0.25, 1, [], [0xA5, 0x5A])));
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry("xl/worksheets/sheet1.xml")!;
        XDocument document;
        using (var stream = entry.Open()) document = XDocument.Load(stream);
        var row = document.Descendants().Single(element => element.Name.LocalName == "row" && element.Attribute("r")?.Value == "3");
        var cells = row.Elements().ToArray();
        for (var index = 0; index < badRow.Length; index++)
            cells[index].Descendants().Single(element => element.Name.LocalName == "t").Value = badRow[index];
        entry.Delete();
        using (var stream = archive.CreateEntry("xl/worksheets/sheet1.xml").Open()) document.Save(stream);
        return path;
    }

    private string WriteProbeInput(string adapterId, byte[] payload)
    {
        var transaction = new SyntheticI2cTransaction(7, 1.25, 1, [], payload);
        if (adapterId == "excel-decoded-i2c")
        {
            var path = Path.Combine(directory, $"{Guid.NewGuid():N}.xlsx");
            DecodedI2cSimulator.WriteExcel(path, [transaction]);
            return path;
        }
        return adapterId switch
        {
            "saleae-decoded-i2c" => Write(".csv", DecodedI2cSimulator.ToSaleaeCsv([transaction])),
            "kingstvis-decoded-i2c" => Write(".csv", DecodedI2cSimulator.ToKingstVisCsv([transaction])),
            "dsl-decoded-i2c" => Write(".csv", DecodedI2cSimulator.ToDslCsv([transaction])),
            "acute-decoded-i2c" => Write(".csv", DecodedI2cSimulator.ToAcuteCsv([transaction])),
            "canonical-i2c-txt" => Write(".txt", DecodedI2cSimulator.ToCanonicalText([transaction])),
            "nds-communication-log" => Write(".txt", "2026-07-29 14:52:41:241 Paint TP 0x01 3 " +
                string.Join(' ', payload.Select(value => $"0x{value:X2}"))),
            _ => throw new ArgumentOutOfRangeException(nameof(adapterId)),
        };
    }
}
