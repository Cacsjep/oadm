using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Oadm.Plugins.DeviceConfig;
using Oadm.Sdk.Certificates;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Plugins.VapixCommander;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>The nodes 3 to 13 of the spec with fake tasks and cameras: Success, Warning, Error and Not needed with their messages, the payloads and the checks.</summary>
public sealed class StageTwoNodeTests
{
    private static FakeDevice Device(Harness h, string model = "P3265-LVE", string firmware = "11.11.160", int i = 1)
    {
        var device = new FakeDevice { Serial = Harness.Serial(i), Address = $"192.168.15.{100 + i}", Model = model, FirmwareVersion = firmware, Status = DeviceStatus.Ok };
        h.Devices.Add(device);
        return device;
    }

    private static CameraInfo Camera(FakeDevice device, int i = 1) => new(device.Serial, device.Address, device.Model, device.Id);

    private static async Task<NodeResult> RunAsync(Harness h, IProvisioningNode node, object settings, CameraInfo camera)
    {
        var flow = Harness.Node("n", node.Kind, settings);
        var run = node.RunAsync(new NodeRun(h.Engine, camera, h.Profile(flow), flow), CancellationToken.None);
        await h.RunUntilAsync(() => run.IsCompleted, node.Name + " ended");
        return await run;
    }

    private static async Task<Harness> StartedAsync(bool withStarter = true)
    {
        var h = new Harness();
        if (withStarter)
        {
            h.StarterTasks = h.Ctx;
        }

        await h.StartAsync(h.Profile()); // the administrator turns provisioning on: their tasks for Upgrade AXIS OS online
        return h;
    }

    private static JsonElement Payload(FakeTaskRun run) => JsonDocument.Parse(run.PayloadJson!).RootElement;

    private static ProfileCheckContext Kept(Harness h) => ProfileCheckContext.Empty with { Files = h.Vault.Files, Secrets = h.Vault.SecretIds };

    // ---- Upgrade AXIS OS online ----

    [Fact]
    public async Task Upgrade_online_asks_for_the_latest_version_as_the_user_who_started_and_maps_the_end()
    {
        await using var h = await StartedAsync();
        var camera = Camera(Device(h));
        var node = new UpgradeOnlineNode();

        Assert.Equal(NodeResult.Success("AXIS OS from axis.com installed."), await RunAsync(h, node, new UpgradeOnlineSettings(OsTarget.LatestLts), camera));
        var run = Assert.Single(h.Ctx.TaskRuns);
        Assert.Equal("oadm.firmware", run.PluginId);
        Assert.Equal("latestLts", Payload(run).GetProperty("latest").GetProperty("target").GetString());

        h.Outcomes["oadm.firmware"] = (TaskEndState.DoneWithWarnings, "Already up to date: the device runs 12.11.147. Nothing was installed.");
        Assert.Equal(NodeResult.Success("Already 12.11.147."), await RunAsync(h, node, new UpgradeOnlineSettings(), camera));

        h.Outcomes["oadm.firmware"] = (TaskEndState.Failed, "Sign in to MyAxis to download from axis.com. Nothing was changed.");
        Assert.Equal(NodeResult.Error("Sign in to MyAxis."), await RunAsync(h, node, new UpgradeOnlineSettings(), camera));

        h.Outcomes["oadm.firmware"] = (TaskEndState.Failed, AxisDownloadTexts.Expired);
        Assert.Equal(NodeResult.Error("Sign in to MyAxis."), await RunAsync(h, node, new UpgradeOnlineSettings(), camera));

        h.Outcomes["oadm.firmware"] = (TaskEndState.Failed, "axis.com could not be reached. Nothing was changed.");
        Assert.Equal(NodeResult.Error("axis.com could not be reached. Nothing was changed."), await RunAsync(h, node, new UpgradeOnlineSettings(), camera));
    }

    [Fact]
    public async Task Upgrade_online_still_runs_as_the_starter_after_off_until_the_next_on_replaces_them()
    {
        await using var h = await StartedAsync();
        var camera = Camera(Device(h));

        // Off: queued and running tasks run to their end, Run again still works; they need the starter's sign-in.
        await h.Engine.TurnOffAsync(CancellationToken.None);
        Assert.Equal(NodeResult.Success("AXIS OS from axis.com installed."), await RunAsync(h, new UpgradeOnlineNode(), new UpgradeOnlineSettings(OsTarget.LatestLts), camera));
        Assert.Equal("oadm.firmware", Assert.Single(h.Ctx.TaskRuns).PluginId);

        var next = new FakeCoreContext();
        await h.Engine.TurnOnAsync("admin2", next, CancellationToken.None);
        Assert.Same(next, h.Engine.StarterTasks);
    }

    [Fact]
    public async Task Upgrade_online_skips_a_camera_that_is_not_older_and_needs_the_starter_after_a_restart()
    {
        await using var h = await StartedAsync(withStarter: false);
        var camera = Camera(Device(h, firmware: "12.6.95"));
        var node = new UpgradeOnlineNode();

        Assert.Equal(NodeResult.Success("Already 12.6.95."), await RunAsync(h, node, new UpgradeOnlineSettings(OnlyIfOlderThan: "12.6.95"), camera));
        Assert.Equal(NodeResult.Error(NodeTexts.StartAgain), await RunAsync(h, node, new UpgradeOnlineSettings(OnlyIfOlderThan: "12.11"), camera));
        Assert.Empty(h.Ctx.TaskRuns);

        Assert.Equal("Enter a version like 12.6.95.", node.Check(Harness.Json(new UpgradeOnlineSettings(OnlyIfOlderThan: "twelve")), ProfileCheckContext.Empty));
        Assert.Null(node.Check(Harness.Json(new UpgradeOnlineSettings(OsTarget.LatestActive, "12.6")), ProfileCheckContext.Empty));
    }

