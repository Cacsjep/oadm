using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>The import task against a camera with the configuration API.</summary>
public sealed class ImportTaskTests
{
    private static readonly string[] AllSections =
        ["action-rules.v2", "event-mqtt-bridge.v2", "event-schedules.v2", "param.v2", "power-settings.v2", "time.v2", "user-management.v2"];

    /// <summary>The recorded export with other values in a few areas: what a technician imports into the camera.</summary>
    private static string ChangedFile(string model = "P3265-V")
    {
        var export = (JsonObject)JsonNode.Parse(Fixtures.Export)!;
        var data = (JsonObject)export["data"]!;
        data["time.v2"]!["timeZone"]!["posix"]!["timeZone"] = "CET-1CEST,M3.5.0,M10.5.0/3";
        data["param.v2"]!["Network"]!["DNSServer1"] = "10.0.0.1";
        data["param.v2"]!["Network"]!["HostName"] = "lobby-cam";
        data["param.v2"]!["Network"]!["IPAddress"] = "10.0.0.99";
        data["param.v2"]!["Network"]!["BootProto"] = "none";
        export["metadata"]!["prodNbr"] = model;
        return export.ToJsonString();
    }

    private static (ImportConfigTask Plugin, RecordingContext Ctx, ConfigCamera Camera, FakeDevice Device, string Payload) Setup(
        string? file = null, bool zip = false, IReadOnlyList<string>? sections = null, Action<ConfigCamera>? camera = null)
    {
        var device = new FakeDevice();
        var uploads = new MemoryUploads();
        var content = file ?? ChangedFile();
        string fileId;
        string entry;
        if (zip)
        {
            entry = ConfigFileNames.ForDevice(device.Address, device.Serial, device.Model);
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
                writer.Write(content);
            }

            fileId = uploads.Add("site.zip", buffer.ToArray());
        }
        else
        {
            entry = string.Empty;
            fileId = uploads.Add("lobby.json", Encoding.UTF8.GetBytes(content));
        }

