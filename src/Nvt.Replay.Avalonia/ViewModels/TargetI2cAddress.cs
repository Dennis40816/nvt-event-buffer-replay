// Copyright (c) 2026 Dennis Liu. All rights reserved.

using System.Globalization;

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Parses and formats the 7-bit I²C address the operator picks as the decode target.</summary>
internal static class TargetI2cAddress
{
    internal static bool TryParse(string? value, out int address)
    {
        var text = value?.Trim() ?? string.Empty;
        var style = NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            style = NumberStyles.AllowHexSpecifier;
        }
        return int.TryParse(text, style, CultureInfo.InvariantCulture, out address) &&
               address is >= 0 and <= 0x7F;
    }

    internal static string Format(int address) => $"0x{address:X2}";
}
