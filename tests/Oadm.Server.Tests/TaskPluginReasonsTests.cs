using Oadm.Core.Plugins;
using Oadm.Plugins.Restart;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests;

/// <summary>ListTaskPlugins tells per plugin why it cannot run on the other devices, grouped by reason, ids sent once.</summary>
public sealed class TaskPluginReasonsTests
{
    [Fact]
    public async Task ReasonsAreGroupedMostDevicesFirstAndAThrowingOrEmptyReasonIsTheDefaultText()
    {
        await using var host = await TestServerHost.StartAsync();
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(new ReasonPlugin(), new PluginOrigin("test.reasons", "1.0.0", null)));
        var byOctet = new Dictionary<int, string>();
        for (var i = 1; i <= 9; i++)
        {
            byOctet[i] = (await DeviceServiceTests.AddDeviceAsync(host, $"10.9.0.{i}", i)).Id.ToString();
        }

        // Runnable: 1, 2, 7, 8, 9. "Needs the API x": 3, 6. Default text: 4 (throws), 5 (null).
        var compact = Assert.Single((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest { Compact = true })).Plugins, p => p.Id == ReasonPlugin.PluginId);
        Assert.True(compact.RunnableOnAllExcept);
        Assert.Equal(
            [("Needs the API x", 2), (TaskSupportReasons.Default, 2)],
            compact.NotRunnableGroups.Select(g => (g.Reason, g.Count)).ToArray());
        Assert.All(compact.NotRunnableGroups, g => Assert.Empty(g.DeviceIds));
        Assert.Equal(new[] { byOctet[3], byOctet[6] }.Order(), compact.NotRunnableDeviceIds.Take(2).Order());
        Assert.Equal(new[] { byOctet[4], byOctet[5] }.Order(), compact.NotRunnableDeviceIds.Skip(2).Order());

        // List form (former request): the most common reason covers every other device, the rest list their ids.
        var listed = Assert.Single((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins, p => p.Id == ReasonPlugin.PluginId);
        Assert.False(listed.RunnableOnAllExcept);
        Assert.Equal(5, listed.RunnableDeviceIds.Count);
        var other = listed.NotRunnableGroups[0];
        Assert.True(other.OtherDevices);
        Assert.Equal(("Needs the API x", 2), (other.Reason, other.Count));
        Assert.Empty(other.DeviceIds);
        var rest = listed.NotRunnableGroups[1];
        Assert.False(rest.OtherDevices);
        Assert.Equal(new[] { byOctet[4], byOctet[5] }.Order(), rest.DeviceIds.Order());
    }

    [Fact]
    public async Task RestartSaysWhyItCannotRunOnADeviceWithoutWorkingLogin()
    {
        await using var host = await TestServerHost.StartAsync();
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(new RestartTaskPlugin(), new PluginOrigin("oadm.restart", "1.0.0", null)));
        await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.1", 1);
        await DeviceServiceTests.AddDeviceAsync(host, "10.9.0.2", 2, password: null, status: SdkDeviceStatus.CredentialsRequired);

        var restart = Assert.Single((await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest { Compact = true })).Plugins, p => p.Id == RestartTaskPlugin.PluginId);

        var group = Assert.Single(restart.NotRunnableGroups);
        Assert.Equal("OADM cannot log in to the device: use Log in", group.Reason);
        Assert.Equal(1, group.Count);
    }

    private sealed class ReasonPlugin : ITaskPlugin
    {
        public const string PluginId = "test.reasons";

        public string Id => PluginId;

        public string DisplayName => "Reasons";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(IDeviceInfo device) => Octet(device) is 1 or 2 or 7 or 8 or 9;

        public string? NotSupportedReason(IDeviceInfo device) => Octet(device) switch
        {
            4 => throw new InvalidOperationException("broken plugin"),
            5 => "  ",
            _ => "Needs the API x",
        };

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;

        private static int Octet(IDeviceInfo device) => int.Parse(device.Address.Split('.')[3], System.Globalization.CultureInfo.InvariantCulture);
    }
}
