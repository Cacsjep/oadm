using System.Globalization;

using Avalonia.Data.Converters;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.HardeningScan.Client;

/// <summary>Maps an icon key ("device.camera") to the host theme geometry, like the device grid.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public static IconKeyConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ToolbarButton.ResolveIcon(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Reads one cell of <see cref="HardeningRow.States"/> (one byte per column) as a bool for one chip property: the chip of
/// column <c>index</c> is ok / warn / error / visible. The row replaces the array when it changes, so the binding re-reads it.
/// </summary>
public sealed class CellStateConverter(int index, Func<CheckState, bool> test) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is byte[] states && index < states.Length && test((CheckState)states[index]);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
