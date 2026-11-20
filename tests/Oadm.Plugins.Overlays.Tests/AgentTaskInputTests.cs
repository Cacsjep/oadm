using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;
using Oadm.Server.Tasks;

namespace Oadm.Plugins.Overlays.Tests;

/// <summary>
/// Text overlays as AI agents run them (docs/spec/mcp.md "Tasks"): the input schema the host exports, the dialog's rules at
/// their fields, the plan from cached data. No secrets.
/// </summary>
public sealed class AgentTaskInputTests
{
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());
    private readonly OverlaysTaskPlugin _plugin = new();

    private Dictionary<string, string> Errors(string? json) => _plugin.ValidatePayload(json, Caller).ToDictionary(e => e.Field, e => e.Message);

    [Fact]
    public void The_host_exports_a_strict_schema_with_the_limits()
    {
        var schema = TaskPayloadSchemas.Of(_plugin)!;
        Assert.Equal(["mode", "overlays"], schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(OverlayRules.MaxOverlays, schema["properties"]!["overlays"]!["maxItems"]!.GetValue<int>());
        var overlay = schema["properties"]!["overlays"]!["items"]!;
        Assert.Equal(OverlayRules.MaxTextLength, overlay["properties"]!["text"]!["maxLength"]!.GetValue<int>());
        Assert.Contains("semiTransparent", overlay["properties"]!["textColor"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Contains("removeMatching", schema["properties"]!["mode"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Contains("removeAll", schema["properties"]!["mode"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Empty(((ITaskPlugin)_plugin).SecretsOf("""{"mode":"add","overlays":[{"text":"x"}]}"""));
        Assert.Same(OverlaysJson.Options, _plugin.PayloadJsonOptions);
    }

    [Fact]
    public void The_dialogs_rules_answer_at_their_fields()
    {
        Assert.Empty(Errors("""{"mode":"add","overlays":[{"text":"%F %X","position":"bottomLeft"},{"text":"#n","position":"topRight","fontSize":32}]}"""));
        Assert.Empty(Errors("""{"mode":"replace","overlays":[]}""")); // an older form of Remove all, still valid
        Assert.Empty(Errors("""{"mode":"removeAll"}"""));
        Assert.Equal(OverlayRules.RemoveAllTakesNone, Errors("""{"mode":"removeAll","overlays":[{"text":"x"}]}""")["overlays"]);
        Assert.Equal(OverlayRules.AddOne, Errors("""{"mode":"add","overlays":[]}""")["overlays"]);
        Assert.Equal(OverlayRules.AddOne, Errors("""{"mode":"removeMatching"}""")["overlays"]);
        Assert.Equal(OverlayRules.EnterText, Errors("""{"overlays":[{"text":" "}]}""")["overlays[0].text"]);
        Assert.Equal(OverlayRules.NoControlCharacters, Errors("""{"overlays":[{"text":"a\tb"}]}""")["overlays[0].text"]);
        Assert.Equal(OverlayRules.TooLong(512), Errors("{\"overlays\":[{\"text\":\"" + new string('a', 513) + "\"}]}")["overlays[0].text"]);
        Assert.Equal(OverlayRules.SizeRange(1, 999), Errors("""{"overlays":[{"text":"a","fontSize":0}]}""")["overlays[0].fontSize"]);
        Assert.Equal(OverlayRules.CustomNeedsPlace, Errors("""{"overlays":[{"text":"a","position":"custom"}]}""")["overlays[0].position"]);
        Assert.Empty(Errors("""{"overlays":[{"text":"a","position":"custom","customPosition":[-0.25,0.5]}]}"""));
        Assert.Equal(OverlayRules.TooMany, Errors("{\"overlays\":[" + string.Join(",", Enumerable.Repeat("{\"text\":\"a\"}", 9)) + "]}")["overlays"]);
        Assert.Equal(OverlayRules.Unreadable, Errors("{")[string.Empty]);
        Assert.Equal(OverlayRules.NoSettings, Errors(null)[string.Empty]);
        Assert.Empty(Errors("""{"mode":"removeMatching","overlays":[{"text":"Lobby","fontSize":0}]}""")); // only the text counts
    }

    [Fact]
    public async Task The_plan_skips_cameras_without_the_api_and_says_what_replace_removes()
    {
        var ok = Fixtures.Camera();
        var noApi = new FakeDevice { Apis = [new DeviceApi("param-cgi", "1.0")] };
        var down = Fixtures.Camera("10.0.0.49") with { Status = DeviceStatus.Unreachable };
        var plan = await _plugin.PlanAsync("""{"mode":"replace","overlays":[{"text":"%F"}]}""", [ok, noApi, down], CancellationToken.None);
        Assert.Null(plan.Devices[0].SkipReason);
        Assert.StartsWith("Needs text overlays", plan.Devices[1].SkipReason, StringComparison.Ordinal);
        Assert.NotNull(plan.Devices[2].SkipReason);
        Assert.Equal([OverlaysTaskPlugin.ReplaceNote], plan.Warnings);

        var all = await _plugin.PlanAsync("""{"mode":"removeAll"}""", [ok], CancellationToken.None);
        Assert.Equal(OverlaysTaskPlugin.RemoveAllNote, Assert.Single(all.Warnings));
        var empty = await _plugin.PlanAsync("""{"mode":"replace","overlays":[]}""", [ok], CancellationToken.None);
        Assert.Equal(OverlaysTaskPlugin.RemoveAllNote, Assert.Single(empty.Warnings));
    }
}
