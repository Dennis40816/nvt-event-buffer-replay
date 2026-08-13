using System.Text.Json;
using Nvt.Replay.Formats;
using Nvt.Replay.Sources;

return await ReplayCli.RunAsync(args, Console.Out, Console.Error);

internal static class ReplayCli
{
    private const int UsageError = 2;
    private const int InputError = 3;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var operands = args.Where(argument => !argument.Equals("--json", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (operands.Length == 0 || operands[0] is "help" or "--help" or "-h")
        {
            await WriteHelpAsync(output);
            return 0;
        }

        switch (operands[0].ToLowerInvariant())
        {
            case "formats":
                await WriteAsync(output, BuiltInFormats.All, json);
                return 0;
            case "sources":
                var sources = BuiltInSources.All.Select(source => new { source.Id, source.DisplayName });
                await WriteAsync(output, sources, json);
                return 0;
            case "probe" when operands.Length == 2:
                return await ProbeAsync(operands[1], output, error, json);
            default:
                await error.WriteLineAsync("Unknown or incomplete command. Run 'nvt-replay help'.");
                return UsageError;
        }
    }

    private static async Task<int> ProbeAsync(string path, TextWriter output, TextWriter error, bool json)
    {
        if (!File.Exists(path))
        {
            await error.WriteLineAsync($"Input file does not exist: {path}");
            return InputError;
        }

        var results = new List<object>();
        foreach (var source in BuiltInSources.All)
        {
            var result = await source.ProbeAsync(path);
            results.Add(result);
        }

        await WriteAsync(output, results, json);
        return 0;
    }

    private static async Task WriteAsync<T>(TextWriter output, T value, bool json)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        switch (value)
        {
            case IEnumerable<Nvt.Replay.Core.FormatDescriptor> formats:
                foreach (var format in formats)
                {
                    await output.WriteLineAsync($"{format.Id,-12} {format.DisplayName} [{format.EvidenceStatus}]");
                }

                break;
            case IEnumerable<object> items:
                foreach (var item in items)
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(item));
                }

                break;
            default:
                await output.WriteLineAsync(JsonSerializer.Serialize(value));
                break;
        }
    }

    private static Task WriteHelpAsync(TextWriter output)
    {
        return output.WriteLineAsync(
            """
            NVT Event Buffer Replay CLI

            Usage:
              nvt-replay formats [--json]   List supported Event Buffer formats
              nvt-replay sources [--json]   List available input adapters
              nvt-replay probe <file> [--json]
                                           Suggest a source adapter without selecting a format

            Source detection is advisory. Event Buffer Version and Benz Palm remain explicit choices.
            """);
    }
}
