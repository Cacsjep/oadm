using System.Text;
using System.Xml.Linq;

using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>Configure devices on the server: parameter definitions, ADM files, the action engine and the task.</summary>
public sealed class ConfigureTests
{
    private static ParamDefinition Def(string name) =>
        ParamDefinitionsReader.Parse(ConfigureFixtures.Read("listdefinitions-all.xml")).Single(p => p.Name == name);

    [Fact]
    public void Definitions_have_types_ranges_choices_and_values_and_leave_out_what_cannot_be_configured()
    {
        var all = ParamDefinitionsReader.Parse(ConfigureFixtures.Read("listdefinitions-all.xml"));

        Assert.Equal(ConfigureFixtures.ConfigurableParameters, all.Count);
        Assert.Equal((ParamKind.Number, 0L, 100L, "30", "Compression"), (Def("Image.I0.Appearance.Compression").Kind, Def("Image.I0.Appearance.Compression").Min!.Value, Def("Image.I0.Appearance.Compression").Max!.Value, Def("Image.I0.Appearance.Compression").Value, Def("Image.I0.Appearance.Compression").DisplayName));
        var color = Def("Image.I0.Appearance.ColorEnabled");
        Assert.Equal((ParamKind.Enum, "Color enabled"), (color.Kind, color.DisplayName));
        Assert.Equal(["Black and White", "Color"], color.Choices.Select(c => c.Display));
        var mirror = Def("Image.I0.Appearance.MirrorEnabled");
        Assert.Equal((ParamKind.Bool, "yes", "no"), (mirror.Kind, mirror.TrueValue, mirror.FalseValue));
        Assert.Equal(("Syslog format", "RFC5424"), (Def("API.RemoteSyslog1.Server1.SyslogFormat").DisplayName, Def("API.RemoteSyslog1.Server1.SyslogFormat").Value));
        Assert.Equal((ParamKind.Password, ""), (Def("RemoteService.ProxyPassword").Kind, Def("RemoteService.ProxyPassword").Value));
        // Device facts nobody may write (write level 7) are not configurable; a real setting in Properties is.
        Assert.DoesNotContain(all, p => p.Name is "Properties.Firmware.Version" or "Properties.System.SerialNumber" or "Brand.ProdNbr");
        Assert.Contains(all, p => p.Name == "Properties.Image.I0.ShowSuboptimalResolutions");
        Assert.Equal("Image.I0.Appearance", Def("Image.I0.Appearance.Compression").Group);
    }

