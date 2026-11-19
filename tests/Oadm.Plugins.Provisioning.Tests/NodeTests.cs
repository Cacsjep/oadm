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
    private static async Task<NodeResult> RunAloneAsync(Harness h, IProvisioningNode node, object settings, CameraInfo? camera = null)
    {
        var flow = Harness.Node("n", node.Kind, settings);
        camera ??= Harness.Camera();
        var run = node.RunAsync(new NodeRun(h.Engine, camera, h.Profile(flow), flow), CancellationToken.None);
        await h.RunUntilAsync(() => run.IsCompleted, node.Name + " ended");
        return await run;
    }

    [Fact]
    public async Task Date_and_time_starts_its_task_with_the_payload_and_maps_its_end()
    {
        await using var h = new Harness();

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

        // No device (removed meanwhile): nothing starts.
        var count = h.Ctx.TaskRuns.Count;
        var noDevice = await RunAloneAsync(h, new DateTimeNode(), Harness.Time1, Harness.Camera() with { DeviceId = null });
        Assert.Equal(NodeResult.Error(NodeTexts.NotInOadm), noDevice);
        Assert.Equal(count, h.Ctx.TaskRuns.Count);
    }

    private const string ProvisioningJsonOwner = "Provisioning";

    [Fact]
    public async Task Date_and_time_with_oadms_ntp_server_sets_the_zone_then_oadms_address()
    {
        await using var h = new Harness();
        var settings = new DateTimeSettings("Europe/Vienna", OadmNtp: true);

        Assert.Equal(NodeResult.Success("Time zone Europe/Vienna, OADM's NTP server."), await RunAloneAsync(h, new DateTimeNode(), settings));
        Assert.Equal(["oadm.datetime", "oadm.ntp-server.use"], h.Ctx.TaskRuns.Select(r => r.PluginId));
        Assert.Contains("\"source\":\"Dhcp\"", h.Ctx.TaskRuns[0].PayloadJson, StringComparison.Ordinal);

        h.Outcomes["oadm.ntp-server.use"] = (TaskEndState.Failed, "The OADM NTP server is not running. Enable it on the NTP server page. Nothing was changed.");
        Assert.Equal(
            NodeResult.Error("Time zone Europe/Vienna set. OADM's NTP server was not set: The OADM NTP server is not running. Enable it on the NTP server page."),
            await RunAloneAsync(h, new DateTimeNode(), settings));

        // The zone failing: OADM's NTP server is not tried.
        h.Outcomes["oadm.datetime"] = (TaskEndState.Failed, "The device does not support the time API. Nothing was changed.");
        var before = h.Ctx.TaskRuns.Count;
        Assert.Equal(NodeState.Error, (await RunAloneAsync(h, new DateTimeNode(), settings)).State);
        Assert.Equal(before + 1, h.Ctx.TaskRuns.Count);
    }

    [Fact]
    public void Date_and_time_takes_oadms_ntp_server_or_typed_servers()
    {
        var node = new DateTimeNode();
        Assert.Null(node.Check(Harness.Json(new DateTimeSettings("UTC", OadmNtp: true)), ProfileCheckContext.Empty));
        Assert.Equal("Choose OADM's NTP server or enter NTP servers, not both.", node.Check(Harness.Json(new DateTimeSettings("UTC", ["10.0.0.1"], OadmNtp: true)), ProfileCheckContext.Empty));
        Assert.Equal("OADM's NTP server has no NTS.", node.Check(Harness.Json(new DateTimeSettings("UTC", [], Nts: true, OadmNtp: true)), ProfileCheckContext.Empty));
    }

    [Fact]
    public async Task A_task_node_that_does_not_apply_is_not_needed_without_a_task()
    {
        await using var h = new Harness();

        var result = await RunAloneAsync(h, new NoCardNode(), new DateTimeSettings("UTC"));

        Assert.Equal(NodeState.NotNeeded, result.State);
        Assert.Equal("Not needed: no SD card.", result.Message);
        Assert.Empty(h.Ctx.TaskRuns);
    }

    [Fact]
    public async Task A_task_that_never_ends_is_an_error_after_the_timeout()
    {
        await using var h = new Harness(new ProvisioningOptions { TaskTimeout = TimeSpan.FromMinutes(5) });
        var flow = Harness.Node("n", NodeKind.DateTime, Harness.Time1);
        var camera = Harness.Camera();

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
    public async Task Set_password_sets_it_and_waits_until_oadm_read_the_camera()
    {
        await using var h = new Harness();
        var (cam, device) = h.NewCamera(1);

        var result = await RunAloneAsync(h, new SetPasswordNode(), new SetPasswordSettings(h.AutoAdd.Root), Harness.Camera(deviceId: device!.Id));

        Assert.Equal(NodeResult.Success(SetPasswordNode.PasswordSet), result);
        Assert.Equal(1, cam.PasswordWrites);
        Assert.Equal(DeviceStatus.Ok, h.Device(1).Status);
    }

    [Fact]
    public async Task Set_password_warns_when_oadm_has_not_read_the_camera_within_two_minutes()
    {
        await using var h = new Harness();
        h.AutoAdd.ReadsAfterPassword = false;
        var (_, device) = h.NewCamera(1);

        var result = await RunAloneAsync(h, new SetPasswordNode(), new SetPasswordSettings(h.AutoAdd.Root), Harness.Camera(deviceId: device!.Id));

        Assert.Equal(NodeResult.Warning(SetPasswordNode.PasswordSet + " " + SetPasswordNode.NotReadYet), result);
    }

    [Fact]
    public async Task Set_password_says_why_the_camera_refused_it()
    {
        await using var h = new Harness();
        var (_, device) = h.NewCamera(1, configure: c => c.RefusesPassword = true);

        var result = await RunAloneAsync(h, new SetPasswordNode(), new SetPasswordSettings(h.AutoAdd.Root), Harness.Camera(deviceId: device!.Id));

        Assert.Equal(NodeResult.Error("The device rejected the password: too short."), result);
    }

    /// <summary>A task node that does not apply to the camera (like SD card without a card).</summary>
    private sealed class NoCardNode : TaskNode<DateTimeSettings>
    {
        public override NodeKind Kind => NodeKind.DateTime;

        public override string Name => "SD card";

        public override string StepName => "Set up the SD card";

        public override string TaskPluginId => "oadm.edge-storage";

        public override object DefaultSettings => new DateTimeSettings();

        protected override string? CheckSettings(DateTimeSettings settings, ProfileCheckContext context) => null;

        protected override string Payload(DateTimeSettings settings, NodeRun run) => "{}";

        protected override string Done(DateTimeSettings settings, NodeRun run) => "Formatted.";

        protected override Task<NodeResult?> BeforeAsync(DateTimeSettings settings, NodeRun run, CancellationToken ct) =>
            Task.FromResult<NodeResult?>(NodeResult.NotNeeded("no SD card."));
    }
}