    // ---- Upgrade AXIS OS from files ----

    [Fact]
    public async Task Upgrade_from_files_hands_the_kept_files_to_the_task_and_says_what_was_installed()
    {
        await using var h = await StartedAsync();
        var camera = Camera(Device(h));
        var p3265 = await h.FileAsync("P3265-LVE_12_6_95.bin", [1, 2, 3]);
        var m3086 = await h.FileAsync("M3086-V_11_11_160.bin", [4, 5]);
        var settings = new UpgradeFilesSettings([new OsFile(p3265.Id, p3265.Name), new OsFile(m3086.Id, m3086.Name)]);
        var node = new UpgradeFilesNode();
        Assert.Null(node.Check(Harness.Json(settings), Kept(h)));

        Assert.Equal(NodeResult.Success("AXIS OS 12.6.95 installed."), await RunAsync(h, node, settings, camera));
        var payload = Payload(Assert.Single(h.Ctx.TaskRuns));
        Assert.False(payload.GetProperty("allowDowngrade").GetBoolean()); // never a downgrade
        var files = payload.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(["P3265-LVE_12_6_95.bin", "M3086-V_11_11_160.bin"], files.Select(f => f.GetProperty("fileName").GetString()));
        var upload = await h.Ctx.FakeUploads.FindAsync(files[0].GetProperty("fileId").GetString()!, CancellationToken.None);
        Assert.Equal(p3265.Sha256, upload!.Sha256);

        // The server deleted the upload after a day: the file is added again.
        h.Ctx.FakeUploads.Remove(upload.Id);
        h.Outcomes["oadm.firmware"] = (TaskEndState.DoneWithWarnings, "No file for this model. Nothing was installed.");
        Assert.Equal(NodeResult.Warning("No file for this model."), await RunAsync(h, node, settings, camera));
        Assert.NotEqual(upload.Id, Payload(h.Ctx.TaskRuns[1]).GetProperty("files")[0].GetProperty("fileId").GetString());

        // A file older than the camera's AXIS OS: skipped, the camera keeps its version.
        h.Outcomes["oadm.firmware"] = (TaskEndState.Failed, "Downgrade 12.11.147 → 12.6.95 is not allowed. Turn on \"Allow downgrade\". Nothing was changed.");
        Assert.Equal(NodeResult.Success("Already 12.11.147."), await RunAsync(h, node, settings, camera));

        h.Outcomes["oadm.firmware"] = (TaskEndState.Failed, "The file is for Q6135-LE, the device is P3265-LVE. Nothing was changed.");
        Assert.Equal(NodeState.Error, (await RunAsync(h, node, settings, camera)).State);
    }

    [Fact]
    public async Task Upgrade_from_files_without_its_file_starts_nothing_and_checks_the_files()
    {
        await using var h = await StartedAsync();
        var camera = Camera(Device(h));
        var node = new UpgradeFilesNode();
        var gone = new UpgradeFilesSettings([new OsFile(Guid.NewGuid(), "P3265-LVE_12_6_95.bin")]);

        Assert.Equal(NodeResult.Error(NodeVault.FileGone), await RunAsync(h, node, gone, camera));
        Assert.Empty(h.Ctx.TaskRuns);

        Assert.Equal("P3265-LVE_12_6_95.bin is no longer on the server. Choose it again.", node.Check(Harness.Json(gone), Kept(h)));
        Assert.Equal("Choose the AXIS OS files.", node.Check(Harness.Json(new UpgradeFilesSettings([])), ProfileCheckContext.Empty));
        var a = await h.FileAsync("readme.txt", [1]);
        Assert.Equal("readme.txt is not an AXIS OS file (e.g. P3265-LVE_12_6_95.bin).", node.Check(Harness.Json(new UpgradeFilesSettings([new OsFile(a.Id)])), Kept(h)));
        var b = await h.FileAsync("P3265-LVE_12_6_95.bin", [1]);
        var c = await h.FileAsync("P3265-LVE_12_11_77.bin", [2]);
        Assert.Equal("Two files are for P3265-LVE.", node.Check(Harness.Json(new UpgradeFilesSettings([new OsFile(b.Id), new OsFile(c.Id)])), Kept(h)));
    }

    // ---- Network: DNS servers and search domain (the address: NetworkNodeTests) ----

