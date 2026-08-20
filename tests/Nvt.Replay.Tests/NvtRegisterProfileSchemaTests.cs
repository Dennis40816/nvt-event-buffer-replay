using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

public sealed class NvtRegisterProfileSchemaTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"nvt-register-profile-{Guid.NewGuid():N}");

    public NvtRegisterProfileSchemaTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Canonical_serialization_and_hash_do_not_depend_on_definition_order()
    {
        var first = Create(
            scope: ["B", "A"],
            registers:
            [
                new(NvtRegisterRegionV1.History, 1, "History Status"),
                new(NvtRegisterRegionV1.EventBuffer, 0x60, "FW State"),
            ],
            commands:
            [
                new(NvtRegisterRegionV1.EventBuffer, 0x50, 0x42, "Command B", false),
                new(NvtRegisterRegionV1.EventBuffer, 0x50, 0x23, "Baseline Reset", true),
            ]);
        var second = Create(
            scope: ["A", "B"],
            registers: first.Registers.Reverse().ToArray(),
            commands: first.Commands.Reverse().ToArray());

        Assert.Equal(first.ContentSha256, second.ContentSha256);
        Assert.Equal(
            NvtRegisterProfileFile.SerializeCanonical(first),
            NvtRegisterProfileFile.SerializeCanonical(second));
        Assert.Matches("^[0-9a-f]{64}$", first.ContentSha256);
    }

    [Fact]
    public async Task Save_and_load_roundtrip_the_canonical_document()
    {
        var path = Path.Combine(directory, "profile.json");
        var expected = Create();
        var cancellationToken = CancellationToken.None;

        await NvtRegisterProfileFile.SaveAsync(path, expected, cancellationToken);
        var actual = await NvtRegisterProfileFile.LoadAsync(path, cancellationToken);

        Assert.Equal(expected.ContentSha256, actual.ContentSha256);
        Assert.Equal(
            NvtRegisterProfileFile.SerializeCanonical(expected),
            NvtRegisterProfileFile.SerializeCanonical(actual));
        Assert.Equal(NvtRegisterProfileFile.SerializeCanonical(expected), await File.ReadAllTextAsync(path, cancellationToken));
    }

    [Fact]
    public void Built_in_profiles_project_and_roundtrip_without_changing_runtime_semantics()
    {
        foreach (var profile in NvtRegisterCatalog.Profiles)
        {
            var before = NvtRegisterCatalog.DescribeForProfile(
                profile.EventBufferBase + 0x50,
                BusOperation.Write,
                [0x23],
                profile);
            var document = NvtRegisterProfileFile.FromBuiltIn(profile);
            var parsed = NvtRegisterProfileFile.Parse(NvtRegisterProfileFile.SerializeCanonical(document));
            var roundtripped = NvtRegisterProfileFile.ToRuntimeProfile(parsed);
            var after = NvtRegisterCatalog.DescribeForProfile(
                roundtripped.EventBufferBase + 0x50,
                BusOperation.Write,
                [0x23],
                roundtripped);

            Assert.Equal(profile, roundtripped);
            Assert.Equal(before, after);
            Assert.Contains(parsed.Registers, item =>
                item.Region == NvtRegisterRegionV1.EventBuffer && item.Offset == 0x78);
            Assert.Contains(parsed.Commands, item => item.Opcode == 0x23 && item.Confirmed);
        }
    }

    [Fact]
    public void Unknown_schema_is_rejected_before_deserialization()
    {
        var error = Assert.Throws<NvtRegisterProfileValidationException>(() =>
            NvtRegisterProfileFile.Parse("{\"schemaVersion\":2}"));

        Assert.Contains("Unknown register-profile schemaVersion 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_IC_scope_is_rejected()
    {
        var error = Assert.Throws<NvtRegisterProfileValidationException>(() => Create(scope: []));

        Assert.Contains("icScope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_register_offset_is_rejected_within_a_region()
    {
        var error = Assert.Throws<NvtRegisterProfileValidationException>(() => Create(registers:
        [
            new(NvtRegisterRegionV1.EventBuffer, 0x60, "FW State"),
            new(NvtRegisterRegionV1.EventBuffer, 0x60, "Other State"),
        ]));

        Assert.Contains("Duplicate register offset", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_register_name_is_rejected_case_insensitively()
    {
        var error = Assert.Throws<NvtRegisterProfileValidationException>(() => Create(registers:
        [
            new(NvtRegisterRegionV1.EventBuffer, 0x60, "FW State"),
            new(NvtRegisterRegionV1.EventBuffer, 0x61, "fw state"),
        ]));

        Assert.Contains("Duplicate register name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_offsets_must_resolve_inside_the_24_bit_address_space()
    {
        var error = Assert.Throws<NvtRegisterProfileValidationException>(() => Create(
            addresses: new NvtRegisterAddressMapV1(0xFFFFF0, 0x8EC98, 0x99200),
            registers: [new(NvtRegisterRegionV1.EventBuffer, 0x20, "Outside") ]));

        Assert.Contains("outside the 24-bit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tampered_content_is_rejected_by_SHA256_identity()
    {
        var document = Create();

        var error = Assert.Throws<NvtRegisterProfileValidationException>(() =>
            NvtRegisterProfileFile.Validate(document with { DisplayName = "Tampered" }));

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_executable_or_plugin_fields_are_rejected()
    {
        var json = NvtRegisterProfileFile.SerializeCanonical(Create());
        json = json.Insert(json.IndexOf('{') + 1, "\n  \"plugin\": \"arbitrary-code\",");

        var error = Assert.Throws<NvtRegisterProfileValidationException>(() => NvtRegisterProfileFile.Parse(json));

        Assert.Contains("Invalid register-profile JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Precancelled_load_and_save_do_not_publish_data()
    {
        var source = Path.Combine(directory, "source.json");
        var destination = Path.Combine(directory, "destination.json");
        await File.WriteAllTextAsync(
            source,
            NvtRegisterProfileFile.SerializeCanonical(Create()),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NvtRegisterProfileFile.LoadAsync(source, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NvtRegisterProfileFile.SaveAsync(destination, Create(), cancellation.Token));
        Assert.False(File.Exists(destination));
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static NvtRegisterProfileDocumentV1 Create(
        IReadOnlyList<string>? scope = null,
        NvtRegisterAddressMapV1? addresses = null,
        IReadOnlyList<NvtRegisterDefinitionV1>? registers = null,
        IReadOnlyList<NvtCommandDefinitionV1>? commands = null) =>
        NvtRegisterProfileFile.CreateV1(
            "test.51927",
            "Test 51927",
            "51927",
            scope ?? ["51927"],
            new NvtFirmwareScopeV1(">=1.0 <2.0"),
            addresses ?? new NvtRegisterAddressMapV1(0x99000, 0x8EC98, 0x99200),
            registers ?? [new(NvtRegisterRegionV1.EventBuffer, 0x60, "FW State")],
            commands ?? [new(NvtRegisterRegionV1.EventBuffer, 0x50, 0x23, "Baseline Reset", true)]);
}
