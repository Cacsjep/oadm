using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.Radius.Client;

/// <summary>The plugin's id and names on the page.</summary>
public static class RadiusPluginInfo
{
    public const string PluginId = "oadm.radius";

    public const string DisplayName = "802.1X server";

    public const string Subtitle = "Switches ask OADM which cameras may use their ports.";
}

/// <summary>The page of the "802.1X server" core plugin: its own cards. A tab of the PKI page (<see cref="TabOf"/>), no rail entry of its own.</summary>
public sealed class RadiusPage : ICorePluginPage
{
    public string PluginId => RadiusPluginInfo.PluginId;

    public string Title => RadiusPluginInfo.DisplayName;

    public bool HasOwnCards => true;

    /// <summary>The page is the "802.1X server" tab of the PKI page (docs/spec/pki.md "The PKI page").</summary>
    public string? TabOf => Oadm.Plugins.Pki.PkiPluginInfo.PluginId;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RadiusView { DataContext = new RadiusViewModel(ctx) };
    }
}

/// <summary>A choice of a select field: the value and its label.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
