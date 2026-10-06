using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nvt.Replay.Tests;

// Zero-difference evidence for replacing NFU's AtomicOutput with Nvt.Core.IO.AtomicOutput.
public sealed class AtomicOutputAdoptionParityTests : IDisposable
{
    private const string FileName = "output.bin";
    private static readonly byte[] PriorBytes = "prior output"u8.ToArray();
    private readonly string root = Path.Combine(Path.GetTempPath(), $"nvt-atomic-parity-{Guid.NewGuid():N}");
    private int runs;

    public AtomicOutputAdoptionParityTests() => Directory.CreateDirectory(root);

    public static TheoryData<string> Payloads => ["empty", "one-byte", "64KiB", "1MiB", "utf8", "utf8-bom"];

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task New_output_has_identical_bytes(string payload)
    {
        var (oracle, core) = await RunBothAsync(existing: null, Writer(payload));

        AssertSameSuccess(oracle, core);
        if (payload == "utf8-bom") Assert.Equal(Encoding.UTF8.Preamble.ToArray(), core.Bytes![..3]);
        if (payload == "utf8") Assert.NotEqual(0xEF, core.Bytes![0]);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task Overwritten_output_has_identical_bytes(string payload)
    {
        var (oracle, core) = await RunBothAsync(PriorBytes, Writer(payload));

        AssertSameSuccess(oracle, core);
    }

    [Fact]
    public async Task Writer_failure_keeps_prior_output_and_leaves_no_temporary_file()
    {
        var (oracle, core) = await RunBothAsync(PriorBytes, async (stream, _, token) =>
        {
            await stream.WriteAsync("partial"u8.ToArray(), token);
            throw new InvalidOperationException("synthetic writer failure");
        });

        AssertSameFailure(oracle, core);
        Assert.IsType<InvalidOperationException>(core.Error);
    }

    [Fact]
    public async Task Cancellation_before_the_call_keeps_prior_output_and_leaves_no_temporary_file()
    {
        var (oracle, core) = await RunBothAsync(PriorBytes, (stream, _, token) => stream.WriteAsync(Seeded(64), token).AsTask(), cancelBefore: true);

        AssertSameFailure(oracle, core);
        Assert.IsAssignableFrom<OperationCanceledException>(core.Error);
    }

    [Fact]
    public async Task Cancellation_inside_the_writer_keeps_prior_output_and_leaves_no_temporary_file()
    {
        var (oracle, core) = await RunBothAsync(PriorBytes, async (stream, cancellation, token) =>
        {
            await stream.WriteAsync("replacement"u8.ToArray(), token);
            await cancellation.CancelAsync();
        });

        AssertSameFailure(oracle, core);
        Assert.IsAssignableFrom<OperationCanceledException>(core.Error);
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static void AssertSameSuccess(Outcome oracle, Outcome core)
    {
        Assert.Null(oracle.Error);
        Assert.Null(core.Error);
        Assert.Equal(oracle.Bytes, core.Bytes);
        Assert.Equal(SHA256.HashData(oracle.Bytes!), SHA256.HashData(core.Bytes!));
        Assert.Equal(oracle.Calls, core.Calls);
        Assert.Empty(oracle.Leftovers);
        Assert.Empty(core.Leftovers);
    }

    private static void AssertSameFailure(Outcome oracle, Outcome core)
    {
        Assert.NotNull(oracle.Error);
        Assert.Equal(oracle.Error.GetType(), core.Error?.GetType());
        Assert.Equal(oracle.Error.Message, core.Error!.Message);
        Assert.Equal(oracle.Calls, core.Calls);
        Assert.Equal(PriorBytes, oracle.Bytes);
        Assert.Equal(PriorBytes, core.Bytes);
        Assert.Empty(oracle.Leftovers);
        Assert.Empty(core.Leftovers);
    }

    private async Task<(Outcome Oracle, Outcome Core)> RunBothAsync(byte[]? existing, Write write, bool cancelBefore = false) =>
        (await RunAsync(OracleWriteAsync, existing, write, cancelBefore),
         await RunAsync(Nvt.Core.IO.AtomicOutput.WriteAsync, existing, write, cancelBefore));

    private async Task<Outcome> RunAsync(Helper helper, byte[]? existing, Write write, bool cancelBefore)
    {
        var folder = Path.Combine(root, (++runs).ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        if (existing is not null) await File.WriteAllBytesAsync(path, existing);
        using var cancellation = new CancellationTokenSource();
        if (cancelBefore) await cancellation.CancelAsync();
        var calls = 0;
        Exception? error = null;
        try
        {
            await helper(path, (stream, token) =>
            {
                calls++;
                return write(stream, cancellation, token);
            }, cancellation.Token);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        var bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        var leftovers = Directory.GetFiles(folder).Select(file => Path.GetFileName(file)).Where(name => name != FileName).ToArray();
        return new Outcome(bytes, error, calls, leftovers);
    }

    private static Write Writer(string payload) => payload switch
    {
        "empty" => (stream, _, token) => stream.WriteAsync(Array.Empty<byte>(), token).AsTask(),
        "one-byte" => (stream, _, token) => stream.WriteAsync(new byte[] { 0x5A }, token).AsTask(),
        "64KiB" => (stream, _, token) => stream.WriteAsync(Seeded(64 * 1024), token).AsTask(),
        "1MiB" => (stream, _, token) => stream.WriteAsync(Seeded(1024 * 1024), token).AsTask(),
        "utf8" => Text(withBom: false),
        "utf8-bom" => Text(withBom: true),
        _ => throw new ArgumentOutOfRangeException(nameof(payload), payload, null),
    };

    private static Write Text(bool withBom) => async (stream, _, token) =>
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(withBom), leaveOpen: true);
        await writer.WriteAsync("time,event,詳細\r\n0.001,touch,\"a,b\"\r\n".AsMemory(), token);
        await writer.FlushAsync(token);
    };

    private static byte[] Seeded(int length)
    {
        var bytes = new byte[length];
        new Random(20261006).NextBytes(bytes);
        return bytes;
    }

    // Test-only oracle: verbatim copy of NFU's former Nvt.Replay.Core.AtomicOutput.WriteAsync from
    // src/Nvt.Replay.Core/AtomicOutput.cs at 26d66bd377a4ad051392bd7cc7e9d1c2e6287dba, which this change deletes.
    private static async Task OracleWriteAsync(
        string path,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Output path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await write(stream, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private delegate Task Helper(string path, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken);

    private delegate Task Write(Stream stream, CancellationTokenSource cancellation, CancellationToken token);

    private sealed record Outcome(byte[]? Bytes, Exception? Error, int Calls, string[] Leftovers);
}
