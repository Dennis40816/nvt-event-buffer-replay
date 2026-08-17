using System.Text;

namespace Nvt.Replay.Sources;

internal sealed record TextFileLayout(Encoding Encoding, int PreambleLength, int NewLineByteCount)
{
    public long FirstLineOffset => PreambleLength;

    public long Advance(string line) => Encoding.GetByteCount(line) + NewLineByteCount;

    public static TextFileLayout Detect(string path)
    {
        Span<byte> sample = stackalloc byte[4096];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Read(sample);
        var bytes = sample[..length];
        var (encoding, preambleLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes[preambleLength..]);
        var newlineIndex = text.IndexOf('\n');
        var newline = newlineIndex < 0
            ? Environment.NewLine
            : newlineIndex > 0 && text[newlineIndex - 1] == '\r' ? "\r\n" : "\n";
        return new TextFileLayout(encoding, preambleLength, encoding.GetByteCount(newline));
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), 4);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }
}
