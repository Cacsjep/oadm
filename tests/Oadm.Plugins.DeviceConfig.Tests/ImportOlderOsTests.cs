using System.Text;
using System.Text.Json.Nodes;

using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>
/// Import configuration on a camera below AXIS OS 12.5 (no configuration API): the file's parameters through param.cgi with
/// the checks of Configure devices, every other area skipped with a warning. The camera answers like 10.0.0.48 from the
/// recorded param.cgi answers (<see cref="ConfigureCamera"/>, which fails the test on any other request, the configuration
/// API included).
/// </summary>
public sealed class ImportOlderOsTests
{
    private static readonly string[] NodeSections = [ConfigSections.Param, ConfigSections.ActionRules, "event-schedules.v2"];

    /// <summary>The recorded export with a few changed values, the identity and connection values changed too.</summary>
    private static string File()
    {
        var export = (JsonObject)JsonNode.Parse(Fixtures.Export)!;
        var param = (JsonObject)export["data"]!["param.v2"]!;
        param["ImageCollection"]![0]!["Name"] = "Lobby";
        param["ImageCollection"]![0]!["Appearance"]!["Compression"] = "35";
        param["Network"]!["HostName"] = "lobby-cam";
        param["Network"]!["IPAddress"] = "10.0.0.99";
        param["Network"]!["DNSServer1"] = "10.0.0.1";
        param["HTTPS"]!["Enabled"] = "no";
        param["System"]!["BoaPort"] = "8080";
        param["Time"]!["POSIXTimeZone"] = "CET-1CEST,M3.5.0,M10.5.0/3";
        return export.ToJsonString();
    }

    private static (RecordingContext Ctx, ConfigureCamera Camera, FakeDevice Device, string Payload) Setup(IReadOnlyList<string> sections, string? file = null, Action<ConfigureCamera>? camera = null)
    {
        var device = new FakeDevice { FirmwareVersion = "11.9.65" };
        var uploads = new MemoryUploads();
        var fake = new ConfigureCamera { Version = "11.9.65" };
        camera?.Invoke(fake);
        var payload = new ImportPayload
        {
            FileId = uploads.Add("lobby.json", Encoding.UTF8.GetBytes(file ?? File())),
            FileName = "lobby.json",
            Sections = sections,
            Files = new Dictionary<Guid, string> { [device.Id] = string.Empty },
        }.ToJson();
        return (new RecordingContext(fake, uploads), fake, device, payload);
    }

    private static Task RunAsync((RecordingContext Ctx, ConfigureCamera Camera, FakeDevice Device, string Payload) s) =>
        StepRun.RunAsync(s.Ctx.Steps, () => new ImportConfigTask().ExecuteAsync(s.Ctx, s.Device, s.Payload, CancellationToken.None));

    private static IEnumerable<string> Written(ConfigureCamera camera) => camera.Updates.SelectMany(u => u).Select(p => p.Key);

