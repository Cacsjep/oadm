using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Events;

/// <summary>
/// The event rules under load: slow targets, a full queue, floods of events and many devices never starve the other
/// rules, never leave the engine's devices stale and never fill the logs.
/// </summary>
public sealed class EngineLoadTests(ITestOutputHelper output)
{
    // ---------- action slots ----------
    [Fact]
    public async Task ALongActionNeverDelaysAShortOne()
    {
        var pulse = new HeldAction("test.long", TimeSpan.FromHours(1));
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r =>
        {
            r.Registry.RegisterActionPlugin(pulse, r.ActionOrigin);
            r.RunnerOptions = new ActionRunnerOptions { MaxParallel = 1, MaxParallelLong = 1, ActionTimeout = TimeSpan.FromMinutes(10) };
        });
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Pulse, new RuleAction { ActionId = pulse.Id }));
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, rig.WithoutDevice()));

        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        await Eventually.TrueAsync(() => pulse.Started == 1, "the long action runs");

        // The only slot of the short actions is free: the long one runs in its own lane.
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true);
        await Eventually.TrueAsync(() => rig.NoDevice.Runs.Count == 1, "the short action ran while the long one runs");

        pulse.Release.SetResult();
        await rig.SettleAsync();
        Assert.Equal([ActionEdge.Fired], pulse.Done);
    }

    [Fact]
    public async Task ARunThatWaitedTooLongIsSkippedAndLoggedButATurnOffAlwaysRuns()
    {
        var busy = new HeldAction("test.busy", null);
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r =>
        {
            r.Registry.RegisterActionPlugin(busy, r.ActionOrigin);
            r.RunnerOptions = new ActionRunnerOptions { MaxParallel = 1, MaxWait = TimeSpan.FromMinutes(1), ActionTimeout = TimeSpan.FromMinutes(10) };
        });
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var doors = new[] { await rig.AddDeviceAsync("10.0.0.2"), await rig.AddDeviceAsync("10.0.0.3"), await rig.AddDeviceAsync("10.0.0.4") };
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        rig.Publisher.ReplaceStates(EventRulesRig.Input, []);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Blur, new RuleAction { ActionId = busy.Id }));
        var fired = await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Pulse, rig.OnEventDevice()));
        var onMany = await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.ReadingSent, rig.OnDevices([.. doors.Select(d => d.Id)])));
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.Input, rig.WithoutDevice()));

        // The relay rule turned on while there was room.
        rig.Publisher.SetState(EventRulesRig.Input, camera.Id, true);
        await rig.SettleAsync();
        Assert.Equal([ActionEdge.TurnedOn], rig.NoDevice.Edges.Select(e => e.Edge));

        // A target that does not answer holds the only slot; two fired edges (one on three devices) and the relay's
        // "turns off" wait behind it.
        rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true);
        await Eventually.TrueAsync(() => busy.Started == 1, "the slot is taken");
        rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        rig.Publisher.Fire(EventRulesRig.ReadingSent, camera.Id);
        rig.Publisher.SetState(EventRulesRig.Input, camera.Id, false);
        await rig.Engine.DrainAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Runner.Waiting == 5, "every run waits for the slot");

        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await Eventually.TrueAsync(() => rig.Runner.Waiting == 1, "the fired edges gave up");
        IReadOnlyList<EventLogLine> skipped = [];
        await Eventually.TrueAsync(
            async () => (skipped = [.. (await rig.LogLinesAsync()).Where(l => l.Kind == EventLogKind.Action && l.Result == EventLogResult.Skipped)]).Count == 2,
            "both skipped runs are in the event log");
        var one = Assert.Single(skipped, l => l.RuleId == fired.Id);
        Assert.Equal((camera.Id, ActionRunner.WaitedTooLong), (one.DeviceId!.Value, one.Message));
        var many = Assert.Single(skipped, l => l.RuleId == onMany.Id); // one line for the three devices
        Assert.Equal(ActionRunner.WaitedTooLong + " (3 of 3 devices)", many.Message);
        Assert.Contains(rig.ServerLog.Lines, l => l.Text.Contains(ActionRunner.WaitedTooLong, StringComparison.Ordinal));

        // The "turns off" of the same age still waits, and runs once the slot is free: the relay never stays on.
        busy.Release.SetResult();
        await rig.SettleAsync();
        Assert.Empty(rig.OnDevice.Runs);
        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.NoDevice.Edges.Select(e => e.Edge));
    }

    [Fact]
    public async Task ARuleThatFillsItsShareLeavesTheOtherSlotsToTheOtherRules()
    {
        var mail = new HeldAction("test.mail", null);
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r =>
        {
            r.Registry.RegisterActionPlugin(mail, r.ActionOrigin);
            r.RunnerOptions = new ActionRunnerOptions { MaxParallel = 4, MaxPerRule = 2, MaxWait = TimeSpan.FromMinutes(1), ActionTimeout = TimeSpan.FromMinutes(10) };
        });
        var cameras = new[] { await rig.AddDeviceAsync("10.0.0.1"), await rig.AddDeviceAsync("10.0.0.2"), await rig.AddDeviceAsync("10.0.0.3") };
        var busy = EventRulesRig.Rule(EventRulesRig.Pulse, new RuleAction { ActionId = mail.Id });
        busy.MaxRunsPerMinute = 1000;
        busy = await rig.SaveAsync(busy);
        await rig.SaveAsync(EventRulesRig.Rule(EventRulesRig.ReadingSent, rig.WithoutDevice()));

        // A target that does not answer: the rule holds its two slots, its third run waits in the rule's own queue.
        rig.Publisher.Fire(EventRulesRig.Pulse, cameras[0].Id);
        rig.Publisher.Fire(EventRulesRig.Pulse, cameras[1].Id);
        await Eventually.TrueAsync(() => mail.Started == 2, "the rule holds its share");
        rig.Publisher.Fire(EventRulesRig.Pulse, cameras[2].Id);
        await rig.Engine.DrainAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Runner.Waiting == 1, "its third run waits");
        Assert.Equal(2, mail.Started); // although two of the four slots are free

        // Another rule's run starts at once, ahead of the waiting one.
        rig.Publisher.Fire(EventRulesRig.ReadingSent, cameras[0].Id);
        await Eventually.TrueAsync(() => rig.NoDevice.Runs.Count == 1, "the other rule ran");
        Assert.Equal(1, rig.Runner.Waiting);

        // The waiting run is skipped after the limit like any waiting run.
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await Eventually.TrueAsync(() => rig.Runner.Waiting == 0, "the third run gave up");
        await Eventually.TrueAsync(
            async () => (await rig.LogLinesAsync()).Any(l => l.Kind == EventLogKind.Action && l.Result == EventLogResult.Skipped && l.RuleId == busy.Id && l.Message == ActionRunner.WaitedTooLong),
            "the skipped run is in the event log");

        mail.Release.SetResult();
        await rig.SettleAsync();
        Assert.Equal(2, mail.Done.Count);
    }

    [Fact]
    public async Task ATurnOffOfARuleThatFilledItsShareWaitsForASlotOfItAndRuns()
    {
        var relay = new HeldAction("test.relay", null, supportsTurnOff: true);
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r =>
        {
            r.Registry.RegisterActionPlugin(relay, r.ActionOrigin);
            r.RunnerOptions = new ActionRunnerOptions { MaxParallel = 4, MaxPerRule = 2, MaxWait = TimeSpan.FromMinutes(1), ActionTimeout = TimeSpan.FromMinutes(10) };
        });
        var cameras = new[] { await rig.AddDeviceAsync("10.0.0.1"), await rig.AddDeviceAsync("10.0.0.2"), await rig.AddDeviceAsync("10.0.0.3"), await rig.AddDeviceAsync("10.0.0.4") };
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, new RuleAction { ActionId = relay.Id });
        rule.MaxRunsPerMinute = 1000;
        await rig.SaveAsync(rule);

        // The rule turned on for the third camera while there was room.
        relay.Free[cameras[2].Id] = true;
        rig.Publisher.SetState(EventRulesRig.Blur, cameras[2].Id, true);
        await rig.SettleAsync();
        Assert.Equal([ActionEdge.TurnedOn], relay.Done);

        // Two runs that do not end hold the rule's share; its "turns off" and one more "turns on" wait in its queue.
        rig.Publisher.SetState(EventRulesRig.Blur, cameras[0].Id, true);
        rig.Publisher.SetState(EventRulesRig.Blur, cameras[1].Id, true);
        await Eventually.TrueAsync(() => relay.Started == 2, "the rule holds its share");
        rig.Publisher.SetState(EventRulesRig.Blur, cameras[2].Id, false);
        rig.Publisher.SetState(EventRulesRig.Blur, cameras[3].Id, true);
        await rig.Engine.DrainAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Runner.Waiting == 2, "both wait");

        // After the limit the "turns on" gave up; the "turns off" still waits.
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await Eventually.TrueAsync(() => rig.Runner.Waiting == 1, "the turns on gave up");
        Assert.Equal([ActionEdge.TurnedOn], relay.Done);

        // A slot of the share is free again: the "turns off" runs.
        relay.Release.SetResult();
        await rig.SettleAsync();
        Assert.Equal(2, relay.Started);
        Assert.Single(relay.Done, e => e == ActionEdge.TurnedOff);
    }

    // ---------- the device list of actions on tags ----------
    [Fact]
    public async Task TheDeviceListOfActionsOnTagsIsReadOncePerVersionOfTheDeviceTable()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var cameras = new List<Device>();
        for (var i = 0; i < 20; i++)
        {
            cameras.Add(await rig.AddDeviceAsync("10.0.0." + (i + 1), "Door"));
        }

        var rule = EventRulesRig.Rule(EventRulesRig.Pulse,
            new RuleAction { ActionId = rig.OnDevice.Id, Targets = new DeviceSelector { Mode = DeviceSelectorMode.Devices, Tags = ["Door"] } });
        rule.MaxRunsPerMinute = 1000;
        await rig.SaveAsync(rule);

        // Twenty edges at once (one per camera): one read for all of them.
        foreach (var camera in cameras)
        {
            rig.Publisher.Fire(EventRulesRig.Pulse, camera.Id);
        }

        await rig.SettleAsync();
        Assert.Equal(20 * 20, rig.OnDevice.Runs.Count);
        Assert.Equal(1, rig.Runner.DeviceListReads);

        // A changed device table is read again, once.
        await rig.Devices.UpdateAsync(cameras[0].Id, d => d.Tags = [], CancellationToken.None);
        rig.Publisher.Fire(EventRulesRig.Pulse, cameras[1].Id);
        rig.Publisher.Fire(EventRulesRig.Pulse, cameras[2].Id);
        await rig.SettleAsync();
        Assert.Equal((20 * 20) + (2 * 19), rig.OnDevice.Runs.Count);
        Assert.Equal(2, rig.Runner.DeviceListReads);

        // Unused for a while: the list is dropped, the next edge reads it again.
        rig.Time.Advance(TimeSpan.FromMinutes(2));
        rig.Publisher.Fire(EventRulesRig.Pulse, cameras[1].Id);
        await rig.SettleAsync();
        Assert.Equal(3, rig.Runner.DeviceListReads);
    }

    // ---------- "on for any of these devices" ----------
    [Fact]
    public async Task AnAnyOfConditionFollowsItsLastHolderThroughEventsTagsAndRemovals()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var first = await rig.AddDeviceAsync("10.0.0.2", "Door");
        var second = await rig.AddDeviceAsync("10.0.0.3", "Door");
        var other = await rig.AddDeviceAsync("10.0.0.4");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        rig.Publisher.ReplaceStates(EventRulesRig.Input, [other.Id]); // not a door: never counts
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.WithoutDevice());
        rule.Conditions.Add(new RuleCondition
        {
            Kind = RuleConditionKind.Event,
            EventTypeId = EventRulesRig.Input,
            Devices = new DeviceSelector { Mode = DeviceSelectorMode.Devices, Tags = ["Door"] },
        });
        await rig.SaveAsync(rule);
        Assert.Empty(rig.NoDevice.Edges);

        // Events: on with the first holder, off only with the last one.
        rig.Publisher.SetState(EventRulesRig.Input, first.Id, true);
        rig.Publisher.SetState(EventRulesRig.Input, second.Id, true);
        rig.Publisher.SetState(EventRulesRig.Input, first.Id, false);
        await rig.SettleAsync();
        Assert.Equal([ActionEdge.TurnedOn], rig.NoDevice.Edges.Select(e => e.Edge));
        rig.Publisher.SetState(EventRulesRig.Input, second.Id, false);
        await rig.SettleAsync();
        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.NoDevice.Edges.Select(e => e.Edge));

        // Tags: a holder that leaves the tag counts no longer; the last one turns the rule off, and on again when it is back.
        rig.Publisher.SetState(EventRulesRig.Input, first.Id, true);
        rig.Publisher.SetState(EventRulesRig.Input, second.Id, true);
        await rig.SettleAsync();
        await rig.Devices.UpdateAsync(first.Id, d => d.Tags = [], CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal(3, rig.NoDevice.Edges.Count);
        await rig.Devices.UpdateAsync(second.Id, d => d.Tags = [], CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal(ActionEdge.TurnedOff, rig.NoDevice.Edges[^1].Edge);
        Assert.Equal(4, rig.NoDevice.Edges.Count);
        await rig.Devices.UpdateAsync(second.Id, d => d.Tags = ["Door"], CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal(ActionEdge.TurnedOn, rig.NoDevice.Edges[^1].Edge);
        Assert.Equal(5, rig.NoDevice.Edges.Count);

        // Removal: the last holder gone turns the rule off.
        await rig.Devices.RemoveAsync(second.Id, CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal(ActionEdge.TurnedOff, rig.NoDevice.Edges[^1].Edge);
        Assert.Equal(6, rig.NoDevice.Edges.Count);
    }

    [Fact]
    public async Task ANewCountOfAnAnyDeviceEventNeverTurnsARuleOffAndOn()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var down = await rig.AddDeviceAsync("10.0.0.2");
        var alsoDown = await rig.AddDeviceAsync("10.0.0.3");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.WithoutDevice());
        rule.Conditions.Add(new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = BuiltInEvents.AnyUnreachable });
        await rig.SaveAsync(rule);

        await rig.Devices.UpdateAsync(down.Id, d => d.Status = DeviceStatus.Unreachable, CancellationToken.None);
        await rig.Devices.UpdateAsync(alsoDown.Id, d => d.Status = DeviceStatus.Unreachable, CancellationToken.None); // count 2: nothing new
        await rig.Devices.UpdateAsync(down.Id, d => d.Status = DeviceStatus.Ok, CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal([ActionEdge.TurnedOn], rig.NoDevice.Edges.Select(e => e.Edge));

        await rig.Devices.UpdateAsync(alsoDown.Id, d => d.Status = DeviceStatus.Ok, CancellationToken.None);
        await rig.SettleAsync();
        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.NoDevice.Edges.Select(e => e.Edge));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task TaggingAndRemovingEveryDeviceWithATagRuleOnStaysLinear()
    {
        // The devices live only in the feed here (no table rows): a reload after a full queue would drop them.
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r => r.QueueCapacity = 10 * DeviceLimits.MaxDevices);
        var feed = rig.Devices.Changes;
        var devices = Enumerable.Range(0, DeviceLimits.MaxDevices).Select(i => new Device
        {
            Id = Guid.NewGuid(),
            Serial = i.ToString("X12", System.Globalization.CultureInfo.InvariantCulture),
            Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
            Model = "P3265-V",
            Status = DeviceStatus.Ok,
            Category = DeviceCategory.Camera,
        }).ToList();
        foreach (var device in devices)
        {
            feed.Publish(new DeviceChange(DeviceChangeKind.Added, device.Id, device));
        }

        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [.. devices.Select(d => d.Id)]);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.WithoutDevice());
        rule.Conditions[0].Devices = new DeviceSelector { Mode = DeviceSelectorMode.Devices, Tags = ["Building A"] };
        rule.MaxRunsPerMinute = 1000;
        rule = await rig.SaveAsync(rule);

        // Every device gets the tag, one change each: the rule turns on for it (the limit holds most runs back).
        var watch = Stopwatch.StartNew();
        foreach (var device in devices)
        {
            var tagged = device.Clone();
            tagged.Tags = ["Building A"];
            feed.Publish(new DeviceChange(DeviceChangeKind.Updated, device.Id, tagged));
        }

        await rig.Engine.DrainAsync(CancellationToken.None);
        var tagging = watch.Elapsed;
        Assert.Equal(1000, rig.Engine.Status[rule.Id].ActiveCount);

        // Every device is removed again.
        watch.Restart();
        foreach (var device in devices)
        {
            feed.Publish(new DeviceChange(DeviceChangeKind.Removed, device.Id, null));
        }

        await rig.Engine.DrainAsync(CancellationToken.None);
        var removing = watch.Elapsed;
        Assert.Equal(0, rig.Engine.Status[rule.Id].ActiveCount);
        await rig.SettleAsync();

        output.WriteLine($"{DeviceLimits.MaxDevices:N0} devices with a tag rule on: tagging {tagging.TotalMilliseconds:N0} ms, removing {removing.TotalMilliseconds:N0} ms");
        Assert.True(tagging < TimeSpan.FromSeconds(30), $"Tagging took {tagging.TotalSeconds:N1} s (measured 2 s).");
        Assert.True(removing < TimeSpan.FromSeconds(30), $"Removing took {removing.TotalSeconds:N1} s (measured 1.5 s).");
    }

    // ---------- the engine's devices ----------
    [Fact]
    public async Task AnOlderDeviceChangeThatArrivesLateNeverReplacesTheNewerOne()
    {
        // The feed raises its changes outside its lock: a second change published while the first is still on its way to
        // the handlers reaches the engine first.
        Device? newer = null;
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r =>
        {
            var feed = r.Devices.Changes;
            feed.Changed += (_, change) =>
            {
                if (Interlocked.Exchange(ref newer, null) is { } device)
                {
                    feed.Publish(new DeviceChange(DeviceChangeKind.Updated, device.Id, device));
                }
            };
        });
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [camera.Id]);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.WithoutDevice());
        rule.Conditions[0].Devices = new DeviceSelector { Mode = DeviceSelectorMode.Devices, Tags = ["Building A"] };
        await rig.SaveAsync(rule);

        var tagged = camera.Clone();
        tagged.Tags = ["Building A"];
        newer = tagged;
        rig.Devices.Changes.Publish(new DeviceChange(DeviceChangeKind.Updated, camera.Id, camera.Clone())); // the older one, without the tag
        await rig.SettleAsync();

        Assert.Equal([ActionEdge.TurnedOn], rig.NoDevice.Edges.Select(e => e.Edge)); // the engine kept the tag
    }

    [Fact]
    public async Task DeviceChangesDroppedOnAFullQueueAreReadAgainOnceTheQueueIsEmpty()
    {
        await using var rig = await EventRulesRig.StartAsync(beforeStart: r => r.QueueCapacity = 4);
        var cameras = new List<Device>();
        for (var i = 0; i < 12; i++)
        {
            cameras.Add(await rig.AddDeviceAsync("10.0.0." + (i + 1)));
        }

        var gone = await rig.AddDeviceAsync("10.0.0.99", "Building A");
        await rig.SettleAsync();
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, [.. cameras.Select(c => c.Id), gone.Id]);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.WithoutDevice());
        rule.Conditions[0].Devices = new DeviceSelector { Mode = DeviceSelectorMode.Devices, Tags = ["Building A"] };
        rule.MaxRunsPerMinute = 1000;
        rule = await rig.SaveAsync(rule);
        Assert.Equal(1, rig.Engine.Status[rule.Id].ActiveCount);

        // Hold the engine loop (status changes are raised on it), then change more devices than its queue holds.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hold = new ManualResetEventSlim(false);
        var armed = 1;
        rig.Engine.StatusChanged += (_, _) =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                entered.SetResult();
                hold.Wait(TimeSpan.FromSeconds(30));
            }
        };
        await rig.Devices.RemoveAsync(gone.Id, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        foreach (var camera in cameras)
        {
            await rig.Devices.UpdateAsync(camera.Id, d => d.Tags = ["Building A"], CancellationToken.None);
        }

        hold.Set();
        await rig.SettleAsync();

        // Eight changes never reached the queue; the engine read the devices again and has every tag.
        Assert.Equal(12, rig.Engine.Status[rule.Id].ActiveCount);
    }

    // ---------- the logs ----------
    [Fact]
    public async Task TheWriterPrunesTheEventLogDuringAFlood()
    {
        await using var rig = await EventRulesRig.StartAsync();
        rig.Log.Retention = _ => Task.FromResult<(TimeSpan?, int?)>((null, 1000));
        var most = 0;
        for (var batch = 0; batch < 8; batch++)
        {
            for (var i = 0; i < 1000; i++)
            {
                rig.Log.Write(EventLogKind.Event, EventLogResult.Info, "Line " + ((batch * 1000) + i));
            }

            await rig.Log.FlushAsync(CancellationToken.None);
            most = Math.Max(most, (await rig.Log.ListAsync(1, 0, null, CancellationToken.None)).Total);
        }

        // Never more than twice the limit between two hourly runs, and the newest lines stay.
        Assert.InRange(most, 1000, 2000);
        var (lines, total) = await rig.Log.ListAsync(1, 0, null, CancellationToken.None);
        Assert.InRange(total, 1000, 2000);
        Assert.Equal("Line 7999", lines[0].What);
    }

    [Fact]
    public async Task HeldBackRunsAreOneLineAMinuteWithTheirCount()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, []);
        var rule = EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
        rule.CooldownSeconds = 3600;
        rule = await rig.SaveAsync(rule);

        // A flickering camera: the first "turns on" runs, the next 50 are held back by the dead time.
        for (var i = 0; i < 51; i++)
        {
            rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, true);
            rig.Publisher.SetState(EventRulesRig.Blur, camera.Id, false);
        }

        await rig.SettleAsync();
        Assert.Equal([ActionEdge.TurnedOn, ActionEdge.TurnedOff], rig.OnDevice.Edges.Select(e => e.Edge));
        var held = (await rig.LogLinesAsync()).Where(l => l.Kind == EventLogKind.Rule && l.Result == EventLogResult.Skipped).ToList();
        Assert.Equal("Dead time (3600 s after the last run)", Assert.Single(held).Message); // the first one, with its device
        Assert.Single(rig.EngineLog.Lines, l => l.Text.Contains("held back", StringComparison.Ordinal));
        Assert.Equal(2, (await rig.LogLinesAsync()).Count(l => l.Kind == EventLogKind.Rule && l.Result == EventLogResult.Info)); // on and off of the run

        // A minute later the others are one line with their count.
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        rig.Engine.Tick();
        await rig.SettleAsync();
        held = [.. (await rig.LogLinesAsync()).Where(l => l.Kind == EventLogKind.Rule && l.Result == EventLogResult.Skipped)];
        Assert.Equal(2, held.Count);
        Assert.Equal(RuleEngine.HeldBackText, held[1].What);
        Assert.Equal(rule.Id, held[1].RuleId);
        Assert.Equal("49 more runs: Dead time (3600 s after the last run)", held[1].Message);
        Assert.Equal(2, rig.EngineLog.Lines.Count(l => l.Text.Contains("held back", StringComparison.Ordinal)));
    }

    /// <summary>An action without devices that runs until it is released; <paramref name="maxRunTime"/> is what it asks for.</summary>
    private sealed class HeldAction(string id, TimeSpan? maxRunTime, bool supportsTurnOff = false) : IActionPlugin
    {
        private int _started;

        /// <summary>Runs of events of these devices, and every "turns off" run, end at once.</summary>
        public ConcurrentDictionary<Guid, bool> Free { get; } = new();

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started => Volatile.Read(ref _started);

        public ConcurrentQueue<ActionEdge> Done { get; } = new();

        public string Id { get; } = id;

        public string DisplayName => "Hold " + Id;

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public bool SupportsTurnOff => supportsTurnOff;

        public IReadOnlyList<ActionField> Fields => [];

        public TimeSpan? MaxRunTime(JsonElement settings) => maxRunTime;

        public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
        {
            if (invocation.Edge != ActionEdge.TurnedOff && !(invocation.Trigger.DeviceId is { } device && Free.ContainsKey(device)))
            {
                Interlocked.Increment(ref _started);
                await Release.Task.WaitAsync(ct);
            }

            Done.Enqueue(invocation.Edge);
        }
    }
}
