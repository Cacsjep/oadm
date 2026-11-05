using System.Diagnostics;
using System.Text.Json.Nodes;

using Oadm.Core.Events;
using Oadm.Core.Tests.Events;
using Oadm.Sdk.Events;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>The Recipients list at its limits (docs/spec/recipients.md): 200 recipients used by 500 rules.</summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
[Collection(PerfScale.OneAtATime)]
public sealed class RecipientScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TheListAtTheLimitStaysFast()
    {
        await using var rig = await EventRulesRig.StartAsync();
        var ids = new List<Guid>(RecipientStore.MaxRecipients);
        await using (var db = await rig.DbFactory.CreateDbContextAsync())
        {
            for (var i = 0; i < RecipientStore.MaxRecipients; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.Recipients.Add(new RecipientEntity { Id = id, Name = "Recipient " + i, TypeId = "test.target" });
            }

            for (var i = 0; i < RuleStore.MaxRules; i++)
            {
                var rule = EventRulesRig.Rule(EventRulesRig.Pulse, [.. Enumerable.Range(0, RuleDefinition.MaxActions).Select(a => rig.WithoutDevice(new JsonObject { ["recipient"] = ids[(i + a) % ids.Count].ToString() }))]);
                rule.Id = Guid.NewGuid();
                rule.Name = "Rule " + i;
                db.EventRules.Add(new EventRuleEntity { Id = rule.Id, Name = rule.Name, DefinitionJson = rule.ToJson() });
            }

            await db.SaveChangesAsync();
        }

        var clock = Stopwatch.StartNew();
        var list = await rig.Rules.ListRecipientsAsync(CancellationToken.None);
        var elapsed = clock.Elapsed;
        output.WriteLine($"{list.Count} recipients with {list.Sum(r => r.UsedBy.Count)} rule names: {elapsed.TotalMilliseconds:N0} ms");

        Assert.Equal(RecipientStore.MaxRecipients, list.Count);
        Assert.Equal(RuleStore.MaxRules * RuleDefinition.MaxActions, list.Sum(r => r.UsedBy.Count));
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"The list took {elapsed.TotalMilliseconds:N0} ms.");
    }
}