    [Fact]
    public void Values_are_checked_against_the_definition()
    {
        var compression = Def("Image.I0.Appearance.Compression");
        Assert.Null(ParamRules.Problem(compression, "30"));
        Assert.Equal("Enter 0 to 100.", ParamRules.Problem(compression, "101"));
        Assert.Equal("Enter a whole number.", ParamRules.Problem(compression, "high"));
        Assert.Equal("0 to 100", ParamRules.Hint(compression));
        Assert.Equal("Choose a value from the list.", ParamRules.Problem(Def("Image.I0.Appearance.ColorEnabled"), "red"));
        Assert.Null(ParamRules.Problem(Def("Image.I0.Appearance.MirrorEnabled"), "yes"));
        Assert.Equal("Enter an IP address.", ParamRules.Problem(Def("Network.DNSServer1"), "1.2.3"));
        Assert.Null(ParamRules.Problem(Def("Network.DNSServer1"), "10.0.0.1"));
        // Several NTP servers are one value, separated by spaces, as the device stores them.
        Assert.Null(ParamRules.Problem(Def("Time.NTP.Server"), "10.0.0.17 ts1.univie.ac.at"));
        Assert.Null(ParamRules.Problem(Def("Time.NTP.Server"), "ts1.univie.ac.at"));
        Assert.Equal("Host name or IP address, several separated by spaces", ParamRules.Hint(Def("Time.NTP.Server")));
        Assert.Equal("Enter a host name or an IP address.", ParamRules.Problem(Def("Time.NTP.Server"), "10.0.0.17 bad_name"));
        Assert.Equal("Enter the password.", ParamRules.Problem(Def("RemoteService.ProxyPassword"), string.Empty));
        Assert.Equal(DangerousParameters.ConnectionReason, DangerousParameters.Reason("Network.Filter.Enabled"));
        Assert.Equal("Line breaks and control characters are not allowed.", ParamRules.Problem(Def("API.RemoteSyslog1.Server1.Address"), "a\nb"));

        Assert.Equal(DangerousParameters.IdentityReason, DangerousParameters.Reason("Network.IPAddress"));
        Assert.Equal(DangerousParameters.IdentityReason, DangerousParameters.Reason("Network.IPv6.IPAddress"));
        Assert.Equal(DangerousParameters.ConnectionReason, DangerousParameters.Reason("HTTPS.Port"));
        Assert.Equal(DangerousParameters.ConnectionReason, DangerousParameters.Reason("Network.HTTP.AuthenticationPolicy"));
        Assert.Null(DangerousParameters.Reason("Network.UPnP.Enabled"));
        Assert.Null(DangerousParameters.Reason("Image.I0.Appearance.Compression"));

        // The web server decides how OADM reaches the device: its ports, HTTP or HTTPS, HTTP/2, the web interface, RTSPS.
        foreach (var name in new[] { "System.BoaPort", "System.AlternateBoaPort", "System.BoaGroupPolicy.admin", "System.HTTP2.Enabled", "System.WebInterfaceDisabled", "Network.RTSPS.Enabled" })
        {
            Assert.Equal(DangerousParameters.ConnectionReason, DangerousParameters.Reason(name));
        }

        // Addresses are never written here (Network settings checks them and follows the device).
        Assert.True(DangerousParameters.IsAddress("Network.IPAddress") && DangerousParameters.IsAddress("Network.IPv6.IPAddress"));
        Assert.False(DangerousParameters.IsAddress("Network.DNSServer1"));
    }

    [Fact]
    public void An_ADM_file_is_read_and_written_back_byte_for_byte()
    {
        var original = ConfigureFixtures.Bytes("adm-p3265v-12.11.77.cfg");
        var config = AdmConfigFile.Read(Encoding.UTF8.GetString(original));

        Assert.Equal(("AXIS P3265-V", "12.11.77", "B8A44F631339"), (config.Model, config.FirmwareVersion, config.MacAddress));
        Assert.Equal(["ImageSource.I0.CaptureFrequency", "ImageSource.I0.Sensor.CaptureMode", "System.CaptureFrequencySet", "System.CaptureModeSet"], config.Parameters.Select(p => p.Name));
        Assert.Equal(5, config.Schedules.Count);
        Assert.Equal("DTSTART:19700101T000000\nRRULE:FREQ=SECONDLY;INTERVAL=10", config.Schedules[0].Definition);
        Assert.Equal(4, config.Schedules.Count(s => s.IsBuiltIn));
        var rule = Assert.Single(config.Rules);
        Assert.Equal(("re", true, "com.axis.action.fixed.recording.storage", "Record video"), (rule.Name, rule.Enabled, rule.Action.Template, rule.Action.Name));
        Assert.Equal("&streamprofile=&camera=1", rule.Action.Parameters.Single(p => p.Name == "stream_options").Value);
        Assert.Equal("tns1:UserAlarm/tnsaxis:Recurring/Pulse", rule.StartEvent!.Topic);
        Assert.Equal(["com.axis.schedules.genid.id-0"], rule.ScheduleIds);
        Assert.Equal(["View Area 1_ACS_Pro_High", "View Area 1_ACS_Pro_Low", "View Area 1_ACS_Pro_Medium"], config.StreamProfiles.Select(p => p.Name));

        Assert.Equal(original, AdmConfigFile.Write(config));

        var old = AdmConfigFile.Read(ConfigureFixtures.Read("adm-m3206lve-10.2.0.cfg"));
        Assert.Equal(("AXIS M3206-LVE", 2, 0), (old.Model, old.Parameters.Count, old.Rules.Count));
    }

