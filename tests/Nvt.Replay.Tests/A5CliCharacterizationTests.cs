using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nvt.Replay.Tests;

public sealed class A5CliCharacterizationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"nvt-a5-cli-{Guid.NewGuid():N}");

    public A5CliCharacterizationTests() => Directory.CreateDirectory(directory);

    // Documented inputs/outputs are the contract; exit codes and error fragments below
    // characterize the existing CLI without making its observed errors a new contract.

    // README.md, Build examples:
    // "dotnet run --project src/Nvt.Replay.Cli -- inspect ./capture.txt --event-buffer-version 0x83 --i2c-address 0x01"
    // "dotnet run --project src/Nvt.Replay.Cli -- analyze ./capture.txt --event-buffer-version 0x83 --output ./analysis"
    // "dotnet run --project src/Nvt.Replay.Cli -- export ./capture.txt --event-buffer-version 0x83 --output ./replay.mp4"
    [Theory]
    [InlineData("inspect", "file")]
    [InlineData("inspect", "version")]
    [InlineData("analyze", "file")]
    [InlineData("analyze", "version")]
    [InlineData("analyze", "output")]
    [InlineData("export", "file")]
    [InlineData("export", "version")]
    [InlineData("export", "output")]
    public async Task Required_arguments_report_usage_on_stderr(string command, string missing)
    {
        var args = new List<string> { command };
        if (missing != "file") args.Add(Fixture("common-0x83-asil-lifecycle.nds.txt"));
        if (missing != "version") args.AddRange(["--event-buffer-version", "0x83"]);
        if (command != "inspect" && missing != "output")
            args.AddRange(["--output", Path.Combine(directory, "output")]);
        args.Add("--json");

        var result = await RunAsync(args.ToArray());

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains($"Usage: nvt-replay {command}", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    // README.md, Build examples: "dotnet run --project src/Nvt.Replay.Cli -- probe ./capture.txt --json"
    // and "dotnet run --project src/Nvt.Replay.Cli -- inspect ./capture.txt --event-buffer-version 0x83 --i2c-address 0x01".
    [Theory]
    [InlineData("probe")]
    [InlineData("inspect")]
    public async Task Missing_capture_reports_input_failure_without_JSON(string command)
    {
        var path = Path.Combine(directory, "missing.txt");
        string[] args = command == "probe"
            ? [command, path, "--json"]
            : [command, path, "--event-buffer-version", "0x83", "--json"];

        var result = await RunAsync(args);

        Assert.Equal(3, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains(path, result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    // README.md: "CLI discovery and probe commands with optional JSON output".
    // docs/source-adapters.md: "A source adapter may identify a transport export, but it must never infer Common
    // `0x82`–`0x85`, Desay `0x97`, or the `0x97` Standard/Benz Palm profile."
    [Fact]
    public async Task Probe_JSON_identifies_source_evidence_without_selecting_semantics()
    {
        var result = await RunAsync("probe", Fixture("common-0x83-asil-lifecycle.nds.txt"), "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        var match = Assert.Single(json.RootElement.EnumerateArray(), item =>
            item.GetProperty("Confidence").GetString() != "None");
        Assert.Equal("nds-communication-log", match.GetProperty("AdapterId").GetString());
        Assert.Equal("High", match.GetProperty("Confidence").GetString());
        Assert.NotEmpty(match.GetProperty("Reasons").EnumerateArray());
        Assert.All(json.RootElement.EnumerateArray(), item =>
        {
            Assert.False(item.TryGetProperty("Version", out _));
            Assert.False(item.TryGetProperty("Profile", out _));
        });
    }

    // docs/source-adapters.md: "A tie is an operator-decision gate (`SourceSelectionRequiredException`);
    // CLI callers can resolve it with `--source-adapter <id>`."
    [Fact]
    public async Task Source_probe_tie_requires_and_accepts_an_explicit_adapter()
    {
        var path = Path.Combine(directory, "ambiguous.csv");
        await File.WriteAllTextAsync(path, "Time [s],Packet ID,Address,Data,Read/Write,ACK\n" +
            await File.ReadAllTextAsync(Fixture("common-0x83-asil-lifecycle.nds.txt")));

        var ambiguous = await RunAsync("inspect", path, "--event-buffer-version", "0x83", "--json");

        Assert.Equal(3, ambiguous.ExitCode);
        Assert.Empty(ambiguous.Output);
        Assert.Contains("nds-communication-log", ambiguous.Error, StringComparison.Ordinal);
        Assert.Contains("kingstvis-decoded-i2c", ambiguous.Error, StringComparison.Ordinal);

        var selected = await RunAsync("inspect", path, "--event-buffer-version", "0x83",
            "--source-adapter", "nds-communication-log", "--json");

        Assert.Equal(0, selected.ExitCode);
        Assert.Empty(selected.Error);
        using var json = JsonDocument.Parse(selected.Output);
        Assert.Equal("V83", json.RootElement.GetProperty("Version").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("Frames").GetArrayLength());
        Assert.Equal(path, json.RootElement.GetProperty("SourcePath").GetString());
    }

    // docs/source-adapters.md: "The normalized slave address is always 7-bit."
    // "The desktop/CLI decode target defaults to `0x01` and may be changed without hiding or rewriting
    // transactions from other devices; W/R remains a separate property."
    [Theory]
    [InlineData("0x00", 0)]
    [InlineData("127", 127)]
    public async Task I2c_target_boundaries_select_frames_without_mutating_source(string option, int address)
    {
        var original = await File.ReadAllTextAsync(Fixture("common-0x83-asil-lifecycle.nds.txt"));
        var path = Path.Combine(directory, "two-devices.txt");
        await File.WriteAllTextAsync(path, original + original.Replace("Paint TP 0x01", $"Paint TP 0x{address:X2}"));
        var bytes = await File.ReadAllBytesAsync(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var defaultTarget = await RunAsync("inspect", path, "--event-buffer-version", "0x83", "--json");
        var selectedTarget = await RunAsync("inspect", path, "--event-buffer-version", "0x83",
            "--i2c-address", option, "--json");

        Assert.Equal(0, defaultTarget.ExitCode);
        Assert.Equal(0, selectedTarget.ExitCode);
        Assert.Empty(defaultTarget.Error);
        Assert.Empty(selectedTarget.Error);
        using var defaultJson = JsonDocument.Parse(defaultTarget.Output);
        using var selectedJson = JsonDocument.Parse(selectedTarget.Output);
        Assert.Equal(hash, defaultJson.RootElement.GetProperty("SourceSha256").GetString());
        Assert.Equal(hash, selectedJson.RootElement.GetProperty("SourceSha256").GetString());
        var defaultFrames = defaultJson.RootElement.GetProperty("Frames");
        var selectedFrames = selectedJson.RootElement.GetProperty("Frames");
        Assert.Equal(3, defaultFrames.GetArrayLength());
        Assert.Equal(3, selectedFrames.GetArrayLength());
        Assert.All(defaultFrames.EnumerateArray(), frame =>
            Assert.Equal(1, frame.GetProperty("Source").GetProperty("I2c").GetProperty("SlaveAddress").GetInt32()));
        Assert.All(selectedFrames.EnumerateArray(), frame =>
            Assert.Equal(address, frame.GetProperty("Source").GetProperty("I2c").GetProperty("SlaveAddress").GetInt32()));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    // docs/source-adapters.md: "The normalized slave address is always 7-bit."
    [Theory]
    [InlineData("-1")]
    [InlineData("0x80")]
    public async Task Inspect_rejects_non_7bit_decode_targets(string option)
    {
        var result = await RunAsync("inspect", Fixture("common-0x83-asil-lifecycle.nds.txt"),
            "--event-buffer-version", "0x83", "--i2c-address", option, "--json");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("7-bit", result.Error, StringComparison.Ordinal);
    }

    // README.md: "The desktop startup options and IC register profiles are explicit operator choices
    // for reproducible QA and screenshot runs. They do not infer Event Buffer Version or Benz Palm."
    [Fact]
    public async Task Inspect_rejects_an_unknown_register_profile()
    {
        var result = await RunAsync("inspect", Fixture("common-0x83-asil-lifecycle.nds.txt"),
            "--event-buffer-version", "0x83", "--register-profile", "unknown", "--json");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("--register-profile", result.Error, StringComparison.Ordinal);
    }

    // README.md: "Desay `0x97` uses a dedicated two-transaction assembler before decoding and
    // requires an explicit Standard or Benz Palm profile."
    [Fact]
    public async Task Desay_rejects_an_unknown_Palm_selection()
    {
        var result = await RunAsync("inspect", Fixture("desay97-full-reread.nds.txt"),
            "--event-buffer-version", "0x97", "--register-profile", "51927",
            "--desay97-profile", "unknown", "--json");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("--desay97-profile", result.Error, StringComparison.Ordinal);
    }

    // docs/analysis-outputs.md: "The CLI range is one-based and inclusive. Without `--range`,
    // all decoded frames are included."
    [Theory]
    [InlineData(null, 0, 2)]
    [InlineData("1:1", 0, 0)]
    [InlineData("2:3", 1, 2)]
    public async Task Analyze_JSON_and_manifest_preserve_the_selected_inclusive_range(string? range, int first, int last)
    {
        var outputDirectory = Path.Combine(directory, "analysis");
        var args = new List<string> { "analyze", Fixture("common-0x83-asil-lifecycle.nds.txt"),
            "--event-buffer-version", "0x83", "--output", outputDirectory, "--json" };
        if (range is not null) args.AddRange(["--range", range]);

        var result = await RunAsync(args.ToArray());

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var summary = JsonDocument.Parse(result.Output);
        Assert.Equal(outputDirectory, summary.RootElement.GetProperty("Directory").GetString());
        Assert.Equal(last - first + 1, summary.RootElement.GetProperty("Frames").GetInt32());
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "manifest.json")));
        Assert.Equal(first, manifest.RootElement.GetProperty("range").GetProperty("startLogicalIndex").GetInt32());
        Assert.Equal(last, manifest.RootElement.GetProperty("range").GetProperty("endLogicalIndex").GetInt32());
        using var events = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "events.json")));
        Assert.Equal(Enumerable.Range(first, last - first + 1),
            events.RootElement.EnumerateArray().Select(item => item.GetProperty("logicalIndex").GetInt32()));
    }

    // docs/analysis-outputs.md: "The CLI range is one-based and inclusive."
    // docs/video-export.md: "`nvt-replay export` accepts a one-based inclusive range".
    [Theory]
    [InlineData("analyze", "0:1")]
    [InlineData("analyze", "3:2")]
    [InlineData("analyze", "1:4")]
    [InlineData("export", "0:1")]
    [InlineData("export", "3:2")]
    [InlineData("export", "1:4")]
    public async Task Ranges_outside_decoded_frames_are_rejected_before_writing(string command, string range)
    {
        var result = await RunAsync(command, Fixture("common-0x83-asil-lifecycle.nds.txt"),
            "--event-buffer-version", "0x83", "--output", Path.Combine(directory, "output"),
            "--range", range, "--json");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("--range", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    // docs/analysis-outputs.md: "`manifest.json`: source SHA-256, adapter, format evidence status, selected
    // range, captured/frame clock summary, and heatmap transform".
    // "Diagnostic aggregates retain diagnostic IDs, event IDs, and physical source record IDs."
    // "The origin is top-left and the linear coordinate extent is stored in the manifest alongside the PNG dimensions."
    [Fact]
    public async Task Analyze_outputs_keep_JSON_provenance_links_and_requested_heatmap_dimensions()
    {
        var fixture = Fixture("common-0x83-asil-lifecycle.nds.txt");
        var outputDirectory = Path.Combine(directory, "analysis");

        var result = await RunAsync("analyze", fixture, "--event-buffer-version", "0x83",
            "--output", outputDirectory, "--heatmap-size", "16x8", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        using var summary = JsonDocument.Parse(result.Output);
        var files = summary.RootElement.GetProperty("Files");
        foreach (var (property, name) in new[] { ("ReportJson", "analysis-report.json"), ("EventsJson", "events.json"),
            ("EventsCsv", "events.csv"), ("DiagnosticsJson", "diagnostics.json"), ("DiagnosticsCsv", "diagnostics.csv"),
            ("ManifestJson", "manifest.json"), ("HeatmapPng", "heatmap.png") })
        {
            var path = Path.Combine(outputDirectory, name);
            Assert.Equal(path, files.GetProperty(property).GetString());
            Assert.True(File.Exists(path), name);
        }
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "analysis-report.json")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(fixture))),
            root.GetProperty("sourceSha256").GetString());
        Assert.Equal("nds-communication-log", root.GetProperty("decodeConfiguration").GetProperty("sourceAdapterId").GetString());
        Assert.Equal("0x83", root.GetProperty("decodeConfiguration").GetProperty("eventBufferVersion").GetString());
        Assert.Equal("verified", root.GetProperty("formatEvidenceStatus").GetString());
        Assert.Equal("captured", root.GetProperty("clock").GetProperty("domain").GetString());
        Assert.Equal("linear-origin-top-left", root.GetProperty("heatmapTransform").GetProperty("kind").GetString());
        Assert.Equal(16, root.GetProperty("heatmapPixelWidth").GetInt32());
        Assert.Equal(8, root.GetProperty("heatmapPixelHeight").GetInt32());
        var png = await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "heatmap.png"));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(8, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        using var events = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "events.json")));
        using var diagnostics = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "diagnostics.json")));
        Assert.Equal(3, events.RootElement.GetArrayLength());
        Assert.Equal(diagnostics.RootElement.GetArrayLength(), summary.RootElement.GetProperty("Diagnostics").GetInt32());
        var aggregates = report.RootElement.GetProperty("diagnosticAggregates");
        Assert.NotEmpty(aggregates.EnumerateArray());
        Assert.All(aggregates.EnumerateArray(), aggregate =>
        {
            Assert.NotEmpty(aggregate.GetProperty("diagnosticIds").EnumerateArray());
            Assert.NotEmpty(aggregate.GetProperty("eventIds").EnumerateArray());
            Assert.NotEmpty(aggregate.GetProperty("sourceRecordIds").EnumerateArray());
            Assert.All(aggregate.GetProperty("diagnosticIds").EnumerateArray(), id =>
                Assert.Contains(diagnostics.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == id.GetString()));
            Assert.All(aggregate.GetProperty("eventIds").EnumerateArray(), id =>
                Assert.Contains(events.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == id.GetString()));
            Assert.All(aggregate.GetProperty("sourceRecordIds").EnumerateArray(), id =>
                Assert.Contains(events.RootElement.EnumerateArray(), item =>
                    item.GetProperty("physicalRecordIds").EnumerateArray().Any(physical => physical.GetString() == id.GetString())));
        });
        Assert.False(File.Exists(Path.Combine(outputDirectory, "analysis-journal.json")));
        Assert.Empty(Directory.EnumerateFiles(outputDirectory, "*.tmp"));
    }

    // docs/analysis-outputs.md, example: "--heatmap-size 1280x720".
    // "The origin is top-left and the linear coordinate extent is stored in the manifest alongside the PNG dimensions."
    [Fact]
    public async Task Analyze_rejects_a_zero_heatmap_dimension()
    {
        var result = await RunAsync("analyze", Fixture("common-0x83-asil-lifecycle.nds.txt"),
            "--event-buffer-version", "0x83", "--output", Path.Combine(directory, "analysis"),
            "--heatmap-size", "0x8", "--json");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("--heatmap-size", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    // docs/video-export.md: "`nvt-replay export` accepts a one-based inclusive range, Recorded or Frame
    // clock, speed, frame rate, even output dimensions, an optional review sidecar, and an optional FFmpeg executable".
    // The existing CLI help's 1-240 FPS limits are also covered by ReplayCliContractTests.
    [Theory]
    [InlineData("--size", "321x180")]
    [InlineData("--size", "320x181")]
    [InlineData("--clock", "unknown")]
    [InlineData("--speed", "0")]
    [InlineData("--fps", "0")]
    [InlineData("--fps", "241")]
    public async Task Export_option_boundaries_fail_before_any_encoder_or_output(string option, string value)
    {
        var result = await RunAsync("export", Fixture("common-0x83-asil-lifecycle.nds.txt"),
            "--event-buffer-version", "0x83", "--output", Path.Combine(directory, "replay.mp4"),
            option, value, "--ffmpeg", Path.Combine(directory, "missing-ffmpeg.exe"), "--json");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains(option, result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    // docs/video-export.md: "`nvt-replay export` accepts a one-based inclusive range, Recorded or Frame
    // clock, speed, frame rate, even output dimensions, an optional review sidecar, and an optional FFmpeg executable".
    // "If no reviewed encoder is found or it exits incompatibly, MP4 export stops with a visible error
    // and creates no fallback output."
    [Theory]
    [InlineData("recorded", "1", "1:1")]
    [InlineData("frame", "240", "3:3")]
    public async Task Valid_export_boundaries_reach_the_encoder_gate_without_fallback(string clock, string fps, string range)
    {
        var result = await RunAsync("export", Fixture("common-0x83-asil-lifecycle.nds.txt"),
            "--event-buffer-version", "0x83", "--output", Path.Combine(directory, "replay.mp4"),
            "--size", "320x180", "--clock", clock, "--fps", fps, "--range", range, "--speed", "1",
            "--ffmpeg", Path.Combine(directory, "missing-ffmpeg.exe"), "--json");

        Assert.Equal(4, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("Encoder error:", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    // docs/video-export.md: "The temporary MP4 is removed on failure or cancellation, and an existing
    // MP4 or manifest is never overwritten."
    [Fact]
    public async Task Export_preserves_existing_MP4_and_manifest()
    {
        var path = Path.Combine(directory, "replay.mp4");
        await File.WriteAllTextAsync(path, "prior-video");
        await File.WriteAllTextAsync(path + ".json", "{\"prior\":true}");

        var result = await RunAsync("export", Fixture("common-0x83-asil-lifecycle.nds.txt"),
            "--event-buffer-version", "0x83", "--output", path,
            "--ffmpeg", Path.Combine(directory, "missing-ffmpeg.exe"), "--json");

        Assert.Equal(3, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains(path, result.Error, StringComparison.Ordinal);
        Assert.Equal("prior-video", await File.ReadAllTextAsync(path));
        Assert.Equal("{\"prior\":true}", await File.ReadAllTextAsync(path + ".json"));
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(directory).Count());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", name));

    private static async Task<CliResult> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await ReplayCli.RunAsync(args, output, error);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
