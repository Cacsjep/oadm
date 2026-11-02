using System.Globalization;

using Oadm.Core.Devices;
using Oadm.Core.Events;
using Oadm.Core.Tests.Events;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>
/// Field checks at the rule limit (docs/spec/event-rules.md "Field checks"): 500 rules with 5 checks each, 1,000 devices
/// sending 20,000 value changes. The checks are compiled once per rule; the time per event stays small.
/// </summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
[Collection(PerfScale.OneAtATime)]
public sealed class FieldCheckScaleTests(ITestOutputHelper output)
{
    private const int DeviceCount = 1_000;
    private const int ChangesPerDevice = 20;

    [Fact]
    public async Task FiveHundredRulesWithFiveChecksEachKeepUpWithManyValueChanges()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var ids = new List<Guid>(DeviceCount);
        await using (var db = await rig.DbFactory.CreateDbContextAsync())
        {
            for (var i = 0; i < DeviceCount; i++)
            {
                var device = new Device { Serial = $"B8A44F{i:X6}", Address = $"10.0.{i / 256}.{i % 256}", Model = "P3265-V", Status = DeviceStatus.Ok };
                ids.Add(device.Id);
                db.Devices.Add(device);
            }

            await db.SaveChangesAsync();
        }

        await rig.Devices.PublishStoredAsync(ids);
        rig.Publisher.ReplaceStates(EventRulesRig.Reading, []);
        rig.Publisher.ReplaceStates(EventRulesRig.Blur, ids);
        for (var i = 0; i < RuleStore.MaxRules; i++)
        {
            List<RuleCheck> checks =
            [
                new() { Field = "temperature", Op = RuleCheckOperator.Gt, Value = (1000 + i).ToString(CultureInfo.InvariantCulture) },
                new() { Field = "temperature", Op = RuleCheckOperator.Lt, Value = "5000" },
                new() { Field = "door", Op = RuleCheckOperator.Yes },
                new() { Field = "state", Op = RuleCheckOperator.Contains, Value = "act" },
                new() { Field = "state", Op = RuleCheckOperator.Ne, Value = "test" },
            ];
            // 400 rules on the reading itself, 100 with the reading as a further condition on the same device.
            var rule = i < 400 ? EventRulesRig.Rule(EventRulesRig.Reading, rig.OnEventDevice()) : EventRulesRig.Rule(EventRulesRig.Blur, rig.OnEventDevice());
            rule.Name = "Rule " + i.ToString(CultureInfo.InvariantCulture);
            if (i < 400)
            {
                rule.Conditions[0].Checks = checks;
            }
            else
            {
                rule.Conditions.Add(new RuleCondition
                {
                    Kind = RuleConditionKind.Event,
                    EventTypeId = EventRulesRig.Reading,
                    Devices = new DeviceSelector { Mode = DeviceSelectorMode.EventDevice },
                    Checks = checks,
                });
            }

            await rig.Rules.SaveAsync(rule, CancellationToken.None);
        }

        await rig.SettleAsync();
        const int events = DeviceCount * ChangesPerDevice;
        var elapsed = await PerfScale.MeasureAsync(output, $"{events:N0} value changes against 500 rules with 5 checks", TimeSpan.FromSeconds(60), async () =>
        {
            for (var round = 0; round < ChangesPerDevice; round++)
            {
                foreach (var id in ids)
                {
                    // Below every limit: the checks run, no rule turns on.
                    rig.Publisher.SetState(EventRulesRig.Reading, id, true, new Dictionary<string, string>
                    {
                        ["temperature"] = (round * 5).ToString(CultureInfo.InvariantCulture), ["door"] = "true", ["state"] = "active",
                    });
                }
            }

            await rig.SettleAsync();
        });
        output.WriteLine($"{events / elapsed.TotalSeconds:N0} events per second");
        Assert.Empty(rig.OnDevice.Edges);

        // One value above every limit turns all 500 rules on for that device.
        rig.Publisher.SetState(EventRulesRig.Reading, ids[0], true, new Dictionary<string, string> { ["temperature"] = "4999", ["door"] = "1", ["state"] = "Active" });
        await rig.SettleAsync();
        Assert.Equal(RuleStore.MaxRules, rig.OnDevice.Edges.Count(e => e is { Edge: ActionEdge.TurnedOn } && e.Device == ids[0]));
    }
}
