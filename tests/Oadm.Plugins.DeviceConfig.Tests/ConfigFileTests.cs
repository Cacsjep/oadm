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
            ["Event rules", "MQTT events", "Schedules", "Network and system settings", "Power", "Time zone", "Password policy", "Connection settings"],
            file.Sections.Select(s => s.Name));
        Assert.True(file.Sections.Single(s => s.Key == ConfigSections.Param).ValueCount > 1000);
        Assert.Equal(30, file.Sections.Single(s => s.Key == ConfigSections.Connection).ValueCount);
        Assert.Empty(file.UnknownSections);
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
        Assert.False(((JsonObject)network["DNSUpdate"]!).ContainsKey("DNSName"));
        Assert.Equal("30", (string?)network["DNSUpdate"]!["TTL"]);
        Assert.Equal("0", (string?)network["QoS"]!["Class1"]!["DSCP"]);
        var dot1x = (JsonObject)ConfigIdentity.Clean(ConfigSections.Connection, file.Data[ConfigSections.Param]!)["Network"]!["InterfaceCollection"]![0]!["dot1x"]!;
        Assert.Null(dot1x["EAPTLS"]);
        Assert.Equal("3", (string?)dot1x["EAPOLVersion"]);
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
    public void Only_the_areas_OADM_knows_are_offered()
    {
        var export = (JsonObject)JsonNode.Parse(Fixtures.Export)!;
        export["data"]!["firewall.v1"] = new JsonObject { ["activated"] = true };
        export["data"]!["param.v3"] = new JsonObject { ["Network"] = new JsonObject { ["HostName"] = "same-on-all" } };
        export["data"]!["connection"] = new JsonObject { ["HTTPS"] = new JsonObject { ["Enabled"] = "no" } }; // not an area of a file
        var file = ConfigFile.Parse(export.ToJsonString());

        Assert.Equal(["firewall.v1", "param.v3"], file.UnknownSections);
        Assert.DoesNotContain(file.Sections, s => s.Key is "firewall.v1" or "param.v3");
        Assert.Equal(8, file.Sections.Count); // the seven known areas and the connection settings
        Assert.All(ConfigSections.Known, key => Assert.True(ConfigSections.IsKnown(key)));
        Assert.True(ConfigSections.IsKnown(ConfigSections.Connection));
        Assert.False(ConfigSections.IsKnown("firewall.v1"));
        Assert.Equal("Not imported: firewall.v1, param.v3. OADM does not import these settings.", ConfigSections.UnknownNote(file.UnknownSections));
        Assert.Null(ConfigSections.UnknownNote([]));
    }

    [Fact]
    public void Only_the_password_policy_of_the_user_area_is_imported()
    {
        // What the camera exports: the policy and nothing else, so nothing is left out.
        var recorded = ConfigFile.Parse(Fixtures.Export);
        Assert.Equal("""{"settings":{"passphraseComplexity":{"policy":"none"}}}""", recorded.Data[ConfigSections.UserManagement]!.ToJsonString());
        Assert.Empty(recorded.NotImported);

        // A file made by hand with accounts in the area.
        var export = (JsonObject)JsonNode.Parse(Fixtures.Export)!;
        var area = (JsonObject)export["data"]!["user-management.v2"]!;
        area["users"] = new JsonArray(new JsonObject { ["name"] = "root", ["role"] = "viewer" });
        area["groups"] = new JsonObject { ["admin"] = new JsonArray() };
        area["settings"]!["anonymousAdmin"] = true;
        export["data"]!["firewall.v1"] = new JsonObject { ["activated"] = true };
        var file = ConfigFile.Parse(export.ToJsonString());

        var cleaned = ConfigIdentity.Clean(ConfigSections.UserManagement, file.Data[ConfigSections.UserManagement]!);
        Assert.Equal("""{"settings":{"passphraseComplexity":{"policy":"none"}}}""", cleaned.ToJsonString());
        Assert.Equal(1, file.Sections.Single(s => s.Key == ConfigSections.UserManagement).ValueCount);
        Assert.Equal(["firewall.v1"], file.UnknownSections);
        Assert.Equal(["firewall.v1", "user-management.v2 (settings.anonymousAdmin, users, groups)"], file.NotImported);
        Assert.Equal(
            "Not imported: firewall.v1, user-management.v2 (settings.anonymousAdmin, users, groups). OADM does not import these settings.",
            ConfigSections.UnknownNote(file.NotImported));

        // Settings that are not an object, or no policy: nothing of the area is imported.
        area["settings"] = "all";
        var broken = ConfigFile.Parse(export.ToJsonString());
        Assert.Equal("{}", ConfigIdentity.Clean(ConfigSections.UserManagement, broken.Data[ConfigSections.UserManagement]!).ToJsonString());
        Assert.Contains("user-management.v2 (settings, users, groups)", broken.NotImported);
    }

    [Fact]
    public void The_parameters_Configure_devices_warns_about_are_connection_settings_of_an_import()
    {
        // One table (DangerousParameters): what needs the dangerous right in Configure devices needs the connection right here.
        var param = new JsonObject
        {
            ["Network"] = new JsonObject
            {
                ["Enabled"] = "yes",
                ["Media"] = "auto",
                ["DNSServer1"] = "1.1.1.1",
                ["Filter"] = new JsonObject { ["Enabled"] = "yes" },
                ["UPnP"] = new JsonObject { ["Enabled"] = "no" },
                ["InterfaceCollection"] = new JsonArray(
                    new JsonObject { ["groupId"] = "I0", ["Link"] = new JsonObject { ["MTU"] = "1500" } },
                    new JsonObject { ["groupId"] = "I1", ["dot1x"] = new JsonObject { ["Enabled"] = "yes" }, ["Note"] = "kept" }),
            },
            ["HTTPS"] = new JsonObject { ["Ciphers"] = "x" },
            ["System"] = new JsonObject { ["AlternateBoaPort"] = "8080", ["AccessLog"] = "Off" },
        };

        var rest = ConfigIdentity.Clean(ConfigSections.Param, param);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "Network": { "UPnP": { "Enabled": "no" }, "InterfaceCollection": [ { "groupId": "I1", "Note": "kept" } ] },
              "System": { "AccessLog": "Off" }
            }
            """), rest), rest.ToJsonString());

        var connection = ConfigIdentity.Clean(ConfigSections.Connection, param);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "Network": {
                "Enabled": "yes", "Media": "auto", "DNSServer1": "1.1.1.1", "Filter": { "Enabled": "yes" },
                "InterfaceCollection": [
                  { "Link": { "MTU": "1500" }, "groupId": "I0" },
                  { "dot1x": { "Enabled": "yes" }, "groupId": "I1" }
                ]
              },
              "HTTPS": { "Ciphers": "x" },
              "System": { "AlternateBoaPort": "8080" }
            }
            """), connection), connection.ToJsonString());
        Assert.Equal(8, ConfigIdentity.CountImported(ConfigSections.Connection, param));
        Assert.Equal(3, ConfigIdentity.CountImported(ConfigSections.Param, param) - 1); // the kept item's groupId counts as before

        foreach (var name in new[] { "Network.Enabled", "Network.Media", "HTTPS.Ciphers", "Network.Interface.I0.Link.MTU", "Network.Interface.I1.dot1x.Enabled", "System.AlternateBoaPort", "Network.DNSServer1", "Network.Resolver.ObtainFromDHCP" })
        {
            Assert.True(DangerousParameters.IsDangerous(name), name);
            Assert.True(ConfigIdentity.IsConnection(name), name);
        }

        Assert.False(ConfigIdentity.IsConnection("Network.UPnP.Enabled"));
    }

    [Fact]
    public void Connection_settings_and_multicast_addresses_are_not_network_and_system_settings()
    {
        var file = ConfigFile.Parse(Fixtures.Export);
        var param = (JsonObject)ConfigIdentity.Clean(ConfigSections.Param, file.Data[ConfigSections.Param]!);

        // Left out of the parameters: what decides how OADM reaches the device (the table of Configure devices), and the
        // MAC-derived multicast addresses.
        Assert.False(param.ContainsKey("HTTPS")); // Enabled, Port and Ciphers
        var system = (JsonObject)param["System"]!;
        Assert.False(system.ContainsKey("BoaPort") || system.ContainsKey("BoaGroupPolicy") || system.ContainsKey("WebInterfaceDisabled"));
        Assert.False(system.ContainsKey("AlternateBoaPort") || system.ContainsKey("HTTP2"));
        Assert.Equal("Off", (string?)system["AccessLog"]);
        var network = (JsonObject)param["Network"]!;
        foreach (var name in new[] { "HTTP", "IPv6", "InterfaceCollection", "Enabled", "Media", "DNSServer1", "DNSServer2", "DomainName", "Resolver" })
        {
            Assert.False(network.ContainsKey(name), name); // link (MTU), IEEE 802.1X and DNS included
        }

        Assert.False(((JsonObject)network["RTSP"]!).ContainsKey("Port") || ((JsonObject)network["RTSP"]!).ContainsKey("Enabled"));
        Assert.Equal("10", (string?)network["RTSP"]!["Timeout"]);
        Assert.Equal("yes", (string?)network["SSH"]!["Enabled"]);
        Assert.All(((JsonArray)network["RTPCollection"]!).OfType<JsonObject>(), rtp =>
        {
            Assert.False(rtp.ContainsKey("VideoAddress") || rtp.ContainsKey("AudioAddress"));
            Assert.NotNull(rtp["groupId"]);
        });

        // The connection settings are an area of their own with only those values; collection items keep their group id.
        var connection = ConfigIdentity.Clean(ConfigSections.Connection, file.Data[ConfigSections.Param]!);
        var ciphers = (string?)file.Data[ConfigSections.Param]!["HTTPS"]!["Ciphers"];
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""
            {
              "HTTPS": { "Ciphers": "{{ciphers}}", "Enabled": "yes", "Port": "443" },
              "Network": {
                "DNSServer1": "1.1.1.1",
                "DNSServer2": "0.0.0.0",
                "DomainName": "",
                "Enabled": "yes",
                "HTTP": { "AuthenticationPolicy": "recommended", "AuthenticationWithQop": "no" },
                "IPv6": { "AcceptRA": "yes", "DHCPv6": "auto", "Enabled": "no" },
                "InterfaceCollection": [
                  { "Link": { "MTU": "1500" }, "dot1x": { "EAP": { "MultiAuthEnabled": "no" }, "EAPOLVersion": "3", "Enabled": "no" }, "groupId": "I0" }
                ],
                "Media": "auto",
                "RTSP": { "Enabled": "yes", "Port": "554", "ProtViewer": "password" },
                "RTSPS": { "Enabled": "no", "Port": "322" },
                "Resolver": { "ObtainFromDHCP": "no" }
              },
              "System": {
                "AlternateBoaPort": "0",
                "BoaGroupPolicy": { "admin": "both", "operator": "both", "viewer": "both" },
                "BoaPort": "80",
                "HTTP2": { "AllowH2": "yes" },
                "WebInterfaceDisabled": "no"
              }
            }
            """), connection), connection.ToJsonString());

        // Every value of the parameters is in exactly one place: never imported, the connection area, or the rest.
        Assert.Equal(30, ConfigIdentity.CountImported(ConfigSections.Connection, file.Data[ConfigSections.Param]!));
        Assert.Equal(ConfigSections.Param, ConfigSections.SourceKey(ConfigSections.Connection));
        Assert.Equal("time.v2", ConfigSections.SourceKey("time.v2"));

        // A file without connection values has no such area.
        var plain = ConfigFile.Parse("""{"data":{"param.v2":{"Image":{"DateFormat":"YYYY-MM-DD"}}},"metadata":{"prodNbr":"P3265-V"}}""");
        Assert.Equal([ConfigSections.Param], plain.Sections.Select(s => s.Key));
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