    [Fact]
    public async Task Network_keeps_the_address_writes_the_dns_servers_and_never_the_host_name()
    {
        await using var h = await StartedAsync();
        var first = Camera(Device(h, i: 1), 1);
        var settings = new NetworkNodeSettings(DnsServers: ["10.0.0.1"], SearchDomain: "site.local");
        var node = new NetworkNode();

        Assert.Equal(NodeResult.Success("DNS 10.0.0.1, search domain site.local."), await RunAsync(h, node, settings, first));
        Assert.Equal(NodeResult.Success("DNS 10.0.0.1."), await RunAsync(h, node, new NetworkNodeSettings(DnsServers: ["10.0.0.1"]), first));

        Assert.All(h.Ctx.TaskRuns, r => Assert.Equal("oadm.network", r.PluginId)); // Keep: never an address
        var payload = Payload(h.Ctx.TaskRuns[0]);
        Assert.False(payload.TryGetProperty("hostName", out _));
        Assert.False(payload.TryGetProperty("ipv4", out _));
        Assert.False(payload.TryGetProperty("devices", out _));
        Assert.False(payload.GetProperty("dns").GetProperty("useDhcp").GetBoolean());
        Assert.Equal("10.0.0.1", payload.GetProperty("dns").GetProperty("servers")[0].GetString());
        Assert.Equal("site.local", payload.GetProperty("dns").GetProperty("searchDomains")[0].GetString());
        Assert.True(Payload(h.Ctx.TaskRuns[1]).GetProperty("dns").GetProperty("keepDomains").GetBoolean());

        h.Outcomes["oadm.network"] = (TaskEndState.Failed, "DNS: enter at least one DNS server or use DNS from DHCP. Nothing was changed.");
        Assert.Equal(NodeResult.Error("DNS: enter at least one DNS server or use DNS from DHCP. Nothing was changed."), await RunAsync(h, node, settings, first));
    }

    [Theory]
    [InlineData(null, null, NetworkNode.SetOnePart)]
    [InlineData(null, "site.local", NetworkNode.DnsForDomain)]
    [InlineData("10.0.0.300", null, "10.0.0.300 is not an IPv4 address.")]
    [InlineData("10.0.0.1", "site_local", "site_local is not a domain name.")]
    [InlineData("10.0.0.1", "site.local", null)]
    [InlineData("10.0.0.1", null, null)]
    public void Network_with_keep_needs_dns_servers_or_a_search_domain(string? dns, string? domain, string? error) =>
        Assert.Equal(error, new NetworkNode().Check(Harness.Json(new NetworkNodeSettings(DnsServers: dns is null ? [] : [dns], SearchDomain: domain)), ProfileCheckContext.Empty));

    // ---- Configuration ----

