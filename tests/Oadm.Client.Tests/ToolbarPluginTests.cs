using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Devices.Toolbar;
using Oadm.Client.Plugins;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>Toolbar plugins: order and groups, the built-in entries, and loading an external one.</summary>
public sealed class ToolbarPluginTests
{
    /// <summary>artifacts/test-plugins, where Oadm.TestPlugins.Sample.Client deploys its build output.</summary>
    private static string TestPluginRoot =>
        Path.Combine(ClientPluginLoader.DevelopmentRoot(AppContext.BaseDirectory)!, "..", "test-plugins");

    [Fact]
    public void Plugins_are_ordered_by_group_order_and_id_and_built_in_ids_win()
    {
        IClientPluginRegistry registry = Substitute.For<IClientPluginRegistry>();
        registry.ToolbarPlugins.Returns([new FakeToolbarPlugin("z.plugin", 0, ToolbarGroup.Plugins), new FakeToolbarPlugin("oadm.toolbar.scan", -5, ToolbarGroup.Plugins), new FakeToolbarPlugin("a.first", -1, ToolbarGroup.Add)]);

        var toolbar = new DeviceToolbar(BuiltInToolbarPlugins.All, registry, NullLogger<DeviceToolbar>.Instance);

        Assert.Equal(
            ["a.first", "oadm.toolbar.scan", "oadm.toolbar.range", "oadm.toolbar.manual", "oadm.toolbar.remove", "oadm.toolbar.export", "oadm.toolbar.tasks", "z.plugin", "oadm.toolbar.release-notes"],
            toolbar.Plugins.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Sample_toolbar_plugin_loads_from_a_client_dll()
    {
        var loader = new ClientPluginLoader([TestPluginRoot], NullLogger<ClientPluginLoader>.Instance);

        IToolbarPlugin plugin = Assert.Single(loader.ToolbarPlugins);
        Assert.Equal("oadm.sample.toolbar", plugin.Id);
        Assert.Equal(ToolbarGroup.Plugins, plugin.Group);
    }

    [Fact]
    public async Task Toolbar_controls_follow_selection_and_task_plugins()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(async () =>
        {
            using var f = new DevicesFixture(registry: Registry(new ClientPluginLoader([TestPluginRoot], NullLogger<ClientPluginLoader>.Instance).ToolbarPlugins));
            f.SeedDevices(
                TestSupport.Device("1", "B8A44F631339", "10.0.0.48", "AXIS P3265-V"),
                TestSupport.Device("2", "ACCC8E0A1B2C", "10.0.0.21", "AXIS M3106-L Mk II"));
            var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            var window = new Window { Width = 1200, Height = 200, Content = panel };
            window.Show();
            f.Devices.Toolbar.AttachTo(panel, f.Devices.ToolbarContext);
            Dispatcher.UIThread.RunJobs();

            // Scan Scan-IP-range Add-manually | Remove | (tasks: none yet, separator hidden) | Sample, AXIS OS - Release Notes
            ToolbarButton[] buttons = panel.Children.OfType<ToolbarButton>().ToArray();
            Assert.Equal(["Scan", "Scan IP range", "Add manually", "Remove", "Sample (0)", "AXIS OS - Release Notes"], buttons.Select(b => b.Text ?? "").ToArray());
            Assert.Contains("primary", buttons[0].Classes);
            Assert.Contains("toolbar", buttons[1].Classes);
            Assert.NotNull(buttons[0].Label.Icon);
            ToolbarSeparator[] separators = panel.Children.OfType<ToolbarSeparator>().ToArray();
            Assert.Equal(3, separators.Length);
            Assert.Equal([true, false, true], separators.Select(s => s.IsVisible).ToArray());
            Assert.False(buttons[3].IsEnabled);

            await f.SetPluginsAsync(
                TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "1"),
                TestSupport.Plugin("oadm.identify", "Identify", toolbar: false, dialog: false, "1"));
            Dispatcher.UIThread.RunJobs();
            ToolbarButton restart = panel.GetVisualDescendantsOfType<ToolbarButton>().Single(b => b.Text == "Restart");
            Assert.True(separators[1].IsVisible);
            Assert.False(restart.IsEnabled);

            f.Select("1");
            Assert.True(restart.IsEnabled);
            Assert.True(buttons[3].IsEnabled);
            Assert.Equal("Sample (1)", buttons[4].Text);
            f.Select("1", "2");
            Assert.False(restart.IsEnabled); // Restart cannot run on device 2

            // A new view of the page gets the same controls.
            var other = new StackPanel();
            f.Devices.Toolbar.AttachTo(other, f.Devices.ToolbarContext);
            Assert.Empty(panel.Children);
            Assert.Same(buttons[0], other.Children[0]);
            window.Close();
        }, CancellationToken.None);
    }

    private static IClientPluginRegistry Registry(IReadOnlyList<IToolbarPlugin> toolbar)
    {
        IClientPluginRegistry registry = Substitute.For<IClientPluginRegistry>();
        registry.FindDialog(Arg.Any<string>()).Returns((ITaskPluginDialog?)null);
        registry.ToolbarPlugins.Returns(toolbar);
        return registry;
    }

    private sealed class FakeToolbarPlugin(string id, int order, ToolbarGroup group) : IToolbarPlugin
    {
        public string Id => id;
        public int Order => order;
        public ToolbarGroup Group => group;
        public Control CreateControl(IToolbarContext ctx) => new TextBlock { Text = id };
    }
}

internal static class VisualExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual visual) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(visual).OfType<T>();
}
