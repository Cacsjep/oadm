using Avalonia.Controls;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Plugins;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.Tests;

/// <summary>
/// Pages shown as tabs of another plugin's page (<see cref="ICorePluginPage.TabOf"/>, docs/spec/plugins.md "Pages shown as
/// tabs"): no rail entry, the host page's context lists them, OpenAsync and the start page open the host on the tab.
/// </summary>
public sealed class PluginTabTests
{
    private sealed class Page(string id, string title, string? tabOf = null) : ICorePluginPage
    {
        public List<ICorePluginClientContext> Contexts { get; } = [];

        public List<Model> Models { get; } = [];

        public string PluginId => id;

        public string Title => title;

        public string? TabOf => tabOf;

        public Control CreateView(ICorePluginClientContext ctx)
        {
            Contexts.Add(ctx);
            var model = new Model();
            Models.Add(model);
            return new TextBlock { Text = title, DataContext = model };
        }
    }

    private sealed class Model : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static readonly CorePluginInfo Pki = new() { Id = "oadm.pki", DisplayName = "PKI", Group = "Security" };
    private static readonly CorePluginInfo Radius = new() { Id = "oadm.radius", DisplayName = "802.1X server", Group = "Security" };
    private static readonly CorePluginInfo Est = new() { Id = "oadm.est", DisplayName = "EST server", Group = "Security" };

    private sealed class Rig : IDisposable
    {
        public Rig(UserSession? session = null)
        {
            var registry = Substitute.For<IClientPluginRegistry>();
            registry.FindDialog(Arg.Any<string>()).Returns((ITaskPluginDialog?)null);
            registry.FindPage("oadm.pki").Returns(Host);
            registry.FindPage("oadm.radius").Returns(Radius);
            registry.FindPage("oadm.est").Returns(Est);
            registry.ToolbarPlugins.Returns([]);
            Fixture = new DevicesFixture(registry: registry);
            Connection = new ServerConnection(Fixture.Api, Fixture.Store, Fixture.Tasks, Fixture.Ui, NullLogger<ServerConnection>.Instance);
            Shell = Fixture.CreateShell(Connection, session);
        }

        public Page Host { get; } = new("oadm.pki", "PKI");

        public Page Radius { get; } = new("oadm.radius", "802.1X server", "oadm.pki");

        public Page Est { get; } = new("oadm.est", "EST server", "oadm.pki");

        public DevicesFixture Fixture { get; }

        public ServerConnection Connection { get; }

        public MainWindowViewModel Shell { get; }

        public ICorePluginClientContext HostContext => Host.Contexts[0];

        public string[] RailKeys => [.. Shell.NavItems.Where(n => n.Key.StartsWith("plugin:", StringComparison.Ordinal)).Select(n => n.Key)];

        public void Dispose()
        {
            Connection.Dispose();
            Fixture.Dispose();
        }
    }

    [Fact]
    public void Tab_pages_have_no_rail_entry_and_the_host_page_lists_them_in_the_server_order()
    {
        using var rig = new Rig();
        rig.Shell.SyncCorePluginPages([Est, Pki, Radius]);

        Assert.Equal(["plugin:oadm.pki"], rig.RailKeys);
        Assert.Equal(["oadm.est", "oadm.radius"], rig.HostContext.Tabs.Select(t => t.PluginId).ToArray());
        Assert.Equal(["EST server", "802.1X server"], rig.HostContext.Tabs.Select(t => t.Title).ToArray());

        // Each tab has its own context: its plugin id, not the host's.
        Assert.Single(rig.Radius.Contexts);
        Assert.NotSame(rig.HostContext, rig.Radius.Contexts[0]);
        Assert.Same(rig.Radius.Models[0], ((TextBlock)rig.HostContext.Tabs.Single(t => t.PluginId == "oadm.radius").View).DataContext);
    }

    [Fact]
    public void A_tab_whose_host_page_is_not_listed_is_a_rail_page_until_the_host_comes()
    {
        using var rig = new Rig();
        rig.Shell.SyncCorePluginPages([Radius]); // the role shows the 802.1X server but not PKI
        Assert.Equal(["plugin:oadm.radius"], rig.RailKeys);

        rig.Shell.SyncCorePluginPages([Pki, Radius]);
        Assert.Equal(["plugin:oadm.pki"], rig.RailKeys);
        Assert.True(rig.Radius.Models[0].Disposed); // the rail page was released
        Assert.Equal(["oadm.radius"], rig.HostContext.Tabs.Select(t => t.PluginId).ToArray());

        rig.Shell.SyncCorePluginPages([Radius]); // PKI's show permission removed: back into the rail
        Assert.Equal(["plugin:oadm.radius"], rig.RailKeys);
        Assert.True(rig.Radius.Models[1].Disposed);
    }

