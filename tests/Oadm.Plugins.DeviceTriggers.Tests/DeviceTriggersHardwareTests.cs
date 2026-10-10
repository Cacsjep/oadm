using System.Net;

using Oadm.Core.Vapix;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceTriggers.Tests;

/// <summary>
/// A real device through the real VAPIX client (session token, WebSocket, pinned certificate). Runs only with
/// <c>OADM_TRIGGER_URL</c> (e.g. https://10.0.0.48), <c>OADM_TRIGGER_USER</c> and <c>OADM_TRIGGER_PASSWORD</c> set.
/// The read test is read-only; the write test switches virtual input 64 on and off (user approval 2026-10-10 for
/// 10.0.0.48) and runs only on request (<c>Category=HardwareWrite</c>).
/// </summary>
public sealed class DeviceTriggersHardwareTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("OADM_TRIGGER_URL");

    public sealed class TriggerFactAttribute : FactAttribute
    {
        public TriggerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                Skip = "OADM_TRIGGER_URL is not set.";
            }
        }
    }

    [TriggerFact]
    [Trait("Category", "Hardware")]
    public async Task A_real_device_reports_its_virtual_inputs_over_the_websocket()
    {
        await using var rig = await StartAsync();
        var device = (await rig.Devices.ListAsync(CancellationToken.None)).Single();

        var read = await rig.CallAsync<ReadStatusReply>(DeviceTriggersMethods.StartRead, new StartReadRequest([device.Id]));
        await Wait.UntilAsync(() => (read = rig.CallAsync<ReadStatusReply>(DeviceTriggersMethods.ReadStatus, new ReadStatusRequest(read.JobId)).Result).Finished, "the event list", 30_000);
        Console.WriteLine($"{read.Events.Count} events: " + string.Join("; ", read.Events.Select(e => e.Name)));
        Assert.Null(read.Devices.Single().Error);
        Assert.Contains(read.Events, e => e.Topic == Fixture.VirtualInput && e.Stateful);

        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Any virtual input", [device.Id], port: null));
        await Wait.UntilAsync(() => rig.Publisher.Lines.Any(l => l.StartsWith("replace trigger", StringComparison.Ordinal)), "the device's report", 30_000);
        Console.WriteLine(string.Join(Environment.NewLine, rig.Publisher.Lines));
        var state = rig.Plugin.Runtime!.StatesOf(trigger).Single();
        Assert.Equal(TriggerConnection.Connected, state.Connection);
    }

    [TriggerFact]
    [Trait("Category", "HardwareWrite")]
    public async Task Switching_virtual_input_64_turns_the_trigger_on_and_off()
    {
        await using var rig = await StartAsync();
        var device = (await rig.Devices.ListAsync(CancellationToken.None)).Single();
        await rig.SaveAsync(Fixture.VirtualInputTrigger("Virtual input 64", [device.Id], port: "64"));
        await Wait.UntilAsync(() => rig.Publisher.Lines.Any(l => l.StartsWith("replace trigger", StringComparison.Ordinal)), "the device's report", 30_000);
        var vapix = await rig.Cameras.CreateAsync(device.Id, CancellationToken.None);

        try
        {
            await SwitchAsync(vapix, "activate");
            await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"set trigger {device.Id} on active=1,port=64"), "on", 15_000);
        }
        finally
        {
            await SwitchAsync(vapix, "deactivate");
        }

        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"set trigger {device.Id} off"), "off", 15_000);
        Console.WriteLine(string.Join(Environment.NewLine, rig.Publisher.Lines));
    }

    private static async Task SwitchAsync(IVapixClient vapix, string action)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/axis-cgi/virtualinput/{action}.cgi?schemaversion=1&port=64");
        using var response = await vapix.SendAsync(request, CancellationToken.None);
        Assert.True(response.IsSuccessStatusCode, $"{action}: HTTP {(int)response.StatusCode}");
    }

    private static async Task<RealRig> StartAsync()
    {
        var uri = new Uri(Url!);
        var vapix = VapixClient.Create(new VapixConnectionOptions
        {
            Address = uri.Authority,
            Scheme = uri.Scheme,
            Credentials = new NetworkCredential(Environment.GetEnvironmentVariable("OADM_TRIGGER_USER"), Environment.GetEnvironmentVariable("OADM_TRIGGER_PASSWORD")),
        });
        var rig = new RealRig(vapix);
        rig.Devices.Add(new TestDevice(uri.Host));
        await rig.StartAsync();
        return rig;
    }

    /// <summary>The plugin with one real device (trust on first use for its certificate).</summary>
    private sealed class RealRig(VapixClient vapix) : IAsyncDisposable
    {
        private readonly Rig _rig = new(new Runtime.DeviceTriggersOptions());

        public FakeDevices Devices => _rig.Devices;

        public RecordingPublisher Publisher => _rig.Publisher;

        public DeviceTriggersPlugin Plugin => _rig.Plugin;

        public Single Cameras { get; } = new(vapix);

        public async Task StartAsync()
        {
            await Plugin.StartAsync(new RealContext(_rig, Cameras), CancellationToken.None);
        }

        public Task<T> CallAsync<T>(string method, object? payload = null) => _rig.CallAsync<T>(method, payload);

        public Task<TriggerDefinition> SaveAsync(TriggerDefinition trigger) => _rig.SaveAsync(trigger);

        public async ValueTask DisposeAsync()
        {
            await _rig.DisposeAsync();
            vapix.Dispose();
        }
    }

    private sealed class Single(IVapixClient client) : IVapixClientFactory
    {
        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult(client);
    }

    private sealed class RealContext(Rig rig, IVapixClientFactory vapix) : Oadm.Sdk.Plugins.ICorePluginContext
    {
        public Oadm.Sdk.Devices.IDeviceRepository Devices => rig.Devices;

        public IVapixClientFactory Vapix => vapix;

        public Oadm.Sdk.Tasks.ITaskRunner Tasks => throw new NotSupportedException();

        public Oadm.Sdk.Plugins.IPluginSettings Settings => rig.Settings;

        public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public Oadm.Sdk.Events.IEventPublisher? EventPublisher => rig.Publisher;
    }
}
