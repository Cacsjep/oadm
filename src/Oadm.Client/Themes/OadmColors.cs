using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Oadm.Client.Themes;

/// <summary>The appearance of the client (Settings > This computer); applies at the next start.</summary>
public enum Appearance
{
    Dark,
    Light,
}

/// <summary>
/// The colors of the chosen appearance (Colors.Dark.axaml or Colors.Light.axaml), merged into OadmTheme.axaml, so the theme's
/// brushes and styles take them when the theme loads. <see cref="Current"/> is set before the app loads its styles.
/// </summary>
public sealed class OadmColors : ResourceDictionary
{
    public static Appearance Current { get; set; } = Appearance.Dark;

    public OadmColors()
    {
        MergedDictionaries.Add(Load(Current));
    }

    /// <summary>The Fluent theme variant of an appearance (the built-in controls follow it).</summary>
    public static ThemeVariant VariantOf(Appearance appearance) => appearance == Appearance.Light ? ThemeVariant.Light : ThemeVariant.Dark;

    public static ResourceDictionary Load(Appearance appearance) =>
        (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri($"avares://Oadm.Client/Themes/Colors.{appearance}.axaml"));
}
