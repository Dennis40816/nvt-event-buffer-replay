using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed class NvtRegisterTracker
{
    private readonly Dictionary<int, uint> pageBySlave = [];
    private readonly Dictionary<int, List<IReadOnlyList<byte>>> pendingWritesBySlave = [];

    public SourceRecord Observe(SourceRecord record)
    {
        if (record.I2c is not { } transport)
        {
            return record;
        }

        if (record.Operation == BusOperation.Write)
        {
            pendingWritesBySlave.GetOrAdd(transport.SlaveAddress).Add(record.Data);
            return record;
        }
        if (record.Operation != BusOperation.Read)
        {
            return record;
        }

        var commands = new List<IReadOnlyList<byte>>();
        if (pendingWritesBySlave.Remove(transport.SlaveAddress, out var pending))
        {
            commands.AddRange(pending);
        }
        commands.AddRange(transport.WriteCommands);
        var address = ResolveAddress(transport.SlaveAddress, commands, out var offset);
        var fields = new Dictionary<string, string>(record.SourceFields ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (address is { } register)
        {
            fields["register_address"] = $"0x{register:X}";
        }
        if (offset is { } registerOffset)
        {
            fields["register_offset"] = $"0x{registerOffset:X2}";
            fields["register_name"] = NvtRegisterCatalog.EventBufferOffsetName(registerOffset);
            fields["register_page_known"] = (address is not null).ToString().ToLowerInvariant();
        }
        return NvtRegisterCatalog.Annotate(record with
        {
            Address = address ?? record.Address,
            I2c = transport with { WriteCommands = commands },
            SourceFields = fields,
        });
    }

    private uint? ResolveAddress(int slave, IReadOnlyList<IReadOnlyList<byte>> commands, out byte? offset)
    {
        offset = null;
        foreach (var command in commands)
        {
            if (command.Count >= 4 && command[0] == 0xFF)
            {
                var page = (uint)((command[1] << 16) | (command[2] << 8));
                pageBySlave[slave] = page;
                offset = command[3];
            }
            else if (command.Count > 0)
            {
                offset = command[0];
            }
        }
        return offset is { } value && pageBySlave.TryGetValue(slave, out var pageBase)
            ? pageBase + value
            : null;
    }

}

internal static class DictionaryExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
        where TKey : notnull
        where TValue : new()
    {
        if (!dictionary.TryGetValue(key, out var value))
        {
            value = new TValue();
            dictionary.Add(key, value);
        }
        return value;
    }
}
