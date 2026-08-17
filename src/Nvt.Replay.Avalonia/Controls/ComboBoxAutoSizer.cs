using System.Collections;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Nvt.Replay.Avalonia.ViewModels;

namespace Nvt.Replay.Avalonia.Controls;

internal static class ComboBoxAutoSizer
{
    private const double MinimumWidth = 72;
    internal const double ChromeAndPaddingWidth = 48;

    public static void Fit(params ComboBox[] comboBoxes)
    {
        foreach (var comboBox in comboBoxes)
        {
            var requiredWidth = RequiredWidth(comboBox);
            comboBox.Width = double.NaN;
            comboBox.MinWidth = Math.Max(MinimumWidth, requiredWidth);
        }
    }

    internal static double RequiredWidth(ComboBox comboBox)
    {
        var labels = Labels(comboBox).ToArray();
        var longest = labels.Length == 0 ? 0 : labels.Max(label => MeasureLabelWidth(comboBox, label));
        return Math.Ceiling(longest + ChromeAndPaddingWidth);
    }

    internal static double MeasureLabelWidth(ComboBox comboBox, string label)
    {
        var typeface = new Typeface(
            comboBox.FontFamily,
            comboBox.FontStyle,
            comboBox.FontWeight,
            comboBox.FontStretch);
        var foreground = comboBox.Foreground ?? Brushes.Black;
        var fontSize = comboBox.FontSize > 0 ? comboBox.FontSize : 14;
        return new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            comboBox.FlowDirection,
            typeface,
            fontSize,
            foreground).Width;
    }

    private static IEnumerable<string> Labels(ComboBox comboBox)
    {
        if (!string.IsNullOrWhiteSpace(comboBox.PlaceholderText))
            yield return comboBox.PlaceholderText;

        var source = comboBox.ItemsSource as IEnumerable ?? comboBox.Items;
        foreach (var item in source)
        {
            var value = item is ComboBoxItem comboBoxItem ? comboBoxItem.Content : item;
            var label = value switch
            {
                SelectOption option => option.Label,
                RawRegisterFilterChoice option => option.Label,
                RegisterProfileChoice option => option.Label,
                _ => value?.ToString(),
            };
            if (!string.IsNullOrWhiteSpace(label)) yield return label;
        }
    }
}