    [Fact]
    public void The_file_parameters_have_the_names_of_param_cgi()
    {
        // Export and parameter list of the same AXIS P3265-V (AXIS OS 12.11.77): every value has its parameter.
        var names = ConfigureFixtures.Read("param-list-all.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line[5..line.IndexOf('=', StringComparison.Ordinal)])
            .ToHashSet(StringComparer.Ordinal);
        var values = ConfigParameters.Flatten(ConfigFile.Parse(Fixtures.Export).Data[ConfigSections.Param]);

        Assert.Equal(1183, values.Count);
        Assert.All(values, v => Assert.Contains(v.Name, names));
        Assert.Contains(new NameValue("Image.I0.Appearance.Compression", "30"), values);
        Assert.Contains(new NameValue("Network.Interface.I0.Link.MTU", "1500"), values);
        Assert.DoesNotContain(values, v => v.Name.EndsWith(".groupId", StringComparison.Ordinal));
    }

    [Fact]
    public void A_list_of_values_is_one_parameter_and_other_value_kinds_are_text()
    {
        var values = ConfigParameters.Flatten(JsonNode.Parse("""{"A":{"List":["x","y"],"Number":5,"Flag":true,"Empty":[]},"BCollection":[{"groupId":"B3","C":"1"},{"C":"2"}]}"""));
        Assert.Equal([new("A.List", "x,y"), new("A.Number", "5"), new("A.Flag", "true"), new("B.B3.C", "1"), new("B.I1.C", "2")], values);
    }

    [Fact]
    public async Task The_parameters_are_set_through_param_cgi_and_rules_and_schedules_are_skipped_with_a_warning()
    {
        var s = Setup(NodeSections);
        await RunAsync(s);

        Assert.Equal(
        [
            "Read configuration file: Done",
            "Check compatibility: Done",
            "Import network and system settings: Done",
            "Import event rules: Skipped",
            "Import schedules: Skipped",
            "Verify configuration: Done",
            "Completed: Done",
        ], StepRun.Lines(s.Ctx.Steps));
        Assert.Equal("AXIS P3265-V, AXIS OS 11.9.65", StepRun.Detail(s.Ctx.Steps, "Check compatibility"));
        Assert.Equal(ImportConfigTask.OlderOsReason, StepRun.Detail(s.Ctx.Steps, "Import event rules"));
        Assert.Equal(ImportConfigTask.OlderOsReason, StepRun.Detail(s.Ctx.Steps, "Import schedules"));
        Assert.Matches("^[0-9,]+ values, [0-9,]+ not on this device or read-only$", StepRun.Detail(s.Ctx.Steps, "Import network and system settings"));
        Assert.Equal("All imported values match", StepRun.Detail(s.Ctx.Steps, "Verify configuration"));
        Assert.Equal("Event rules and schedules were not applied: this needs AXIS OS 12.5 or later.", s.Ctx.Warnings[^1]);

        // The checks before the write: the fresh API list, the definitions of the groups in the file; one update.
        Assert.Contains("GET param.cgi list", s.Camera.Requests);
        Assert.Contains(s.Camera.Requests, r => r.Contains("action=listdefinitions", StringComparison.Ordinal));
        Assert.Single(s.Camera.Updates);
        Assert.Equal("35", s.Camera.Values["Image.I0.Appearance.Compression"]);
        Assert.Equal("Lobby", s.Camera.Values["Image.I0.Name"]);

        // Left out exactly like the 12.5 import: identity, time zone parameters and, unchosen, the connection settings.
        var written = Written(s.Camera).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("Network.HostName", written);
        Assert.DoesNotContain("Network.IPAddress", written);
        Assert.DoesNotContain("Time.POSIXTimeZone", written);
        Assert.DoesNotContain("Network.DNSServer1", written);
        Assert.DoesNotContain("HTTPS.Enabled", written);
        Assert.DoesNotContain("System.BoaPort", written);
        Assert.DoesNotContain(written, name => name.Contains("VideoAddress", StringComparison.Ordinal));
        Assert.Equal("yes", s.Camera.Values["HTTPS.Enabled"]);
        Assert.Equal("80", s.Camera.Values["System.BoaPort"]);

        // Only names the device has: everything written is one of its configurable parameters.
        var configurable = ParamDefinitionsReader.Parse(ConfigureFixtures.Read("listdefinitions-all.xml")).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(written, name => Assert.Contains(name, configurable));
    }

    [Fact]
    public async Task Connection_settings_are_written_last_in_their_own_step_when_chosen()
    {
        var s = Setup([ConfigSections.Param, ConfigSections.Connection]);
        await RunAsync(s);

        Assert.Equal("Import connection settings: Done", StepRun.Lines(s.Ctx.Steps)[3]);
        Assert.Equal(2, s.Camera.Updates.Count);
        Assert.Contains(s.Camera.Updates[1], p => p.Key == "HTTPS.Enabled" && p.Value == "no");
        Assert.All(s.Camera.Updates[1], p => Assert.True(DangerousParameters.IsDangerous(p.Key), p.Key));
        Assert.Equal("8080", s.Camera.Values["System.BoaPort"]);
        Assert.Empty(s.Ctx.Warnings);
    }

    [Fact]
    public async Task Invalid_and_refused_values_are_skipped_by_name_and_the_rest_is_set()
    {
        var file = (JsonObject)JsonNode.Parse(File())!;
        file["data"]!["param.v2"]!["ImageCollection"]![0]!["Appearance"]!["Compression"] = "999";
        var s = Setup([ConfigSections.Param], file.ToJsonString(), c => c.Refuse.Add("Image.MaxViewers"));
        await RunAsync(s);

        var detail = StepRun.Detail(s.Ctx.Steps, "Import network and system settings");
        Assert.Equal("Import network and system settings: Warning", StepRun.Lines(s.Ctx.Steps)[2]);
        Assert.Contains("2 skipped: ", detail, StringComparison.Ordinal);
        Assert.Contains("Image.I0.Appearance.Compression (", detail, StringComparison.Ordinal);
        Assert.Contains("Image.MaxViewers (the device refused it)", detail, StringComparison.Ordinal);
        Assert.Equal("30", s.Camera.Values["Image.I0.Appearance.Compression"]);
        Assert.Equal("Verify configuration: Done", StepRun.Lines(s.Ctx.Steps)[3]);
    }

    [Fact]
    public async Task Only_areas_that_need_12_5_change_nothing()
    {
        var s = Setup([ConfigSections.ActionRules, "event-schedules.v2"]);
        var ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => RunAsync(s));
        Assert.Equal("Needs AXIS OS 12.5 or later (this device has 11.9.65). Nothing was changed.", ex.Message);
        Assert.Empty(s.Camera.Updates);
    }

