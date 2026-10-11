using System.Security.Cryptography;
using Nvt.Replay.Core;

namespace Nvt.Replay.Tests;

public sealed class FileHasherTests
{
    private const long Limit = 1024;

    [Fact]
    public async Task Hash_matches_the_framework_hash_of_the_same_bytes()
    {
        var bytes = Enumerable.Range(0, 300).Select(value => (byte)value).ToArray();
        using var stream = new MemoryStream(bytes);

        var hash = await FileHasher.ComputeSha256Async(stream, bytes.Length, Limit, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), hash);
    }

    [Fact]
    public async Task Empty_content_has_the_empty_hash()
    {
        using var stream = new MemoryStream();

        var hash = await FileHasher.ComputeSha256Async(stream, 0, Limit, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), hash);
    }

    [Fact]
    public async Task Content_at_the_limit_is_hashed()
    {
        using var stream = new MemoryStream(new byte[Limit]);

        var hash = await FileHasher.ComputeSha256Async(stream, Limit, Limit, CancellationToken.None);

        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public async Task Content_above_the_limit_is_refused_before_reading()
    {
        using var stream = new MemoryStream(new byte[Limit + 1]);

        var exception = await Assert.ThrowsAsync<FileNotHashableException>(() =>
            FileHasher.ComputeSha256Async(stream, Limit + 1, Limit, CancellationToken.None));

        Assert.Equal(0, stream.Position);
        Assert.Contains("maximum", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Content_that_grew_after_it_was_measured_is_refused_with_the_copy_message()
    {
        using var stream = new MemoryStream(new byte[100]);

        var exception = await Assert.ThrowsAsync<FileNotHashableException>(() =>
            FileHasher.ComputeSha256Async(stream, 90, Limit, CancellationToken.None));

        Assert.Equal("The file changed while it was read. Copy the file and load the copy.", exception.Message);
        Assert.Equal("The file changed while it was read.", exception.Reason);
    }

    [Fact]
    public async Task Content_that_shrank_after_it_was_measured_is_refused_with_the_copy_message()
    {
        using var stream = new MemoryStream(new byte[80]);

        var exception = await Assert.ThrowsAsync<FileNotHashableException>(() =>
            FileHasher.ComputeSha256Async(stream, 90, Limit, CancellationToken.None));

        Assert.Equal("The file changed while it was read. Copy the file and load the copy.", exception.Message);
    }

    [Fact]
    public async Task Refusal_message_names_the_file_when_a_name_is_given()
    {
        using var stream = new MemoryStream(new byte[100]);

        var exception = await Assert.ThrowsAsync<FileNotHashableException>(() =>
            FileHasher.ComputeSha256Async(stream, 90, Limit, CancellationToken.None, "kernel.log"));

        Assert.StartsWith("kernel.log: ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_byte_over_the_limit_does_not_read_as_equal_to_it()
    {
        using var stream = new MemoryStream();

        var exception = await Assert.ThrowsAsync<FileNotHashableException>(() =>
            FileHasher.ComputeSha256Async(stream, (2 * 1024 * 1024) + 1, 2 * 1024 * 1024, CancellationToken.None));

        Assert.Equal("The file is 3 MiB. The maximum is 2 MiB.", exception.Message);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_hash()
    {
        using var stream = new MemoryStream(new byte[100]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileHasher.ComputeSha256Async(stream, 100, Limit, cancellation.Token));
    }

    [Fact]
    public void The_shared_limit_is_512_mebibytes()
    {
        Assert.Equal(512L * 1024 * 1024, FileHasher.MaximumFileBytes);
    }
}