    [Fact]
    public void Other_files_are_refused_and_a_generic_template_is_read()
    {
        Assert.Equal(AdmConfigFile.NotAnAdmFile, Assert.Throws<FormatException>(() => AdmConfigFile.Read("""{"data":{}}""")).Message);
        Assert.Equal(AdmConfigFile.NotAnAdmFile, Assert.Throws<FormatException>(() => AdmConfigFile.Read("<other/>")).Message);
        var generic = AdmConfigFile.Read("""
            <?xml version="1.0" encoding="utf-8"?>
            <AcmDeviceParameterExport><FileFormatVersion>3</FileFormatVersion><Model>Generic</Model>
              <ParameterList><Parameter><Name>Network.UPnP.Enabled</Name><Value>no</Value></Parameter></ParameterList>
            </AcmDeviceParameterExport>
            """);
        Assert.True(generic.IsGeneric);
        Assert.Equal([new NameValue("Network.UPnP.Enabled", "no")], generic.Parameters);
        Assert.Equal("Configuration - AXIS P3265-V 12.11.77.cfg", AdmConfigFile.SuggestedFileName("P3265-V", "12.11.77"));
    }

    [Fact]
    public void Rules_configurations_and_schedules_are_read_from_the_device_answers()
    {
        var configurations = ActionEngineApi.ParseConfigurations(XDocument.Parse(ConfigureFixtures.Read("action-configurations.xml")));
        var rule = Assert.Single(ActionEngineApi.ParseRules(XDocument.Parse(ConfigureFixtures.Read("action-rules.xml")), configurations));
        Assert.Equal(("re", "3", 4), (rule.Name, rule.Action.Id, rule.Action.Parameters.Count));
        Assert.Equal("boolean(//SimpleItem[@Name=\"id\" and @Value=\"com.axis.schedules.genid.id-0\"])", rule.StartEvent!.Message);
        var schedules = ActionEngineApi.ParseSchedules(XDocument.Parse(ConfigureFixtures.Read("scheduled-events.xml")));
        Assert.Equal(["com.axis.schedules.genid.id-0", "com.axis.schedules.after_hours", "com.axis.schedules.office_hours", "com.axis.schedules.weekdays", "com.axis.schedules.weekends"], schedules.Select(s => s.Id));

        Assert.Equal(
            "boolean(//SimpleItem[@Name=\"id\" and @Value=\"com.axis.schedules.genid.id-7\"])",
            ScheduleRefs.Rewrite(rule.StartEvent.Message, new Dictionary<string, string> { ["com.axis.schedules.genid.id-0"] = "com.axis.schedules.genid.id-7" }));
    }

    [Fact]
    public async Task The_read_query_returns_parameters_with_values_rules_schedules_and_stream_profiles()
    {
        var camera = new ConfigureCamera();
        camera.Values["Image.I0.Appearance.Compression"] = "40";
        var settings = DeviceSettings.Parse(await new ConfigureDevicesTask().QueryAsync(new QueryContext(camera), new FakeDevice(), ConfigureIds.ReadQuery, null, CancellationToken.None));

        Assert.Null(settings.Error);
        Assert.Equal(("P3265-V", "12.11.77"), (settings.Model, settings.Firmware));
        Assert.Equal(ConfigureFixtures.ConfigurableParameters, settings.Parameters.Count);
        Assert.Equal("40", settings.Parameters.Single(p => p.Name == "Image.I0.Appearance.Compression").Value);
        Assert.Equal("re", Assert.Single(settings.Rules).Name);
        Assert.Equal(5, settings.Schedules.Count);
        Assert.Equal(3, settings.StreamProfiles.Count);
        Assert.Empty(settings.Problems);

        // The whole tree fits the XML limit: one request.
        Assert.Equal(1, camera.Requests.Count(r => r.Contains("listdefinitions", StringComparison.Ordinal)));

        // Larger than the limit (a multi-sensor camera): one request per top-level group, the same parameters.
        var large = new ConfigureCamera { OversizeWholeTree = true };
        DeviceSettingsReader.ClearCache();
        var grouped = DeviceSettings.Parse(await new ConfigureDevicesTask().QueryAsync(new QueryContext(large), new FakeDevice(), ConfigureIds.ReadQuery, null, CancellationToken.None));
        Assert.Equal(ConfigureFixtures.ConfigurableParameters, grouped.Parameters.Count);
        Assert.Equal(1 + 29, large.Requests.Count(r => r.Contains("listdefinitions", StringComparison.Ordinal)));

        var refused = DeviceSettings.Parse(await new ConfigureDevicesTask().QueryAsync(new QueryContext(camera), new FakeDevice { Status = DeviceStatus.CredentialsRequired }, ConfigureIds.ReadQuery, null, CancellationToken.None));
        Assert.NotNull(refused.Error);
    }

