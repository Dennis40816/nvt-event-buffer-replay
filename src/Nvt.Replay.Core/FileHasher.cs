using System.Security.Cryptography;
using Nvt.Core.Files;

namespace Nvt.Replay.Core;

/// <summary>Raised when a file is refused for hashing: too large, or changed while it was read.</summary>
public sealed class FileNotHashableException : IOException
{
    public FileNotHashableException(string reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>The short cause without the file name and without advice, for warnings that already name the file.</summary>
    public string Reason { get; }
}

/// <summary>Computes the SHA-256 of a whole file through Core's bounded reader.</summary>
public static class FileHasher
{
    /// <summary>The largest file NFU hashes and decodes. One value for every call site.</summary>
    public const long MaximumFileBytes = 512L * 1024 * 1024;

    /// <summary>Hashes a file. Throws <see cref="FileNotHashableException"/> when it is too large or changes while it is read.</summary>
    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken,
        long maximumBytes = MaximumFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            useAsync: true);
        return await ComputeSha256Async(stream, stream.Length, maximumBytes, cancellationToken, path).ConfigureAwait(false);
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
            var reason = $"The file is {FormatSize(observedLength)}. The maximum is {FormatSize(maximumBytes)}.";
            throw new FileNotHashableException(reason, prefix + reason);
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
                "The file changed while it was read.",
                prefix + "The file changed while it was read. Copy the file and load the copy.",
                exception);
        }
    }

    // Sizes under 1 MiB show bytes. Larger sizes round up, so a file one byte over the limit never reads as equal to it.
    private static string FormatSize(long bytes)
    {
        const long Mebibyte = 1024 * 1024;
        return bytes < Mebibyte
            ? bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes"
            : ((bytes + Mebibyte - 1) / Mebibyte).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MiB";
    }
}
