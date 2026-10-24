using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.Pki.Client;

/// <summary>One fixed tab of the PKI page: Certificates or Policies (PKI's own content) or the page of a server (its view from the host).</summary>
public sealed partial class PkiTabViewModel : ObservableObject
{
    public PkiTabViewModel(string id, string title, object? view, Action<PkiTabViewModel> activate)
    {
        Id = id;
        Title = title;
        View = view;
        ActivateCommand = new RelayCommand(() => activate(this));
    }

    /// <summary>The tab's core plugin id ("oadm.radius"); PKI's own id for Certificates, <see cref="PkiViewModel.PoliciesTabId"/> for Policies.</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>The server page's view (<see cref="CorePluginTab.View"/>); null for Certificates and Policies.</summary>
    public object? View { get; }

    /// <summary>The shown tab.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public IRelayCommand ActivateCommand { get; }
}

/// <summary>
/// The tabs of the PKI page (docs/spec/pki.md "The PKI page"): Certificates, Policies, then the 802.1X server and the EST
/// server when the host hands their pages over (<see cref="ICorePluginClientContext.Tabs"/>: only the ones the role may show).
/// The host asks for a tab with <see cref="ICorePluginClientContext.OpenTabRequested"/>; Edit policies of a server tab asks
/// for Policies through <see cref="PkiTabRequest"/>.
/// </summary>
public sealed partial class PkiViewModel
{
    /// <summary>The title of PKI's own tab.</summary>
    public const string CertificatesTabTitle = "Certificates";

    /// <summary>The id and title of the Policies tab (PKI's own content, no plugin of its own).</summary>
    public const string PoliciesTabId = "policies";

    public const string PoliciesTabTitle = "Policies";

    /// <summary>The order of the known server tabs; any other comes after them in the host's order.</summary>
    private static readonly string[] TabOrder = [Oadm.Plugins.Radius.Client.RadiusPluginInfo.PluginId, Oadm.Plugins.Est.EstPluginInfo.PluginId];

    /// <summary>The tabs in their order, Certificates and Policies first.</summary>
    public ObservableCollection<PkiTabViewModel> Tabs { get; } = [];

    /// <summary>The tab row shows: there is a tab besides Certificates (Policies only for users with all devices).</summary>
    public bool HasTabs => Tabs.Count > 1;

    /// <summary>The shown tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCertificatesTab), nameof(IsPoliciesTab), nameof(IsServerTab), nameof(ActiveTabView))]
    public partial PkiTabViewModel? ActiveTab { get; private set; }

    /// <summary>The Certificates tab is shown (PKI's own cards).</summary>
    public bool IsCertificatesTab => ActiveTab is null || ActiveTab.Id == PkiPluginInfo.PluginId;

    /// <summary>The Policies tab is shown.</summary>
    public bool IsPoliciesTab => ActiveTab?.Id == PoliciesTabId;

    /// <summary>A server tab is shown (its own view).</summary>
    public bool IsServerTab => !IsCertificatesTab && !IsPoliciesTab;

    /// <summary>The view of the shown server tab; null on Certificates and Policies (only the shown tab's view is in the window).</summary>
    public object? ActiveTabView => IsServerTab ? ActiveTab?.View : null;

    /// <summary>Shows the tab of <paramref name="id"/> (a server's plugin id, or PKI's own id for Certificates); unknown ids are ignored.</summary>
    public void ShowTab(string id)
    {
        // Edit policies of a server tab opens PKI's page (PKI's own id) and asks for Policies.
        if (id == PkiPluginInfo.PluginId && PkiTabRequest.Take() is { } requested)
        {
            id = requested;
        }

        if (Tabs.FirstOrDefault(t => t.Id == id) is { } tab)
        {
            Activate(tab);
        }
    }

    private void InitTabs()
    {
        RebuildTabs();
        _ctx.TabsChanged += (_, _) => RebuildTabs();
        _ctx.OpenTabRequested += (_, id) => ShowTab(id);
    }

    /// <summary>The tabs again from the host's list; the shown tab stays while it is there, else Certificates.</summary>
    private void RebuildTabs()
    {
        var shown = ActiveTab?.Id ?? PkiPluginInfo.PluginId;
        var servers = _ctx.Tabs
            .Select((tab, index) => (tab, index))
            .OrderBy(t => Array.IndexOf(TabOrder, t.tab.PluginId) is var known and >= 0 ? known : TabOrder.Length + t.index)
            .Select(t => t.tab);
        Tabs.Clear();
        Tabs.Add(new PkiTabViewModel(PkiPluginInfo.PluginId, CertificatesTabTitle, null, Activate));
        if (Policies.IsShown)
        {
            Tabs.Add(new PkiTabViewModel(PoliciesTabId, PoliciesTabTitle, null, Activate));
        }

        foreach (var tab in servers)
        {
            Tabs.Add(new PkiTabViewModel(tab.PluginId, tab.Title, tab.View, Activate));
        }

        OnPropertyChanged(nameof(HasTabs));
        Activate(Tabs.FirstOrDefault(t => t.Id == shown) ?? Tabs[0]);
    }

    private void Activate(PkiTabViewModel tab)
    {
        foreach (var other in Tabs)
        {
            other.IsActive = ReferenceEquals(other, tab);
        }

        ActiveTab = tab;
    }
}

/// <summary>
/// The tab a server tab's Edit policies asks for on PKI's page (the host opens the page with PKI's own id; the page shows
/// this tab instead of Certificates). One request, taken once.
/// </summary>
public static class PkiTabRequest
{
    private static string? _pending;

    /// <summary>The next open of PKI's page shows <paramref name="tabId"/>.</summary>
    public static void Set(string tabId) => Volatile.Write(ref _pending, tabId);

    /// <summary>The waiting request, cleared.</summary>
    public static string? Take() => Interlocked.Exchange(ref _pending, null);
}
