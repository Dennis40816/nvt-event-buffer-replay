using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public static class BuiltInSources
{
    public static IReadOnlyList<ISourceAdapter> All { get; } =
    [
        new NdsCommunicationLogAdapter(),
    ];
}
