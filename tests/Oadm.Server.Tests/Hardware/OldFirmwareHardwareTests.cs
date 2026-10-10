using Oadm.Core.Plugins;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Hardware;

/// <summary>
/// A camera with old AXIS OS (no apidiscovery.cgi, no anonymous basicdeviceinfo; verified with an AXIS Q6154-E on 8.40.104):
/// the real server with the bundled plugins adds it through "Add manually" with a typed login, refreshes it and asks every
/// task plugin's read-only dialog query. Read-only for the camera. Runs only with <c>OADM_OLD_CAMERA_URL</c> (e.g.
/// <c>http://10.0.0.60</c> or a port forward), <c>OADM_OLD_CAMERA_USER</c> and <c>OADM_OLD_CAMERA_PASSWORD</c> set.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class OldFirmwareHardwareTests(ITestOutputHelper output)
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("OADM_OLD_CAMERA_URL");

    public sealed class OldCameraFactAttribute : FactAttribute
    {
        public OldCameraFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                Skip = "OADM_OLD_CAMERA_URL is not set.";
            }
        }
    }

    [OldCameraFact]
    public async Task AnOldCameraIsAddedAndGetsItsTasks()
    {
        var root = PluginPaths.Development(AppContext.BaseDirectory);
        Assert.NotNull(root);
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true, pluginRoots: [root]);

        // Add manually with a typed login: the Axis check must pass on the realm (no anonymous basicdeviceinfo).
        var session = await host.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest
        {
            Address = Url,
            UserName = Environment.GetEnvironmentVariable("OADM_OLD_CAMERA_USER"),
            Password = Environment.GetEnvironmentVariable("OADM_OLD_CAMERA_PASSWORD"),
        });
        var found = Assert.Single((await TestHelpers.WatchToEndAsync(host, session.SessionId, TimeSpan.FromSeconds(120))).Values);
        output.WriteLine($"Discovered: {found.Serial} {found.Model} auth {found.AuthState} {found.AuthDetail}");
        Assert.Equal(Proto.AuthState.Authenticated, found.AuthState);

        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session.SessionId, DiscoveredIds = { found.DiscoveredId } });
        var deviceId = Assert.Single(reply.Results).DeviceId;
        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(host, deviceId)).Apis.Count > 0,
            "API list after the first full refresh",
            TimeSpan.FromSeconds(90));

        var device = await TestHelpers.GetDeviceAsync(host, deviceId);
        output.WriteLine($"Device: {device.Model} {device.FirmwareVersion} {device.Soc} status {device.Status}, DHCP {device.DhcpEnabled}, HTTPS {device.HttpsEnabled}");
        output.WriteLine("APIs: " + string.Join(", ", device.Apis.Select(a => $"{a.Id} {a.Version}")));
        Assert.Equal(Proto.DeviceStatus.Ok, device.Status);
        Assert.Contains(device.Apis, a => a.Id == "param-cgi");

        // Which context menu entries the device gets, and why not.
        var plugins = (await host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest())).Plugins;
        foreach (var plugin in plugins.OrderBy(p => p.Group).ThenBy(p => p.DisplayName))
        {
            var runnable = plugin.RunnableDeviceIds.Contains(deviceId);
            var reason = plugin.NotRunnableGroups.FirstOrDefault()?.Reason;
            output.WriteLine($"{plugin.Group} / {plugin.DisplayName}: {(runnable ? "runs" : "greyed: " + reason)}");
        }

        // The read-only dialog queries.
        (string Plugin, string Method)[] queries =
        [
            ("oadm.firmware", "status"),
            ("oadm.users", "listUsers"),
            ("oadm.network", "getNetworkInfo"),
            ("oadm.datetime", "getTimeSettings"),
            ("oadm.acap", "listApplications"),
        ];
        foreach (var (plugin, method) in queries)
        {
            try
            {
                var answer = await host.Tasks.QueryAsync(new Proto.TaskQueryRequest { PluginId = plugin, DeviceId = deviceId, Method = method });
                output.WriteLine($"{plugin} {method}: {Shorten(answer.PayloadJson)}");
            }
            catch (Grpc.Core.RpcException ex)
            {
                output.WriteLine($"{plugin} {method}: {ex.StatusCode} {ex.Status.Detail}");
            }
        }
    }

    private static string Shorten(string text) => text.Length <= 400 ? text : text[..400] + "…";
}
