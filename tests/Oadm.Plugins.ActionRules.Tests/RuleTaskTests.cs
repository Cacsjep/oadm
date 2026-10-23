using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>The task against the stateful fake camera: in place on AXIS OS 12, recreated on AXIS OS 10.</summary>
public sealed class RuleTaskTests : IAsyncLifetime
{
    private const string Tcp = "com.axis.action.fixed.notification.tcp";
    private const string TcpWhileActive = "com.axis.action.unlimited.notification.tcp";
    private const string Ftp = "com.axis.action.fixed.send_images.ftp";
    private const string FtpWhileActive = "com.axis.action.unlimited.send_images.ftp";

    private static readonly EventFilter VirtualInput = new(
        "tns1:Device/tnsaxis:IO/VirtualInput",
        "boolean(//SimpleItem[@Name=\"port\" and @Value=\"20\"]) and boolean(//SimpleItem[@Name=\"active\" and @Value=\"1\"])");

    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDevice _device = new();
    private readonly ActionRulesPlugin _plugin = new();

    public async Task InitializeAsync()
    {
        var devices = new FakeDevices();
        devices.Items.Add(_device);
        await _plugin.StartAsync(new FakeContext(devices, new FakeCameras(), new RecordingRunner(), new RecordingEvents(), _data), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private static string AddTcp(RuleCamera camera, string message = "cam-0815") =>
        camera.Add("Door alarm", Tcp, [("port", "9999"), ("qos", "0"), ("message", message), ("host", "192.0.2.10")], start: VirtualInput);

    private static readonly RulePatch WhileActive = new()
    {
        Template = TcpWhileActive,
        TriggerKind = TriggerKinds.Condition,
        Parameters = new Dictionary<string, string> { ["period"] = "5" },
    };

    [Theory]
    [InlineData("os12")]
    [InlineData("os10")]
    public async Task Once_becomes_while_active_and_the_message_of_each_camera_stays(string os)
    {
        var camera = new RuleCamera(os);
        var id = AddTcp(camera, "camera 17 of 500");

        var ctx = await RunAsync(camera, RuleTaskActions.Edit, WhileActive, id);

        var rule = Assert.Single(camera.Rules);
        Assert.Equal(os == "os12" ? id : "2", rule.Key); // in place on AXIS OS 12, recreated on AXIS OS 10
        Assert.Null(rule.Value.Start);
        Assert.Equal(RuleText.Key(VirtualInput), RuleText.Key(Assert.Single(rule.Value.Conditions)));
        Assert.Equal(TcpWhileActive, camera.Configs[rule.Value.ConfigId].Template);
        Assert.Equal("camera 17 of 500", camera.Param(rule.Key, "message"));
        Assert.Equal(("192.0.2.10", "5"), (camera.Param(rule.Key, "host"), camera.Param(rule.Key, "period")));
        Assert.Single(camera.Configs); // the old action is gone with the old rule
        Assert.All(StepRun.Lines(ctx.Steps), l => Assert.Matches(": Done$", l));
        Assert.Equal(os == "os12", camera.Requests.Contains("PATCH rules/" + id));
        Assert.Equal(os == "os10", camera.Requests.Contains("SOAP AddActionRule"));
    }

    [Theory]
    [InlineData("os12", "The device refused: Validation error: Action configuration is of type 'continuous' and requires at least one condition")]
    [InlineData("os10", "The device refused: Primary action with id '2' and of type 'continuous' requires at least one condition")]
    public async Task A_refused_combination_fails_and_changes_nothing(string os, string message)
    {
        var camera = new RuleCamera(os);
        var id = AddTcp(camera);

        var ex = await Assert.ThrowsAsync<ActionEngineException>(() => RunAsync(camera, RuleTaskActions.Edit, WhileActive with { TriggerKind = null }, id));

        Assert.Equal(message, ex.Message);
        var rule = Assert.Single(camera.Rules);
        Assert.Equal(Tcp, camera.Configs[rule.Value.ConfigId].Template);
        Assert.NotNull(rule.Value.Start);
        Assert.Single(camera.Configs); // the refused new action was removed again
    }

    [Fact]
    public async Task On_and_off_never_sends_the_action()
    {
        var camera = new RuleCamera();
        var id = camera.Add("Upload", Ftp, FtpParameters("secret1"), start: VirtualInput);

        await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Enabled = false }, id);

        Assert.False(camera.Rules[id].Enabled);
        var body = Assert.Single(camera.RestBodies);
        Assert.DoesNotContain("actionConfig", body, StringComparison.Ordinal);
        Assert.Equal("secret1", camera.Param(id, "password"));
    }

