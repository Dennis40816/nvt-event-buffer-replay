using Nvt.Replay.Core;

namespace Nvt.Replay.Formats;

public static class BuiltInFormats
{
    private static readonly IReadOnlyList<string> EventVersions = ["0x82", "0x83", "0x84", "0x85"];

    public static IReadOnlyList<FormatDescriptor> All { get; } =
    [
        .. EventVersions.Select(version => new FormatDescriptor(
            $"common-{version[2..]}",
            "Common",
            version,
            $"Common {version}",
            version is "0x83" or "0x84" ? EvidenceStatus.Verified : EvidenceStatus.Provisional,
            [
                new ConfigurationField(
                    "event-buffer-version",
                    "Event Buffer Version",
                    true,
                    [version],
                    "The capture does not reliably identify this value; the operator must confirm it."),
            ])),
        new FormatDescriptor(
            "desay-97",
            "Desay",
            "0x97",
            "Desay 0x97 two-transaction",
            EvidenceStatus.Provisional,
            [
                new ConfigurationField(
                    "event-buffer-version",
                    "Event Buffer Version",
                    true,
                    ["0x97"]),
                new ConfigurationField(
                    "benz-palm",
                    "Benz Palm",
                    true,
                    ["Standard", "Benz"],
                    "0x97 Palm interpretation differs for Benz projects and must be confirmed before decoding."),
            ]),
    ];
}
