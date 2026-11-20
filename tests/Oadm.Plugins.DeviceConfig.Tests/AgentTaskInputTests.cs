using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;
using Oadm.Server.Tasks;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>
/// The four tasks as AI agents run them (docs/spec/mcp.md "Tasks"): the input schema the host exports, the field errors with the
/// dialog's texts, the plan from cached data, the permissions, and that an invalid input never reaches the device.
/// </summary>
public sealed class AgentTaskInputTests
{
    private static readonly ICallContext Caller = Oadm.Sdk.Testing.FakeCallContext.AllDevices(new Oadm.Sdk.Testing.FakeCoreContext());

    private static Dictionary<string, string> Errors(ITaskPlugin task, string? json) =>
        task.ValidatePayload(json, Caller).ToDictionary(e => e.Field, e => e.Message);

    private static string Json(JsonNode node) => node.ToJsonString();

    // ---- Schemas ----

    [Fact]
    public void The_host_exports_a_strict_schema_for_every_task()
    {
        var configure = TaskPayloadSchemas.Of(new ConfigureDevicesTask())!;
        Assert.Equal(["settings", "actionRules", "schedules", "streamProfiles"], configure["properties"]!.AsObject().Select(p => p.Key));
        Assert.False(configure["additionalProperties"]!.GetValue<bool>());
        var setting = configure["properties"]!["settings"]!["items"]!;
        Assert.Equal(["name", "value"], setting["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(ConfigureIds.MaxParameters, configure["properties"]!["settings"]!["maxItems"]!.GetValue<int>());

        var create = TaskPayloadSchemas.Of(new CreateConfigFileTask())!;
        Assert.Equal(["parameters", "actionRules", "streamProfiles"], create["properties"]!.AsObject().Select(p => p.Key));

        Assert.NotNull(TaskPayloadSchemas.Of(new ExportConfigTask()));

        var import = TaskPayloadSchemas.Of(new ImportConfigTask())!;
        Assert.Equal(["fileId", "fileName", "sections"], import["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order());
        var areas = import["properties"]!["sections"]!["items"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
        Assert.Equal([.. ConfigSections.Known, ConfigSections.Connection], areas);
    }

    // ---- Configure devices ----

    [Fact]
    public void Configure_refuses_addresses_and_broken_values_at_their_fields_and_waits_for_entered_passwords()
    {
        var task = new ConfigureDevicesTask();
        var errors = Errors(task, Json(new JsonObject
        {
            ["settings"] = new JsonArray(
                new JsonObject { ["name"] = "Network.IPAddress", ["value"] = "10.0.0.99" },
                new JsonObject { ["name"] = "RemoteService.ProxyPassword", ["value"] = string.Empty },
                new JsonObject { ["name"] = "SNMP.V2ReadCommunity", ["value"] = string.Empty },
                new JsonObject { ["name"] = "Image.I0.Appearance.Compression", ["value"] = "3\n0" }),
        }));
        Assert.Equal(DangerousParameters.AddressNote, errors["settings[0].name"]);
        Assert.Equal("Line breaks and control characters are not allowed.", errors["settings[3].value"]);
        Assert.DoesNotContain("settings[1].value", errors.Keys); // the other problems first: the preview ignores the secrets' fields

        // A secret's value is entered by the person (the run's payload has it); an empty one is refused at its field only.
        var passwords = Errors(task, """{"settings":[{"name":"RemoteService.ProxyPassword","value":""},{"name":"SNMP.V2ReadCommunity","value":""}]}""");
        Assert.Equal(ConfigurePayload.EnterPassword, passwords["settings[0].value"]);
        Assert.Equal(ConfigurePayload.EnterPassword, passwords["settings[1].value"]);
        Assert.Empty(Errors(task, """{"settings":[{"name":"RemoteService.ProxyPassword","value":"entered"}]}"""));
        Assert.Equal(ConfigurePayload.TooManySecrets, Errors(task, Json(new JsonObject
        {
            ["settings"] = new JsonArray([.. Enumerable.Range(0, 9).Select(i => (JsonNode)new JsonObject { ["name"] = "Mqtt.Password" + i, ["value"] = string.Empty })]),
        }))["settings"]);

        Assert.Equal(ConfigurePayload.BadParameter, Errors(task, """{"settings":[{"name":"A.B","value":"1"},{"name":"A.B","value":"2"}]}""")["settings[1].name"]);
        Assert.Equal(ConfigurePayload.NothingToApply, Errors(task, """{"settings":[]}""")[string.Empty]);
        Assert.Equal(ConfigurePayload.NothingToApply, Errors(task, null)[string.Empty]);
        Assert.Equal(ConfigurePayload.BothForms, Errors(task, """{"settings":[{"name":"A.B","value":"1"}],"parameters":[{"name":"A.C","value":"1"}]}""")[string.Empty]);

        var rule = new JsonObject
        {
            ["name"] = "Mail",
            ["enabled"] = true,
            ["conditions"] = new JsonArray(),
            ["action"] = new JsonObject
            {
                ["name"] = "Mail",
                ["template"] = "com.axis.action.fixed.notification.smtp",
                ["parameters"] = new JsonArray(new JsonObject { ["name"] = "password", ["value"] = string.Empty }),
            },
        };
        Assert.Equal(ConfigurePayload.EnterPassword, Errors(task, Json(new JsonObject { ["actionRules"] = new JsonArray(rule) }))["actionRules[0].action.parameters[0].value"]);
        Assert.Equal(ConfigurePayload.BadRule, Errors(task, """{"actionRules":[{"name":"x","enabled":true,"conditions":null,"action":{"name":"a","template":"t","parameters":[]}}]}""")["actionRules[0]"]);
        Assert.Equal(ConfigurePayload.SameProfileName, Errors(task, """{"streamProfiles":[{"name":"A","parameters":"fps=1"},{"name":"A","parameters":"fps=2"}]}""")["streamProfiles[1].name"]);
    }

    [Fact]
    public void Configure_accepts_the_dialogs_payload_with_a_password_and_the_agents_without()
    {
        var task = new ConfigureDevicesTask();
        var dialog = new ConfigurePayload { Parameters = [new NameValue("RemoteService.ProxyPassword", "typed in the dialog")], Source = "10.0.0.48 (AXIS P3265-V)" };
        Assert.Empty(task.ValidatePayload(dialog.ToJson(), Caller));
        Assert.False(ConfigurePayload.Parse(dialog.ToJson()).FromAgent);

        var agent = """{"settings":[{"name":"ImageSource.I0.Sensor.CaptureFrequency","value":"50Hz"}],"streamProfiles":[{"name":"Night","description":"Low light","parameters":"fps=10"}]}""";
        Assert.Empty(task.ValidatePayload(agent, Caller));
        var parsed = ConfigurePayload.Parse(agent);
        Assert.True(parsed.FromAgent);
        Assert.Equal([new NameValue("ImageSource.I0.Sensor.CaptureFrequency", "50Hz")], parsed.Parameters);
        Assert.Equal("Configure 1 parameter, 1 profile", task.GetTaskName(agent));
    }

    [Fact]
    public async Task Configure_declares_a_secret_per_password_setting_and_action_parameter_and_plans_without_their_values()
    {
        var task = new ConfigureDevicesTask();
        var agent = """{"settings":[{"name":"Image.I0.Appearance.Compression","value":"30"},{"name":"RemoteService.ProxyPassword","value":""}],"actionRules":[{"name":"Mail","enabled":true,"conditions":[],"action":{"name":"Mail","template":"com.axis.action.fixed.notification.smtp","parameters":[{"name":"host","value":"mail"},{"name":"password","value":""}]}}]}""";
        Assert.Equal(
            [
                new TaskSecret("setting_1", "password for RemoteService.ProxyPassword", "settings[1].value"),
                new TaskSecret("rule_0_parameter_1", "password for password", "actionRules[0].action.parameters[1].value"),
            ],
            task.SecretsOf(agent));
        Assert.Empty(task.SecretsOf(new ConfigurePayload { Parameters = [new NameValue("RemoteService.ProxyPassword", "typed")] }.ToJson())); // the dialog's form
        Assert.Empty(task.SecretsOf("not json"));
        Assert.True(ConfigurePayload.SecretLabel("root." + new string('A', 80) + ".Password").Length <= 64);

        var plan = await task.PlanAsync(agent, [new FakeDevice()], CancellationToken.None);
        Assert.Null(Assert.Single(plan.Devices).SkipReason);
        Assert.Contains("Sets RemoteService.ProxyPassword, Mail: password with the values the user enters.", plan.Warnings);
    }

    [Fact]
    public async Task Configure_writes_an_entered_password_and_never_shows_it()
    {
        const string canary = "canary-config-pw-77";
        var camera = new ConfigureCamera();
        var ctx = new RecordingContext(camera, new MemoryUploads());
        await StepRun.RunAsync(ctx.Steps, () => new ConfigureDevicesTask().ExecuteAsync(ctx, new FakeDevice(),
            "{\"settings\":[{\"name\":\"RemoteService.ProxyPassword\",\"value\":\"" + canary + "\"}]}", CancellationToken.None));

        Assert.Equal(canary, camera.Values["RemoteService.ProxyPassword"]);
        Assert.DoesNotContain(canary, string.Join("|", StepRun.Lines(ctx.Steps)), StringComparison.Ordinal);
        Assert.DoesNotContain(canary, string.Join("|", ctx.Logs.Select(l => l.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public void Configure_needs_the_dangerous_right_for_connection_and_identity_settings()
    {
        var task = new ConfigureDevicesTask();
        Assert.Equal([ConfigurePermissions.Configure], task.RequiredPermissions("""{"settings":[{"name":"Image.I0.Appearance.Compression","value":"30"}]}"""));
        Assert.Equal([ConfigurePermissions.Configure, ConfigurePermissions.Dangerous], task.RequiredPermissions("""{"settings":[{"name":"Network.DNSServer1","value":"10.0.0.1"}]}"""));
        Assert.Equal([ConfigurePermissions.Configure, ConfigurePermissions.Dangerous], task.RequiredPermissions("""{"settings":[{"name":"HTTPS.Port","value":"8443"}]}"""));
    }

    [Fact]
    public async Task Configure_plans_from_the_cached_devices_with_the_dialogs_warnings()
    {
        var task = new ConfigureDevicesTask();
        var ok = new FakeDevice();
        var down = new FakeDevice { Serial = "B8A44F000002", Address = "10.0.0.49", Status = DeviceStatus.Unreachable };
        var noApi = new FakeDevice { Serial = "B8A44F000003", Address = "10.0.0.50", Apis = [new DeviceApi("time-service", "1.0")] };
        var plan = await task.PlanAsync("""{"settings":[{"name":"Network.DNSServer1","value":"10.0.0.1"},{"name":"HTTPS.Port","value":"8443"},{"name":"Image.I0.Appearance.Compression","value":"30"}],"streamProfiles":[{"name":"Night","parameters":"fps=10"}]}""",
            [ok, down, noApi], CancellationToken.None);

        Assert.Null(plan.Devices[0].SkipReason);
        Assert.NotNull(plan.Devices[1].SkipReason);
        Assert.Equal("The device has no parameter API", plan.Devices[2].SkipReason);
        Assert.Contains("Network.DNSServer1: " + DangerousParameters.IdentityReason, plan.Warnings);
        Assert.Contains("HTTPS.Port: " + DangerousParameters.ConnectionReason, plan.Warnings);
        Assert.Contains(ConfigureDevicesTask.CheckedOnDeviceNote, plan.Warnings);
        Assert.Contains(ConfigureDevicesTask.ProfilesReplacedNote, plan.Warnings);
        Assert.DoesNotContain(ConfigureDevicesTask.RulesReplacedNote, plan.Warnings);

        // A value that differs per device is never given to several devices (the dialog does not offer it there).
        var name = """{"settings":[{"name":"Network.HostName","value":"lobby"}]}""";
        var several = await task.PlanAsync(name, [ok, new FakeDevice { Serial = "B8A44F000004", Address = "10.0.0.51" }], CancellationToken.None);
        Assert.All(several.Devices, d => Assert.Equal(DangerousParameters.PerDeviceNote, d.SkipReason));
        var one = await task.PlanAsync(name, [ok], CancellationToken.None);
        Assert.Null(Assert.Single(one.Devices).SkipReason);
        Assert.Contains("Network.HostName: " + DangerousParameters.IdentityReason, one.Warnings);
    }

    [Fact]
    public async Task Configure_never_touches_the_device_with_an_invalid_agent_input()
    {
        var camera = new ConfigureCamera();
        var ctx = new RecordingContext(camera, new MemoryUploads());
        await Assert.ThrowsAsync<ArgumentException>(() => new ConfigureDevicesTask().ExecuteAsync(ctx, new FakeDevice(),
            """{"settings":[{"name":"RemoteService.ProxyPassword","value":""}]}""", CancellationToken.None)); // no password entered
        Assert.Empty(camera.Requests);
        Assert.Empty(camera.Updates);
    }

    [Fact]
    public async Task Configure_without_the_parameter_api_changes_nothing()
    {
        var camera = new ConfigureCamera { ApiList = [new DeviceApi("time-service", "1.0")] };
        var ctx = new RecordingContext(camera, new MemoryUploads());
        var error = await Assert.ThrowsAsync<ConfigDeviceException>(() => StepRun.RunAsync(ctx.Steps, () => new ConfigureDevicesTask().ExecuteAsync(ctx, new FakeDevice(),
            """{"settings":[{"name":"Image.I0.Appearance.Compression","value":"45"}]}""", CancellationToken.None)));
        Assert.EndsWith("Nothing was changed.", error.Message, StringComparison.Ordinal);
        Assert.Empty(camera.Updates);
        Assert.Equal("30", camera.Values["Image.I0.Appearance.Compression"]);
    }

    [Fact]
    public async Task Configure_sets_an_agents_parameters_and_skips_a_password_parameter_whatever_its_name()
    {
        var camera = new ConfigureCamera();
        // A password parameter whose name says nothing about it: the device's definition decides.
        var proxy = camera.DefinitionsXml.Descendants().Single(e => (string?)e.Attribute("name") == "ProxyPassword");
        proxy.SetAttributeValue("name", "ProxyKey");
        camera.Values["RemoteService.ProxyKey"] = string.Empty;
        var ctx = new RecordingContext(camera, new MemoryUploads());
        await StepRun.RunAsync(ctx.Steps, () => new ConfigureDevicesTask().ExecuteAsync(ctx, new FakeDevice(),
            """{"settings":[{"name":"Image.I0.Appearance.Compression","value":"45"},{"name":"RemoteService.ProxyKey","value":"abc"}]}""", CancellationToken.None));

        Assert.Equal("45", camera.Values["Image.I0.Appearance.Compression"]);
        Assert.Equal(string.Empty, camera.Values["RemoteService.ProxyKey"]);
        Assert.DoesNotContain(camera.Updates.SelectMany(u => u), p => p.Key == "RemoteService.ProxyKey");
        Assert.Contains("RemoteService.ProxyKey (Set the password in OADM)", StepRun.Detail(ctx.Steps, ConfigureDevicesTask.Steps.SetParameters), StringComparison.Ordinal);
    }

    // ---- Create configuration file ----

    [Fact]
    public void Create_checks_its_names_with_the_dialogs_text()
    {
        var task = new CreateConfigFileTask();
        Assert.Equal(CreateConfigFileRequest.NothingChosen, Errors(task, "{}")[string.Empty]);
        Assert.Equal(CreateConfigFileRequest.NothingChosen, Errors(task, null)[string.Empty]);
        Assert.Equal(CreateConfigFileRequest.NoSecrets, Errors(task, """{"parameters":["RemoteService.ProxyPassword"]}""")["parameters[0]"]);
        Assert.Equal(CreateConfigFileRequest.BadName, Errors(task, """{"parameters":["Image.","Image."]}""")["parameters[1]"]);
        Assert.Equal(CreateConfigFileRequest.BadName, Errors(task, """{"streamProfiles":[""]}""")["streamProfiles[0]"]);
        Assert.Empty(task.ValidatePayload("""{"parameters":["Image.I0.Appearance."],"actionRules":["Recording"]}""", Caller));
    }

    [Fact]
    public async Task Create_reads_the_device_and_keeps_the_file_as_the_result_without_writing_anything()
    {
        var camera = new ConfigureCamera();
        var rule = camera.Rules[0].Name;
        var (rules, actions, profiles) = (camera.Rules.Count, camera.Configurations.Count, camera.Profiles.Count);
        var ctx = new RecordingContext(camera, new MemoryUploads());
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        await StepRun.RunAsync(ctx.Steps, () => new CreateConfigFileTask(time).ExecuteAsync(ctx, new FakeDevice(),
            Json(new JsonObject { ["parameters"] = new JsonArray("Image.I0.Appearance.", "RemoteService.", "No.Such.Parameter"), ["actionRules"] = new JsonArray(rule) }), CancellationToken.None));

        Assert.Empty(camera.Updates);
        Assert.Equal((rules, actions, profiles), (camera.Rules.Count, camera.Configurations.Count, camera.Profiles.Count));
        var file = AdmConfigFile.Read(FileOf(ctx));
        Assert.Contains(file.Parameters, p => p.Name == "Image.I0.Appearance.Compression" && p.Value == "30");
        Assert.DoesNotContain(file.Parameters, p => SecretNames.IsSecret(p.Name)); // the proxy password stays out
        Assert.Contains(file.Parameters, p => p.Name.StartsWith("RemoteService.", StringComparison.Ordinal));
        Assert.Equal(rule, Assert.Single(file.Rules).Name);
        Assert.Equal("AXIS P3265-V", file.Model);
        Assert.Contains("1 not on this device: No.Such.Parameter", StepRun.Detail(ctx.Steps, CreateConfigFileTask.Steps.Create), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_fails_when_the_device_has_none_of_the_chosen_settings()
    {
        var camera = new ConfigureCamera();
        var ctx = new RecordingContext(camera, new MemoryUploads());
        var error = await Assert.ThrowsAsync<ConfigDeviceException>(() => StepRun.RunAsync(ctx.Steps, () => new CreateConfigFileTask().ExecuteAsync(ctx, new FakeDevice(),
            """{"streamProfiles":["No such profile"]}""", CancellationToken.None)));
        Assert.StartsWith("None of the chosen settings are on this device", error.Message, StringComparison.Ordinal);
        Assert.Empty(camera.Updates);
    }

    /// <summary>The task's result file (one per task, text), with its line in the task log and never its text there.</summary>
    private static string FileOf(RecordingContext ctx)
    {
        var (name, type, bytes) = ctx.ResultFile ?? throw new Xunit.Sdk.XunitException("The task kept no result file.");
        Assert.Equal("text/plain", type);
        Assert.DoesNotContain(ctx.Logs, l => l.Message.StartsWith("Part ", StringComparison.Ordinal));
        Assert.Contains(ctx.Logs, l => l.Message.StartsWith("File " + name + ":", StringComparison.Ordinal) && l.Message.Contains("SHA-256", StringComparison.Ordinal));
        return new System.Text.UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF');
    }

    /// <summary>The file of a task's log put together again from its parts (a host without result files).</summary>
    private static string LoggedFileOf(RecordingContext ctx)
    {
        Assert.Null(ctx.ResultFile);
        var parts = ctx.Logs.Select(l => l.Message).Where(m => m.StartsWith("Part ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(parts);
        Assert.All(parts, p => Assert.True(p.Length <= 2000));
        Assert.Contains(ctx.Logs, l => l.Message.StartsWith("File ", StringComparison.Ordinal) && l.Message.Contains("SHA-256", StringComparison.Ordinal));
        return string.Concat(parts.Select(p => p[(p.IndexOf(": ", StringComparison.Ordinal) + 2)..]));
    }

    // ---- Export configuration ----

    [Fact]
    public async Task Export_takes_no_settings_and_keeps_the_devices_export_as_the_result()
    {
        var task = new ExportConfigTask();
        Assert.Empty(task.ValidatePayload("{}", Caller));
        Assert.Empty(task.ValidatePayload(null, Caller));
        Assert.Equal(ExportConfigTask.NoSettings, Errors(task, """{"all":true}""")[string.Empty]);

        var camera = new ConfigCamera();
        var ctx = new RecordingContext(camera, new MemoryUploads());
        await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, new FakeDevice(), "{}", CancellationToken.None));
        var exported = JsonNode.Parse(FileOf(ctx))!;
        Assert.Equal("P3265-V", exported["metadata"]!["prodNbr"]!.GetValue<string>());
        Assert.Empty(camera.Imports);
        Assert.StartsWith("10.0.0.48_B8A44F631339_P3265-V_config.json, ", StepRun.Detail(ctx.Steps, ExportConfigTask.Steps.Export), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_on_a_host_without_result_files_writes_the_file_into_the_task_log()
    {
        var camera = new ConfigCamera();
        var ctx = new RecordingContext(camera, new MemoryUploads()) { KeepsResultFiles = false };
        await StepRun.RunAsync(ctx.Steps, () => new ExportConfigTask().ExecuteAsync(ctx, new FakeDevice(), "{}", CancellationToken.None));
        Assert.Equal("P3265-V", JsonNode.Parse(LoggedFileOf(ctx))!["metadata"]!["prodNbr"]!.GetValue<string>());
    }

    [Fact]
    public async Task Export_of_an_old_axis_os_asks_for_nothing_more()
    {
        var camera = new ConfigCamera { Version = "11.11.73" };
        var ctx = new RecordingContext(camera, new MemoryUploads());
        var error = await Assert.ThrowsAsync<ConfigDeviceException>(() => StepRun.RunAsync(ctx.Steps, () => new ExportConfigTask().ExecuteAsync(ctx, new FakeDevice(), null, CancellationToken.None)));
        Assert.StartsWith("Needs AXIS OS 12.5 or later", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, camera.Exports);
    }

    // ---- Import configuration ----

    private static string AgentImport(string fileId, params string[] sections) =>
        Json(new JsonObject { ["fileId"] = fileId, ["fileName"] = "lobby.json", ["sections"] = new JsonArray([.. sections.Select(s => (JsonNode)s)]) });

    [Fact]
    public void Import_checks_the_file_and_the_areas_at_their_fields()
    {
        var task = new ImportConfigTask();
        Assert.Empty(task.ValidatePayload(AgentImport("f1", ConfigSections.Param, "time.v2"), Caller));
        Assert.Equal(ImportPayload.ChooseArea, Errors(task, """{"fileId":"f1","fileName":"lobby.json","sections":[]}""")["sections"]);
        Assert.Equal(ImportPayload.NeedsFile, Errors(task, """{"fileId":"","fileName":"lobby.json","sections":["time.v2"]}""")["fileId"]);
        Assert.Equal(ConfigSections.UnknownReason + ".", Errors(task, AgentImport("f1", "firewall.v1"))["sections[0]"]);
        Assert.Equal(ImportPayload.OneFileOnly, Errors(task, """{"fileId":"f1","isZip":true,"sections":["time.v2"]}""")["fileId"]);
        Assert.Equal(ImportPayload.NeedsFile, Errors(task, null)["fileId"]);

        Assert.True(ImportPayload.Parse(AgentImport("f1", "time.v2")).ForEveryDevice);
        Assert.Equal(string.Empty, ImportPayload.Parse(AgentImport("f1", "time.v2")).EntryFor(Guid.NewGuid()));
        Assert.Equal([DeviceConfigPermissions.Import], task.RequiredPermissions(AgentImport("f1", "time.v2")));
        Assert.Equal([DeviceConfigPermissions.Import, DeviceConfigPermissions.Connection], task.RequiredPermissions(AgentImport("f1", ConfigSections.Param, ConfigSections.Connection)));
    }

    [Fact]
    public async Task Import_plans_every_model_with_the_dialogs_warnings()
    {
        var uploads = new MemoryUploads();
        var fileId = uploads.Add("lobby.json", Encoding.UTF8.GetBytes(Fixtures.Export));
        var same = new FakeDevice();
        var other = new FakeDevice { Serial = "B8A44F000002", Address = "10.0.0.49", Model = "AXIS M3215-LVE" };
        var old = new FakeDevice { Serial = "B8A44F000003", Address = "10.0.0.50", FirmwareVersion = "11.11.73" };
        var task = new ImportConfigTask();

        var plan = await task.PlanAsync(AgentImport(fileId, ConfigSections.Param, ConfigSections.Connection), [same, other, old], uploads, CancellationToken.None);
        Assert.Null(plan.Devices[0].SkipReason);
        Assert.Null(plan.Devices[1].SkipReason); // another model takes the settings it has
        Assert.StartsWith("Needs AXIS OS 12.5 or later", plan.Devices[2].SkipReason, StringComparison.Ordinal);
        Assert.Equal([ImportTexts.ReplaceNote, ImportTexts.ConnectionNote, ImportTexts.IdentityNote, ImportTexts.OtherModelNote(1, 2)], plan.Warnings);
        Assert.Equal("lobby.json", Assert.Single(plan.Files).Name);

        var calm = await task.PlanAsync(AgentImport(fileId, "time.v2"), [same], uploads, CancellationToken.None);
        Assert.Equal([ImportTexts.ReplaceNote], calm.Warnings);

        var gone = await task.PlanAsync(AgentImport("0123456789abcdef0123456789abcdef", "time.v2"), [same], uploads, CancellationToken.None);
        Assert.Equal(ImportTexts.FileGone, Assert.Single(gone.Devices).SkipReason);

        var brokenId = uploads.Add("broken.json", Encoding.UTF8.GetBytes("{\"data\":1}"));
        var broken = await task.PlanAsync(AgentImport(brokenId, "time.v2"), [same], uploads, CancellationToken.None);
        Assert.StartsWith(DeviceConfigIds.NotAConfigFile, Assert.Single(broken.Devices).SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_of_an_agent_gives_every_device_the_one_file_and_never_reads_an_invalid_one()
    {
        var uploads = new MemoryUploads();
        var export = (JsonObject)JsonNode.Parse(Fixtures.Export)!;
        export["data"]!["time.v2"]!["timeZone"]!["posix"]!["timeZone"] = "CET-1CEST,M3.5.0,M10.5.0/3";
        var fileId = uploads.Add("lobby.json", Encoding.UTF8.GetBytes(export.ToJsonString()));
        var camera = new ConfigCamera();
        var ctx = new RecordingContext(camera, uploads);
        await StepRun.RunAsync(ctx.Steps, () => new ImportConfigTask().ExecuteAsync(ctx, new FakeDevice(), AgentImport(fileId, "time.v2"), CancellationToken.None));
        Assert.Equal("time.v2", Assert.Single(camera.Imports).Key);

        var untouched = new ConfigCamera();
        var refused = new RecordingContext(untouched, uploads);
        await Assert.ThrowsAsync<ArgumentException>(() => new ImportConfigTask().ExecuteAsync(refused, new FakeDevice(), """{"fileId":"","sections":["time.v2"]}""", CancellationToken.None));
        Assert.Empty(untouched.Requests);
    }
}
