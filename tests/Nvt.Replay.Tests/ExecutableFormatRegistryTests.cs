using Nvt.Replay.Analysis;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;

namespace Nvt.Replay.Tests;

public sealed class ExecutableFormatRegistryTests
{
    [Theory]
    [InlineData("0x82", CommonEventBufferVersion.V82, "Common 0x82")]
    [InlineData("83", CommonEventBufferVersion.V83, "Common 0x83")]
    [InlineData("0X84", CommonEventBufferVersion.V84, "Common 0x84")]
    [InlineData("85", CommonEventBufferVersion.V85, "Common 0x85")]
    public void Common_versions_resolve_to_typed_selections(
        string versionText,
        CommonEventBufferVersion expected,
        string display)
    {
        var resolved = ExecutableFormatRegistry.TryResolve(
            new FormatDecodeRequest(versionText),
            out var selection,
            out var error);

        Assert.True(resolved, error);
        var common = Assert.IsType<CommonFormatSelection>(selection);
        Assert.Equal(expected, common.Version);
        Assert.Equal(display, common.DisplayIdentity);
        Assert.Equal(display, common.Descriptor.DisplayName);
        Assert.Equal(0x01, common.TargetI2cAddress);
    }

    [Theory]
    [InlineData(null, null, "--register-profile")]
    [InlineData("51927", null, "--desay97-profile")]
    [InlineData("51927", "unknown", "--desay97-profile")]
    public void Desay_requires_IC_and_explicit_Palm_configuration(
        string? registerProfile,
        string? palmProfile,
        string expectedError)
    {
        var resolved = ExecutableFormatRegistry.TryResolve(
            new FormatDecodeRequest("0x97", registerProfile, palmProfile),
            out var selection,
            out var error);

        Assert.False(resolved);
        Assert.Null(selection);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("standard", Desay97Profile.Standard, "Desay 0x97 / Standard")]
    [InlineData("benz", Desay97Profile.BenzPalm, "Desay 0x97 / Benz Palm")]
    [InlineData("BENZ_PALM", Desay97Profile.BenzPalm, "Desay 0x97 / Benz Palm")]
    public void Desay_profiles_resolve_with_the_IC_event_buffer_base(
        string palmProfile,
        Desay97Profile expected,
        string display)
    {
        var resolved = ExecutableFormatRegistry.TryResolve(
            new FormatDecodeRequest("97", "51927", palmProfile),
            out var selection,
            out var error);

        Assert.True(resolved, error);
        var desay = Assert.IsType<Desay97FormatSelection>(selection);
        Assert.Equal(expected, desay.Profile);
        Assert.Equal(0x99000u, desay.EventBufferBase);
        Assert.Equal(display, desay.DisplayIdentity);
    }

    [Fact]
    public void Unknown_versions_and_register_profiles_fail_without_guessing()
    {
        Assert.False(ExecutableFormatRegistry.TryResolve(
            new FormatDecodeRequest("0x86"), out _, out var formatError));
        Assert.Equal("Unsupported Event Buffer Version: 0x86", formatError);

        Assert.False(ExecutableFormatRegistry.TryResolve(
            new FormatDecodeRequest("0x83", "unknown"), out _, out var profileError));
        Assert.Contains("--register-profile must be one of:", profileError, StringComparison.Ordinal);

        Assert.False(ExecutableFormatRegistry.TryResolve(
            new FormatDecodeRequest("0x83", TargetI2cAddress: 0x80), out _, out var addressError));
        Assert.Contains("7-bit", addressError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Selection_and_configuration_preserve_the_operator_selected_7bit_address()
    {
        Assert.True(ExecutableFormatRegistry.TryResolve(
            new FormatDecodeRequest("0x83", TargetI2cAddress: 0x2A),
            out var selection,
            out var error), error);

        Assert.Equal(0x2A, selection!.TargetI2cAddress);
    }

    [Fact]
    public async Task Common_provider_owns_decode_replay_configuration_and_diagnostics()
    {
        var capture = await CaptureSession.LoadAsync(Fixture("common-0x83-asil-lifecycle.nds.txt"));
        Assert.True(ExecutableFormatRegistry.TryResolve(
            new FormatDecodeRequest("0x83"), out var selection, out var error), error);

        var result = Assert.IsType<CommonFormatDecodeResult>(selection!.Decode(capture));

        Assert.Equal(3, result.Report.Frames.Count);
        Assert.Equal(result.Report.Frames.Count, result.Replay.Count);
        Assert.Equal("Common 0x83", result.DisplayIdentity);
        Assert.Equal("0x83", result.Configuration.EventBufferVersion);
        Assert.Equal("nds-communication-log", result.Configuration.SourceAdapterId);
        Assert.Equal(result.Report.Diagnostics.Count + result.Replay.Diagnostics.Count, result.Diagnostics.Count);
        Assert.Same(result.Report, result.InspectionReport);
    }

    [Fact]
    public async Task Desay_provider_applies_the_profile_and_returns_a_typed_result()
    {
        var capture = await CaptureSession.LoadAsync(Fixture("desay97-full-reread.nds.txt"));
        Assert.Null(capture.RegisterProfile);
        Assert.True(ExecutableFormatRegistry.TryResolve(
            new FormatDecodeRequest("0x97", "51927", "benz-palm"),
            out var selection,
            out var error), error);

        var result = Assert.IsType<Desay97FormatDecodeResult>(selection!.Decode(capture));

        Assert.Equal(2, result.Report.Frames.Count);
        Assert.Equal(result.Report.Frames.Count, result.Replay.Count);
        Assert.Equal("51927", result.Capture.RegisterProfile);
        Assert.Equal("Benz Palm", result.Configuration.Desay97Profile);
        Assert.Equal(0x99000u, result.Configuration.EventBufferBase);
        Assert.Equal("Desay 0x97 / Benz Palm", result.DisplayIdentity);
        Assert.Same(result.Report, result.InspectionReport);
        Assert.Same(capture.Records, result.Capture.Records);
        var eventRead = result.Capture.Records.First(record => record.Address == 0x99000);
        Assert.True(result.Capture.RegisterAnnotations.TryGet(eventRead, out var annotation));
        Assert.Equal("Event Buffer", annotation.Description.Region);
    }

    [Fact]
    public async Task Provider_decode_observes_cancellation()
    {
        var capture = await CaptureSession.LoadAsync(Fixture("common-0x83-asil-lifecycle.nds.txt"));
        Assert.True(ExecutableFormatRegistry.TryResolve(
            new FormatDecodeRequest("0x83"), out var selection, out var error), error);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => selection!.Decode(capture, cancellation.Token));
    }

    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..",
        "fixtures",
        name));
}
