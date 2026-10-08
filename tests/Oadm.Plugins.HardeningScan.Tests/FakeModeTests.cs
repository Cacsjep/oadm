using System.Runtime.CompilerServices;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Plugins.HardeningScan.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.HardeningScan.Tests;

/// <summary>The client's fake backend (<c>--fake</c>) speaks the plugin's JSON; the page works against it.</summary>
public sealed class FakeModeTests : IDisposable
{
    private readonly FakeOadmApi _api = new(TimeSpan.FromMilliseconds(5)) { HardeningScanDuration = TimeSpan.FromMilliseconds(120) };

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task The_fake_backend_answers_with_the_plugin_contract()
    {
        Assert.Contains(await _api.ListCorePluginsAsync(CancellationToken.None), p => p.Id == HardeningScanPluginInfo.PluginId && p.IconKey == HardeningScanPluginInfo.IconKey);
        var state = HardeningJson.Deserialize<HardeningState>(await _api.InvokeCorePluginAsync(HardeningScanPluginInfo.PluginId, HardeningMethods.GetState, null, CancellationToken.None));
        Assert.Equal(HardeningCatalog.ColumnIds, state.Columns);
        Assert.NotEmpty(state.Results);
        Assert.All(state.Results, r => Assert.Equal(HardeningCatalog.Columns.Count, r.States.Length));
        Assert.All(state.Results, r => Assert.Equal(ScanLevel.Basic, r.Level));
        Assert.Contains(state.Results, r => r.Status == "Credentials required - the device rejects the stored credentials");

        var id = state.Results[0].DeviceId;
        var detail = HardeningJson.Deserialize<DetailReply>(await _api.InvokeCorePluginAsync(HardeningScanPluginInfo.PluginId, HardeningMethods.GetDetail, HardeningJson.Serialize(new DetailRequest { DeviceIds = [id] }), CancellationToken.None));
        Assert.Equal(15, detail.Devices.Single().Checks.Count);
    }

    [Fact]
    public async Task The_page_scans_against_the_fake_backend()
    {
        var devices = (await _api.ListDevicesAsync(CancellationToken.None)).Select(d => (IDeviceInfo)new DeviceRowViewModel(d)).ToList();
        var ctx = new FakeContext(_api, devices);
        using var vm = new HardeningScanViewModel(ctx, new HardeningClientSettingsStore(Path.Combine(Path.GetTempPath(), "oadm-hardening-tests", Guid.NewGuid().ToString("N"), "client.json")));
        vm.Activate();
        await Wait.UntilAsync(() => vm.CountOf(RowKind.NotScanned) == 0);
        Assert.True(vm.CountOf(RowKind.NotReachable) > 0);
        Assert.True(vm.CountOf(RowKind.Pass) + vm.CountOf(RowKind.Warn) + vm.CountOf(RowKind.Fail) > 0);

        vm.Level = ScanLevel.Extended;
        await vm.ScanAllCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => vm.ProgressText.StartsWith("Scan finished", StringComparison.Ordinal));
        await Task.Delay(50);
        var snmp = vm.Columns.ToList().FindIndex(c => c.Check.Id == HardeningCatalog.Snmp);
        Assert.All(vm.AllRows.Where(r => r.Kind != RowKind.NotReachable), r => Assert.NotEqual(CheckState.NotScanned, r.StateAt(snmp)));
        vm.Deactivate();

        // The client rows expose the device table's DHCP / HTTPS / 802.1X values to plugins (SDK DIM members).
        Assert.Contains(devices, d => d.DhcpEnabled == true);
        Assert.Contains(devices, d => d.Dot1xEnabled == true);
    }

    private sealed class FakeContext(FakeOadmApi api, IReadOnlyList<IDeviceInfo> devices) : ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices { get; } = devices;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            api.InvokeCorePluginAsync(HardeningScanPluginInfo.PluginId, method, payloadJson, ct);

        public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var item in api.WatchCorePluginAsync(HardeningScanPluginInfo.PluginId, ct))
            {
                yield return new PluginEvent(item.Topic, item.PayloadJson);
            }
        }
    }
}