    [Fact]
    public async Task A_device_without_the_parameter_api_changes_nothing()
    {
        var s = Setup(NodeSections, camera: c => c.ApiList = [new DeviceApi("basic-device-info", "1.3")]);
        var ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => RunAsync(s));
        Assert.Equal("Needs AXIS OS 12.5 or later (this device has 11.9.65). Nothing was changed.", ex.Message);
        Assert.Empty(s.Camera.Updates);
        Assert.DoesNotContain(s.Camera.Requests, r => r.Contains("listdefinitions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Older_devices_can_run_it_and_the_plan_says_what_they_take()
    {
        var task = new ImportConfigTask();
        var old = new FakeDevice { Serial = "B8A44F000003", Address = "10.0.0.12", FirmwareVersion = "11.9.65" };
        var current = new FakeDevice();
        Assert.True(task.CanRun(old));
        Assert.Null(task.NotSupportedReason(old));
        Assert.False(task.CanRun(new FakeDevice { FirmwareVersion = "11.9.65", Apis = [new DeviceApi("basic-device-info", "1.3")] }));

        var uploads = new MemoryUploads();
        var fileId = uploads.Add("lobby.json", Encoding.UTF8.GetBytes(Fixtures.Export));
        string Payload(params string[] sections) => new JsonObject { ["fileId"] = fileId, ["fileName"] = "lobby.json", ["sections"] = new JsonArray([.. sections.Select(k => (JsonNode)k)]) }.ToJsonString();

        var plan = await task.PlanAsync(Payload(NodeSections), [current, old], uploads, CancellationToken.None);
        Assert.All(plan.Devices, d => Assert.Null(d.SkipReason));
        Assert.Contains(ImportTexts.OlderOsNote(1, 2), plan.Warnings);
        Assert.Equal("1 of 2 devices have AXIS OS older than 12.5: they take only the network and system settings.", ImportTexts.OlderOsNote(1, 2));

        var rulesOnly = await task.PlanAsync(Payload(ConfigSections.ActionRules), [current, old], uploads, CancellationToken.None);
        Assert.Null(rulesOnly.Devices[0].SkipReason);
        Assert.StartsWith("Needs AXIS OS 12.5 or later", rulesOnly.Devices[1].SkipReason, StringComparison.Ordinal);

        var parametersOnly = await task.PlanAsync(Payload(ConfigSections.Param), [old], uploads, CancellationToken.None);
        Assert.DoesNotContain(parametersOnly.Warnings, w => w.Contains("older than", StringComparison.Ordinal));
    }

    [Fact]
    public void The_warning_names_the_areas_in_plain_words()
    {
        Assert.Equal("Time zone was not applied: this needs AXIS OS 12.5 or later.", ImportTexts.NotAppliedNote(["Time zone"]));
        Assert.Equal("Event rules were not applied: this needs AXIS OS 12.5 or later.", ImportTexts.NotAppliedNote(["Event rules"]));
        Assert.Equal("Event rules, MQTT events and time zone were not applied: this needs AXIS OS 12.5 or later.",
            ImportTexts.NotAppliedNote(["Event rules", "MQTT events", "Time zone"]));
    }
}
