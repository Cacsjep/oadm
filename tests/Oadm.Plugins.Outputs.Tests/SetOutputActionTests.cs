using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Outputs.Tests;

public sealed class SetOutputActionTests
{
    private static readonly IReadOnlyList<DeviceApi> WithPortManagement = [new("io-port-management", "1.1"), new("param-cgi", "1.0")];

    [Fact]
    public async Task Pulse_sends_one_state_sequence_from_the_normal_state()
    {
        var device = new FakeDevice(WithPortManagement);
        var vapix = new FakeVapix { Ports = Ports(("0", "output", "open", "open")) };

        var log = await RunAsync(device, vapix, new JsonObject { ["port"] = 1, ["mode"] = "pulse", ["seconds"] = 3 }, ActionEdge.Fired);

        var set = vapix.Calls.Single(c => c["method"]!.ToString() == "setStateSequence");
        Assert.Equal("0", set["params"]!["port"]!.ToString());
        Assert.Equal("""[{"state":"closed","time":3000},{"state":"open","time":0}]""", set["params"]!["sequence"]!.ToJsonString());
        Assert.Equal(["Output 1 pulsed for 3 s"], log);
    }

    [Theory]
    [InlineData("active", ActionEdge.TurnedOn, "closed", "Output 2 active")]
    [InlineData("inactive", ActionEdge.Fired, "open", "Output 2 inactive")]
    [InlineData("toggle", ActionEdge.Fired, "closed", "Output 2 active")]
    [InlineData("follow", ActionEdge.TurnedOn, "closed", "Output 2 active")]
    [InlineData("follow", ActionEdge.TurnedOff, "open", "Output 2 inactive")]
    public async Task Modes_set_the_state_that_means_active_or_inactive(string mode, ActionEdge edge, string state, string result)
    {
        var device = new FakeDevice(WithPortManagement);
        var vapix = new FakeVapix { Ports = Ports(("0", "input", "open", "open"), ("1", "output", "open", "open")) };

        var log = await RunAsync(device, vapix, new JsonObject { ["port"] = 2, ["mode"] = mode }, edge);

        var set = vapix.Calls.Single(c => c["method"]!.ToString() == "setPorts");
        Assert.Equal($$"""[{"port":"1","state":"{{state}}"}]""", set["params"]!["ports"]!.ToJsonString());
        Assert.Equal([result], log);
    }

    [Fact]
    public async Task A_normally_closed_output_is_active_when_open()
    {
        var device = new FakeDevice(WithPortManagement);
        var vapix = new FakeVapix { Ports = Ports(("0", "output", "open", "closed")) }; // normally closed, open now = active

        await RunAsync(device, vapix, new JsonObject { ["port"] = 1, ["mode"] = "toggle" }, ActionEdge.Fired);

        Assert.Equal("""[{"port":"0","state":"closed"}]""", vapix.Calls.Single(c => c["method"]!.ToString() == "setPorts")["params"]!["ports"]!.ToJsonString());
    }

