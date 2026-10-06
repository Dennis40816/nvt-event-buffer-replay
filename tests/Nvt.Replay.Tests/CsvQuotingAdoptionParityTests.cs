using Nvt.Core.Csv;

namespace Nvt.Replay.Tests;

// Zero-difference evidence for replacing NFU's two private CSV helpers with Nvt.Core.Csv.CsvQuoting.
public sealed class CsvQuotingAdoptionParityTests
{
    private static readonly char[] Alphabet = ['a', ' ', '\t', ';', ',', '"', '\r', '\n', 'é', '雪'];

    public static TheoryData<string> Corpus =>
    [
        "",
        "plain ASCII 123",
        "觸控事件",
        "café",
        "😀",
        "a,b",
        "\"",
        "say \"hi\" twice \"\"",
        "line\rbreak",
        "line\nbreak",
        "line\r\nbreak",
        "tab\tseparated",
        "semi;colon",
        "  leading",
        "trailing  ",
        " ,\" mixed \r\n雪 ",
    ];

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Corpus_value_is_quoted_identically(string value) =>
        Assert.Equal(OracleCsv(value), CsvQuoting.Quote(value));

    [Fact]
    public void Seeded_random_values_are_quoted_identically()
    {
        var random = new Random(20261006);
        for (var index = 0; index < 10_000; index++)
        {
            var characters = new char[random.Next(0, 13)];
            for (var position = 0; position < characters.Length; position++)
            {
                characters[position] = Alphabet[random.Next(Alphabet.Length)];
            }

            var value = new string(characters);
            Assert.Equal(OracleCsv(value), CsvQuoting.Quote(value));
        }
    }

    // Invalid input: both reject null but with different exception types. NVT CORE decided that this
    // difference is acceptable. This test records it. No NFU caller passes null.
    [Fact]
    public void Null_value_throws_in_both_with_different_exception_types()
    {
        Assert.Throws<NullReferenceException>(() => OracleCsv(null!));
        Assert.Throws<ArgumentNullException>(() => CsvQuoting.Quote(null!));
    }

    // Test-only oracle: verbatim copy of the private Csv helper that this change deletes from
    // src/Nvt.Replay.Rendering/AnalysisOutputWriter.cs and src/Nvt.Replay.Rendering/ReadableCommunicationLogWriter.cs
    // at 26d66bd377a4ad051392bd7cc7e9d1c2e6287dba. Both copies were identical.
    private static string OracleCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
}
