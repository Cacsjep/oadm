using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Discovery;
using Oadm.Contracts.Sites;
using Oadm.Contracts.V1;
using Oadm.Client.Infrastructure;

namespace Oadm.Client.Tests;

/// <summary>
/// One rule for every Site column (docs/spec/sites.md, Client): shown while the server has sites, hidden otherwise; Discovery
/// names the sites; devices at a site open in Web View with the right reason when they cannot.
/// </summary>
public sealed class SiteColumnRulesTests
{
    private static SiteList WithSite() => new() { Sites = { new Site { Id = Guid.NewGuid().ToString(), Name = "Building B", State = SiteState.Online } } };

    [Fact]
    public void The_device_grid_offers_the_site_column_only_while_the_server_has_sites()
    {
        using var f = new DevicesFixture();
        var site = f.Devices.Columns.Find(DevicesViewModel.SiteColumnKey)!;
        Assert.False(f.Devices.HasSites);
        Assert.Equal((false, DevicesViewModel.NoSitesText), (site.IsAvailable, site.UnavailableReason));

        f.Store.Sites.Apply(WithSite());
        Assert.True(f.Devices.HasSites);
        Assert.Equal((true, (string?)null), (site.IsAvailable, site.UnavailableReason));

        // A waiting node is not a site yet; a list without sites hides the column again.
        f.Store.Sites.Apply(new SiteList { Sites = { new Site { Name = "site-pc-3", State = SiteState.Waiting, NodeId = "n1" } } });
        Assert.False(f.Devices.HasSites);
    }

    [Fact]
    public void Without_the_site_list_the_devices_decide()
    {
        var presence = new Sites.SitePresence();
        presence.SetFromDevices(true);
        Assert.True(presence.HasSites);
        presence.Apply(new SiteList()); // the server's list wins while it is known
        Assert.False(presence.HasSites);
        presence.Clear();
        Assert.True(presence.HasSites);
    }

    [Fact]
    public void The_add_page_and_the_waiting_list_show_the_site_column_while_the_server_has_sites()
    {
        var api = Substitute.For<IOadmApi>();
        var without = new AddDevicesViewModel(api, new ImmediateUiDispatcher(), NullLogger<AddDevicesViewModel>.Instance, AddDevicesMode.Scan);
        var with = new AddDevicesViewModel(api, new ImmediateUiDispatcher(), NullLogger<AddDevicesViewModel>.Instance, AddDevicesMode.Scan) { HasSites = true };

        Assert.Equal((false, SiteDiscoveryTexts.FoundLocally), (without.ShowSiteColumn, without.HeaderDescription));
        Assert.Equal((true, SiteDiscoveryTexts.FoundHereAndAtSites), (with.ShowSiteColumn, with.HeaderDescription));
        Assert.Equal("Axis devices found on this server's network and at the sites.", with.HeaderDescription);

        var dialogs = Substitute.For<IDialogService>();
        Assert.False(new WaitingDevicesViewModel(api, dialogs, new WaitingDevicesState()).ShowSiteColumn);
        Assert.True(new WaitingDevicesViewModel(api, dialogs, new WaitingDevicesState(), hasSites: true).ShowSiteColumn);
    }

    [Fact]
    public void A_device_at_a_site_says_why_it_cannot_open_in_web_view()
    {
        using var f = new DevicesFixture();
        var atSite = TestSupport.Device("1", "B8A44F631339", "192.168.0.20", "AXIS P3265-V");
        atSite.SiteId = Guid.NewGuid().ToString();
        atSite.SiteName = "Building B";
        f.SeedDevices(atSite);
        f.Select("1");

        f.Devices.WebViewAccess = SiteWebAccess.NotAllowed; // Web View is on, the role may not open devices
        var entry = f.Devices.ContextMenuEntries.Single(e => e.Header == "Open web interface");
        Assert.Equal((false, Oadm.Sdk.Client.DeviceTexts.OpenNotAllowed), (entry.IsEnabled, entry.ToolTip));
        Assert.Equal("Your role does not allow opening devices.", f.Devices.SiteWebInterfaceTip);

        f.Devices.WebViewAccess = SiteWebAccess.Off;
        entry = f.Devices.ContextMenuEntries.Single(e => e.Header == "Open web interface");
        Assert.Equal((false, SiteWebViewTexts.TurnOnWebView), (entry.IsEnabled, entry.ToolTip));

        f.Devices.WebViewAccess = SiteWebAccess.Available;
        entry = f.Devices.ContextMenuEntries.Single(e => e.Header == "Open web interface");
        Assert.Equal((true, SiteWebViewTexts.OpenInWebView), (entry.IsEnabled, entry.ToolTip));
    }
}