    private static ConfigurePayload SourcePayload(ConfigureCamera source, params NameValue[] parameters)
    {
        var file = AdmConfigFile.Read(ConfigureFixtures.Read("adm-p3265v-12.11.77.cfg"));
        return new ConfigurePayload
        {
            Parameters = parameters,
            Rules = file.Rules,
            Schedules = [.. file.Schedules.Where(s => !s.IsBuiltIn)],
            StreamProfiles = [file.StreamProfiles[0] with { Parameters = "camera=1&videocodec=h265&fps=25" }, new ConfigStreamProfile("Night", "Low light", "camera=1&fps=10")],
            Source = "10.0.0.48 (AXIS P3265-V)",
        };
    }

    private static async Task<(RecordingContext Ctx, Exception? Error)> RunAsync(ConfigureCamera camera, ConfigurePayload payload, IDeviceInfo? device = null)
    {
        var ctx = new RecordingContext(camera, new MemoryUploads());
        try
        {
            await StepRun.RunAsync(ctx.Steps, () => new ConfigureDevicesTask().ExecuteAsync(ctx, device ?? new FakeDevice(), payload.ToJson(), CancellationToken.None));
            return (ctx, null);
        }
        catch (ConfigDeviceException ex)
        {
            return (ctx, ex);
        }
    }

    [Fact]
    public async Task Parameters_rules_and_profiles_are_applied_and_what_the_device_lacks_is_skipped()
    {
        var camera = new ConfigureCamera();
        var payload = SourcePayload(camera,
            new NameValue("Image.I0.Appearance.Compression", "45"),
            new NameValue("Network.UPnP.Enabled", "yes"),
            new NameValue("Network.AxisNS.Enabled", "no"),               // AXIS OS 12 has no such parameter
            new NameValue("Image.I0.Appearance.ColorEnabled", "sepia"),  // not a choice of this device
            new NameValue("RemoteService.ProxyPassword", "s3cret!"));
        var (ctx, error) = await RunAsync(camera, payload);

        Assert.Null(error);
        Assert.Equal(
        [
            "Check compatibility: Done",
            "Set parameters: Warning",
            "Create schedules: Done",
            "Create action rules: Done",
            "Set stream profiles: Done",
            "Verify parameters: Done",
            "Completed: Done",
        ], StepRun.Lines(ctx.Steps));
        Assert.Equal("AXIS P3265-V, AXIS OS 12.11.77", StepRun.Detail(ctx.Steps, "Check compatibility"));
        Assert.Equal("3 parameters set, 2 skipped: Network.AxisNS.Enabled (not on this device); Image.I0.Appearance.ColorEnabled (Choose a value from the list)",
            StepRun.Detail(ctx.Steps, "Set parameters"));
        Assert.Equal(("45", "yes", "s3cret!"), (camera.Values["Image.I0.Appearance.Compression"], camera.Values["Network.UPnP.Enabled"], camera.Values["RemoteService.ProxyPassword"]));
        Assert.Single(camera.Updates); // one request for all of them
        Assert.DoesNotContain(ctx.Logs, l => l.Message.Contains("s3cret", StringComparison.Ordinal));

        // The schedule exists already with the same name and recurrence: used again, the rule replaced (same name).
        Assert.Equal("1 already there", StepRun.Detail(ctx.Steps, "Create schedules"));
        Assert.Equal("1 replaced", StepRun.Detail(ctx.Steps, "Create action rules"));
        var rule = Assert.Single(camera.Rules);
        Assert.Equal("re", rule.Name);
        Assert.Contains("com.axis.schedules.genid.id-0", rule.StartEventXml, StringComparison.Ordinal);
        Assert.Single(camera.Configurations); // the old action went with the old rule
        Assert.Equal(5, camera.Schedules.Count);

        Assert.Equal("1 stream profile created, 1 replaced", StepRun.Detail(ctx.Steps, "Set stream profiles"));
        Assert.Equal("camera=1&videocodec=h265&fps=25", camera.Profiles.Single(p => p.Name == "View Area 1_ACS_Pro_High").Parameters);
        Assert.Contains(camera.Profiles, p => p.Name == "Night");
        Assert.Equal("All set values match", StepRun.Detail(ctx.Steps, "Verify parameters"));

        // Running the same again changes nothing more: no copies of rules, schedules or profiles.
        var (again, _) = await RunAsync(camera, payload);
        Assert.Single(camera.Rules);
        Assert.Equal(5, camera.Schedules.Count);
        Assert.Equal(4, camera.Profiles.Count);
        Assert.Equal("2 replaced", StepRun.Detail(again.Steps, "Set stream profiles"));
    }

