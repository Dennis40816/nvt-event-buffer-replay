using System.Text.Json;

namespace Nvt.Replay.Tests;

public sealed class ReplayCliContractTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"nvt-cli-contract-{Guid.NewGuid():N}");

    public ReplayCliContractTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("120", 120)]
    [InlineData("180", 180)]
    [InlineData("240", 240)]
    public void Export_accepts_the_same_frame_rate_range_as_the_renderer(string text, int expected)
    {
        Assert.True(ReplayCli.TryParseExportFrameRate(text, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("241")]
    [InlineData("invalid")]
    public void Export_rejects_frame_rates_outside_the_renderer_contract(string text)
    {
        Assert.False(ReplayCli.TryParseExportFrameRate(text, out _));
    }

    [Fact]
    public async Task Help_advertises_the_1_to_240_FPS_contract()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ReplayCli.RunAsync(["help"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("[--fps <1-240>]", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("[--i2c-address <0x00-0x7F>]", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("0x00", 0x00)]
    [InlineData("0x2a", 0x2A)]
    [InlineData("127", 0x7F)]
    public void I2c_address_option_accepts_hex_or_decimal_7bit_values(string value, int expected)
    {
        Assert.True(ReplayCli.TryReadI2cAddress(["--i2c-address", value], out var address, out var error), error);
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData("0x80")]
    [InlineData("-1")]
    [InlineData("invalid")]
    public void I2c_address_option_rejects_values_outside_7bit_range(string value)
    {
        Assert.False(ReplayCli.TryReadI2cAddress(["--i2c-address", value], out _, out var error));
        Assert.Contains("7-bit", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Formats_command_is_backed_by_the_executable_registry()
    {
        var result = await RunAsync("formats", "--json");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(5, json.RootElement.GetArrayLength());
        Assert.Contains(json.RootElement.EnumerateArray(), item =>
            item.GetProperty("Id").GetString() == "desay-97" &&
            item.GetProperty("Version").GetString() == "0x97");
    }

    [Fact]
    public async Task Common_inspect_preserves_the_typed_JSON_and_text_contract()
    {
        var fixture = Fixture("common-0x83-asil-lifecycle.nds.txt");
        var jsonResult = await RunAsync(
            "inspect", fixture, "--event-buffer-version", "0x83", "--json");

        Assert.Equal(0, jsonResult.ExitCode);
        using var json = JsonDocument.Parse(jsonResult.Output);
        Assert.Equal("V83", json.RootElement.GetProperty("Version").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("Frames").GetArrayLength());
        Assert.Equal(fixture, json.RootElement.GetProperty("SourcePath").GetString());

        var textResult = await RunAsync("inspect", fixture, "--event-buffer-version", "0x83");
        Assert.Equal(0, textResult.ExitCode);
        Assert.Contains("Format  Common 0x83 (operator selected)", textResult.Output, StringComparison.Ordinal);
        Assert.Contains("Frames  3", textResult.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("standard", "Standard", "Desay 0x97 / Standard")]
    [InlineData("benz-palm", "BenzPalm", "Desay 0x97 / Benz Palm")]
    public async Task Desay_inspect_preserves_Standard_and_Benz_contracts(
        string option,
        string expectedJsonProfile,
        string expectedTextIdentity)
    {
        var fixture = Fixture("desay97-full-reread.nds.txt");
        var jsonResult = await RunAsync(
            "inspect", fixture,
            "--event-buffer-version", "0x97",
            "--register-profile", "51927",
            "--desay97-profile", option,
            "--json");

        Assert.Equal(0, jsonResult.ExitCode);
        using var json = JsonDocument.Parse(jsonResult.Output);
        Assert.Equal(expectedJsonProfile, json.RootElement.GetProperty("Profile").GetString());
        Assert.Equal(0x99000u, json.RootElement.GetProperty("EventBufferBase").GetUInt32());
        Assert.Equal(2, json.RootElement.GetProperty("Frames").GetArrayLength());

        var textResult = await RunAsync(
            "inspect", fixture,
            "--event-buffer-version", "0x97",
            "--register-profile", "51927",
            "--desay97-profile", option);
        Assert.Equal(0, textResult.ExitCode);
        Assert.Contains($"Format  {expectedTextIdentity} (operator selected)", textResult.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspect_configuration_failures_keep_usage_exit_and_messages()
    {
        var common = Fixture("common-0x83-asil-lifecycle.nds.txt");
        var unsupported = await RunAsync(
            "inspect", common, "--event-buffer-version", "0x86");
        Assert.Equal(2, unsupported.ExitCode);
        Assert.Equal("Unsupported Event Buffer Version: 0x86", unsupported.Error.Trim());

        var missingIc = await RunAsync(
            "inspect", common, "--event-buffer-version", "0x97");
        Assert.Equal(2, missingIc.ExitCode);
        Assert.Equal(
            "Desay 0x97 requires --register-profile <family>; its Event Buffer base is IC-specific.",
            missingIc.Error.Trim());

        var desay = Fixture("desay97-full-reread.nds.txt");
        var missingPalm = await RunAsync(
            "inspect", desay, "--event-buffer-version", "0x97");
        Assert.Equal(2, missingPalm.ExitCode);
        Assert.Equal(
            "Desay 0x97 requires --desay97-profile <standard|benz-palm>; it is never auto-detected.",
            missingPalm.Error.Trim());
    }

    [Fact]
    public async Task Analyze_manifest_keeps_format_evidence_and_decode_configuration()
    {
        var outputDirectory = Path.Combine(directory, "analysis");
        var result = await RunAsync(
            "analyze", Fixture("common-0x83-asil-lifecycle.nds.txt"),
            "--event-buffer-version", "0x83",
            "--output", outputDirectory,
            "--json");

        Assert.Equal(0, result.ExitCode);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "manifest.json")));
        Assert.Equal("0x83", manifest.RootElement.GetProperty("decodeConfiguration").GetProperty("eventBufferVersion").GetString());
        Assert.Equal("nds-communication-log", manifest.RootElement.GetProperty("decodeConfiguration").GetProperty("sourceAdapterId").GetString());
        Assert.Equal("verified", manifest.RootElement.GetProperty("formatEvidenceStatus").GetString());
    }

    [Fact]
    public async Task Export_with_missing_encoder_fails_clearly_without_a_silent_fallback()
    {
        var outputPath = Path.Combine(directory, "replay.mp4");
        var result = await RunAsync(
            "export", Fixture("common-0x83-asil-lifecycle.nds.txt"),
            "--event-buffer-version", "0x83",
            "--output", outputPath,
            "--ffmpeg", Path.Combine(directory, "missing-ffmpeg.exe"),
            "--json");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("Encoder error:", result.Error, StringComparison.Ordinal);
        Assert.Contains("No PNG fallback", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outputPath));
        Assert.False(Directory.Exists(outputPath + ".frames"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..",
        "fixtures",
        name));

    private static async Task<CliResult> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await ReplayCli.RunAsync(args, output, error);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);
}
