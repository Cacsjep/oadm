using System.Diagnostics;

using Oadm.Plugins.DeviceTriggers.Device;
using Oadm.Plugins.DeviceTriggers.Runtime;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Plugins.DeviceTriggers.Tests;

/// <summary>Order of the resolves, stopping, scale of All devices, refused filters and the first report to the rules.</summary>
public sealed class RobustnessTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string> Port3 = new() { ["port"] = "3" };
    private static readonly Dictionary<string, string> On = new() { ["active"] = "1" };

    [Fact]
    public async Task AnOlderResolveNeverBringsBackADeletedTrigger()
    {
        await using var rig = new Rig(Rig.FastOptions(sweep: TimeSpan.FromHours(1))); // only the resolve reads the device table
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();
        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", []) with { AllDevices = true });
        await Wait.UntilAsync(() => rig.Plugin.Runtime!.ConnectionCount == 1, "connected");

        // A timer round read the list with the trigger and waits for the device table; meanwhile the trigger is deleted.
        var hold = rig.Devices.HoldNextList(out var held);
        await held;
        var delete = rig.Plugin.InvokeAsync(DeviceTriggersMethods.Delete, TriggerJson.Serialize(new IdRequest(trigger.Id)), CancellationToken.None);
        await Wait.UntilAsync(() => rig.Plugin.Triggers.All(t => t.Id != trigger.Id), "the delete stored"); // its round queues behind the held one
        hold.SetResult();
        await delete;

        // The older round finished first; the delete's round comes after it, and nothing brings the trigger back.
        await Wait.UntilAsync(() => rig.Plugin.Runtime!.ConnectionCount == 0, "connection closed");
        await Oadm.Sdk.Testing.Eventually.StaysTrueAsync(
            () => rig.Plugin.Runtime!.ConnectionCount == 0 && rig.Plugin.Runtime.TriggersFor(a.Id).Count == 0,
            "the trigger stays deleted",
            TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task StoppingWaitsForAResolveInFlightAndLeavesNoConnection()
    {
        await using var rig = new Rig(Rig.FastOptions(sweep: TimeSpan.FromHours(1)));
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();
        await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", []) with { AllDevices = true });
        await Wait.UntilAsync(() => rig.Cameras[a.Id].IsConnected, "connected");

        var hold = rig.Devices.HoldNextList(out var held);
        await held;
        var stop = rig.Plugin.StopAsync(CancellationToken.None);
        // Waits for the resolve that holds the runtime (no sign of the wait itself: it must simply not end).
        await Oadm.Sdk.Testing.Eventually.StaysTrueAsync(() => !stop.IsCompleted, "stop waits for the held resolve", TimeSpan.FromMilliseconds(300));
        hold.SetResult();
        await stop;

        Assert.Null(rig.Plugin.Runtime);
        await Wait.UntilAsync(() => !rig.Cameras[a.Id].IsConnected, "the camera sees the connection closed"); // the close reaches it asynchronously
    }

    [Fact]
    public async Task ADisposedRuntimeOpensNoConnections()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var runtime = new TriggerRuntime(rig.Context, Rig.FastOptions());
        await runtime.DisposeAsync();

        runtime.Apply([Fixture.VirtualInputTrigger("Gate open", [a.Id])]);

        Assert.Equal(0, runtime.ConnectionCount);
        await Oadm.Sdk.Testing.Eventually.StaysTrueAsync(() => rig.Cameras[a.Id].Connections == 0, "no connection", TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task OnlyDevicesWhoseTriggersChangedGetANewFilter()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var b = rig.AddDevice("10.0.0.49");
        await rig.StartAsync();
        await rig.SaveAsync(Fixture.VirtualInputTrigger("A", [a.Id]));
        var onB = await rig.SaveAsync(Fixture.VirtualInputTrigger("B", [b.Id]));
        await Wait.UntilAsync(() => rig.Cameras[a.Id].Configures.Count == 1 && rig.Cameras[b.Id].Configures.Count == 1, "both filters");

        await rig.SaveAsync(Fixture.TamperingTrigger("Tampering", [a.Id]));
        await Wait.UntilAsync(() => rig.Cameras[a.Id].Configures.Count == 2, "a new filter on 10.0.0.48");
        await rig.SaveAsync(onB with { Name = "B renamed" });

        // Neither the other trigger nor the rename touches 10.0.0.49 (no sign that nothing happens: watch a while).
        await Oadm.Sdk.Testing.Eventually.StaysTrueAsync(
            () => rig.Cameras[a.Id].Configures.Count == 2 && rig.Cameras[b.Id].Configures.Count == 1,
            "no new filter",
            TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task DevicesOfAllDevicesWithoutTheEventAreNotReadAgainOnEveryChange()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        rig.Cameras[a.Id].Declarations = WithoutVirtualInput();
        await rig.StartAsync();
        var trigger = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", []) with { AllDevices = true });
        await Wait.UntilAsync(() => rig.Plugin.Runtime!.StatesOf(rig.Plugin.Resolved(trigger)).SingleOrDefault()?.Problem == TriggerRuntime.NotOfferedText, "left out");
        var reads = rig.Cameras[a.Id].Reads;

        await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate closed", [], "4") with { AllDevices = true });
        await rig.SaveAsync(rig.Plugin.Triggers.Single(t => t.Name == "Gate open") with { Filters = [new TriggerFilter("port", "5")] });
        await Wait.UntilAsync(() => rig.Cameras[a.Id].Connections == 1, "the session");

        // The regular check every 5 minutes reads it again, not every change.
        await Oadm.Sdk.Testing.Eventually.StaysTrueAsync(
            () => rig.Cameras[a.Id].Reads == reads && rig.Cameras[a.Id].Connections == 1,
            "no new read",
            TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task AtMostTheConnectLimitOfDevicesConnectAtOnce()
    {
        await using var rig = new Rig();
        var devices = Enumerable.Range(1, 8).Select(i => rig.AddDevice($"10.0.0.{i}")).ToList();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Cameras.HoldReads = release.Task;
        var runtime = new TriggerRuntime(rig.Context, Rig.FastOptions(connectParallelism: 3));
        try
        {
            runtime.Apply([Fixture.VirtualInputTrigger("Gate open", devices.Select(d => d.Id))]);
            await Wait.UntilAsync(() => Volatile.Read(ref rig.Cameras.ReadsInFlight) == 3, "three reading");
            await Oadm.Sdk.Testing.Eventually.StaysTrueAsync(() => Volatile.Read(ref rig.Cameras.MaxReadsInFlight) == 3, "no fourth read", TimeSpan.FromMilliseconds(200));

            rig.Cameras.HoldReads = null;
            release.SetResult();
            await Wait.UntilAsync(() => devices.All(d => rig.Cameras[d.Id].IsConnected), "every device connected");
            Assert.Equal(3, Volatile.Read(ref rig.Cameras.MaxReadsInFlight));
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARefusedFilterIsAProblemOfItsTriggerOnly()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        var camera = rig.Cameras[a.Id];
        camera.SetState(Fixture.VirtualInput, Port3, On);
        camera.RefusedTopics[Fixture.Tampering] = true;
        await rig.StartAsync();

        var input = await rig.SaveAsync(Fixture.VirtualInputTrigger("Gate open", [a.Id]));
        var tampering = await rig.SaveAsync(Fixture.TamperingTrigger("Tampering", [a.Id]));

        var refused = DeviceConnection.RefusedText("Could not use supplied event filter");
        await Wait.UntilAsync(() => rig.Plugin.Runtime!.StatesOf(tampering).Single().Problem == refused, "the refused trigger");
        await Wait.UntilAsync(() => rig.Plugin.Runtime!.StatesOf(input).Single() is { Connection: TriggerConnection.Connected, IsOn: true }, "the other trigger works");
        await Wait.UntilAsync(() => rig.Publisher.Full.Contains($"set {DeviceTriggersPluginInfo.LostTypeId(tampering.Id)} {a.Id} on"), "connection lost of the refused trigger");
        Assert.DoesNotContain($"set {DeviceTriggersPluginInfo.LostTypeId(input.Id)} {a.Id} on", rig.Publisher.Full);
        Assert.Equal(1, camera.Connections); // the session stays

        camera.SetState(Fixture.VirtualInput, Port3, new Dictionary<string, string> { ["active"] = "0" });
        await Wait.UntilAsync(() => !rig.Plugin.Runtime!.StatesOf(input).Single().IsOn, "live events of the other trigger");

        // Alone the refused trigger is a problem too, still without breaking the session.
        await rig.Plugin.InvokeAsync(DeviceTriggersMethods.Delete, TriggerJson.Serialize(new IdRequest(input.Id)), CancellationToken.None);
        await Oadm.Sdk.Testing.Eventually.StaysTrueAsync(
            () => rig.Plugin.Runtime!.StatesOf(tampering).Single().Problem == refused && camera.Connections == 1,
            "still refused, same session",
            TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public void AValueWithBothQuotesIsAnXPathConcat()
    {
        Assert.Equal("\"plain\"", EventStreamJson.Quote("plain"));
        Assert.Equal("'say \"hi\"'", EventStreamJson.Quote("say \"hi\""));
        Assert.Equal("concat(\"it's \", '\"', \"x\", '\"', \"\")", EventStreamJson.Quote("it's \"x\""));
        Assert.Equal(
            "boolean(//SimpleItem[@Name=\"name\" and @Value=concat(\"a'b\", '\"', \"c\")])",
            EventStreamJson.ContentFilter([new TriggerFilter("name", "a'b\"c")]));
    }

    [Fact]
    public void AForbiddenStreamLoginIsForbidden()
    {
        var timeout = TimeSpan.FromSeconds(15);
        Assert.Equal(DeviceMessages.Forbidden, DeviceConnection.Describe(new UnauthorizedAccessException(DeviceMessages.Forbidden), timeout));
        Assert.Equal(DeviceMessages.Unauthorized, DeviceConnection.Describe(new UnauthorizedAccessException(DeviceMessages.Unauthorized), timeout));
    }

    [Fact]
    public async Task UnreadableStoredTriggersAreKeptAndNeverOverwrittenByTheNextSave()
    {
        await using var rig = new Rig();
        var device = rig.AddDevice("10.0.0.48");
        const string broken = "[{\"id\": \"not a guid";
        rig.Settings.Values[DeviceTriggersPlugin.SettingsKey] = broken;

        await rig.StartAsync();
        await rig.CallAsync<SaveReply>(DeviceTriggersMethods.Save, new SaveRequest(Fixture.VirtualInputTrigger("Gate open", [device.Id]) with { Id = Guid.Empty }));

        Assert.Equal(broken, rig.Settings.Values[DeviceTriggersPlugin.UnreadableKey]);
        Assert.Contains("Gate open", rig.Settings.Values[DeviceTriggersPlugin.SettingsKey], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnedOffTriggersAndTriggersWithoutDevicesReportAtOnce()
    {
        await using var rig = new Rig();
        rig.AddDevice("10.0.0.48");
        var off = Fixture.VirtualInputTrigger("Turned off", []) with { Enabled = false, AllDevices = true };
        var emptyTag = Fixture.VirtualInputTrigger("Nobody", []) with { Tags = ["Empty"] };
        rig.Settings.Values[DeviceTriggersPlugin.SettingsKey] = TriggerJson.Serialize(new[] { off, emptyTag });

        await rig.StartAsync();

        // The rules see everything off at once instead of waiting 2 minutes for a report that never comes.
        Assert.Contains($"replace {DeviceTriggersPluginInfo.TypeId(off.Id)} []", rig.Publisher.Full);
        Assert.Contains($"replace {DeviceTriggersPluginInfo.LostTypeId(off.Id)} []", rig.Publisher.Full);
        Assert.Contains($"replace {DeviceTriggersPluginInfo.TypeId(emptyTag.Id)} []", rig.Publisher.Full);

        // A copy starts turned off: reported too, once.
        var copy = (await rig.CallAsync<SaveReply>(DeviceTriggersMethods.Duplicate, new IdRequest(emptyTag.Id))).Trigger!;
        Assert.Single(rig.Publisher.Full, l => l == $"replace {DeviceTriggersPluginInfo.TypeId(copy.Id)} []");
        await Oadm.Sdk.Testing.Eventually.StaysTrueAsync( // more resolve rounds report nothing again
            () => rig.Publisher.Full.Count(l => l == $"replace {DeviceTriggersPluginInfo.TypeId(copy.Id)} []") == 1
                && rig.Publisher.Full.Count(l => l == $"replace {DeviceTriggersPluginInfo.TypeId(off.Id)} []") == 1,
            "one report each",
            TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task ATriggerOn10000DevicesReportsOnceEveryDeviceSettled()
    {
        await using var rig = new Rig();
        // All triggers together watch at most DeviceTriggersPluginInfo.MaxWatchedDevices (10,000) devices.
        var ids = Enumerable.Range(0, DeviceTriggersPluginInfo.MaxWatchedDevices).Select(_ => Guid.NewGuid()).ToList(); // not managed: each one is a problem at once
        var runtime = new TriggerRuntime(rig.Context, Rig.FastOptions());
        var trigger = Fixture.VirtualInputTrigger("Gate open", ids);
        var watch = Stopwatch.StartNew();
        try
        {
            runtime.Apply([trigger]);
            await Wait.UntilAsync(() => rig.Publisher.Full.Contains($"replace {DeviceTriggersPluginInfo.TypeId(trigger.Id)} []"), "the full report", 120_000);
            output.WriteLine($"10,000 devices settled and reported in {watch.ElapsedMilliseconds} ms");
            Assert.Single(rig.Publisher.Full, l => l.StartsWith($"replace {DeviceTriggersPluginInfo.TypeId(trigger.Id)} ", StringComparison.Ordinal));
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheEditorReadsDevicesWhoseApiListIsNotReadYet()
    {
        await using var rig = new Rig();
        var fresh = rig.AddDevice("10.0.0.48", apisRead: false);
        var old = rig.AddDevice("10.0.0.20", hasApi: false);
        await rig.StartAsync();

        var started = await rig.CallAsync<ReadStatusReply>(DeviceTriggersMethods.StartRead, new StartReadRequest([fresh.Id, old.Id]));
        ReadStatusReply status = started;
        await Wait.UntilAsync(() =>
        {
            status = rig.CallAsync<ReadStatusReply>(DeviceTriggersMethods.ReadStatus, new ReadStatusRequest(started.JobId)).GetAwaiter().GetResult();
            return status.Finished;
        }, "the read");

        Assert.Null(status.Devices.Single(d => d.DeviceId == fresh.Id).Error);
        Assert.Equal(42, status.Devices.Single(d => d.DeviceId == fresh.Id).Events.Count);
        Assert.Equal(DeviceTriggersPluginInfo.NotSupportedReason, status.Devices.Single(d => d.DeviceId == old.Id).Error);
    }

    private static string WithoutVirtualInput() => Fixture.Recorded
        .Replace("<VirtualInput ", "<VirtualInputGone ", StringComparison.Ordinal)
        .Replace("</VirtualInput>", "</VirtualInputGone>", StringComparison.Ordinal);
}