    /// <summary>The configuration file ADM wrote from an AXIS P3265-V: 4 parameters, 1 action rule with its own schedule, 4 built-in schedules, 3 stream profiles.</summary>
    private static byte[] AdmFile => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "adm-p3265v-12.11.77.cfg"));

    /// <summary>The ADM file with values Configure devices never writes on every camera: an address, a name, a connection setting, an empty password.</summary>
    private static byte[] AdmFileWithIdentity
    {
        get
        {
            var file = AdmConfigFile.Read(Encoding.UTF8.GetString(AdmFile));
            return AdmConfigFile.Write(file with
            {
                Parameters =
                [
                    .. file.Parameters,
                    new NameValue("Network.IPAddress", "10.0.0.48"),
                    new NameValue("Network.HostName", "lobby-1"),
                    new NameValue("HTTPS.Enabled", "yes"),
                    new NameValue("SNMP.V2.Community", ""),
                    new NameValue("Image.I0.Appearance.Compression", "30"),
                ],
            });
        }
    }

    [Fact]
    public async Task Configuration_runs_Configure_devices_with_the_settings_of_the_kept_file()
    {
        await using var h = await StartedAsync();
        var camera = Camera(Device(h));
        var file = await h.FileAsync("lobby.cfg", AdmFileWithIdentity);
        var node = new ConfigurationNode();

        Assert.Equal(NodeResult.Success("Parameters, action rules and stream profiles applied."), await RunAsync(h, node, new ConfigurationSettings(file.Id), camera));
        var run = Assert.Single(h.Ctx.TaskRuns);
        Assert.Equal(ConfigureIds.Configure, run.PluginId);
        var payload = ConfigurePayload.Parse(run.PayloadJson);
        Assert.Equal(
            ["ImageSource.I0.CaptureFrequency", "ImageSource.I0.Sensor.CaptureMode", "System.CaptureFrequencySet", "System.CaptureModeSet", "Image.I0.Appearance.Compression"],
            payload.Parameters.Select(p => p.Name));
        Assert.Equal("60Hz", payload.Parameters[0].Value);
        Assert.Equal(["re"], payload.Rules.Select(r => r.Name));
        Assert.Equal(["com.axis.schedules.genid.id-0"], payload.Schedules.Select(s => s.Id)); // the rule's own schedule, never the built-in ones
        Assert.Equal(["View Area 1_ACS_Pro_High", "View Area 1_ACS_Pro_Low", "View Area 1_ACS_Pro_Medium"], payload.StreamProfiles.Select(p => p.Name));
        Assert.Equal("lobby.cfg", payload.Source);
        Assert.Null(payload.SourceDeviceId);
        Assert.DoesNotContain(payload.Parameters, p => DangerousParameters.IsDangerous(p.Name));

        // The chosen areas only.
        lock (h.Ctx.TaskRuns)
        {
            h.Ctx.TaskRuns.Clear();
        }
        Assert.Equal(NodeResult.Success("Stream profiles applied."),
            await RunAsync(h, node, new ConfigurationSettings(file.Id, Parameters: false, ActionRules: false, StreamProfiles: true), camera));
        payload = ConfigurePayload.Parse(Assert.Single(h.Ctx.TaskRuns).PayloadJson);
        Assert.Empty(payload.Parameters);
        Assert.Empty(payload.Rules);
        Assert.Empty(payload.Schedules);
        Assert.Equal(3, payload.StreamProfiles.Count);

        // The task's end: parameters this camera does not have are skipped with a warning.
        h.Outcomes[ConfigureIds.Configure] = (TaskEndState.DoneWithWarnings, "Set parameters: 1 not on this device: System.CaptureModeSet");
        Assert.Equal(NodeResult.Warning("Set parameters: 1 not on this device: System.CaptureModeSet."), await RunAsync(h, node, new ConfigurationSettings(file.Id), camera));
        h.Outcomes[ConfigureIds.Configure] = (TaskEndState.Failed, "Needs the parameter API. Nothing was changed.");
        Assert.Equal(NodeResult.Error("Needs the parameter API. Nothing was changed."), await RunAsync(h, node, new ConfigurationSettings(file.Id), camera));
    }

    [Fact]
    public async Task Configuration_starts_no_task_for_a_file_that_is_no_configuration_file_or_applies_nothing()
    {
        await using var h = await StartedAsync();
        var camera = Camera(Device(h));
        var node = new ConfigurationNode();

        var json = await h.FileAsync("lobby.json", Encoding.UTF8.GetBytes("""{"data":{}}"""));
        Assert.Equal(NodeResult.Error(ConfigurationNode.WrongFile), await RunAsync(h, node, new ConfigurationSettings(json.Id), camera));

        var broken = await h.FileAsync("broken.cfg", Encoding.UTF8.GetBytes("<Configuration />"));
        Assert.Equal(NodeResult.Error(AdmConfigFile.NotAnAdmFile), await RunAsync(h, node, new ConfigurationSettings(broken.Id), camera));

        var empty = await h.FileAsync("empty.cfg", AdmConfigFile.Write(new AdmConfig { Parameters = [new NameValue("Network.IPAddress", "10.0.0.48")] }));
        Assert.Equal(NodeResult.Error(ConfigurePayload.NothingToApply), await RunAsync(h, node, new ConfigurationSettings(empty.Id), camera));

        Assert.Equal(NodeResult.Error(NodeVault.FileGone), await RunAsync(h, node, new ConfigurationSettings(Guid.NewGuid()), camera));
        Assert.Empty(h.Ctx.TaskRuns);
    }

    [Fact]
    public async Task Configuration_needs_a_configuration_file_and_an_area()
    {
        var node = new ConfigurationNode();
        Assert.Equal("Choose a configuration file.", node.Check(Harness.Json(new ConfigurationSettings()), ProfileCheckContext.Empty));
        Assert.Equal("Choose what to apply.", node.Check(Harness.Json(new ConfigurationSettings(Guid.NewGuid(), Parameters: false, ActionRules: false, StreamProfiles: false)), ProfileCheckContext.Empty));
        Assert.Null(node.Check(Harness.Json(new ConfigurationSettings(Guid.NewGuid())), ProfileCheckContext.Empty));

        // The kept file: a .cfg of at most 1 MB.
        await using var h = await StartedAsync();
        var cfg = await h.FileAsync("lobby.cfg", AdmFile);
        var json = await h.FileAsync("lobby.json", Encoding.UTF8.GetBytes("""{"data":{}}"""));
        var large = await h.FileAsync("large.cfg", new byte[AdmConfigFile.MaxFileBytes + 1]);
        Assert.Null(node.Check(Harness.Json(new ConfigurationSettings(cfg.Id)), Kept(h)));
        Assert.Equal(ConfigurationNode.WrongFile, node.Check(Harness.Json(new ConfigurationSettings(json.Id)), Kept(h)));
        Assert.Equal(ConfigurationNode.TooLarge, node.Check(Harness.Json(new ConfigurationSettings(large.Id)), Kept(h)));
        Assert.Equal("The configuration file is no longer on the server. Choose it again.", node.Check(Harness.Json(new ConfigurationSettings(Guid.NewGuid())), Kept(h)));

        // A camera as the source is gone: such settings have no file.
        using var stored = JsonDocument.Parse("""{"source":"templateCamera","templateSerial":"ACCC8E000048","parameters":true}""");
        Assert.Equal("Choose a configuration file.", node.Check(stored.RootElement.Clone(), ProfileCheckContext.Empty));
    }

    /// <summary>
    /// Every task a node starts by its id is declared in the source of a shipped plugin (a task id that is gone or renamed
    /// fails here, not on a camera).
    /// </summary>
    [Fact]
    public void Every_task_a_node_starts_is_a_task_of_a_shipped_plugin()
    {
        var ids = NodeRegistry.Default.All
            .Select(n => n.GetType().GetProperty(nameof(ConfigurationNode.TaskPluginId))?.GetValue(n) as string)
            .OfType<string>()
            .Concat(Directory.EnumerateFiles(TestRepository.Path("plugins", "Oadm.Plugins.Provisioning", "Nodes"), "*.cs")
                .SelectMany(f => Regex.Matches(File.ReadAllText(f), "TaskId\\s*=\\s*\"([^\"]+)\"").Select(m => m.Groups[1].Value)))
            .ToHashSet(StringComparer.Ordinal);
        var shipped = Directory.EnumerateFiles(TestRepository.Path("plugins"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(TestRepository.Path("plugins"), f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.StartsWith("Oadm.Plugins.Provisioning", StringComparison.Ordinal) || part is "obj" or "bin"))
            .Select(File.ReadAllText)
            .ToList();

        Assert.Contains(ConfigureIds.Configure, ids);
        Assert.DoesNotContain("oadm.device-config.import", ids);
        Assert.All(ids, id => Assert.True(shipped.Any(s => s.Contains('"' + id + '"', StringComparison.Ordinal)), id + " is not a task of a shipped plugin"));
    }

    // ---- Users ----

    [Fact]
    public async Task Users_adds_each_account_with_its_kept_password_and_never_stores_it_plainly()
    {
        await using var h = await StartedAsync();
        var camera = Camera(Device(h));
        var op = await h.SecretAsync("Op-Pass-77");
        var viewer = await h.SecretAsync("View-Pass-88");
        var settings = new UsersSettings([new UserAccount("op1", CameraRole.Operator, Ptz: true, SecretId: op), new UserAccount("view1", SecretId: viewer)]);
        var node = new UsersNode();
        Assert.Null(node.Check(Harness.Json(settings), Kept(h)));

        Assert.Equal(NodeResult.Success("Added op1 and view1."), await RunAsync(h, node, settings, camera));
        Assert.Equal(2, h.Ctx.TaskRuns.Count);
        var first = Payload(h.Ctx.TaskRuns[0]);
        Assert.Equal(("add", "op1", "Op-Pass-77", "operator", true),
            (first.GetProperty("mode").GetString(), first.GetProperty("userName").GetString(), first.GetProperty("password").GetString(), first.GetProperty("role").GetString(), first.GetProperty("ptz").GetBoolean()));
        Assert.Equal("viewer", Payload(h.Ctx.TaskRuns[1]).GetProperty("role").GetString());
        Assert.All(h.Ctx.PluginSettings.Values.Values, v => Assert.DoesNotContain("Op-Pass-77", v, StringComparison.Ordinal));
        Assert.DoesNotContain("Op-Pass-77", Harness.Json(settings).GetRawText(), StringComparison.Ordinal);

        h.Decide = run => run.PayloadJson!.Contains("\"op1\"", StringComparison.Ordinal)
            ? (TaskEndState.Failed, "op1 is the account OADM uses for this device and must stay Administrator. Nothing was changed.")
            : (TaskEndState.DoneWithWarnings, "User view1 already exists (Viewer); nothing was changed. Use Change to update it.");
        Assert.Equal(
            NodeResult.Error("view1: User view1 already exists (Viewer); nothing was changed. Use Change to update it. op1: op1 is the account OADM uses for this device and must stay Administrator. Nothing was changed."),
            await RunAsync(h, node, settings, camera));
    }

    [Fact]
    public async Task Users_checks_the_accounts_and_a_gone_password()
    {
        await using var h = await StartedAsync();
        var node = new UsersNode();
        var secret = await h.SecretAsync("x1");
        Assert.Equal("Add an account.", node.Check(Harness.Json(new UsersSettings([])), Kept(h)));
        Assert.Equal("root is the account of Set password.", node.Check(Harness.Json(new UsersSettings([new UserAccount("root", SecretId: secret)])), Kept(h)));
        Assert.Equal("op_1: use 1 to 14 letters and digits.", node.Check(Harness.Json(new UsersSettings([new UserAccount("op_1", SecretId: secret)])), Kept(h)));
        Assert.Equal("The account OP1 is there twice.", node.Check(Harness.Json(new UsersSettings([new UserAccount("op1", SecretId: secret), new UserAccount("OP1", SecretId: secret)])), Kept(h)));
        Assert.Equal("Type the password of op1.", node.Check(Harness.Json(new UsersSettings([new UserAccount("op1")])), Kept(h)));
        var gone = new UsersSettings([new UserAccount("op1", SecretId: Guid.NewGuid())]);
        Assert.Equal("The password of op1 is no longer on the server. Type it again.", node.Check(Harness.Json(gone), Kept(h)));

        Assert.Equal(NodeResult.Error("op1: " + NodeVault.SecretGone), await RunAsync(h, node, gone, Camera(Device(h))));
        Assert.Empty(h.Ctx.TaskRuns);
    }

    // ---- Certificate and 802.1X ----

    [Fact]
    public async Task Certificate_installs_by_policy_or_sets_up_est_with_its_pro_refusal()
    {
        await using var h = await StartedAsync();
        var camera = Camera(Device(h));
        var policy = Guid.NewGuid();
        var node = new CertificateNode();

        Assert.Equal(NodeResult.Success("Certificate installed, HTTPS on."), await RunAsync(h, node, new CertificateSettings(CertificateMode.Policy, policy), camera));
        Assert.Equal("oadm.pki.https-enable", h.Ctx.TaskRuns[0].PluginId);
        var payload = Payload(h.Ctx.TaskRuns[0]);
        Assert.Equal(policy.ToString(), payload.GetProperty("policyId").GetString());
        Assert.False(payload.GetProperty("clockConfirmed").GetBoolean()); // a clock that is behind refuses; the flow never confirms it

        h.Outcomes["oadm.est.setup"] = (TaskEndState.Failed, "OADM PRO is not active. Nothing was changed.");
        Assert.Equal(NodeResult.Error("OADM PRO is not active. Nothing was changed."), await RunAsync(h, node, new CertificateSettings(CertificateMode.Est), camera));
        Assert.Equal("oadm.est.setup", h.Ctx.TaskRuns[1].PluginId);
        Assert.Equal("Setup", Payload(h.Ctx.TaskRuns[1]).GetProperty("action").GetString());

        h.Outcomes.Clear();
        Assert.Equal(NodeResult.Success("EST enrollment set up."), await RunAsync(h, node, new CertificateSettings(CertificateMode.Est), camera));

        h.Outcomes["oadm.pki.https-enable"] = (TaskEndState.Failed, "The device's time is 2 hours behind. The certificate becomes valid on the device only in 2 hours. Set the device to use NTP. Nothing was changed.");
        Assert.Equal(NodeState.Error, (await RunAsync(h, node, new CertificateSettings(), camera)).State);
        Assert.Equal("Choose a PKI policy.", node.Check(Harness.Json(new CertificateSettings(CertificateMode.Policy, Guid.Empty)), ProfileCheckContext.Empty));
        Assert.Equal("Choose a PKI policy.", node.Check(Harness.Json(new CertificateSettings()), ProfileCheckContext.Empty));
        Assert.Null(node.Check(Harness.Json(new CertificateSettings(CertificateMode.Est)), ProfileCheckContext.Empty));
    }

    [Fact]
    public void The_policy_of_certificate_and_802_1x_must_be_one_pki_has()
    {
        var https = new IssuingPolicyInfo(Guid.NewGuid(), "Cameras", IssuingPolicyUse.Https);
        var dot1x = new IssuingPolicyInfo(Guid.NewGuid(), "Switch ports", IssuingPolicyUse.Dot1x);
        var pki = ProfileCheckContext.Empty with { Policies = [https, dot1x] };
        var certificate = new CertificateNode();
        var port = new Dot1xNode();

        Assert.Null(certificate.Check(Harness.Json(new CertificateSettings(CertificateMode.Policy, https.Id)), pki));
        Assert.Equal(PolicyChoice.Gone, certificate.Check(Harness.Json(new CertificateSettings(CertificateMode.Policy, dot1x.Id)), pki)); // another use
        Assert.Null(port.Check(Harness.Json(new Dot1xSettings(dot1x.Id)), pki));
        Assert.Equal(PolicyChoice.Choose, port.Check(Harness.Json(new Dot1xSettings()), pki));
        Assert.Equal(PolicyChoice.Gone, port.Check(Harness.Json(new Dot1xSettings(Guid.NewGuid())), pki));
        Assert.Equal("Add an HTTPS certificate policy on the PKI page first.", certificate.Check(Harness.Json(new CertificateSettings()), ProfileCheckContext.Empty with { Policies = [dot1x] }));
        Assert.Null(port.Check(Harness.Json(new Dot1xSettings(Guid.NewGuid())), ProfileCheckContext.Empty)); // PKI not running: its task checks
    }

    [Fact]
    public async Task Dot1x_turns_on_802_1x_with_the_chosen_policy()
    {
        await using var h = await StartedAsync();
        var camera = Camera(Device(h));

        var policy = Guid.NewGuid();
        Assert.Equal(NodeResult.Success("802.1X on."), await RunAsync(h, new Dot1xNode(), new Dot1xSettings(policy), camera));
        Assert.Equal("oadm.pki.dot1x-enable", Assert.Single(h.Ctx.TaskRuns).PluginId);
        Assert.Equal(policy.ToString(), Payload(h.Ctx.TaskRuns[0]).GetProperty("policyId").GetString());

        h.Outcomes["oadm.pki.dot1x-enable"] = (TaskEndState.Failed, "The chosen policy no longer exists. Nothing was changed.");
        Assert.Equal(NodeResult.Error("The chosen policy no longer exists. Nothing was changed."), await RunAsync(h, new Dot1xNode(), new Dot1xSettings(policy), camera));
    }

    // ---- SD card ----

    [Fact]
    public async Task Sd_card_encrypts_with_the_kept_passphrase_and_sets_the_age()
    {
        await using var h = await StartedAsync();
        var camera = Camera(Device(h));
        var passphrase = await h.SecretAsync("card secret");
        var settings = new SdCardSettings(Format: true, Encrypt: true, SecretId: passphrase, MaxAgeDays: 30);
        var node = new SdCardNode();

        Assert.Equal(NodeResult.Success("SD card encrypted and formatted, recordings kept 30 days."), await RunAsync(h, node, settings, camera));
        Assert.Equal(["encryptionOn", "retention"], h.Ctx.TaskRuns.Select(r => Payload(r).GetProperty("action").GetString()));
        Assert.Equal("card secret", Payload(h.Ctx.TaskRuns[0]).GetProperty("passphrase").GetString());
        Assert.Equal(30, Payload(h.Ctx.TaskRuns[1]).GetProperty("retentionDays").GetInt32());

        h.Outcomes["oadm.edge-storage.task"] = (TaskEndState.Failed, "No SD card. Nothing was changed.");
        Assert.Equal(NodeResult.NotNeeded("no SD card."), await RunAsync(h, node, new SdCardSettings(), camera));

        h.Outcomes["oadm.edge-storage.task"] = (TaskEndState.Failed, "The SD card is encrypted already. Nothing was changed.");
        Assert.Equal(NodeResult.Error("The SD card is encrypted already. Nothing was changed."), await RunAsync(h, node, settings, camera));

        Assert.Equal("Choose what to do with the SD card.", node.Check(Harness.Json(new SdCardSettings(Format: false)), ProfileCheckContext.Empty));
        Assert.Equal("Type the passphrase.", node.Check(Harness.Json(new SdCardSettings(Encrypt: true)), ProfileCheckContext.Empty));
        Assert.Equal("Keep recordings 1 to 7000 days.", node.Check(Harness.Json(new SdCardSettings(MaxAgeDays: 0)), ProfileCheckContext.Empty));
    }

    // ---- Autofocus ----

    private const string Capabilities = """{"apiVersion": "1.3", "method": "getCapabilities", "data": {"numberOfOptics": 1,"optics": [{"opticsId": "0", "maxMagnification": 2.7057, "capabilities": ["zoom", "focus"]}]}}""";

    [Fact]
    public async Task Autofocus_runs_on_a_camera_with_remote_focus_and_is_not_needed_without()
    {
        await using var h = await StartedAsync();
        var device = Device(h);
        var camera = Camera(device);
        var client = h.Vapix.For(device.Id);
        var node = new AutofocusNode();

        Assert.Equal(NodeResult.NotNeeded("no remote focus."), await RunAsync(h, node, new AutofocusSettings(), camera));
        Assert.Empty(client.Requests); // no optics API: nothing is sent

        client.Apis = [new DeviceApi("optics-control", "2.0")]; // another major version is another API
        Assert.Equal(NodeResult.NotNeeded("no remote focus."), await RunAsync(h, node, new AutofocusSettings(), camera));
        Assert.Empty(client.Requests);

        client.Apis = [new DeviceApi("optics-control", "1.3")];
        client.Handler = (_, _, _) => Task.FromResult(ScriptedVapix.Json("""{"apiVersion": "1.3", "method": "getCapabilities", "data": {"numberOfOptics": 1,"optics": [{"opticsId": "0", "capabilities": ["irCutFilter"]}]}}"""));
        Assert.Equal(NodeResult.NotNeeded("no remote focus."), await RunAsync(h, node, new AutofocusSettings(), camera));

        client.Handler = (_, body, _) => Task.FromResult(ScriptedVapix.Json(body!.Contains("getCapabilities", StringComparison.Ordinal) ? Capabilities : """{"apiVersion":"1.3","method":"performAutofocus","data":{}}"""));
        Assert.Equal(NodeResult.Success("Focus found."), await RunAsync(h, node, new AutofocusSettings(), camera));
        Assert.Contains(client.Requests, r => r.Body!.Contains("\"performAutofocus\"", StringComparison.Ordinal) && r.Body.Contains("\"opticsId\":\"0\"", StringComparison.Ordinal));

        client.Handler = (_, body, _) => Task.FromResult(ScriptedVapix.Json(body!.Contains("getCapabilities", StringComparison.Ordinal) ? Capabilities : """{"apiVersion":"1.3","method":"performAutofocus","error":{"code":1300,"message":"Focus not found"}}"""));
        Assert.Equal(NodeResult.Error("The camera could not find focus. Set focus by hand."), await RunAsync(h, node, new AutofocusSettings(), camera));
        Assert.Empty(h.Ctx.TaskRuns);
    }

    [Fact]
    public async Task Autofocus_that_never_answers_ends_after_two_minutes()
    {
        await using var h = await StartedAsync();
        var device = Device(h);
        var client = h.Vapix.For(device.Id);
        client.Apis = [new DeviceApi("optics-control", "1.3")];
        client.Handler = async (_, body, ct) =>
        {
            if (body!.Contains("getCapabilities", StringComparison.Ordinal))
            {
                return ScriptedVapix.Json(Capabilities);
            }

            await Task.Delay(Timeout.Infinite, ct); // the camera searches until the node gives up
            return ScriptedVapix.Json("{}");
        };

        var result = await RunAsync(h, new AutofocusNode(), new AutofocusSettings(), Camera(device));

        Assert.Equal(NodeResult.Error("The autofocus did not end within 2 minutes."), result);
    }

    // ---- VAPIX commands ----

    private static CommandLibrary Library() => CommandLibrary.Load([("test.json", () => """
        {
          "formatVersion": 1,
          "category": "Image",
          "commands": [
            { "id": "image.text.set", "version": 1, "name": "Set overlay text", "category": "Image",
              "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ], "writes": true,
              "fields": [ { "name": "text", "label": "Text", "type": "string", "pattern": "^[A-Za-z ]{1,20}$" } ],
              "request": { "method": "GET", "path": "/axis-cgi/param.cgi", "query": { "action": "update", "Image.I0.Text.String": "{{text}}" }, "headers": {}, "bodyType": "none", "body": null, "timeoutSeconds": 15 },
              "response": { "kind": "param-cgi" } },
            { "id": "image.optics.autofocus", "version": 1, "name": "Run autofocus", "category": "Image",
              "requires": [ { "api": "optics-control", "minVersion": "1.0" } ], "writes": true,
              "fields": [],
              "request": { "method": "POST", "path": "/axis-cgi/opticscontrol.cgi", "query": {}, "headers": {}, "bodyType": "json", "body": { "apiVersion": "1.0", "method": "performAutofocus" }, "timeoutSeconds": 60 },
              "response": { "kind": "json-rpc" } },
            { "id": "image.users.set", "version": 1, "name": "Set a password", "category": "Image",
              "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ], "writes": true,
              "fields": [ { "name": "pwd", "label": "Password", "type": "password" } ],
              "request": { "method": "GET", "path": "/axis-cgi/param.cgi", "query": { "action": "update", "x": "{{pwd}}" }, "headers": {}, "bodyType": "none", "body": null, "timeoutSeconds": 15 },
              "response": { "kind": "param-cgi" } }
          ]
        }
        """)]);

    private static VapixCommandStep Step(string id, string? text = null) =>
        new(id, text is null ? null : new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement(text) });

    [Fact]
    public async Task Vapix_commands_run_what_the_camera_has_and_warn_about_the_rest()
    {
        var library = Library();
        Assert.Empty(library.Problems);
        await using var h = await StartedAsync();
        var device = Device(h);
        var camera = Camera(device);
        h.Vapix.For(device.Id).Apis = [new DeviceApi("param-cgi", "1.0")];
        var node = new VapixCommandsNode(library);
        var settings = new VapixCommandsSettings([Step("image.text.set", "Lobby"), Step("image.optics.autofocus")]);
        Assert.Null(node.Check(Harness.Json(settings), ProfileCheckContext.Empty));

        Assert.Equal(NodeResult.Warning("Ran Set overlay text. Not on this camera: Run autofocus."), await RunAsync(h, node, settings, camera));
        var run = Assert.Single(h.Ctx.TaskRuns);
        Assert.Equal("oadm.vapix-commander.run", run.PluginId);
        var payload = Payload(run);
        Assert.True(payload.GetProperty("confirmed").GetBoolean());
        Assert.False(payload.GetProperty("confirmedDangerous").GetBoolean());
        var command = Assert.Single(payload.GetProperty("commands").EnumerateArray());
        Assert.Equal("image.text.set", command.GetProperty("command").GetProperty("id").GetString());
        Assert.Equal("Lobby", command.GetProperty("values").GetProperty("text").GetString());

        Assert.Equal(NodeResult.Warning("Not on this camera: Run autofocus."), await RunAsync(h, node, new VapixCommandsSettings([Step("image.optics.autofocus")]), camera));
        Assert.Single(h.Ctx.TaskRuns); // nothing to run: no task

        h.Outcomes["oadm.vapix-commander.run"] = (TaskEndState.Failed, "Set overlay text: HTTP 400 Bad Request");
        Assert.Equal(NodeResult.Error("Set overlay text: HTTP 400 Bad Request."), await RunAsync(h, node, new VapixCommandsSettings([Step("image.text.set", "Lobby")]), camera));
    }

    [Fact]
    public void Vapix_commands_check_the_library_values_and_refuse_passwords()
    {
        var node = new VapixCommandsNode(Library());
        Assert.Equal("Add a command.", node.Check(Harness.Json(new VapixCommandsSettings([])), ProfileCheckContext.Empty));
        Assert.Equal("image.nothing is not in the VAPIX Commander library.", node.Check(Harness.Json(new VapixCommandsSettings([Step("image.nothing")])), ProfileCheckContext.Empty));
        Assert.Equal("Set a password needs a password or a file; it cannot run in a profile.", node.Check(Harness.Json(new VapixCommandsSettings([Step("image.users.set")])), ProfileCheckContext.Empty));
        Assert.StartsWith("Set overlay text: Text", node.Check(Harness.Json(new VapixCommandsSettings([Step("image.text.set", "Lobby 1!")])), ProfileCheckContext.Empty), StringComparison.Ordinal);
        Assert.StartsWith("Set overlay text: Text is required", node.Check(Harness.Json(new VapixCommandsSettings([Step("image.text.set")])), ProfileCheckContext.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_node_kind_is_registered_once_with_valid_defaults_except_what_must_be_chosen()
    {
        var registry = NodeRegistry.Default;
        NodeKind[] gone = [NodeKind.AddToOadm, NodeKind.WaitForApproval, NodeKind.SetIp, NodeKind.NetworkSettings]; // converted on load
        NodeKind[] schedulesOnly = [NodeKind.Restart]; // a Schedules action, never offered in a profile
        Assert.Equal(Enum.GetValues<NodeKind>().Except(gone).Except(schedulesOnly).Order(), registry.All.Select(n => n.Kind).Order());
        Assert.Equal(registry.All.Count, registry.All.Select(n => n.Kind).Distinct().Count());
        foreach (var node in registry.All)
        {
            // The defaults are readable settings; their error (if any) is what the person must choose first.
            var error = node.Check(NodeSettings.Write(node.DefaultSettings), ProfileCheckContext.Empty);
            Assert.True(error is null || !error.Contains("cannot be read", StringComparison.Ordinal), node.Name + ": " + error);
        }
    }
}
