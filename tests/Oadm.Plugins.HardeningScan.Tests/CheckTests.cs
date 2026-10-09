using System.Net;
using System.Net.Sockets;

using Oadm.Plugins.HardeningScan.Checks;
using Oadm.Plugins.HardeningScan.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.HardeningScan.Tests;

/// <summary>Every rule against the answers recorded from 10.0.0.48, and hand-made variants of them.</summary>
public sealed class CheckTests
{
    private static async Task<Dictionary<string, CheckResult>> ScanAsync(FakeCamera camera, TestDevice? device = null, ScanLevel level = ScanLevel.Extended)
    {
        var facts = await new DeviceFactsReader().ReadAsync(camera, device ?? new TestDevice(), level, CancellationToken.None);
        Assert.Empty(camera.Violations);
        return HardeningChecks.Evaluate(facts, level).ToDictionary(r => r.Id);
    }

    private static void Expect(Dictionary<string, CheckResult> results, string id, CheckState state, string? value = null)
    {
        var result = results[id];
        Assert.True(state == result.State, $"{id}: expected {state}, got {result.State} ({result.Value})");
        if (value is not null)
        {
            Assert.Equal(value, result.Value);
        }
    }

    [Fact]
    public async Task The_dev_camera_answers_give_the_expected_basic_results()
    {
        var results = await ScanAsync(new FakeCamera(), level: ScanLevel.Basic);

        Expect(results, HardeningCatalog.AxisOs, CheckState.Info, "AXIS OS 12.11.77");
        Expect(results, HardeningCatalog.Accounts, CheckState.Pass, "4 administrators, 1 operator");
        Expect(results, HardeningCatalog.PasswordPolicy, CheckState.Warn, "Password policy: none");
        Expect(results, HardeningCatalog.IpConfiguration, CheckState.Warn, "DHCP");
        Expect(results, HardeningCatalog.TimeSync, CheckState.Warn, "NTP on, 2 servers, not synchronized");
        Expect(results, HardeningCatalog.StorageEncryption, CheckState.Fail, "SD card not encrypted");
        Expect(results, HardeningCatalog.Applications, CheckState.Warn, "Unsigned applications allowed, 6 of 8 not signed, 6 installed but stopped");
        Expect(results, HardeningCatalog.WebInterface, CheckState.Warn, "Web interface on");
        Expect(results, HardeningCatalog.Discovery, CheckState.Warn, "Bonjour, ZeroConf on");
        Assert.Contains("LLDP", results[HardeningCatalog.Discovery].Detail, StringComparison.Ordinal);
        Expect(results, HardeningCatalog.Audio, CheckState.Pass, "Audio off");
        Expect(results, HardeningCatalog.SdCardSlot, CheckState.Pass, "Card in use");
        Expect(results, HardeningCatalog.Ssh, CheckState.Fail, "SSH on");
        Expect(results, HardeningCatalog.Uart, CheckState.Pass);
        Expect(results, HardeningCatalog.Firewall, CheckState.Warn, "Firewall on, default accept, 1 rule (limit rules only)");
        Expect(results, HardeningCatalog.Ciphers, CheckState.Pass, "Recommended ciphers only");

        // Basic does not evaluate the Extended columns.
        foreach (var column in HardeningCatalog.Columns.Where(c => c.Level == ScanLevel.Extended))
        {
            Expect(results, column.Id, CheckState.NotScanned);
        }
    }

    [Fact]
    public async Task The_dev_camera_answers_give_the_expected_extended_results()
    {
        var device = new TestDevice { CertTrustName = "SelfSigned", CertNotAfterUtc = DateTime.UtcNow.AddDays(400) };
        var results = await ScanAsync(new FakeCamera(), device);

        Expect(results, HardeningCatalog.Certificate, CheckState.Warn, "Self-signed certificate, expires in 399 days");
        Expect(results, HardeningCatalog.RemoteSyslog, CheckState.Fail, "Remote syslog off");
        Expect(results, HardeningCatalog.Snmp, CheckState.Pass, "SNMP off");
        Expect(results, HardeningCatalog.Rtsps, CheckState.Warn, "RTSPS off");
        Expect(results, HardeningCatalog.OAuth, CheckState.Warn, "Not configured");
        Expect(results, HardeningCatalog.HttpsOnly, CheckState.Warn, "HTTP and HTTPS");
        Expect(results, HardeningCatalog.Dot1x, CheckState.Warn, "IEEE 802.1X off");
        Expect(results, HardeningCatalog.BruteForce, CheckState.Pass, "Password throttling on");
        Expect(results, HardeningCatalog.AccessLog, CheckState.Warn, "Access log off");
        Expect(results, HardeningCatalog.SignedVideo, CheckState.Warn, "Signed video off");
        Expect(results, HardeningCatalog.Nts, CheckState.Warn, "NTS off");
        Assert.All(results.Values, r => Assert.NotEqual(CheckState.NotScanned, r.State));
    }

