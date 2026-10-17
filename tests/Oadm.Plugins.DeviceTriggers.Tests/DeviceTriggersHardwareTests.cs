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

    public sealed class TriggerTheoryAttribute : TheoryAttribute
    {
        public TriggerTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                Skip = "OADM_TRIGGER_URL is not set.";
            }
        }
    }

    /// <summary>
    /// App events (verified on a loan AXIS Q6411-LE, AXIS OS 13.0.88, 2026-10-10, with both apps running): AXIS Image Health
    /// Analytics and AXIS Object Analytics. A device without the app passes with a note.
    /// </summary>
    [TriggerTheory]
    [Trait("Category", "Hardware")]
    [InlineData("tnsaxis:CameraApplicationPlatform/AXISImageHealthAnalytics/Any-Active-Events")]
    [InlineData("tnsaxis:CameraApplicationPlatform/ObjectAnalytics/Device1ScenarioANY")]
    public async Task An_app_reports_its_events_to_a_trigger(string topic)
    {
        var Topic = topic;
        await using var rig = await StartAsync();
        var device = (await rig.Devices.ListAsync(CancellationToken.None)).Single();
        var read = await rig.CallAsync<ReadStatusReply>(DeviceTriggersMethods.StartRead, new StartReadRequest([device.Id]));
        await Wait.UntilAsync(() => (read = rig.CallAsync<ReadStatusReply>(DeviceTriggersMethods.ReadStatus, new ReadStatusRequest(read.JobId)).Result).Finished, "the event list", 30_000);
        if (read.Events.FirstOrDefault(e => e.Topic == Topic) is not { } aiha)
        {
            Console.WriteLine($"{Topic} is not on this device (the app does not run): nothing to check.");
            return;
        }

        var trigger = await rig.SaveAsync(new TriggerDefinition
        {
            Name = "App event",
            Topic = aiha.Topic,
            EventName = aiha.Name,
            Stateful = aiha.Stateful,
            StateItem = aiha.StateItem,
            Values = [.. aiha.Data.Select(d => d.Name)],
            DeviceIds = [device.Id],
        });
        await Wait.UntilAsync(() => rig.Publisher.Lines.Any(l => l.StartsWith("replace trigger", StringComparison.Ordinal)), "the device's report", 30_000);
        Console.WriteLine(aiha.Name + ": " + string.Join(" | ", rig.Publisher.Lines));
        Assert.Equal(TriggerConnection.Connected, rig.Plugin.Runtime!.StatesOf(trigger).Single().Connection);
    }

    /// <summary>
    /// How often an app's one-shot event with a changing value reaches the trigger (AXIS Object Analytics occupancy in area,
    /// which a camera sends about once a second): read-only, prints the arrival times and values for 40 s.
    /// <c>OADM_TRIGGER_TOPIC</c> picks the event, else the first whose name or topic says occupancy.
    /// </summary>
    [TriggerFact]
    [Trait("Category", "Hardware")]
    public async Task A_frequent_app_event_reaches_the_trigger_every_time()
    {
        await using var rig = await StartAsync();
        var device = (await rig.Devices.ListAsync(CancellationToken.None)).Single();
        var read = await rig.CallAsync<ReadStatusReply>(DeviceTriggersMethods.StartRead, new StartReadRequest([device.Id]));
        await Wait.UntilAsync(() => (read = rig.CallAsync<ReadStatusReply>(DeviceTriggersMethods.ReadStatus, new ReadStatusRequest(read.JobId)).Result).Finished, "the event list", 30_000);
        foreach (var e in read.Events.Where(e => e.Topic.Contains("ObjectAnalytics", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("ccupan", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"{e.Name} | {e.Topic} | stateful {e.Stateful} | data {string.Join(",", e.Data.Select(d => d.Name))}");
        }

        var wanted = Environment.GetEnvironmentVariable("OADM_TRIGGER_TOPIC");
        var chosen = read.Events.FirstOrDefault(e => wanted is not null ? e.Topic == wanted
            : e.Name.Contains("ccupan", StringComparison.OrdinalIgnoreCase) || e.Topic.Contains("ccupan", StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            Console.WriteLine("No occupancy event on this device: nothing to check.");
            return;
        }

        Console.WriteLine($"Chosen: {chosen.Name} ({chosen.Topic}), stateful {chosen.Stateful}");
        await rig.SaveAsync(new TriggerDefinition
        {
            Name = "Occupancy",
            Topic = chosen.Topic,
            EventName = chosen.Name,
            Stateful = chosen.Stateful,
            StateItem = chosen.StateItem,
            Values = [.. chosen.Data.Select(d => d.Name)],
            DeviceIds = [device.Id],
        });

        var start = DateTime.UtcNow;
        var seen = 0;
        var arrivals = new List<(double Seconds, string Line)>();
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(40))
        {
            var lines = rig.Publisher.Lines;
            for (; seen < lines.Count; seen++)
            {
                arrivals.Add(((DateTime.UtcNow - start).TotalSeconds, lines[seen]));
            }

            await Task.Delay(20);
        }

        foreach (var (seconds, line) in arrivals)
        {
            Console.WriteLine($"{seconds,6:F2}s  {line}");
        }

        var data = arrivals.Where(a => !a.Line.StartsWith("replace", StringComparison.Ordinal)).Select(a => a.Seconds).ToList();
        var gaps = data.Zip(data.Skip(1), (a, b) => b - a).ToList();
        Console.WriteLine($"{data.Count} events in 40 s; largest gap {(gaps.Count == 0 ? 0 : gaps.Max()):F2} s");
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

    /// <summary>
    /// The "Test alarm" of an Object Analytics scenario (<c>sendAlarmEvent</c>; the scenario is on for about 6 s) turns the
    /// trigger on and off. Needs a fence, motion or tailgating scenario with id <c>OADM_TRIGGER_OA_SCENARIO</c> (default 2);
    /// counting scenarios send nothing on a test alarm. Verified on a loan AXIS Q6411-LE (AXIS OS 13.0.88), 2026-10-10.
    /// </summary>
    [TriggerFact]
    [Trait("Category", "HardwareWrite")]
    public async Task An_Object_Analytics_test_alarm_turns_the_trigger_on_and_off()
    {
        var scenario = Environment.GetEnvironmentVariable("OADM_TRIGGER_OA_SCENARIO") is { Length: > 0 } n ? int.Parse(n, System.Globalization.CultureInfo.InvariantCulture) : 2;
        var topic = $"tnsaxis:CameraApplicationPlatform/ObjectAnalytics/Device1Scenario{scenario}";
        await using var rig = await StartAsync();
        var device = (await rig.Devices.ListAsync(CancellationToken.None)).Single();
        await rig.SaveAsync(new TriggerDefinition
        {
            Name = "Scenario " + scenario,
            Topic = topic,
            EventName = "AXIS Object Analytics › Scenario " + scenario,
            Stateful = true,
            StateItem = "active",
            Values = ["scenarioType", "classTypes", "objectId", "triggerTime", "active"],
            DeviceIds = [device.Id],
        });
        await Wait.UntilAsync(() => rig.Publisher.Lines.Any(l => l.StartsWith("replace trigger", StringComparison.Ordinal)), "the device's report", 30_000);
        var vapix = await rig.Cameras.CreateAsync(device.Id, CancellationToken.None);

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/local/objectanalytics/control.cgi")
        {
            Content = new StringContent($$$"""{"apiVersion":"1.6","context":"oadm","method":"sendAlarmEvent","params":{"scenario":{{{scenario}}}}}""", System.Text.Encoding.UTF8, "application/json"),
        })
        using (var response = await vapix.SendAsync(request, CancellationToken.None))
        {
            Assert.True(response.IsSuccessStatusCode, $"sendAlarmEvent: HTTP {(int)response.StatusCode}");
        }

        await Wait.UntilAsync(() => rig.Publisher.Lines.Any(l => l.StartsWith($"set trigger {device.Id} on", StringComparison.Ordinal)), "on", 20_000);
        await Wait.UntilAsync(() => rig.Publisher.Lines.Contains($"set trigger {device.Id} off"), "off again", 20_000);
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
