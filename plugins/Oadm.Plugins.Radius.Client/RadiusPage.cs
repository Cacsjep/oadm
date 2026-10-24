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

/// <summary>Navigation rail page of the "802.1X server" core plugin (group Security): fixed tabs with their own cards.</summary>
public sealed class RadiusPage : ICorePluginPage
{
    public string PluginId => RadiusPluginInfo.PluginId;

    public string Title => RadiusPluginInfo.DisplayName;

    public bool HasOwnCards => true;

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