    [Fact]
    public async Task The_source_device_keeps_its_own_rules_schedules_and_profiles_and_gets_the_parameters()
    {
        var camera = new ConfigureCamera();
        var device = new FakeDevice();
        var rulesBefore = camera.Rules.Select(r => r.Id).ToList();
        var payload = SourcePayload(camera, new NameValue("Image.I0.Appearance.Compression", "45")) with { SourceDeviceId = device.Id };
        var (ctx, error) = await RunAsync(camera, payload, device);

        Assert.Null(error);
        Assert.Equal(
        [
            "Check compatibility: Done",
            "Set parameters: Done",
            "Create schedules: Skipped",
            "Create action rules: Skipped",
            "Set stream profiles: Skipped",
            "Verify parameters: Done",
            "Completed: Done",
        ], StepRun.Lines(ctx.Steps));
        Assert.Equal(ConfigureDevicesTask.Steps.IsSource, StepRun.Detail(ctx.Steps, "Create action rules"));
        Assert.Equal("45", camera.Values["Image.I0.Appearance.Compression"]);
        Assert.Equal(rulesBefore, camera.Rules.Select(r => r.Id)); // the rule was not removed and created again
        Assert.DoesNotContain(camera.Requests, r => r.Contains("streamprofile", StringComparison.Ordinal));

        // Another device of the same run gets everything.
        var other = new ConfigureCamera();
        var (otherCtx, _) = await RunAsync(other, payload);
        Assert.Equal("Done", otherCtx.Steps.Snapshot().Single(s => s.Name == "Create action rules").State.ToString());
    }

    [Fact]
    public async Task A_new_schedule_gets_the_devices_id_and_the_rule_points_to_it()
    {
        var camera = new ConfigureCamera();
        camera.Schedules.RemoveAll(s => !s.IsBuiltIn);
        camera.Rules.Clear();
        camera.Configurations.Clear();
        var (ctx, error) = await RunAsync(camera, SourcePayload(camera) with { StreamProfiles = [] });

        Assert.Null(error);
        Assert.Equal("1 schedule created", StepRun.Detail(ctx.Steps, "Create schedules"));
        Assert.Equal("1 action rule created", StepRun.Detail(ctx.Steps, "Create action rules"));
        var created = camera.Schedules.Single(s => !s.IsBuiltIn);
        Assert.NotEqual("com.axis.schedules.genid.id-0", created.Id);
        Assert.Contains(created.Id, Assert.Single(camera.Rules).StartEventXml, StringComparison.Ordinal);
        Assert.Equal("com.axis.action.fixed.recording.storage", Assert.Single(camera.Configurations).Template);
    }