    [Fact]
    public void A_tab_that_leaves_the_list_leaves_the_host_page_and_is_released()
    {
        using var rig = new Rig();
        rig.Shell.SyncCorePluginPages([Pki, Radius, Est]);
        var changes = 0;
        rig.HostContext.TabsChanged += (_, _) => changes++;
        var closed = new List<string?>();
        rig.Shell.ClosePluginWindowsRequested += (_, id) => closed.Add(id);

        rig.Shell.SyncCorePluginPages([Pki, Est]); // the role lost "show" of the 802.1X server

        Assert.Equal(["oadm.est"], rig.HostContext.Tabs.Select(t => t.PluginId).ToArray());
        Assert.Equal(1, changes);
        Assert.True(rig.Radius.Models[0].Disposed);
        Assert.Contains("oadm.pki", closed); // the windows of PKI had the old tabs

        rig.Shell.SyncCorePluginPages([Pki, Est]); // nothing changed: nothing raised
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Opening_a_tab_page_opens_the_host_page_on_that_tab()
    {
        using var rig = new Rig();
        rig.Shell.SyncCorePluginPages([Pki, Radius, Est]);
        var requested = new List<string>();
        rig.HostContext.OpenTabRequested += (_, id) => requested.Add(id);

        await rig.Est.Contexts[0].OpenAsync("plugin:oadm.est");
        Assert.Equal("plugin:oadm.pki", rig.Shell.CurrentItem?.Key);
        Assert.Equal(["oadm.est"], requested);

        // Edit policies of a server tab: the host page on its own first tab.
        await rig.Radius.Contexts[0].OpenAsync("plugin:oadm.pki");
        Assert.Equal(["oadm.est", "oadm.pki"], requested);

        // A click in the rail keeps the tab that is open.
        rig.Shell.NavigateCommand.Execute(rig.Shell.NavItems.Single(n => n.Key == "devices"));
        rig.Shell.NavigateCommand.Execute(rig.Shell.NavItems.Single(n => n.Key == "plugin:oadm.pki"));
        Assert.Equal(2, requested.Count);
    }

    [Fact]
    public void A_start_page_of_a_tab_opens_the_host_page_on_that_tab_once_both_are_there()
    {
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = "otto", Role = UserRole.Operator, StartPage = "plugin:oadm.radius" }, "https://localhost:5080");
        using var rig = new Rig(session);
        rig.Shell.Start();
        Assert.Equal("devices", rig.Shell.CurrentItem?.Key);

        var requested = new List<string>();
        rig.Shell.SyncCorePluginPages([Pki]);
        rig.HostContext.OpenTabRequested += (_, id) => requested.Add(id);
        Assert.Equal("devices", rig.Shell.CurrentItem?.Key); // the tab is not there yet

        rig.Shell.SyncCorePluginPages([Pki, Radius]);
        Assert.Equal("plugin:oadm.pki", rig.Shell.CurrentItem?.Key);
        Assert.Equal(["oadm.radius"], requested);
        rig.Shell.Stop();
    }

    [Fact]
    public void A_window_of_the_host_page_gets_its_own_copies_of_the_tabs()
    {
        using var rig = new Rig();
        rig.Shell.SyncCorePluginPages([Pki, Radius]);
        CorePluginPageViewModel? opened = null;
        rig.Shell.PluginWindowRequested += (_, p) => opened = p;

        rig.Shell.NavItems.Single(n => n.Key == "plugin:oadm.pki").OpenInNewWindowCommand.Execute(null);

        Assert.NotNull(opened);
        var windowContext = rig.Host.Contexts[1];
        var copy = Assert.Single(windowContext.Tabs);
        Assert.NotSame(rig.HostContext.Tabs[0].View, copy.View);
        Assert.Equal(2, rig.Radius.Models.Count);

        opened.Dispose(); // the window closed: its copy is released, the rail's tab stays
        Assert.True(rig.Radius.Models[1].Disposed);
        Assert.False(rig.Radius.Models[0].Disposed);
    }
}
