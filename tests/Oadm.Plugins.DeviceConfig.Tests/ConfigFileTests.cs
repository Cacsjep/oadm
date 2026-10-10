using System.Net;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>The file format, the identity filter, names, payload and the device answers.</summary>
public sealed class ConfigFileTests
{
    [Fact]
    public void The_recorded_export_has_seven_areas_with_plain_names()
    {
        var file = ConfigFile.Parse(Fixtures.Export);

        Assert.Equal("P3265-V", file.Model);
        Assert.Equal("12.11.77", file.FirmwareVersion);
        Assert.Equal("From AXIS P3265-V, AXIS OS 12.11.77", file.SourceText);
        Assert.Equal(
            ["Event rules", "MQTT events", "Schedules", "Network and system settings", "Power", "Time zone", "Password policy"],
            file.Sections.Select(s => s.Name));
        Assert.True(file.Sections.Single(s => s.Key == ConfigSections.Param).ValueCount > 1000);
        Assert.Equal(0, file.Sections.Single(s => s.Key == ConfigSections.ActionRules).ValueCount);
        Assert.False(file.HasRecipients);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"status":"success"}""")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"Network":{"a":1}},"metadata":{"prodNbr":"P3265-V"}}""")]
    [InlineData("""{"data":{"time.v2":"x"},"metadata":{"prodNbr":"P3265-V"}}""")]
    public void Anything_else_is_not_a_configuration_file(string json)
    {
        var ex = Assert.Throws<ConfigFileException>(() => ConfigFile.Parse(json));
        Assert.Equal(DeviceConfigIds.NotAConfigFile, ex.Message);
    }

    [Fact]
    public void A_file_without_the_model_is_refused()
    {
        var ex = Assert.Throws<ConfigFileException>(() => ConfigFile.Parse("""{"data":{"time.v2":{"timeZone":{}}}}"""));
        Assert.Equal(DeviceConfigIds.NoModel, ex.Message);
    }

    [Fact]
    public async Task A_file_larger_than_2_MB_is_refused()
    {
        using var stream = new MemoryStream(new byte[DeviceConfigIds.MaxFileBytes + 1]);
        var ex = await Assert.ThrowsAsync<ConfigFileException>(() => ConfigFile.ReadAsync(stream, CancellationToken.None));
        Assert.StartsWith(DeviceConfigIds.NotAConfigFile, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Identity_and_read_only_values_are_removed_and_the_rest_kept()
    {
        var file = ConfigFile.Parse(Fixtures.Export);
        var param = (JsonObject)ConfigIdentity.Clean(ConfigSections.Param, file.Data[ConfigSections.Param]!);
        var network = (JsonObject)param["Network"]!;

        foreach (var name in new[] { "HostName", "IPAddress", "SubnetMask", "DefaultRouter", "Broadcast", "BootProto", "VolatileHostName" })
        {
            Assert.False(network.ContainsKey(name), name);
        }

        Assert.False(((JsonObject)network["Bonjour"]!).ContainsKey("FriendlyName"));
        Assert.Equal("yes", (string?)network["Bonjour"]!["Enabled"]);
        Assert.False(((JsonObject)network["UPnP"]!).ContainsKey("FriendlyName"));
        Assert.False(network.ContainsKey("DHCP"));
        Assert.Null(network["InterfaceCollection"]![0]!["dot1x"]!["EAPTLS"]);
        Assert.Equal("no", (string?)network["InterfaceCollection"]![0]!["dot1x"]!["Enabled"]);
        Assert.Equal("1.1.1.1", (string?)network["DNSServer1"]);
        Assert.False(((JsonObject)param["SNMP"]!).ContainsKey("EngineBoots"));
        Assert.Equal("public", (string?)param["SNMP"]!["V1ReadCommunity"]);

        // The original is unchanged.
        Assert.Equal("axis-b8a44f631339", (string?)file.Data[ConfigSections.Param]!["Network"]!["HostName"]);

        var power = (JsonObject)ConfigIdentity.Clean(ConfigSections.PowerSettings, file.Data[ConfigSections.PowerSettings]!);
        Assert.Equal(["dynamicPowerMode"], power.Select(p => p.Key));

        var time = ConfigIdentity.Clean("time.v2", file.Data["time.v2"]!);
        Assert.True(JsonNode.DeepEquals(file.Data["time.v2"], time));
    }

    [Fact]
    public void Recipients_mean_passwords_are_missing()
    {
        var file = ConfigFile.Parse("""{"data":{"action-rules.v2":{"recipients":[{"id":"r1","name":"ftp"}],"rules":[]}},"metadata":{"prodNbr":"P3265-V"}}""");
        Assert.True(file.HasRecipients);
    }

    [Theory]
    [InlineData("10.0.0.48_B8A44F631339_P3265-V_config.json", "B8A44F631339")]
    [InlineData("export/b8:a4:4f:63:13:39.json", "B8A44F631339")]
    [InlineData("axis-b8a44f631339.json", "B8A44F631339")]
    [InlineData("B8-A4-4F-63-13-39 lobby.json", "B8A44F631339")]
    [InlineData("lobby.json", null)]
    [InlineData("10.0.0.48_config.json", null)]
    [InlineData("B8A44F6313391.json", null)]
    public void The_mac_address_is_found_in_any_notation(string name, string? serial)
    {
        var serials = ConfigFileNames.SerialsIn(name);
        if (serial is null)
        {
            Assert.Empty(serials);
        }
        else
        {
            Assert.Contains(serial, serials);
        }
    }

    [Fact]
    public void Export_names_carry_address_mac_and_model()
    {
        Assert.Equal("10.0.0.48_B8A44F631339_P3265-V_config.json", ConfigFileNames.ForDevice("10.0.0.48", "B8A44F631339", "AXIS P3265-V"));
        Assert.Equal("fe80--1_B8A44F631339_Q6135-LE-Mk-II_config.json", ConfigFileNames.ForDevice("fe80::1", "B8A44F631339", "AXIS Q6135-LE Mk II"));
        Assert.Equal("oadm-config-2026-10-10.zip", ConfigFileNames.ForZip(new DateTime(2026, 10, 10)));
        Assert.Equal(["B8A44F631339"], ConfigFileNames.SerialsIn(ConfigFileNames.ForDevice("10.0.0.48", "B8A44F631339", "AXIS P3265-V")));
    }

    [Theory]
    [InlineData("P3265-V", "AXIS P3265-V", true)]
    [InlineData("p3265-v", " axis P3265-V ", true)]
    [InlineData("P3265-V", "AXIS P3265-LV", false)]
    [InlineData("", "", false)]
    public void Models_compare_without_the_brand(string a, string b, bool same) => Assert.Equal(same, ModelNames.Same(a, b));

    [Theory]
    [InlineData("12.11.77", true)]
    [InlineData("12.5.56", true)]
    [InlineData("12.4.59", false)]
    [InlineData("11.11.160", false)]
    [InlineData("13.0.1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("garbage", false)]
    public void Configuration_files_need_axis_os_12_5(string? version, bool supported)
    {
        Assert.Equal(supported, FirmwareVersions.SupportsConfigFiles(version));
        var device = new FakeDevice { FirmwareVersion = version };
        Assert.Equal(supported, DeviceConfigSupport.CanRun(device));
        if (!supported)
        {
            Assert.StartsWith("Needs AXIS OS 12.5 or later", DeviceConfigSupport.NotSupportedReason(device), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_device_that_is_not_ok_cannot_run_it()
    {
        var device = new FakeDevice { Status = DeviceStatus.CredentialsRequired };
        Assert.False(DeviceConfigSupport.CanRun(device));
        Assert.Equal("OADM cannot log in to the device: use Log in", DeviceConfigSupport.NotSupportedReason(device));
    }

    [Fact]
    public void The_payload_round_trips_and_names_the_task()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var json = new ImportPayload
        {
            FileId = "f1",
            FileName = "lobby.json",
            Sections = ["time.v2"],
            Files = new Dictionary<Guid, string> { [id] = string.Empty },
            LeftOut = new Dictionary<Guid, string> { [other] = "Other model" },
        }.ToJson();
        var payload = ImportPayload.Parse(json);

        Assert.Equal(("f1", "lobby.json", false), (payload.FileId, payload.FileName, payload.IsZip));
        Assert.Equal(string.Empty, payload.Files[id]);
        Assert.Equal("Other model", payload.LeftOut[other]);
        Assert.Equal("Import configuration lobby.json", new ImportConfigTask().GetTaskName(json));
        Assert.Equal("Import configuration", new ImportConfigTask().GetTaskName(null));
        Assert.Throws<ArgumentException>(() => ImportPayload.Parse("""{"fileId":"f1","sections":[]}"""));
        Assert.Throws<ArgumentException>(() => ImportPayload.Parse("""{"fileId":"f1","sections":null}"""));
        Assert.Empty(ImportPayload.Parse("""{"fileId":"f1","sections":["time.v2"]}""").Files);
    }

    [Fact]
    public void Discover_lists_every_area_with_its_major_version()
    {
        var keys = ConfigApi.ParseDiscover(Fixtures.Discover);
        Assert.Contains("param.v2", keys);
        Assert.Contains("time.v2", keys);
        Assert.Contains("action-rules.v2", keys);
        Assert.Equal(38, keys.Count);
    }

    [Fact]
    public void Import_answers_and_errors_are_read()
    {
        var answer = ConfigApi.ParseAnswer("""{"status":"success","warnings":["Property x is unknown"],"information":["Restart needed"]}""");
        Assert.Equal(["Property x is unknown"], answer.Warnings);
        Assert.Equal(["Restart needed"], answer.Information);
        Assert.Empty(ConfigApi.ParseAnswer(string.Empty).Warnings);
        Assert.Equal("Bad value", ConfigApi.ErrorMessage("""{"status":"error","error":{"code":400,"message":"Bad value"}}""", out var isError));
        Assert.True(isError);
    }

    [Fact]
    public void The_answers_of_axis_os_12_11_118_are_told_apart()
    {
        // Recorded on AXIS M3098-LV, AXIS OS 12.11.118 (loan camera, write test approved by the user).
        var refused = ConfigApi.ParseAnswer("""{"status":"success","warnings":["Failed to import time.v2: Trying to import 'time.v2' with invalid data: \"notabool\" is not of type \"boolean\" (path: /timeZone/posix/dstEnabled)"]}""");
        Assert.Equal("\"notabool\" is not of type \"boolean\" (path: /timeZone/posix/dstEnabled)", refused.Refused);
        Assert.Empty(refused.Warnings);

        var partial = ConfigApi.ParseAnswer("""{"status":"success","warnings":["Failed to set property: root.Network.DNSUpdate.Enabled"]}""");
        Assert.Null(partial.Refused);
        Assert.Equal(["Failed to set property: root.Network.DNSUpdate.Enabled"], partial.Warnings);

        var removed = ConfigApi.ParseAnswer("""{"status":"success","information":["Field 'Bogus' has been removed from the request"]}""");
        Assert.Equal(["Bogus"], removed.RemovedFields);
        Assert.Null(removed.Refused);
    }

    [Fact]
    public void The_rotation_is_left_out_where_auto_rotation_is_on()
    {
        var param = JsonNode.Parse("""
            {"ImageSourceCollection":[{"groupId":"I0","AutoRotationEnabled":"yes","Rotation":"0"},{"groupId":"I1","AutoRotationEnabled":"no","Rotation":"180"}],
             "ImageCollection":[{"groupId":"I0","Appearance":{"AutoRotationEnabled":"yes","Rotation":"0","Resolution":"1920x1080"}},{"groupId":"I1","Appearance":{"AutoRotationEnabled":"no","Rotation":"90"}}]}
            """)!;
        var clean = ConfigIdentity.Clean(ConfigSections.Param, param);

        Assert.Null(clean["ImageSourceCollection"]![0]!["Rotation"]);
        Assert.Equal("180", (string?)clean["ImageSourceCollection"]![1]!["Rotation"]);
        Assert.Null(clean["ImageCollection"]![0]!["Appearance"]!["Rotation"]);
        Assert.Equal("1920x1080", (string?)clean["ImageCollection"]![0]!["Appearance"]!["Resolution"]);
        Assert.Equal("90", (string?)clean["ImageCollection"]![1]!["Appearance"]!["Rotation"]);
    }

    [Fact]
    public void The_time_zone_parameters_are_left_to_the_time_zone_area()
    {
        var file = ConfigFile.Parse(Fixtures.Export);
        var time = (JsonObject)ConfigIdentity.Clean(ConfigSections.Param, file.Data[ConfigSections.Param]!)["Time"]!;
        Assert.False(time.ContainsKey("POSIXTimeZone"));
        Assert.False(time.ContainsKey("DST"));
        Assert.False(time.ContainsKey("ObtainFromDHCP"));
        Assert.True(time.ContainsKey("NTP"));
    }

    [Fact]
    public async Task Device_errors_become_plain_texts()
    {
        var camera = new ConfigCamera { ConfigStatus = HttpStatusCode.NotFound };
        var ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => ConfigApi.ExportAsync(camera, CancellationToken.None));
        Assert.Equal(DeviceConfigIds.FeatureOff, ex.Message);

        camera.ConfigStatus = HttpStatusCode.OK;
        camera.ImportAnswer = (_, _) => (HttpStatusCode.BadRequest, """{"status":"error","error":{"code":400,"message":"timeZone must be a string"}}""");
        ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => ConfigApi.ImportAsync(camera, "time.v2", new JsonObject(), CancellationToken.None));
        Assert.Equal("Bad Request - HTTP 400: timeZone must be a string", ex.Message);

        camera.ImportAnswer = (_, _) => (HttpStatusCode.OK, """{"status":"error","error":{"message":"Not allowed"}}""");
        ex = await Assert.ThrowsAsync<ConfigDeviceException>(() => ConfigApi.ImportAsync(camera, "time.v2", new JsonObject(), CancellationToken.None));
        Assert.Equal("Not allowed", ex.Message);
        Assert.All(camera.Imports, i => Assert.Equal("importType=merge", i.Query));
    }

    [Fact]
    public void Differences_match_collections_by_group_id()
    {
        var expected = JsonNode.Parse("""{"A":"1","List":[{"groupId":"I0","X":"a"},{"groupId":"I1","X":"b"}],"Arr":[1,2]}""");
        var same = JsonNode.Parse("""{"A":"1","Extra":"z","List":[{"groupId":"I1","X":"b"},{"groupId":"I0","X":"a"}],"Arr":[1,2]}""");
        var other = JsonNode.Parse("""{"A":"2","List":[{"groupId":"I0","X":"c"}],"Arr":[1]}""");

        Assert.Empty(ConfigCompare.Differences(expected, same));
        Assert.Equal(["A", "List.I0.X", "List.I1.groupId", "List.I1.X", "Arr.1"], ConfigCompare.Differences(expected, other));
    }
}