    [Fact]
    public async Task Dangerous_parameters_need_their_own_right_and_are_written_last()
    {
        var camera = new ConfigureCamera();
        var payload = new ConfigurePayload { Parameters = [new NameValue("HTTPS.Port", "8443"), new NameValue("Image.I0.Appearance.Compression", "50")] };
        var plugin = new ConfigureDevicesTask();
        Assert.Equal([ConfigurePermissions.Configure, ConfigurePermissions.Dangerous], plugin.RequiredPermissions(payload.ToJson()));
        Assert.Equal([ConfigurePermissions.Configure], plugin.RequiredPermissions((payload with { Parameters = [payload.Parameters[1]] }).ToJson()));
        Assert.False(plugin.Permissions.Single(p => p.Key == ConfigurePermissions.Dangerous).OperatorDefault);

        var (ctx, error) = await RunAsync(camera, payload);
        Assert.Null(error);
        Assert.Equal(["Check compatibility: Done", "Set parameters: Done", "Set connection parameters: Done", "Verify parameters: Done", "Completed: Done"], StepRun.Lines(ctx.Steps));
        Assert.Equal(["Image.I0.Appearance.Compression", "HTTPS.Port"], camera.Updates.Select(u => u.Single().Key));
        Assert.Equal("Configure 2 parameters", plugin.GetTaskName(payload.ToJson()));
    }

    [Fact]
    public async Task A_refused_parameter_is_skipped_and_the_rest_set_and_too_many_profiles_change_none()
    {
        var camera = new ConfigureCamera { MaxProfiles = 4 };
        camera.Refuse.Add("Image.I0.Appearance.Compression");
        var payload = SourcePayload(camera, new NameValue("Image.I0.Appearance.Compression", "45"), new NameValue("Network.UPnP.Enabled", "yes")) with
        {
            StreamProfiles = [new ConfigStreamProfile("A", null, "fps=1"), new ConfigStreamProfile("B", null, "fps=2")],
        };
        var (ctx, error) = await RunAsync(camera, payload);

        Assert.Equal("1 parameter set, 1 skipped: Image.I0.Appearance.Compression (the device refused it)", StepRun.Detail(ctx.Steps, "Set parameters"));
        Assert.Equal(("yes", "30"), (camera.Values["Network.UPnP.Enabled"], camera.Values["Image.I0.Appearance.Compression"]));
        Assert.Equal("Done", ctx.Steps.Snapshot().Single(s => s.Name == "Create action rules").State.ToString()); // the others still ran
        Assert.NotNull(error);
        Assert.StartsWith("Set stream profiles: The device has room for 4 stream profiles", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, camera.Profiles.Count);
    }

    [Fact]
    public void Schedule_ids_are_rewritten_in_one_pass_even_when_a_new_id_is_another_old_one()
    {
        // The device numbers own schedules: the source's id-0 and id-1 become id-1 and id-2 on a device that has an id-0.
        var map = new Dictionary<string, string> { ["com.axis.schedules.genid.id-0"] = "com.axis.schedules.genid.id-1", ["com.axis.schedules.genid.id-1"] = "com.axis.schedules.genid.id-2" };
        Assert.Equal(
            "boolean(//SimpleItem[@Value=\"com.axis.schedules.genid.id-1\"]) and boolean(//SimpleItem[@Value=\"com.axis.schedules.genid.id-2\"])",
            ScheduleRefs.Rewrite("boolean(//SimpleItem[@Value=\"com.axis.schedules.genid.id-0\"]) and boolean(//SimpleItem[@Value=\"com.axis.schedules.genid.id-1\"])", map));
    }

