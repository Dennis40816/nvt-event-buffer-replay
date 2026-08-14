using System.Text;

namespace Nvt.Replay.Sources;

internal static class DelimitedText
{
    public static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(field.ToString().Trim());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }
        if (quoted)
        {
            throw new FormatException("CSV row contains an unterminated quoted field.");
        }
        fields.Add(field.ToString().Trim());
        return fields.ToArray();
    }

    public static bool HeaderEquals(string? line, params string[] expected)
    {
        if (line is null)
        {
            return false;
        }
        try
        {
            var actual = ParseCsvLine(line);
            if (actual.Length > 0)
            {
                actual[0] = actual[0].TrimStart('\uFEFF');
            }
            return actual.SequenceEqual(expected, StringComparer.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] ParseBytes(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseByte)
            .ToArray();

    public static byte ParseByte(string value)
    {
        var normalized = value.Trim();
        return Convert.ToByte(normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? normalized[2..] : normalized, 16);
    }

    public static int ParseInteger(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(normalized.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)
            : int.Parse(normalized, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);
    }
}
