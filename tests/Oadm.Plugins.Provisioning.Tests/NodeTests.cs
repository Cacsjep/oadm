using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>Every node with its task's fake device: Success, Warning, Error and Not needed with their messages.</summary>
public sealed class NodeTests
{
    private static async Task<NodeResult> RunAloneAsync(Harness h, IProvisioningNode node, object settings, CameraRow? camera = null)
    {
        var flow = Harness.Node("n", node.Kind, settings);
        camera ??= new CameraRow { Mac = Harness.Mac(1), Serial = Harness.Serial(1), Address = "192.168.15.101", DeviceId = Guid.NewGuid() };
        var run = node.RunAsync(new NodeRun(h.Engine, camera, h.Profile(flow), flow), CancellationToken.None);
        await h.RunUntilAsync(() => run.IsCompleted, node.Name + " ended");
        return await run;
    }

    [Fact]
    public async Task Date_and_time_starts_its_task_with_the_payload_and_maps_its_end()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());

        var done = await RunAloneAsync(h, new DateTimeNode(), new DateTimeSettings("Europe/Vienna", ["10.0.0.17", "pool.ntp.org"]));
        Assert.Equal(NodeResult.Success("Time zone Europe/Vienna, NTP 10.0.0.17, pool.ntp.org."), done);
        var run = Assert.Single(h.Ctx.TaskRuns);
        Assert.Equal("oadm.datetime", run.PluginId);
        Assert.Equal(ProvisioningJsonOwner, run.Owner);
        using (var json = JsonDocument.Parse(run.PayloadJson!))
        {
            Assert.Equal("Europe/Vienna", json.RootElement.GetProperty("timeZone").GetString());
            Assert.Equal("Ntp", json.RootElement.GetProperty("mode").GetString());
            Assert.Equal("Static", json.RootElement.GetProperty("ntp").GetProperty("source").GetString());
        }

        var fromDhcp = await RunAloneAsync(h, new DateTimeNode(), new DateTimeSettings("UTC", []));
        Assert.Equal("Time zone UTC, NTP servers from DHCP.", fromDhcp.Message);
        Assert.Contains("\"source\":\"Dhcp\"", h.Ctx.TaskRuns[1].PayloadJson, StringComparison.Ordinal);

        h.Outcomes["oadm.datetime"] = (TaskEndState.DoneWithWarnings, "NTS is not available on AXIS OS 10.12.");
        Assert.Equal(NodeResult.Warning("NTS is not available on AXIS OS 10.12."), await RunAloneAsync(h, new DateTimeNode(), Harness.Time1));

        h.Outcomes["oadm.datetime"] = (TaskEndState.Failed, "The device does not support the time API. Nothing was changed.");
        Assert.Equal(NodeResult.Error("The device does not support the time API. Nothing was changed."), await RunAloneAsync(h, new DateTimeNode(), Harness.Time1));

        // No device yet: nothing starts.
        var count = h.Ctx.TaskRuns.Count;
        var noDevice = await RunAloneAsync(h, new DateTimeNode(), Harness.Time1, new CameraRow { Mac = Harness.Mac(1), Serial = Harness.Serial(1), Address = "192.168.15.101" });
        Assert.Equal(NodeResult.Error(NodeTexts.NotInOadm), noDevice);
        Assert.Equal(count, h.Ctx.TaskRuns.Count);
    }

    private const string ProvisioningJsonOwner = "Provisioning";

    [Fact]
    public async Task A_task_node_that_does_not_apply_is_not_needed_without_a_task()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());

        var result = await RunAloneAsync(h, new NoCardNode(), new DateTimeSettings("UTC"));

        Assert.Equal(NodeState.NotNeeded, result.State);
        Assert.Equal("Not needed: no SD card.", result.Message);
        Assert.Empty(h.Ctx.TaskRuns);
    }

    [Fact]
    public async Task A_task_that_never_ends_is_an_error_after_the_timeout()
    {
        await using var h = new Harness(new ProvisioningOptions { TaskTimeout = TimeSpan.FromMinutes(5) });
        await h.StartAsync(h.Profile());
        var flow = Harness.Node("n", NodeKind.DateTime, Harness.Time1);
        var camera = new CameraRow { Mac = Harness.Mac(1), Serial = Harness.Serial(1), Address = "192.168.15.101", DeviceId = Guid.NewGuid() };

        var run = new DateTimeNode().RunAsync(new NodeRun(h.Engine, camera, h.Profile(flow), flow), CancellationToken.None);
        await Eventually.TrueAsync(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(10));
            return run.IsCompleted;
        }, "the wait ended");

        Assert.Equal(NodeResult.Error("The task did not end within 5 minutes."), await run);
    }

    [Theory]
    [InlineData("", "Choose a time zone.")]
    [InlineData("Not a zone!", "Not a zone! is not a time zone.")]
    [InlineData("America/Argentina/Buenos_Aires", null)]
    public void Date_and_time_checks_the_time_zone(string zone, string? error) =>
        Assert.Equal(error, new DateTimeNode().Check(Harness.Json(new DateTimeSettings(zone)), ProfileCheckContext.Empty));

    [Fact]
    public void Date_and_time_checks_the_servers()
    {
        var node = new DateTimeNode();
        Assert.Equal("Enter at most 5 NTP servers.", node.Check(Harness.Json(new DateTimeSettings("UTC", ["a", "b", "c", "d", "e", "f"])), ProfileCheckContext.Empty));
        Assert.Equal("bad_host is not a host name or IP address.", node.Check(Harness.Json(new DateTimeSettings("UTC", ["bad_host"])), ProfileCheckContext.Empty));
        Assert.Equal("Enter the NTS servers.", node.Check(Harness.Json(new DateTimeSettings("UTC", [], Nts: true)), ProfileCheckContext.Empty));
    }

    [Fact]
    public void Set_password_needs_a_credential_of_root_that_exists()
    {
        var node = new SetPasswordNode();
        var root = Guid.NewGuid();
        var op = Guid.NewGuid();
        var context = new ProfileCheckContext(new Dictionary<Guid, string> { [root] = "root", [op] = "operator" }, null);

        Assert.Equal(SetPasswordNode.ChoosePassword, node.Check(Harness.Json(new SetPasswordSettings()), context));
        Assert.Null(node.Check(Harness.Json(new SetPasswordSettings(root)), context));
        Assert.Equal("Choose a password of the user root.", node.Check(Harness.Json(new SetPasswordSettings(op)), context));
        Assert.Equal(DeviceFirstSetupTexts.CredentialGone, node.Check(Harness.Json(new SetPasswordSettings(Guid.NewGuid())), context));
        Assert.True(node.AlwaysStops);
    }

    [Fact]
    public async Task Add_to_oadm_says_why_a_camera_was_not_added()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        h.AutoAdd.Cam(Harness.Serial(1)); // still without a password
        var camera = new CameraRow { Mac = Harness.Mac(1), Serial = Harness.Serial(1), Address = "192.168.15.101" };

        var result = await RunAloneAsync(h, new AddToOadmNode(), new AddToOadmSettings(["Lobby"]), camera);

        Assert.Equal(NodeResult.Error("The camera still has no password."), result);
        var device = h.Devices.BySerial(Harness.Serial(1));
        Assert.Equal(["Lobby"], device!.Tags);
    }

    [Fact]
    public async Task Add_to_oadm_waits_until_oadm_read_the_camera_and_warns_when_it_did_not()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        h.AutoAdd.Cam(Harness.Serial(1), hasPassword: true);
        h.Devices.Add(new FakeDevice { Serial = Harness.Serial(1), Address = "192.168.15.101", Status = DeviceStatus.Unknown }); // added, not read yet
        var camera = new CameraRow { Mac = Harness.Mac(1), Serial = Harness.Serial(1), Address = "192.168.15.101" };

        var result = await RunAloneAsync(h, new AddToOadmNode(), new AddToOadmSettings(), camera);

        Assert.Equal(NodeState.Warning, result.State);
        Assert.Equal("OADM manages this camera already. OADM has not read the camera yet; the next nodes may fail.", result.Message);
    }

    [Theory]
    [InlineData(new[] { "Lobby", "lobby" }, "The tag lobby is there twice.")]
    [InlineData(new[] { " " }, "Enter a name for every tag.")]
    [InlineData(new[] { "Lobby" }, null)]
    public void Add_to_oadm_checks_the_tags(string[] tags, string? error) =>
        Assert.Equal(error, new AddToOadmNode().Check(Harness.Json(new AddToOadmSettings(tags)), ProfileCheckContext.Empty));

    /// <summary>A task node that does not apply to the camera (like SD card without a card).</summary>
    private sealed class NoCardNode : TaskNode<DateTimeSettings>
    {
        public override NodeKind Kind => NodeKind.DateTime;

        public override string Name => "SD card";

        public override string TaskPluginId => "oadm.edge-storage";

        public override object DefaultSettings => new DateTimeSettings();

        protected override string? CheckSettings(DateTimeSettings settings, ProfileCheckContext context) => null;

        protected override string Payload(DateTimeSettings settings, NodeRun run) => "{}";

        protected override string Done(DateTimeSettings settings, NodeRun run) => "Formatted.";

        protected override Task<NodeResult?> BeforeAsync(DateTimeSettings settings, NodeRun run, CancellationToken ct) =>
            Task.FromResult<NodeResult?>(NodeResult.NotNeeded("no SD card."));
    }
}
