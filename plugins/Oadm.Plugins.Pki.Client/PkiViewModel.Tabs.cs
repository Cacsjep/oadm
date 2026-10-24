using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.Pki.Client;

/// <summary>One fixed tab of the PKI page: Certificates (PKI's own content) or the page of a server (its view from the host).</summary>
public sealed partial class PkiTabViewModel : ObservableObject
{
    public PkiTabViewModel(string id, string title, object? view, Action<PkiTabViewModel> activate)
    {
        Id = id;
        Title = title;
        View = view;
        ActivateCommand = new RelayCommand(() => activate(this));
    }

    /// <summary>The tab's core plugin id ("oadm.radius"); PKI's own id for Certificates.</summary>
    public string Id { get; }

    public string Title { get; }

    /// <summary>The server page's view (<see cref="CorePluginTab.View"/>); null for Certificates.</summary>
    public object? View { get; }

    /// <summary>The shown tab.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public IRelayCommand ActivateCommand { get; }
}

/// <summary>
/// The tabs of the PKI page (docs/spec/pki.md "The PKI page"): Certificates, then the 802.1X server and the EST server when
/// the host hands their pages over (<see cref="ICorePluginClientContext.Tabs"/>: only the ones the role may show). The host
/// asks for a tab with <see cref="ICorePluginClientContext.OpenTabRequested"/> (Edit in PKI: Certificates).
/// </summary>
public sealed partial class PkiViewModel
{
    /// <summary>The title of PKI's own tab.</summary>
    public const string CertificatesTabTitle = "Certificates";

    /// <summary>The order of the known server tabs; any other comes after them in the host's order.</summary>
    private static readonly string[] TabOrder = [Oadm.Plugins.Radius.Client.RadiusPluginInfo.PluginId, Oadm.Plugins.Est.EstPluginInfo.PluginId];

    /// <summary>The tabs in their order, Certificates first.</summary>
    public ObservableCollection<PkiTabViewModel> Tabs { get; } = [];

    /// <summary>The tab row shows: there is a tab besides Certificates.</summary>
    public bool HasTabs => Tabs.Count > 1;

    /// <summary>The shown tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCertificatesTab), nameof(ActiveTabView))]
    public partial PkiTabViewModel? ActiveTab { get; private set; }

    /// <summary>The Certificates tab is shown (PKI's own cards).</summary>
    public bool IsCertificatesTab => ActiveTab is null || ActiveTab.Id == PkiPluginInfo.PluginId;

    /// <summary>The view of the shown server tab; null on Certificates (only the shown tab's view is in the window).</summary>
    public object? ActiveTabView => IsCertificatesTab ? null : ActiveTab?.View;

    /// <summary>Shows the tab of <paramref name="id"/> (a server's plugin id, or PKI's own id for Certificates); unknown ids are ignored.</summary>
    public void ShowTab(string id)
    {
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