    private static ConfigActionRule Rule(string name, string scheduleId, string template = "com.axis.action.fixed.recording.storage") =>
        new(name, true, new ConfigEventFilter("tns1:UserAlarm/tnsaxis:Recurring/Pulse", $"boolean(//SimpleItem[@Name=\"id\" and @Value=\"{scheduleId}\"])"), [],
            new ConfigAction("Record " + name, template, [new NameValue("storage_id", "SD_DISK")]));

    [Fact]
    public async Task Two_new_schedules_on_a_device_with_its_own_id_0_are_pointed_to_correctly()
    {
        var camera = new ConfigureCamera(); // has its own com.axis.schedules.genid.id-0 ("10")
        var payload = new ConfigurePayload
        {
            Schedules = [new ConfigSchedule("com.axis.schedules.genid.id-0", "Day", "DTSTART:19700101T080000\nRRULE:FREQ=DAILY"), new ConfigSchedule("com.axis.schedules.genid.id-1", "Night", "DTSTART:19700101T200000\nRRULE:FREQ=DAILY")],
            Rules = [Rule("Day rule", "com.axis.schedules.genid.id-0"), Rule("Night rule", "com.axis.schedules.genid.id-1")],
        };
        var (_, error) = await RunAsync(camera, payload);

        Assert.Null(error);
        var day = camera.Schedules.Single(s => s.Name == "Day").Id;
        var night = camera.Schedules.Single(s => s.Name == "Night").Id;
        Assert.Equal(("com.axis.schedules.genid.id-1", "com.axis.schedules.genid.id-2"), (day, night));
        Assert.Contains("\"" + day + "\"", camera.Rules.Single(r => r.Name == "Day rule").StartEventXml, StringComparison.Ordinal);
        Assert.Contains("\"" + night + "\"", camera.Rules.Single(r => r.Name == "Night rule").StartEventXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rule_the_device_refuses_keeps_the_old_one_and_the_other_rules_are_still_written()
    {
        var camera = new ConfigureCamera();
        camera.RefuseTemplates.Add("com.axis.action.unknown");
        var configurations = camera.Configurations.Count;
        var payload = new ConfigurePayload
        {
            Schedules = [new ConfigSchedule("com.axis.schedules.genid.id-0", "10", "DTSTART:19700101T000000\nRRULE:FREQ=SECONDLY;INTERVAL=10")],
            Rules = [Rule("re", "com.axis.schedules.genid.id-0", "com.axis.action.unknown"), Rule("New rule", "com.axis.schedules.genid.id-0")],
        };
        var (ctx, error) = await RunAsync(camera, payload);

        Assert.NotNull(error);
        Assert.Equal("Create action rules: 1 action rule created, 1 action rule failed: re (The device refused: Invalid template token)", error.Message);
        Assert.Equal(["re", "New rule"], camera.Rules.Select(r => r.Name)); // the old "re" is still there
        Assert.Equal(configurations + 1, camera.Configurations.Count); // no action left behind by the refused rule
        Assert.Equal("Failed", ctx.Steps.Snapshot().Single(s => s.Name == "Create action rules").State.ToString());
    }

    [Fact]
    public async Task Rules_whose_schedule_could_not_be_created_are_not_written()
    {
        var camera = new ConfigureCamera { FailSchedules = true };
        camera.Schedules.RemoveAll(s => !s.IsBuiltIn);
        camera.Rules.Clear();
        var (ctx, error) = await RunAsync(camera, SourcePayload(camera) with { StreamProfiles = [] });

        Assert.NotNull(error);
        Assert.Equal("Warning", ctx.Steps.Snapshot().Single(s => s.Name == "Create action rules").State.ToString());
        Assert.Equal("1 skipped: re (its schedule could not be created)", StepRun.Detail(ctx.Steps, "Create action rules"));
        Assert.Empty(camera.Rules);
    }

    [Fact]
    public async Task Values_with_spaces_plus_signs_and_line_breaks_arrive_unchanged()
    {
        var camera = new ConfigureCamera();
        camera.Rules.Clear();
        var payload = new ConfigurePayload
        {
            Parameters = [new NameValue("API.RemoteSyslog1.Server1.Address", "log server + 1")],
            Schedules = [new ConfigSchedule("com.axis.schedules.genid.id-0", "10", "DTSTART:19700101T000000\nRRULE:FREQ=SECONDLY;INTERVAL=10")],
            Rules = [Rule("Mail", "com.axis.schedules.genid.id-0") with { Action = new ConfigAction("Mail", "com.axis.action.fixed.recording.storage", [new NameValue("message", "line 1\nline 2\tend")]) }],
        };
        var (_, error) = await RunAsync(camera, payload);

        Assert.Null(error);
        Assert.Equal("log server + 1", camera.Values["API.RemoteSyslog1.Server1.Address"]);
        Assert.Equal("line 1\nline 2\tend", camera.Configurations.Single(c => c.Name == "Mail").Parameters.Single().Value);
    }

    [Fact]
    public async Task Many_parameters_are_read_back_in_small_requests()
    {
        var camera = new ConfigureCamera();
        var all = ParamDefinitionsReader.Parse(ConfigureFixtures.Read("listdefinitions-all.xml"))
            .Where(d => !DangerousParameters.IsDangerous(d.Name) && d.Kind != ParamKind.Password)
            .Select(d => new NameValue(d.Name, camera.Values[d.Name]))
            .ToList();
        var (ctx, error) = await RunAsync(camera, new ConfigurePayload { Parameters = all });

        Assert.Null(error);
        Assert.Equal("All set values match", StepRun.Detail(ctx.Steps, "Verify parameters"));
        Assert.True(camera.Requests.Count(r => r == "GET param.cgi list") > 10);
        Assert.All(ConfigureDevicesTask.Chunks(all.Select(p => p.Name), ConfigureDevicesTask.MaxVerifyUrlCharacters),
            chunk => Assert.True(string.Join(',', chunk).Length <= ConfigureDevicesTask.MaxVerifyUrlCharacters));
    }

    [Fact]
    public void Payloads_are_checked_and_name_the_task()
    {
        var rule = Rule("re", "com.axis.schedules.genid.id-0");
        Assert.Equal("An action rule uses a schedule that is not included (com.axis.schedules.genid.id-0).",
            Assert.Throws<ArgumentException>(() => ConfigurePayload.Parse(new ConfigurePayload { Rules = [rule] }.ToJson())).Message);
        var schedule = new ConfigSchedule("com.axis.schedules.genid.id-0", "10", "DTSTART:19700101T000000");
        Assert.Equal("Two action rules have the same name.",
            Assert.Throws<ArgumentException>(() => ConfigurePayload.Parse(new ConfigurePayload { Rules = [rule, rule], Schedules = [schedule] }.ToJson())).Message);
        Assert.Equal("Two stream profiles have the same name.",
            Assert.Throws<ArgumentException>(() => ConfigurePayload.Parse(new ConfigurePayload { StreamProfiles = [new("A", null, "fps=1"), new("A", null, "fps=2")] }.ToJson())).Message);
        Assert.Throws<ArgumentException>(() => ConfigurePayload.Parse("""{"rules":[{"name":"x","enabled":true,"conditions":null,"action":{"name":"a","template":"t","parameters":[]}}]}"""));
        Assert.Throws<ArgumentException>(() => ConfigurePayload.Parse(null));
        Assert.Throws<ArgumentException>(() => ConfigurePayload.Parse(new ConfigurePayload().ToJson()));
        Assert.Throws<ArgumentException>(() => ConfigurePayload.Parse(new ConfigurePayload { Parameters = [new("A", "1"), new("A", "2")] }.ToJson()));
        var full = SourcePayload(new ConfigureCamera(), new NameValue("A.B", "1"));
        Assert.Equal("Configure 1 parameter, 1 rule, 2 profiles", ConfigurePayload.Parse(full.ToJson()).TaskName());
        Assert.True(TaskPluginNames.MaxTaskNameLength >= full.TaskName().Length);
    }
}
