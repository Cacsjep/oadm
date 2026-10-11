using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    /// <summary>Core plugin pages: the rail group (<c>CorePluginGroup</c> name, e.g. "Servers"); null for host pages.</summary>
    public string? GroupTitle { get; init; }

    /// <summary>Position of the group in the rail (<c>CorePluginGroup</c> value).</summary>
    public int GroupOrder { get; init; }

    /// <summary>First entry of its group while the rail is expanded: the small group header above it.</summary>
    [ObservableProperty] public partial bool ShowGroupHeader { get; set; }

    /// <summary>
    /// Plugin pages only: the rail entry's context menu "Open in new window" opens another,
    /// independent copy of the page in its own window. Set by the shell; null for host pages.
    /// </summary>
    public Action<NavItemViewModel>? OpenInWindow { get; init; }

    public bool CanOpenInWindow => OpenInWindow is not null && Page is CorePluginPageViewModel { HasView: true };

    [RelayCommand]
    private void OpenInNewWindow() => OpenInWindow?.Invoke(this);
}

/// <summary>Navigation page of a core plugin. <see cref="View"/> comes from the plugin's client assembly, if installed.</summary>
/// <remarks>
/// <see cref="Dispose"/> (rail page removed, plugin window closed) detaches the plugin context it was created with and
/// disposes the view's DataContext when it is <see cref="IDisposable"/>, so the page releases its streams and the host
/// keeps no reference to it.
/// </remarks>
public sealed class CorePluginPageViewModel(string pluginId, string title, object? view, bool hasOwnCards = false, Oadm.Client.Tasks.TasksViewModel? tasks = null, string? error = null, IDisposable? context = null) : IDisposable
{
    private bool _disposed;

    /// <summary>Shown instead of the view: the page is not installed on this client, or it failed to open.</summary>
    public string NoViewText { get; } = error is null
        ? "The page of this plugin is not installed on this client."
        : $"The {title} page failed: {error}";

    public string PluginId { get; } = pluginId;
    public string Title { get; } = title;

    /// <summary>"I/O Monitor · OADM": the title of the page's own window.</summary>
    public string WindowTitle => Title + " · OADM";

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

    /// <summary>Secondary line under the page title (<c>ui:PageHeader.Subtitle</c> on the page's root control).</summary>
    public string? Subtitle { get; } = view is Avalonia.Controls.Control c ? Oadm.Sdk.Client.Controls.PageHeader.GetSubtitle(c) : null;

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    /// <summary>Control right of the page title (<c>ui:PageHeader.Trailing</c>), bound to the page's DataContext.</summary>
    public Avalonia.Controls.Control? HeaderTrailing { get; } = AttachTrailing(view);

    private static Avalonia.Controls.Control? AttachTrailing(object? view)
    {
        if (view is not Avalonia.Controls.Control page || Oadm.Sdk.Client.Controls.PageHeader.GetTrailing(page) is not { } trailing)
        {
            return null;
        }

        // The trailing control is shown by the host, outside the page; keep the page's bindings working.
        trailing.Bind(Avalonia.StyledElement.DataContextProperty, Avalonia.AvaloniaObjectExtensions.GetObservable(page, Avalonia.StyledElement.DataContextProperty));
        return trailing;
    }

    /// <summary>The shared tasks pane below the page (ICorePluginPage.ShowTasksPane), or null.</summary>
    public Oadm.Client.Tasks.TasksViewModel? Tasks { get; } = tasks;

    public bool ShowTasksPane => Tasks is not null;

    public bool IsDisposed => _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        context?.Dispose();
        if (View is Avalonia.StyledElement { DataContext: IDisposable disposable })
        {
            try
            {
                disposable.Dispose();
            }
#pragma warning disable CA1031 // A plugin view model that fails while disposing must not break closing its window or page.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