    [Fact]
    public async Task An_input_or_a_missing_port_is_refused_without_a_write()
    {
        var device = new FakeDevice(WithPortManagement);
        var vapix = new FakeVapix { Ports = Ports(("0", "input", "open", "open")) };

        var input = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(device, vapix, new JsonObject { ["port"] = 1, ["mode"] = "active" }, ActionEdge.Fired));
        Assert.Equal("Port 1 is an input. Set it to output in the device's I/O settings.", input.Message);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(device, vapix, new JsonObject { ["port"] = 4, ["mode"] = "active" }, ActionEdge.Fired));
        Assert.Equal("The device has no port 4.", missing.Message);
        Assert.DoesNotContain(vapix.Calls, c => c["method"]!.ToString() != "getPorts");
    }

    [Fact]
    public async Task Without_the_port_management_api_port_cgi_is_used()
    {
        var device = new FakeDevice([new("param-cgi", "1.0")]);
        var vapix = new FakeVapix { CheckOutput = "port1=inactive" };

        await RunAsync(device, vapix, new JsonObject { ["port"] = 1, ["mode"] = "pulse", ["seconds"] = 2 }, ActionEdge.Fired);
        await RunAsync(device, vapix, new JsonObject { ["port"] = 1, ["mode"] = "toggle" }, ActionEdge.Fired);
        await RunAsync(device, vapix, new JsonObject { ["port"] = 1, ["mode"] = "inactive" }, ActionEdge.Fired);

        Assert.Equal(
            ["/axis-cgi/io/port.cgi?action=1:/2000%5C", "/axis-cgi/io/port.cgi?checkactive=1", "/axis-cgi/io/port.cgi?action=1:/", "/axis-cgi/io/port.cgi?action=1:%5C"],
            vapix.Gets);
    }

    [Fact]
    public void Follow_needs_a_rule_that_turns_on_and_off_and_runs_on_the_off_edge()
    {
        var action = new SetOutputAction();
        using var follow = JsonDocument.Parse("""{"port":1,"mode":"follow"}""");
        using var pulse = JsonDocument.Parse("""{"port":3,"mode":"pulse","seconds":5}""");

        Assert.Equal("Follow the rule needs a rule that turns on and off.", action.Validate(follow.RootElement, statefulRule: false)["mode"]);
        Assert.Empty(action.Validate(follow.RootElement, statefulRule: true));
        Assert.True(action.RunsOn(ActionEdge.TurnedOff, follow.RootElement));
        Assert.False(action.RunsOn(ActionEdge.TurnedOff, pulse.RootElement));
        Assert.Equal("Output 1 follows the rule", action.Describe(follow.RootElement));
        Assert.Equal("Pulse output 3 for 5 s", action.Describe(pulse.RootElement));
        Assert.Empty(ActionSettings.Check(action.Fields, ActionSettings.Defaults(action.Fields), statefulRule: false));
        Assert.Equal(TimeSpan.FromSeconds(20), action.MaxRunTime(pulse.RootElement)); // port.cgi answers after the pulse
        Assert.Null(action.MaxRunTime(follow.RootElement));
    }

    private static async Task<List<string>> RunAsync(FakeDevice device, FakeVapix vapix, JsonObject settings, ActionEdge edge)
    {
        using var doc = JsonDocument.Parse(settings.ToJsonString());
        var context = new FakeContext(vapix);
        var trigger = new ActionTrigger("t", "Test", device.Id, device.Address, device.Model, true, DateTimeOffset.UtcNow, new Dictionary<string, string>());
        await new SetOutputAction().ExecuteAsync(context, new ActionInvocation(Guid.NewGuid(), "Rule", edge, doc.RootElement.Clone(), device, trigger), CancellationToken.None);
        return context.Lines;
    }

    private static JsonArray Ports(params (string Port, string Direction, string State, string Normal)[] ports) =>
        [.. ports.Select(p => (JsonNode)new JsonObject { ["port"] = p.Port, ["direction"] = p.Direction, ["state"] = p.State, ["normalState"] = p.Normal })];

    private sealed class FakeDevice(IReadOnlyList<DeviceApi> apis) : IDeviceInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Serial => "ACCC8E000001";
        public string Address => "10.0.0.70";
        public string? HostName => null;
        public string? Model => "A9188";
        public string? FirmwareVersion => "12.11.77";
        public DeviceStatus Status => DeviceStatus.Ok;
        public DeviceCategory Category => DeviceCategory.IoModule;
        public bool HasVideo => false;
        public IReadOnlyList<DeviceApi> Apis { get; } = apis;
    }

    private sealed class FakeContext(IVapixClient vapix) : IActionContext
    {
        public List<string> Lines { get; } = [];
        public IVapixClient? Vapix { get; } = vapix;
        public ILogger Logger => NullLogger.Instance;
        public ICorePlugin? Owner => null;
        public IPluginSettings Settings => throw new NotSupportedException();
        public void Log(string message) => Lines.Add(message);
        public string Text(string? text) => text ?? string.Empty;
    }

    private sealed class FakeVapix : IVapixClient
    {
        public JsonArray Ports { get; set; } = [];
        public string CheckOutput { get; set; } = string.Empty;
        public List<JsonNode> Calls { get; } = [];
        public List<string> Gets { get; } = [];
        public Uri BaseAddress { get; } = new("http://10.0.0.70/");

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString();
            if (uri.StartsWith(PortManagementApi.Path, StringComparison.Ordinal))
            {
                var call = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                Calls.Add(call);
                var data = call["method"]!.ToString() == "getPorts" ? new JsonObject { ["items"] = Ports.DeepClone() } : new JsonObject();
                return Answer(new JsonObject { ["apiVersion"] = "1.0", ["data"] = data }.ToJsonString(), "application/json");
            }

            Gets.Add(uri);
            return Answer(uri.Contains("checkactive", StringComparison.Ordinal) ? CheckOutput : "OK", "text/plain");
        }

        private static HttpResponseMessage Answer(string body, string type) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

        public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();
        public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
