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

/// <summary>Navigation page of a core plugin. <see cref="View"/> comes from the plugin's client assembly, if installed.</summary>
public sealed class CorePluginPageViewModel(string pluginId, string title, object? view, bool hasOwnCards = false)
{
    public string PluginId { get; } = pluginId;
    public string Title { get; } = title;
    public object? View { get; } = view;
    public bool HasView => View is not null;

    /// <summary>The view brings its own cards (<c>ICorePluginPage.HasOwnCards</c>): no host card around it.</summary>
    public bool HasOwnCards { get; } = hasOwnCards && view is not null;

    public bool ShowInCard => !HasOwnCards;

    /// <summary>
    /// The view for the host card, or null. A control can have only one visual parent, so the view
    /// is handed to exactly one of the two presenters (<see cref="CardView"/> / <see cref="OwnCardsView"/>).
    /// </summary>
    public object? CardView => HasOwnCards ? null : View;

    /// <summary>The view for pages that bring their own cards, or null.</summary>
    public object? OwnCardsView => HasOwnCards ? View : null;
}
