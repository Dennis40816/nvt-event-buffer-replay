using System.Security.Cryptography;
using Nvt.Core.Files;

namespace Nvt.Replay.Core;

/// <summary>Raised when a file is refused for hashing: too large, or changed while it was read.</summary>
public sealed class FileNotHashableException : IOException
{
    public FileNotHashableException(string message) : base(message) { }

    public FileNotHashableException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Computes the SHA-256 of a whole file through Core's bounded reader.</summary>
public static class FileHasher
{
    /// <summary>The largest file NFU hashes and decodes. One value for every call site.</summary>
    public const long MaximumFileBytes = 512L * 1024 * 1024;

    /// <summary>Hashes a file. Throws <see cref="FileNotHashableException"/> when it is too large or changes while it is read.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            useAsync: true);
        return await ComputeSha256Async(stream, stream.Length, MaximumFileBytes, cancellationToken, path).ConfigureAwait(false);
    }

    /// <summary>Hashes <paramref name="observedLength"/> bytes from the start of a stream and checks that nothing else follows.</summary>
    public static async Task<string> ComputeSha256Async(
        Stream stream,
        long observedLength,
        long maximumBytes,
        CancellationToken cancellationToken,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = displayName is null ? string.Empty : displayName + ": ";
        if (observedLength > maximumBytes)
        {
            throw new FileNotHashableException(
                $"{prefix}The file is {FormatMebibytes(observedLength)} MiB. The maximum is {FormatMebibytes(maximumBytes)} MiB.");
        }

        try
        {
            var result = await BoundedFileReader
                .ReadAndHashAsync(stream, observedLength, FileCaptureMode.IdentityOnly, cancellationToken)
                .ConfigureAwait(false);
            return Convert.ToHexStringLower(result.Sha256!);
        }
        catch (FileChangedDuringReadException exception)
        {
            throw new FileNotHashableException(
                prefix + "The file changed while it was read. Copy the file and load the copy.", exception);
        }
    }

    // Rounds up so that a file one byte over the limit never reads as equal to it.
    private static string FormatMebibytes(long bytes) =>
        ((bytes + (1024 * 1024) - 1) / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
