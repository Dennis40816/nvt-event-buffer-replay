using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed class NvtRegisterTracker
{
    private readonly Dictionary<int, uint> pageBySlave = [];
    private readonly Dictionary<int, List<IReadOnlyList<byte>>> pendingWritesBySlave = [];

    public void ResetEvidence()
    {
        pageBySlave.Clear();
        pendingWritesBySlave.Clear();
    }

    public SourceRecord Observe(SourceRecord record)
    {
        if (record.I2c is not { } transport)
        {
            return record;
        }

        if (record.Operation == BusOperation.Write)
        {
            return ObserveWrite(record, transport.SlaveAddress);
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
            fields["register_page_known"] = (address is not null).ToString().ToLowerInvariant();
            if (address is null) fields["register_name"] = NvtRegisterCatalog.EventBufferOffsetName(registerOffset);
        }
        return NvtRegisterCatalog.Annotate(record with
        {
            Address = address ?? record.Address,
            I2c = transport with { WriteCommands = commands },
            SourceFields = fields,
        });
    }

    private SourceRecord ObserveWrite(SourceRecord record, int slave)
    {
        if (record.Data.Count == 0)
        {
            pendingWritesBySlave.Remove(slave);
            return record;
        }

        var command = record.Data.ToArray();
        if (command.Length >= 3 && command[0] == 0xFF)
        {
            var selectedPage = (uint)((command[1] << 16) | (command[2] << 8));
            pageBySlave[slave] = selectedPage;
            pendingWritesBySlave[slave] = [command];
            return record;
        }

        if (command.Length == 1)
        {
            var pending = pendingWritesBySlave.GetOrAdd(slave);
            pending.RemoveAll(item => item.Count == 0 || item[0] != 0xFF);
            pending.Add(command);
            return record;
        }

        pendingWritesBySlave.Remove(slave, out var preceding);
        var offset = command[0];
        var address = pageBySlave.TryGetValue(slave, out var page) ? page + offset : (uint?)null;
        var fields = new Dictionary<string, string>(record.SourceFields ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["register_offset"] = $"0x{offset:X2}",
            ["register_page_known"] = (address is not null).ToString().ToLowerInvariant(),
        };
        if (address is null) fields["register_name"] = NvtRegisterCatalog.EventBufferOffsetName(offset);
        if (address is { } register) fields["register_address"] = $"0x{register:X}";

        var transport = record.I2c! with
        {
            WriteCommands = preceding is null ? [] : preceding,
        };
        var withAddress = record with { Address = address ?? record.Address, I2c = transport, SourceFields = fields };
        if (address is null) return withAddress;

        // Register catalogs describe the value written at the resolved offset, not
        // the offset byte carried by the I²C transaction. Restore the untouched raw
        // transaction after applying the semantic annotation.
        var payload = command[1..];
        var annotated = NvtRegisterCatalog.Annotate(withAddress with { Data = payload });
        if (address == 0xFF0FE && payload is [0x69, ..])
        {
            pageBySlave.Remove(slave);
            pendingWritesBySlave.Remove(slave);
        }
        return annotated with { Data = record.Data, DeclaredByteCount = record.DeclaredByteCount };
    }

    private uint? ResolveAddress(int slave, IReadOnlyList<IReadOnlyList<byte>> commands, out byte? offset)
    {
        offset = null;
        foreach (var command in commands)
        {
            if (command.Count >= 3 && command[0] == 0xFF)
            {
                var page = (uint)((command[1] << 16) | (command[2] << 8));
                pageBySlave[slave] = page;
                if (command.Count >= 4)
                {
                    offset = command[3];
                }
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