    [Fact]
    public async Task Basic_reads_nothing_of_extended_and_extended_adds_snmp_oidc_and_the_web_server()
    {
        var basic = new FakeCamera();
        await ScanAsync(basic, level: ScanLevel.Basic);
        Assert.DoesNotContain(basic.Requests, r => r.Contains("snmp", StringComparison.Ordinal) || r.Contains("oidcsetup", StringComparison.Ordinal) || r.Contains("vapix/services", StringComparison.Ordinal));

        var extended = new FakeCamera();
        await ScanAsync(extended);
        Assert.Contains("GET config/rest/snmp/v1", extended.Requests);
        Assert.Contains("GET config/rest/oidcsetup/v1", extended.Requests);
        Assert.Contains("POST vapix/services", extended.Requests);
        Assert.Single(extended.Requests, r => r.StartsWith("GET axis-cgi/param.cgi", StringComparison.Ordinal));
        Assert.Single(extended.Requests, r => r == "GET config/discover");
        Assert.Empty(extended.Violations);
    }

    [Fact]
    public async Task Old_firmware_without_rest_apis_uses_the_parameters()
    {
        var camera = new FakeCamera();
        camera.Statuses["config/discover"] = HttpStatusCode.NotFound;
        camera.Answers["axis-cgi/param.cgi"] = """
            root.HTTPS.Enabled=yes
            root.HTTPS.Ciphers=ECDHE-RSA-AES128-GCM-SHA256:AES128-SHA
            # Error: Error -1 getting param in group 'API.RemoteSyslog1'
            Network.BootProto=none
            root.Network.Bonjour.Enabled=no
            root.Network.UPnP.Enabled=no
            root.Network.ZeroConf.Enabled=no
            root.Network.Filter.Enabled=yes
            root.Network.Filter.Input.Policy=allow
            root.Network.SSH.Enabled=no
            root.Properties.Audio.Audio=no
            root.Properties.LocalStorage.SDCard=no
            root.SNMP.Enabled=yes
            root.SNMP.V1=no
            root.SNMP.V2c=yes
            root.SNMP.V3=no
            # Error: Error -1 getting param in group 'System.WebInterfaceDisabled'
            System.AccessLog=On
            root.Time.SyncSource=NTP
            root.Time.ObtainFromDHCP=no
            root.Time.NTP.Server=10.0.0.17
            """;
        var device = new TestDevice
        {
            FirmwareVersion = "9.80.3",
            DhcpEnabled = null,
            Apis = [new DeviceApi("param-cgi", "1.0")],
        };
        camera.ApiList = device.Apis;

        var results = await ScanAsync(camera, device);

        Expect(results, HardeningCatalog.PasswordPolicy, CheckState.NotApplicable, "No password policy setting on this firmware");
        Expect(results, HardeningCatalog.IpConfiguration, CheckState.Pass, "Static IP address");
        Expect(results, HardeningCatalog.TimeSync, CheckState.Warn, "NTP on, 1 server");
        Expect(results, HardeningCatalog.StorageEncryption, CheckState.NotApplicable, "No edge storage on this device");
        Expect(results, HardeningCatalog.Applications, CheckState.NotApplicable);
        Expect(results, HardeningCatalog.WebInterface, CheckState.NotApplicable, "No setting (needs AXIS OS 9.50 or later)");
        Expect(results, HardeningCatalog.Discovery, CheckState.Pass, "Discovery protocols off");
        Expect(results, HardeningCatalog.Audio, CheckState.Pass, "No audio");
        Expect(results, HardeningCatalog.SdCardSlot, CheckState.NotApplicable, "No SD card slot");
        Expect(results, HardeningCatalog.Ssh, CheckState.Pass, "SSH off");
        Expect(results, HardeningCatalog.Uart, CheckState.Warn, "AXIS OS 9.80.3 is older than 10.11");
        Expect(results, HardeningCatalog.Firewall, CheckState.Warn, "IP address filter on (no firewall on this firmware)");
        Expect(results, HardeningCatalog.Ciphers, CheckState.Warn, "1 other cipher: AES128-SHA");
        Expect(results, HardeningCatalog.RemoteSyslog, CheckState.NotApplicable, "No remote syslog on this firmware");
        Expect(results, HardeningCatalog.Snmp, CheckState.Fail, "SNMP v2c on");
        Expect(results, HardeningCatalog.OAuth, CheckState.NotApplicable, "Needs AXIS OS 11.6 or later");
        Expect(results, HardeningCatalog.AccessLog, CheckState.Pass, "Access log on");
        Expect(results, HardeningCatalog.SignedVideo, CheckState.NotApplicable);
        Expect(results, HardeningCatalog.Nts, CheckState.NotApplicable);
        Assert.DoesNotContain(camera.Requests, r => r.Contains("config/rest", StringComparison.Ordinal));
        Assert.DoesNotContain(camera.Requests, r => r.Contains("ntp.cgi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hardened_variants_pass()
    {
        var camera = new FakeCamera();
        camera.Answers["config/rest/firewall/v1"] = """{"status":"success","data":{"activated":true,"conf":{"rules":{"activeDefaultPolicy":"DROP","activeRules":[{"ruleType":"ALLOW"},{"ruleType":"ALLOW"}]}}}}""";
        camera.Answers["config/rest/snmp/v1"] = """{"status":"success","data":{"enabled":true,"snmpV1":{"enabled":false},"snmpV2":{"enabled":false},"snmpV3":{"enabled":true}}}""";
        camera.Answers["config/rest/oidcsetup/v1"] = """{"status":"success","data":{"BaseConfigEntity":{"OIDC_ProviderMetadataURL":"\"https://idp.example.com/.well-known/openid-configuration\""}}}""";
        camera.Answers["config/rest/user-management/v2"] = """{"status":"success","data":{"settings":{"passphraseComplexity":{"policy":"complex"}}}}""";
        camera.Answers["axis-cgi/disks/list.cgi"] = new System.Text.RegularExpressions.Regex("encryptionenabled=\"false\" diskencrypted=\"false\"").Replace(Fixtures.Read("disks-list.xml"), "encryptionenabled=\"true\" diskencrypted=\"true\"", 1);
        camera.Answers["vapix/services"] = Fixtures.Read("soap-GetWebServerTlsConfiguration.xml").Replace(">HttpAndHttps<", ">Https<", StringComparison.Ordinal);
        camera.Answers["axis-cgi/param.cgi"] = Fixtures.Read("paramcgi-hardening.txt")
            .Replace("root.API.RemoteSyslog1.Enabled=false", "root.API.RemoteSyslog1.Enabled=true", StringComparison.Ordinal)
            .Replace("root.API.RemoteSyslog1.Server1.Address=", "root.API.RemoteSyslog1.Server1.Address=10.0.0.17", StringComparison.Ordinal)
            .Replace("root.Network.SSH.Enabled=yes", "root.Network.SSH.Enabled=no", StringComparison.Ordinal)
            .Replace("root.Network.Bonjour.Enabled=yes", "root.Network.Bonjour.Enabled=no", StringComparison.Ordinal)
            .Replace("root.Network.ZeroConf.Enabled=yes", "root.Network.ZeroConf.Enabled=no", StringComparison.Ordinal)
            .Replace("root.Network.RTSPS.Enabled=no", "root.Network.RTSPS.Enabled=yes", StringComparison.Ordinal)
            .Replace("root.System.WebInterfaceDisabled=no", "root.System.WebInterfaceDisabled=yes", StringComparison.Ordinal)
            .Replace("root.Image.I0.MPEG.SignedVideo.Enabled=no", "root.Image.I0.MPEG.SignedVideo.Enabled=yes", StringComparison.Ordinal)
            .Replace("root.System.AccessLog=Off", "root.System.AccessLog=On", StringComparison.Ordinal);
        var device = new TestDevice { DhcpEnabled = false, Dot1xEnabled = true, CertTrustName = "Trusted", CertNotAfterUtc = DateTime.UtcNow.AddDays(200) };

        var results = await ScanAsync(camera, device);

        Expect(results, HardeningCatalog.Firewall, CheckState.Pass, "Firewall on, default drop, 2 rules");
        Expect(results, HardeningCatalog.Snmp, CheckState.Pass, "SNMP v3 on");
        Expect(results, HardeningCatalog.OAuth, CheckState.Pass, "OpenID provider configured");
        Expect(results, HardeningCatalog.PasswordPolicy, CheckState.Pass);
        Expect(results, HardeningCatalog.StorageEncryption, CheckState.Pass, "SD card encrypted");
        Expect(results, HardeningCatalog.HttpsOnly, CheckState.Pass, "HTTPS only");
        Expect(results, HardeningCatalog.RemoteSyslog, CheckState.Pass, "Remote syslog over TLS");
        Expect(results, HardeningCatalog.Ssh, CheckState.Pass);
        Expect(results, HardeningCatalog.Discovery, CheckState.Pass);
        Expect(results, HardeningCatalog.Rtsps, CheckState.Pass);
        Expect(results, HardeningCatalog.WebInterface, CheckState.Pass);
        Expect(results, HardeningCatalog.SignedVideo, CheckState.Pass);
        Expect(results, HardeningCatalog.AccessLog, CheckState.Pass);
        Expect(results, HardeningCatalog.IpConfiguration, CheckState.Pass);
        Expect(results, HardeningCatalog.Dot1x, CheckState.Pass);
        Expect(results, HardeningCatalog.Certificate, CheckState.Pass);
    }

    [Fact]
    public async Task Weak_variants_fail_or_warn()
    {
        var camera = new FakeCamera();
        camera.Answers["config/rest/snmp/v1"] = """{"status":"success","data":{"enabled":true,"snmpV1":{"enabled":false},"snmpV2":{"enabled":true},"snmpV3":{"enabled":false}}}""";
        camera.Answers["config/rest/firewall/v1"] = """{"status":"success","data":{"activated":false,"conf":{"rules":{"activeDefaultPolicy":"ACCEPT","activeRules":[]}}}}""";
        camera.Answers["axis-cgi/param.cgi"] = Fixtures.Read("paramcgi-hardening.txt")
            .Replace("root.API.RemoteSyslog1.Enabled=false", "root.API.RemoteSyslog1.Enabled=true", StringComparison.Ordinal)
            .Replace("root.API.RemoteSyslog1.Server1.Address=", "root.API.RemoteSyslog1.Server1.Address=10.0.0.17", StringComparison.Ordinal)
            .Replace("root.API.RemoteSyslog1.Server1.Protocol=TLS", "root.API.RemoteSyslog1.Server1.Protocol=UDP", StringComparison.Ordinal)
            .Replace("root.HTTPS.Enabled=yes", "root.HTTPS.Enabled=no", StringComparison.Ordinal)
            .Replace("root.Audio.A0.Enabled=no", "root.Audio.A0.Enabled=yes", StringComparison.Ordinal)
            .Replace("root.System.PreventDoSAttack.ActivatePasswordThrottling=On", "root.System.PreventDoSAttack.ActivatePasswordThrottling=Off", StringComparison.Ordinal);
        camera.Answers["vapix/services"] = Fixtures.Read("soap-GetWebServerTlsConfiguration.xml").Replace(">HttpAndHttps<", ">Http<", StringComparison.Ordinal);
        var device = new TestDevice { HttpsEnabled = null, CertTrustName = "Expired", FirmwareVersion = "10.12.236" };

        var results = await ScanAsync(camera, device);

        Expect(results, HardeningCatalog.Snmp, CheckState.Fail, "SNMP v2c on");
        Expect(results, HardeningCatalog.Firewall, CheckState.Fail, "Firewall off");
        Expect(results, HardeningCatalog.RemoteSyslog, CheckState.Warn, "Remote syslog unencrypted (UDP)");
        Expect(results, HardeningCatalog.Ciphers, CheckState.Fail, "HTTPS off");
        Expect(results, HardeningCatalog.Audio, CheckState.Warn, "Audio on (1 of 8 channels)");
        Expect(results, HardeningCatalog.BruteForce, CheckState.Fail, "Password throttling off");
        Expect(results, HardeningCatalog.HttpsOnly, CheckState.Fail, "HTTP only");
        Expect(results, HardeningCatalog.Certificate, CheckState.Fail, "Certificate expired");
        Expect(results, HardeningCatalog.Uart, CheckState.Pass);
    }

    [Fact]
    public async Task Speakers_and_devices_without_video_skip_audio_rtsps_and_signed_video()
    {
        var speaker = new TestDevice { Category = DeviceCategory.Speaker };
        var results = await ScanAsync(new FakeCamera(), speaker);
        Expect(results, HardeningCatalog.Audio, CheckState.NotApplicable, "Audio is the purpose of this device");
        Expect(results, HardeningCatalog.Rtsps, CheckState.NotApplicable, "No video");
        Expect(results, HardeningCatalog.SignedVideo, CheckState.NotApplicable, "No video");
    }

    [Fact]
    public async Task One_failed_read_marks_only_its_checks()
    {
        var camera = new FakeCamera();
        camera.Statuses["config/rest/firewall/v1"] = HttpStatusCode.InternalServerError;
        camera.Hangs.Add("axis-cgi/pwdgrp.cgi");
        var facts = await new DeviceFactsReader(TimeSpan.FromMilliseconds(200)).ReadAsync(camera, new TestDevice(), ScanLevel.Basic, CancellationToken.None);
        var results = HardeningChecks.Evaluate(facts, ScanLevel.Basic).ToDictionary(r => r.Id);

        Expect(results, HardeningCatalog.Firewall, CheckState.Error, "Internal Server Error - HTTP 500");
        Expect(results, HardeningCatalog.Accounts, CheckState.Error, "Timeout after 0.2 s");
        Expect(results, HardeningCatalog.Ssh, CheckState.Fail, "SSH on");
        Expect(results, HardeningCatalog.Ciphers, CheckState.Pass);
    }

    [Fact]
    public async Task An_unreachable_device_is_read_once_and_every_read_check_says_why()
    {
        var camera = new FakeCamera();
        camera.Throws["*"] = new HttpRequestException("No route", new SocketException((int)SocketError.HostUnreachable));
        var facts = await new DeviceFactsReader().ReadAsync(camera, new TestDevice(), ScanLevel.Extended, CancellationToken.None);
        var results = HardeningChecks.Evaluate(facts, ScanLevel.Extended).ToDictionary(r => r.Id);

        Assert.Single(camera.Requests);
        Assert.StartsWith("Unreachable - ", facts.Params.Error, StringComparison.Ordinal);
        Expect(results, HardeningCatalog.Ssh, CheckState.Error);
        Expect(results, HardeningCatalog.Accounts, CheckState.Error);
        Expect(results, HardeningCatalog.HttpsOnly, CheckState.Error);
        Expect(results, HardeningCatalog.AxisOs, CheckState.Info);
        Expect(results, HardeningCatalog.Uart, CheckState.Pass);
    }

    [Fact]
    public async Task Rejected_credentials_are_one_device_level_error()
    {
        var camera = new FakeCamera();
        camera.Statuses["axis-cgi/param.cgi"] = HttpStatusCode.Unauthorized;
        var facts = await new DeviceFactsReader().ReadAsync(camera, new TestDevice(), ScanLevel.Basic, CancellationToken.None);
        Assert.Equal(DeviceMessages.Unauthorized, facts.Params.Error);
        Assert.Equal(DeviceMessages.Unauthorized, facts.Users.Error);
        Assert.Single(camera.Requests);
    }

    [Fact]
    public void Cache_only_results_keep_the_version_uart_and_certificate()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero); // one fixed time: two clock reads can be equal
        var device = new TestDevice { CertTrustName = "Trusted", CertNotAfterUtc = now.UtcDateTime.AddDays(9.5) };
        var results = HardeningChecks.CacheOnly(device, ScanLevel.Extended, "Credentials required - the device rejects the stored credentials", now).ToDictionary(r => r.Id);
        Expect(results, HardeningCatalog.AxisOs, CheckState.Info);
        Expect(results, HardeningCatalog.Uart, CheckState.Pass);
        Expect(results, HardeningCatalog.Certificate, CheckState.Warn, "Trusted certificate, expires in 9 days");
        Expect(results, HardeningCatalog.Ssh, CheckState.Error, "Credentials required - the device rejects the stored credentials");
    }

    [Fact]
    public void The_catalog_has_the_guide_rows_with_the_decided_levels()
    {
        Assert.Equal(15, HardeningCatalog.ColumnsOf(ScanLevel.Basic).Count);
        Assert.Equal(26, HardeningCatalog.Columns.Count);
        Assert.Equal(6, HardeningCatalog.InfoOf(ScanLevel.Basic).Count);
        Assert.Equal(9, HardeningCatalog.InfoOf(ScanLevel.Extended).Count);
        Assert.All(HardeningCatalog.All.Where(c => c.Id.StartsWith('X')), c => Assert.Equal(ScanLevel.Extended, c.Level));
        Assert.False(HardeningCatalog.All.Single(c => c.Id == HardeningCatalog.AxisOs).IsRated);
        Assert.Contains("OADM", HardeningCatalog.All.Single(c => c.Id == HardeningCatalog.Discovery).Note, StringComparison.Ordinal);
        Assert.Equal(HardeningCatalog.All.Count, HardeningCatalog.All.Select(c => c.Id).Distinct().Count());
        Assert.All(HardeningCatalog.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Recommendation)));
        Assert.Equal(HardeningCatalog.ColumnIds, Oadm.Client.Api.FakeOadmApi.FakeHardeningColumns);
    }
}
