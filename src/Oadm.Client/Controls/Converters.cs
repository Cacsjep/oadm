using Avalonia.Data.Converters;

namespace Oadm.Client.Controls;

public static class Converters
{
    /// <summary>true = full opacity, false = muted (greyed out rows, e.g. devices that are already managed).</summary>
    public static FuncValueConverter<bool, double> MutedOpacity { get; } = new(enabled => enabled ? 1.0 : 0.4);
}
