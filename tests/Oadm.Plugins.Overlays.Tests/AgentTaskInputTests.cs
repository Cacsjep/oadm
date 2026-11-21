using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;
using Oadm.Server.Tasks;

namespace Oadm.Plugins.Overlays.Tests;

/// <summary>
/// The four overlay tasks as AI agents run them (docs/spec/mcp.md "Tasks"): the input schema the host exports, the dialog's
/// rules at their fields, the plan from cached data. No secrets.
/// </summary>
public sealed class AgentTaskInputTests
{
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());
    private readonly OverlaysTaskPlugin _add = new AddOverlaysTask();

    private static Dictionary<string, string> Errors(ITaskPlugin task, string? json) => task.ValidatePayload(json, Caller).ToDictionary(e => e.Field, e => e.Message);

    [Fact]
    public void The_host_exports_a_strict_schema_with_the_limits()
    {
        foreach (var task in new OverlaysTaskPlugin[] { new AddOverlaysTask(), new ReplaceOverlaysTask(), new RemoveMatchingOverlaysTask() })
        {
            var schema = TaskPayloadSchemas.Of(task)!;
            Assert.Equal(["overlays"], schema["properties"]!.AsObject().Select(p => p.Key));
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
            Assert.Equal(OverlayRules.MaxOverlays, schema["properties"]!["overlays"]!["maxItems"]!.GetValue<int>());
            var overlay = schema["properties"]!["overlays"]!["items"]!;
            Assert.Equal(OverlayRules.MaxTextLength, overlay["properties"]!["text"]!["maxLength"]!.GetValue<int>());
            Assert.Contains("semiTransparent", overlay["properties"]!["textColor"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
            Assert.Empty(((ITaskPlugin)task).SecretsOf("""{"overlays":[{"text":"x"}]}"""));
            Assert.Same(OverlaysJson.Options, task.PayloadJsonOptions);
        }

        var removeAll = TaskPayloadSchemas.Of(new RemoveAllOverlaysTask())!;
        Assert.True(removeAll["properties"] is null || removeAll["properties"]!.AsObject().Count == 0, removeAll.ToJsonString()); // no settings
    }

    [Fact]
    public void The_dialogs_rules_answer_at_their_fields()
    {
        Assert.Empty(Errors(_add, """{"overlays":[{"text":"%F %X","position":"bottomLeft"},{"text":"#n","position":"topRight","fontSize":32}]}"""));
        Assert.Equal(OverlayRules.AddOne, Errors(_add, """{"overlays":[]}""")["overlays"]);
        Assert.Equal(OverlayRules.AddOne, Errors(new ReplaceOverlaysTask(), """{"overlays":[]}""")["overlays"]); // Remove all is its own entry
        Assert.Equal(OverlayRules.AddOne, Errors(new RemoveMatchingOverlaysTask(), "{}")["overlays"]);
        Assert.Equal(OverlayRules.EnterText, Errors(_add, """{"overlays":[{"text":" "}]}""")["overlays[0].text"]);
        Assert.Equal(OverlayRules.NoControlCharacters, Errors(_add, """{"overlays":[{"text":"a\tb"}]}""")["overlays[0].text"]);
        Assert.Equal(OverlayRules.TooLong(512), Errors(_add, "{\"overlays\":[{\"text\":\"" + new string('a', 513) + "\"}]}")["overlays[0].text"]);
        Assert.Equal(OverlayRules.SizeRange(1, 999), Errors(_add, """{"overlays":[{"text":"a","fontSize":0}]}""")["overlays[0].fontSize"]);
        Assert.Equal(OverlayRules.CustomNeedsPlace, Errors(_add, """{"overlays":[{"text":"a","position":"custom"}]}""")["overlays[0].position"]);
        Assert.Empty(Errors(_add, """{"overlays":[{"text":"a","position":"custom","customPosition":[-0.25,0.5]}]}"""));
        Assert.Equal(OverlayRules.TooMany, Errors(_add, "{\"overlays\":[" + string.Join(",", Enumerable.Repeat("{\"text\":\"a\"}", 9)) + "]}")["overlays"]);
        Assert.Equal(OverlayRules.Unreadable, Errors(_add, "{")[string.Empty]);
        Assert.Equal(OverlayRules.NoSettings, Errors(_add, null)[string.Empty]);
        Assert.Empty(Errors(new RemoveMatchingOverlaysTask(), """{"overlays":[{"text":"Lobby","fontSize":0}]}""")); // only the text counts
        Assert.Empty(Errors(new RemoveAllOverlaysTask(), "{}"));
        Assert.Empty(Errors(new RemoveAllOverlaysTask(), null));
    }

    [Fact]
    public async Task The_plan_skips_cameras_without_the_api_and_says_what_each_task_removes()
    {
        var ok = Fixtures.Camera();
        var noApi = new FakeDevice { Apis = [new DeviceApi("param-cgi", "1.0")] };
        var down = Fixtures.Camera("10.0.0.49") with { Status = DeviceStatus.Unreachable };
        var plan = await new ReplaceOverlaysTask().PlanAsync("""{"overlays":[{"text":"%F"}]}""", [ok, noApi, down], CancellationToken.None);
        Assert.Null(plan.Devices[0].SkipReason);
        Assert.StartsWith("Needs text overlays", plan.Devices[1].SkipReason, StringComparison.Ordinal);
        Assert.NotNull(plan.Devices[2].SkipReason);
        Assert.Equal([OverlaysTaskPlugin.ReplaceNote], plan.Warnings);

        var all = await new RemoveAllOverlaysTask().PlanAsync("{}", [ok], CancellationToken.None);
        Assert.Equal(OverlaysTaskPlugin.RemoveAllNote, Assert.Single(all.Warnings));
        Assert.Equal([OverlaysTaskPlugin.AddNote], (await _add.PlanAsync("""{"overlays":[{"text":"%F"}]}""", [ok], CancellationToken.None)).Warnings);
    }
}
