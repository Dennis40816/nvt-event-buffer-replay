using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public static class BuiltInSources
{
    public static IReadOnlyList<ISourceAdapter> All { get; } =
    [
        new NdsCommunicationLogAdapter(),
        new SaleaeDecodedI2cAdapter(),
        new KingstVisDecodedI2cAdapter(),
        new DslDecodedI2cAdapter(),
        new ExcelDecodedI2cAdapter(),
        new CanonicalI2cTextAdapter(),
    ];
}
