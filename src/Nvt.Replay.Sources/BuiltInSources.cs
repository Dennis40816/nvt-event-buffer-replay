using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

/// <summary>Provides the built-in capture source adapters.</summary>
public static class BuiltInSources
{
    /// <summary>Gets the built-in adapters for communication logs and decoded I2C captures.</summary>
    public static IReadOnlyList<ISourceAdapter> All { get; } =
    [
        new NdsCommunicationLogAdapter(),
        new SaleaeDecodedI2cAdapter(),
        new KingstVisDecodedI2cAdapter(),
        new DslDecodedI2cAdapter(),
        new AcuteDecodedI2cAdapter(),
        new ExcelDecodedI2cAdapter(),
        new CanonicalI2cTextAdapter(),
    ];
}
