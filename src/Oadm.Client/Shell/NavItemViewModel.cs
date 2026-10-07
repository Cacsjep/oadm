using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Client.Shell;

/// <summary>Entry of the left navigation rail.</summary>
public sealed partial class NavItemViewModel(string key, string title, string iconKey, object page) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string IconKey { get; } = iconKey;
    public object Page { get; } = page;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial string? Badge { get; set; }

    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    /// <summary>Draw a thin separator above this entry (start of a new group).</summary>
    [ObservableProperty] public partial bool HasSeparatorBefore { get; set; }
}

public sealed class AboutViewModel(string serverAddress)
{
    public string Version { get; } = typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public static string SdkVersion => Oadm.Sdk.Plugins.SdkInfo.Version;
    public string ServerAddress { get; } = serverAddress;
    public static string License => "Apache License 2.0";
    public static string Description =>
        "OADM - Open AXIS Device Management. An open source, cross-platform alternative to AXIS Device Manager.";
}

/// <summary>Navigation page of a core plugin. <see cref="View"/> comes from the plugin's client assembly, if installed.</summary>
public sealed class CorePluginPageViewModel(string pluginId, string title, object? view)
{
    public string PluginId { get; } = pluginId;
    public string Title { get; } = title;
    public object? View { get; } = view;
    public bool HasView => View is not null;
}