    [Fact]
    public async Task A_changed_action_keeps_the_real_password()
    {
        var camera = new RuleCamera();
        var id = camera.Add("Upload", Ftp, FtpParameters("secret1"), start: VirtualInput);

        await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Template = FtpWhileActive, TriggerKind = TriggerKinds.Condition }, id);

        Assert.Equal(FtpWhileActive, camera.Configs[camera.Rules[id].ConfigId].Template);
        Assert.Equal("secret1", camera.Param(id, "password"));
        Assert.DoesNotContain(RestActionRules.Redacted, string.Concat(camera.RestBodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_login_inside_an_address_never_reaches_the_page_and_stays_on_the_device()
    {
        var camera = new RuleCamera();
        var id = camera.Add("Notify", "com.axis.action.fixed.notification.http",
            [("upload_url", "http://joe:pw1@192.0.2.10/cgi"), ("login", ""), ("password", ""), ("proxy_host", ""), ("proxy_port", ""), ("proxy_login", ""),
             ("proxy_password", ""), ("qos", "0"), ("parameters", ""), ("message", "m"), ("method", "GET"), ("http_headers", ""), ("body", "")],
            start: VirtualInput);
        var view = RuleView.From(Assert.Single(await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None)));
        Assert.Equal("http://joe:********@192.0.2.10/cgi", view.Get("upload_url"));

        await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Parameters = new Dictionary<string, string> { ["upload_url"] = "http://joe:********@192.0.2.10/other" } }, id);
        Assert.Equal("http://joe:pw1@192.0.2.10/other", camera.Param(Assert.Single(camera.Rules).Key, "upload_url"));

        // Another server never gets the hidden password.
        var key = Assert.Single(camera.Rules).Key;
        await Assert.ThrowsAsync<ActionEngineException>(() => RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Parameters = new Dictionary<string, string> { ["upload_url"] = "http://joe:********@192.0.2.66/cgi" } }, key));
        Assert.Equal("http://joe:pw1@192.0.2.10/other", camera.Param(Assert.Single(camera.Rules).Key, "upload_url"));
    }

    [Theory]
    [InlineData("ftp://joe:pw@host/x", "ftp://joe:********@host/x")]
    [InlineData("http://joe@host/x", "http://joe@host/x")]
    [InlineData("http://joe:@host/x", "http://joe:@host/x")]
    [InlineData("http://host/a:b@c", "http://host/a:b@c")]
    [InlineData("no address", "no address")]
    [InlineData("http://user:p@ss@host/x", "http://user:********@host/x")]
    [InlineData("a=1 sftp://u:p/w@h b ftp://v:q@g", "a=1 sftp://u:p/w@h b ftp://v:********@g")]
    public void Logins_inside_addresses_are_hidden(string value, string shown) => Assert.Equal(shown, RuleText.HideLogin(value));

    [Fact]
    public void A_hidden_password_needs_one_to_keep()
    {
        Assert.Equal("http://joe:pw@host/new", RuleText.KeepLogin("http://joe:********@host/new", "http://joe:pw@host/"));
        Assert.Equal("http://ann:new@other/", RuleText.KeepLogin("http://ann:new@other/", "http://joe:pw@host/"));
        Assert.Throws<ActionEngineException>(() => RuleText.KeepLogin("http://joe:********@attacker.example/", "http://joe:pw@host/")); // never to another server
        Assert.Throws<ActionEngineException>(() => RuleText.KeepLogin("http://ann:********@host/", "http://joe:pw@host/"));
        Assert.Throws<ActionEngineException>(() => RuleText.KeepLogin("http://joe:********@host/", "http://host/"));
    }

    [Fact]
    public async Task A_rule_changed_since_it_was_read_is_skipped()
    {
        var camera = new RuleCamera();
        var id = AddTcp(camera);
        var payload = await PayloadAsync(camera, RuleTaskActions.Edit, new RulePatch { Name = "Renamed" }, id);
        await _plugin.Undo.StartRunAsync(new UndoRun(payload.RunId, DateTimeOffset.UtcNow, "test", payload.Action, 1, false), CancellationToken.None);
        camera.Rules[id] = camera.Rules[id] with { Name = "Changed in the web interface" };

        var ctx = await ExecuteAsync(camera, payload);

        Assert.Equal("Changed in the web interface", camera.Rules[id].Name);
        Assert.Contains(ctx.Warnings, w => w.Contains(RuleTexts.ChangedSinceRead, StringComparison.Ordinal));
        Assert.DoesNotContain(camera.Requests, r => r.StartsWith("PATCH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_setting_this_device_lacks_is_left_out()
    {
        var camera = new RuleCamera("os10");
        var id = camera.Add("Overlay", "com.axis.action.fixed.set_overlay", [("text", "Alarm"), ("channels", "1"), ("duration", "5")], start: VirtualInput);

        // The page offers the settings of every target device: "index" exists on AXIS OS 12 only.
        var ctx = await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Parameters = new Dictionary<string, string> { ["index"] = "1", ["text"] = "Fire" } }, id);

        var rule = Assert.Single(camera.Rules);
        Assert.Equal(("Fire", null), (camera.Param(rule.Key, "text"), camera.Param(rule.Key, "index")));
        Assert.All(StepRun.Lines(ctx.Steps), l => Assert.Matches(": Done$", l));
        Assert.Equal("Overlay: this device's action has no index; left out.", Assert.Single(ctx.Warnings));
    }

    [Theory]
    [InlineData("os12")]
    [InlineData("os10")]
    public async Task Undo_puts_changed_and_deleted_rules_back(string os)
    {
        var camera = new RuleCamera(os);
        var changed = AddTcp(camera, "first");
        var deleted = camera.Add("Upload", Ftp, FtpParameters("secret1"), start: VirtualInput);
        var before = await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None);

        var editRun = await StartRunAsync(camera, RuleTaskActions.Edit, WhileActive, changed);
        var deleteRun = await StartRunAsync(camera, RuleTaskActions.Delete, null, deleted);
        Assert.Single(camera.Rules);

        await UndoAsync(camera, deleteRun);
        await UndoAsync(camera, editRun);

        var after = await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None);
        Assert.Equal(
            before.Select(r => RuleView.VersionOf(r with { Id = string.Empty })).Order(),
            after.Select(r => RuleView.VersionOf(r with { Id = string.Empty })).Order());
        Assert.Equal("secret1", after.Single(r => r.Name == "Upload").Action.Get("password"));
        var runs = await _plugin.Undo.ListAsync(CancellationToken.None);
        Assert.All(runs, r => Assert.True(r.Undone));
    }

    [Fact]
    public async Task Saved_passwords_are_encrypted()
    {
        var camera = new RuleCamera();
        var id = camera.Add("Upload", Ftp, FtpParameters("secret1"), start: VirtualInput);

        await StartRunAsync(camera, RuleTaskActions.Delete, null, id);

        var text = string.Concat(Directory.EnumerateFiles(_data, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("secret1", text, StringComparison.Ordinal);
        Assert.Contains("enc:oadm:action-rules:undo:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_inside_an_address_is_saved_encrypted_and_put_back()
    {
        var camera = new RuleCamera();
        var id = camera.Add("Notify", "com.axis.action.fixed.notification.http",
            [("upload_url", "http://joe:addr-secret@192.0.2.10/cgi"), ("login", ""), ("password", ""), ("proxy_host", ""), ("proxy_port", ""), ("proxy_login", ""),
             ("proxy_password", ""), ("qos", "0"), ("parameters", ""), ("message", "m"), ("method", "GET"), ("http_headers", ""), ("body", "")],
            start: VirtualInput);

        var run = await StartRunAsync(camera, RuleTaskActions.Delete, null, id);

        var text = string.Concat(Directory.EnumerateFiles(_data, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("addr-secret", text, StringComparison.Ordinal);
        await UndoAsync(camera, run);
        Assert.Equal("http://joe:addr-secret@192.0.2.10/cgi", camera.Param(Assert.Single(camera.Rules).Key, "upload_url"));
    }

    private static IEnumerable<(string, string)> FtpParameters(string password) =>
    [
        ("stream_options", "resolution=640x360"), ("pre_duration", "1000"), ("post_duration", "1000"), ("max_images", "1"),
        ("create_folder", string.Empty), ("filename", "image.jpg"), ("max_sequence_number", "0"),
        ("host", "192.0.2.11"), ("upload_path", "x"), ("port", "21"), ("login", "u"), ("password", password),
        ("passive", "1"), ("temporary", "0"), ("qos", "0"),
    ];

    /// <summary>Starts a run like the plugin does (saved for undo first) and runs its task on the camera.</summary>
    private async Task<RecordingContext> RunAsync(RuleCamera camera, string action, RulePatch? patch, params string[] ids)
    {
        var payload = await PayloadAsync(camera, action, patch, ids);
        await _plugin.Undo.StartRunAsync(new UndoRun(payload.RunId, DateTimeOffset.UtcNow, "test", action, 1, false), CancellationToken.None);
        return await ExecuteAsync(camera, payload);
    }

    private async Task<Guid> StartRunAsync(RuleCamera camera, string action, RulePatch? patch, params string[] ids)
    {
        var payload = await PayloadAsync(camera, action, patch, ids);
        await _plugin.Undo.StartRunAsync(new UndoRun(payload.RunId, DateTimeOffset.UtcNow, "test", action, 1, false), CancellationToken.None);
        await ExecuteAsync(camera, payload);
        return payload.RunId;
    }

    private async Task UndoAsync(RuleCamera camera, Guid runId)
    {
        await _plugin.Undo.TryMarkUndoneAsync(runId, true, CancellationToken.None); // like the page: marked before its tasks start
        await ExecuteAsync(camera, new RuleTaskPayload(RuleTaskActions.Undo, Guid.NewGuid(), "Undo", [], null, runId));
    }

    private async Task<RuleTaskPayload> PayloadAsync(RuleCamera camera, string action, RulePatch? patch, params string[] ids)
    {
        var rules = await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None);
        var refs = ids.Select(id => new RuleRef(id, RuleView.VersionOf(rules.Single(r => r.Id == id)))).ToList();
        return new RuleTaskPayload(action, Guid.NewGuid(), "test", [new DeviceTargets(_device.Id, refs)], patch);
    }

    private async Task<RecordingContext> ExecuteAsync(RuleCamera camera, RuleTaskPayload payload)
    {
        var ctx = new RecordingContext(camera);
        var task = (RuleTaskPlugin)_plugin.TaskPlugins[0];
        await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, _device, ActionRulesJson.Serialize(payload), CancellationToken.None));
        return ctx;
    }
}
