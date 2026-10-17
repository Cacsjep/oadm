using System.Runtime.CompilerServices;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Samples.FirmwareOverview.Tests;

/// <summary>The page's context: calls go straight to the server part, events come from the server's fake context (Oadm.Sdk.Testing).</summary>
internal sealed class FakePageContext(FirmwareOverviewPlugin plugin, FakeCoreContext server) : ICorePluginClientContext
{
    public IReadOnlyList<IDeviceInfo> Devices => server.DeviceList;

    /// <summary>The Devices page selection: the page's device choice starts with it.</summary>
    public IReadOnlyList<IDeviceInfo> SelectedDevices { get; set; } = [];

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => plugin.InvokeAsync(method, payloadJson, ct);

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in server.Published.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }
    }
}