        var fake = new ConfigCamera();
        camera?.Invoke(fake);
        var payload = new ImportPayload
        {
            FileId = fileId,
            FileName = zip ? "site.zip" : "lobby.json",
            IsZip = zip,
            Sections = sections ?? AllSections,
            Files = new Dictionary<Guid, string> { [device.Id] = entry },
        }.ToJson();
        return (new ImportConfigTask(), new RecordingContext(fake, uploads), fake, device, payload);
    }

    private static Task RunAsync((ImportConfigTask Plugin, RecordingContext Ctx, ConfigCamera Camera, FakeDevice Device, string Payload) s) =>
        StepRun.RunAsync(s.Ctx.Steps, () => s.Plugin.ExecuteAsync(s.Ctx, s.Device, s.Payload, CancellationToken.None));

    [Fact]
    public async Task Every_area_is_imported_as_a_merge_without_the_device_identity()
    {
        var s = Setup();
        await RunAsync(s);

        Assert.Equal(
        [
            "Read configuration file: Done",
            "Check compatibility: Done",
            "Import event rules: Done",
            "Import MQTT events: Done",
            "Import schedules: Done",
            "Import network and system settings: Done",
            "Import power settings: Done",
            "Import time zone: Done",
            "Import password policy: Done",
            "Verify configuration: Done",
            "Completed: Done",
        ], StepRun.Lines(s.Ctx.Steps));
        Assert.Equal("From AXIS P3265-V, AXIS OS 12.11.77", StepRun.Detail(s.Ctx.Steps, "Read configuration file"));
        Assert.Equal("AXIS P3265-V, AXIS OS 12.11.77", StepRun.Detail(s.Ctx.Steps, "Check compatibility"));
        Assert.Equal("All imported values match", StepRun.Detail(s.Ctx.Steps, "Verify configuration"));
        Assert.Empty(s.Ctx.Warnings);

        // One request per area, always merge; the device's own name and address never leave OADM.
        Assert.Equal(AllSections, s.Camera.Imports.Select(i => i.Key));
        Assert.All(s.Camera.Imports, i => Assert.Equal("importType=merge", i.Query));
        var network = (JsonObject)s.Camera.Imports.Single(i => i.Key == "param.v2").Data["Network"]!;
        Assert.False(network.ContainsKey("HostName"));
        Assert.False(network.ContainsKey("IPAddress"));
        Assert.False(network.ContainsKey("BootProto"));
        Assert.False(((JsonObject)s.Camera.Imports.Single(i => i.Key == "power-settings.v2").Data).ContainsKey("powerStatus"));
        Assert.Equal("axis-b8a44f631339", (string?)s.Camera.Data["param.v2"]!["Network"]!["HostName"]);
        Assert.Equal("dhcp", (string?)s.Camera.Data["param.v2"]!["Network"]!["BootProto"]);
        Assert.Equal("10.0.0.1", (string?)s.Camera.Data["param.v2"]!["Network"]!["DNSServer1"]);
        Assert.Equal("CET-1CEST,M3.5.0,M10.5.0/3", (string?)s.Camera.Data["time.v2"]!["timeZone"]!["posix"]!["timeZone"]);
    }

    [Fact]
    public async Task Only_the_chosen_areas_are_imported_and_a_zip_entry_works_too()
    {
        var s = Setup(zip: true, sections: ["time.v2"]);
        await RunAsync(s);

        Assert.Equal(["time.v2"], s.Camera.Imports.Select(i => i.Key));
        Assert.Equal("Import time zone: Done", StepRun.Lines(s.Ctx.Steps)[2]);
    }

    [Fact]
    public async Task A_connection_reset_after_the_parameter_import_is_retried()
    {
        ConfigApi.RetryDelay = TimeSpan.Zero;
        var s = Setup(sections: ["param.v2", "power-settings.v2"]);
        s.Camera.ImportAnswer = (key, _) =>
        {
            if (key == "param.v2")
            {
                s.Camera.ResetNext = 2;
            }

            return null;
        };
        await RunAsync(s);

        Assert.Equal("Import power settings: Done", StepRun.Lines(s.Ctx.Steps)[3]);
        Assert.Equal(4, s.Camera.Requests.Count(r => r.StartsWith("PATCH", StringComparison.Ordinal))); // param, 2 resets, power
    }

    [Fact]
    public async Task Another_model_is_refused_before_anything_is_written()
    {
        var s = Setup(file: ChangedFile("Q6135-LE"));
        var ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => RunAsync(s));

        Assert.Equal("The file is for AXIS Q6135-LE; this device is AXIS P3265-V. Nothing was changed.", ex.Message);
        Assert.Empty(s.Camera.Imports);
        Assert.Equal("Check compatibility: Failed", StepRun.Lines(s.Ctx.Steps)[1]);
        Assert.Equal("Import time zone: Skipped", StepRun.Lines(s.Ctx.Steps)[7]);
    }

    [Fact]
    public async Task Old_firmware_and_a_turned_off_feature_are_refused()
    {
        var old = Setup(camera: c => c.Version = "12.4.59");
        var ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => RunAsync(old));
        Assert.Equal("Needs AXIS OS 12.5 or later (this device has 12.4.59). Nothing was changed.", ex.Message);
        Assert.Empty(old.Camera.Imports);

        var off = Setup(camera: c => c.ConfigStatus = HttpStatusCode.NotFound);
        ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => RunAsync(off));
        Assert.Equal(DeviceConfigIds.FeatureOff, ex.Message);
        Assert.Empty(off.Camera.Imports);
    }

    [Fact]
    public async Task Areas_the_device_does_not_have_are_skipped()
    {
        var s = Setup(camera: c => c.DiscoverBody = """{"apis":{"time":{"v2":{}},"param":{"v2":{}}}}""");
        await RunAsync(s);

        Assert.Equal(["param.v2", "time.v2"], s.Camera.Imports.Select(i => i.Key));
        Assert.Equal("This device does not have these settings", StepRun.Detail(s.Ctx.Steps, "Import event rules"));

        var none = Setup(sections: ["action-rules.v2"], camera: c => c.DiscoverBody = """{"apis":{"time":{"v2":{}}}}""");
        var ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => RunAsync(none));
        Assert.Equal("None of the settings in the file exist on this device. Nothing was changed.", ex.Message);
    }

    [Fact]
    public async Task A_refused_area_fails_its_step_and_the_others_still_run()
    {
        var s = Setup(camera: c => c.ImportAnswer = (key, _) => key == "time.v2"
            ? (HttpStatusCode.BadRequest, """{"status":"error","error":{"code":400,"message":"Invalid time zone"}}""")
            : null);
        var ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => RunAsync(s));

        Assert.Equal("Time zone: Bad Request - HTTP 400: Invalid time zone", ex.Message);
        Assert.Equal("Import time zone: Failed", StepRun.Lines(s.Ctx.Steps)[7]);
        Assert.Equal("Import password policy: Done", StepRun.Lines(s.Ctx.Steps)[8]);
        Assert.Equal("Verify configuration: Done", StepRun.Lines(s.Ctx.Steps)[9]);
        Assert.Equal(7, s.Camera.Imports.Count);
    }

    [Fact]
    public async Task A_value_of_the_wrong_type_fails_its_area_although_the_device_says_success()
    {
        var s = Setup(camera: c => c.ImportAnswer = (key, _) => key == "time.v2"
            ? (HttpStatusCode.OK, """{"status":"success","warnings":["Failed to import time.v2: Trying to import 'time.v2' with invalid data: \"x\" is not of type \"boolean\" (path: /timeZone/posix/dstEnabled)"]}""")
            : null);
        var ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => RunAsync(s));

        Assert.Equal("Time zone: The device refused these settings: \"x\" is not of type \"boolean\" (path: /timeZone/posix/dstEnabled). Nothing of them was changed.", ex.Message);
        Assert.Equal("Import time zone: Failed", StepRun.Lines(s.Ctx.Steps)[7]);
        Assert.Equal("Verify configuration: Done", StepRun.Lines(s.Ctx.Steps)[9]);
    }

    [Fact]
    public async Task Settings_the_device_does_not_know_are_counted_and_not_differences()
    {
        var export = (JsonObject)JsonNode.Parse(Fixtures.Export)!;
        export["data"]!["time.v2"]!["timeZone"]!["futureSetting"] = true;
        var s = Setup(file: export.ToJsonString(), sections: ["time.v2"], camera: c => c.ImportAnswer = (_, _) =>
            (HttpStatusCode.OK, """{"status":"success","information":["Field 'futureSetting' has been removed from the request"]}"""));
        await RunAsync(s);

        Assert.Equal("4 values, 1 not on this device", StepRun.Detail(s.Ctx.Steps, "Import time zone"));
        Assert.Equal("All imported values match", StepRun.Detail(s.Ctx.Steps, "Verify configuration"));
    }

    [Fact]
    public async Task Device_warnings_and_differing_values_end_with_warnings()
    {
        var s = Setup(camera: c =>
        {
            c.ImportAnswer = (key, _) => key == "user-management.v2"
                ? (HttpStatusCode.OK, """{"status":"success","warnings":["a","b","c","d"],"information":["Restart to apply"]}""")
                : null;
            c.Ignores["param.v2"] = ["Network.DNSServer1"];
        });
        await RunAsync(s);

        Assert.Equal("a; b; c (+1 more)", StepRun.Detail(s.Ctx.Steps, "Import password policy"));
        Assert.Equal("Import password policy: Warning", StepRun.Lines(s.Ctx.Steps)[8]);
        Assert.Equal("1 setting differs after the import: Network.DNSServer1", StepRun.Detail(s.Ctx.Steps, "Verify configuration"));
        Assert.Contains(s.Ctx.Logs, l => l.Level == TaskLogLevel.Info && l.Message == "Password policy: Restart to apply");
        Assert.Contains(s.Ctx.Logs, l => l.Level == TaskLogLevel.Warning && l.Message == "Password policy: d");
        Assert.Equal(2, s.Ctx.Warnings.Count);
    }

    [Fact]
    public async Task A_device_the_dialog_left_out_is_not_touched()
    {
        var s = Setup();
        var device = new FakeDevice();
        var payload = ImportPayload.Parse(s.Payload);
        var json = new ImportPayload
        {
            FileId = payload.FileId,
            FileName = payload.FileName,
            Sections = payload.Sections,
            Files = payload.Files,
            LeftOut = new Dictionary<Guid, string> { [device.Id] = DeviceConfigIds.OtherModel("P3265-V") },
        }.ToJson();
        await StepRun.RunAsync(s.Ctx.Steps, () => s.Plugin.ExecuteAsync(s.Ctx, device, json, CancellationToken.None));

        Assert.Equal(["Check compatibility: Warning", "Completed: Done"], StepRun.Lines(s.Ctx.Steps));
        Assert.Equal("Other model: the file is for AXIS P3265-V. Nothing was changed.", s.Ctx.Warnings.Single());
        Assert.Empty(s.Camera.Requests);
    }

    [Fact]
    public async Task A_broken_or_missing_file_changes_nothing()
    {
        var broken = Setup(file: "{ not json");
        var ex = await Assert.ThrowsAsync<ConfigFileException>(() => RunAsync(broken));
        Assert.Equal(DeviceConfigIds.NotAConfigFile, ex.Message);
        Assert.Empty(broken.Camera.Requests);

        var s = Setup();
        var payload = ImportPayload.Parse(s.Payload);
        var gone = new ImportPayload { FileId = "gone", Sections = payload.Sections, Files = payload.Files }.ToJson();
        ex = await Assert.ThrowsAsync<ConfigFileException>(() => s.Plugin.ExecuteAsync(s.Ctx, s.Device, gone, CancellationToken.None));
        Assert.Equal("The configuration file is no longer on the server. Import it again. Nothing was changed.", ex.Message);
    }

    [Fact]
    public void Both_entries_are_in_the_maintenance_menu_and_only_for_axis_os_12_5()
    {
        ITaskPlugin export = new ExportConfigTask();
        ITaskPlugin import = new ImportConfigTask();
        Assert.Equal((TaskGroups.Maintenance, TaskGroups.Maintenance), (export.Group, import.Group));
        Assert.True(export.RequiresDialog && import.RequiresDialog);
        Assert.False(export.ShowInToolbar || import.ShowInToolbar);
        Assert.False(import.CanRun(new FakeDevice { FirmwareVersion = "11.11.160" }));
        Assert.True(import.CanRun(new FakeDevice()));
        Assert.False(export.CanRun(new FakeDevice { Status = DeviceStatus.Unreachable }));
    }
}
