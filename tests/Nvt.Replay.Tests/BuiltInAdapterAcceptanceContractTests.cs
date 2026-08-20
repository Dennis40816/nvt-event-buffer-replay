using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

public sealed class BuiltInAdapterAcceptanceContractTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"nvt-adapter-contract-{Guid.NewGuid():N}");

    public BuiltInAdapterAcceptanceContractTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Cross_adapter_probe_tie_requires_an_explicit_source_choice()
    {
        var path = Write("ambiguous.csv", string.Join('\n',
            "Time [s],Packet ID,Address,Data,Read/Write,ACK",
            "2026-07-29 14:52:41:241 Read TP 0x99060 1 0xA3",
            "2026-07-29 14:52:41:243 Read TP 0x99061 1 0xA3",
            "2026-07-29 14:52:41:245 Read TP 0x99062 1 0xA3") + "\n");

        var error = await Assert.ThrowsAsync<SourceSelectionRequiredException>(() =>
            CaptureSession.LoadAsync(path));

        Assert.Contains(error.Candidates, item => item.AdapterId == "kingstvis-decoded-i2c");
        Assert.Contains(error.Candidates, item => item.AdapterId == "nds-communication-log");
        Assert.All(error.Candidates, item => Assert.Equal(ProbeConfidence.High, item.Confidence));
    }

    [Fact]
    public async Task Dsl_multiple_I2c_analyzers_are_rejected_as_ambiguous()
    {
        var path = Write("dsl-ambiguous.csv",
            "Id,Time[ns],1:I2C: Address/Data,2:I²C: Address/Data\n" +
            "1,0,Start,Start\n");
        var adapter = new DslDecodedI2cAdapter();
        var diagnostics = new List<ReplayDiagnostic>();

        var probe = await adapter.ProbeAsync(path);
        var records = await Read(adapter, path, "dsl-ambiguous", diagnostics.Add);

        Assert.Equal(ProbeConfidence.None, probe.Confidence);
        Assert.Contains("2 decoded I2C analyzer columns", Assert.Single(probe.Reasons), StringComparison.Ordinal);
        Assert.Empty(records);
        Assert.Equal("DSL_UNSUPPORTED_COLUMNS", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public async Task Simulator_roundtrip_preserves_transaction_boundaries_address_and_ACK_evidence()
    {
        var transaction = new SyntheticI2cTransaction(
            17,
            2.5,
            1,
            [new byte[] { 0xFF, 0x09, 0x90 }, new byte[] { 0x00 }],
            new byte[] { 0x11, 0x22, 0x33 });
        var kingstPath = Write("kingstvis.csv", DecodedI2cSimulator.ToKingstVisCsv([transaction]));
        var dslPath = Write("dsl.csv", DecodedI2cSimulator.ToDslCsv([transaction]));

        var kingstRecords = await Read(
            new KingstVisDecodedI2cAdapter(),
            kingstPath,
            "kingst-roundtrip");
        var dslRecords = await Read(new DslDecodedI2cAdapter(), dslPath, "dsl-roundtrip");
        var kingstRead = Assert.Single(kingstRecords, item => item.Operation == BusOperation.Read);
        var dslRead = Assert.Single(dslRecords, item => item.Operation == BusOperation.Read);

        Assert.Equal(3, kingstRecords.Count);
        Assert.Equal(0x99000u, kingstRead.Address);
        Assert.Empty(kingstRead.I2c?.Acked ?? []);
        Assert.Null(kingstRead.I2c?.AddressAcknowledged);
        Assert.Equal("unavailable", kingstRead.SourceFields?["ack_evidence"]);

        Assert.Single(dslRecords);
        Assert.Equal(0x99000u, dslRead.Address);
        Assert.Equal(2, dslRead.I2c?.WriteCommands.Count);
        Assert.Equal([true, true, false], dslRead.I2c?.Acked);
        Assert.True(dslRead.I2c?.AddressAcknowledged);
        Assert.Equal(transaction.ReadData, dslRead.Data);
    }

    [Fact]
    public async Task Profile_projection_preserves_stable_ID_raw_and_byte_identity_for_all_three_sources()
    {
        var transaction = new SyntheticI2cTransaction(
            3,
            1.0,
            1,
            [new byte[] { 0xFF, 0x09, 0x90, 0x00 }],
            new byte[] { 0x00, 0x01 });
        var dslPath = Write("projection-dsl.csv", DecodedI2cSimulator.ToDslCsv([transaction]));
        var cases = new[]
        {
            (Path: Fixture("kingstvis-common-0x83.csv"), AdapterId: "kingstvis-decoded-i2c"),
            (Path: dslPath, AdapterId: "dsl-decoded-i2c"),
            (Path: Fixture("desay97-full-reread.nds.txt"), AdapterId: "nds-communication-log"),
        };

        foreach (var item in cases)
        {
            var loaded = await CaptureSession.LoadAsync(item.Path, adapterId: item.AdapterId);
            var projected = loaded.WithRegisterProfile("51927");
            var reloaded = await CaptureSession.LoadAsync(item.Path, adapterId: item.AdapterId);

            Assert.NotEmpty(loaded.Records);
            Assert.Same(loaded.Records, projected.Records);
            Assert.Equal(
                loaded.Records.Select(record => record.StableId),
                reloaded.Records.Select(record => record.StableId));
            for (var index = 0; index < loaded.Records.Count; index++)
            {
                var source = loaded.Records[index];
                var annotated = projected.Records[index];
                Assert.Same(source, annotated);
                Assert.Same(source.Data, annotated.Data);
                Assert.Same(source.SourceFields, annotated.SourceFields);
                Assert.Equal(source.RawText, annotated.RawText);
                Assert.Equal(source.Location, annotated.Location);
                Assert.False(string.IsNullOrWhiteSpace(source.RawText));
            }
        }
    }

    [Fact]
    public async Task Repository_fixtures_keep_confirmed_address_normalization_contracts()
    {
        var kingst = await CaptureSession.LoadAsync(
            Fixture("kingstvis-common-0x83.csv"),
            adapterId: "kingstvis-decoded-i2c");
        var nds = await CaptureSession.LoadAsync(
            Fixture("desay97-full-reread.nds.txt"),
            adapterId: "nds-communication-log");

        Assert.All(
            kingst.Records.Where(record => record.Operation == BusOperation.Read),
            record => Assert.Equal(0x99000u, record.Address));
        Assert.All(nds.Records, record => Assert.Equal(0x99000u, record.Address));
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<List<SourceRecord>> Read(
        ISourceAdapter adapter,
        string path,
        string sourceId,
        Action<ReplayDiagnostic>? diagnosticSink = null)
    {
        var records = new List<SourceRecord>();
        await foreach (var record in adapter.ReadAsync(
            new SourceOpenContext(path, sourceId, diagnosticSink)))
        {
            records.Add(record);
        }
        return records;
    }

    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..",
        "fixtures",
        name));
}
