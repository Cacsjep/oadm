using System.Net;

using Avalonia.Headless;

using Oadm.Core.Plugins;
using Oadm.Core.Vapix;
using Oadm.Plugins.IoMonitor.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.IoMonitor.Tests;

/// <summary>
/// Read-only: the plugin reads a real device's ports with the real VAPIX client and the page shows them
/// (<c>io-monitor-live.png</c>). Runs only with <c>OADM_IO_URL</c>, <c>OADM_IO_USER</c> and <c>OADM_IO_PASSWORD</c> set.
/// Passed on a loan AXIS A9210 (AXIS OS 12.10.73.2), 2026-10-10.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class IoMonitorHardwareTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("OADM_IO_URL");

    public sealed class IoFactAttribute : FactAttribute
    {
        public IoFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                Skip = "OADM_IO_URL is not set.";
            }
        }
    }

    [IoFact]
    public async Task The_page_shows_the_ports_of_a_real_device()
    {
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(Environment.GetEnvironmentVariable("OADM_IO_USER"), Environment.GetEnvironmentVariable("OADM_IO_PASSWORD")),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var vapix = new VapixClient(new Uri(Url!.TrimEnd('/') + "/"), handler, disposeHandler: false);
        var info = await vapix.GetBasicDeviceInfoAsync(CancellationToken.None);
        var device = new TestDevice(new Uri(Url).Authority, "AXIS " + info.ProdNbr);
        var hub = new PluginEventHub();
        var devices = new FakeDevices { Items = { device } };
        await using var plugin = new IoMonitorPlugin();
        await plugin.StartAsync(new FakeContext(devices, new Factory(vapix), hub.For(IoMonitorPluginInfo.PluginId)), CancellationToken.None);

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var (inputs, outputs, error) = await session.Dispatch(async () =>
        {
            var vm = new IoMonitorViewModel(new PluginPageContext(plugin, hub, devices.Items, Oadm.Sdk.Testing.FakeCallContext.Over(devices, new Factory(vapix))));
            var window = Headless.Host(new IoMonitorView { DataContext = vm });
            window.Show();
            vm.SelectedDevice = IoDeviceOption.Of(device);
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!vm.IsLive && !vm.HasError && DateTime.UtcNow < until)
            {
                Headless.Pump();
                await Task.Delay(50);
            }

            Headless.Pump();
            Headless.Capture(window, "io-monitor-live.png");
            var result = (vm.Inputs.Select(t => $"{t.Name}={t.ActiveText}").ToList(), vm.Outputs.Select(t => $"{t.Name}={t.ActiveText}").ToList(), vm.ErrorText);
            window.Close();
            vm.Dispose();
            return result;
        }, CancellationToken.None);

        Console.WriteLine("Inputs: " + string.Join(", ", inputs));
        Console.WriteLine("Outputs: " + string.Join(", ", outputs));
        Assert.Null(error);
        Assert.NotEmpty(inputs.Concat(outputs));
    }

    private sealed class Factory(IVapixClient client) : IVapixClientFactory
    {
        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult(client);
    }
}
