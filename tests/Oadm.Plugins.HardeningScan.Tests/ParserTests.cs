using Oadm.Plugins.HardeningScan.Device;

namespace Oadm.Plugins.HardeningScan.Tests;

public sealed class ParserTests
{
    [Fact]
    public void The_recorded_param_answer_is_read_without_the_root_prefix()
    {
        var list = ParamList.Parse(Fixtures.Read("paramcgi-hardening.txt"));
        Assert.Equal(43, list.Count);
        Assert.Empty(list.Errors);
        Assert.Equal("dhcp", list["Network.BootProto"]);
        Assert.True(list.Bool("Network.SSH.Enabled"));
        Assert.False(list.Bool("WebService.DiscoveryMode.Discoverable"));
        Assert.True(list.Bool("System.PreventDoSAttack.ActivatePasswordThrottling"));
        Assert.Equal("oneclick", list["RemoteService.Enabled"]);
        Assert.Null(list["System.HTTPServerHeaderComments"]);
    }

    [Fact]
    public void Error_lines_mean_not_available_and_the_group_after_them_may_lack_the_root_prefix()
    {
        // AXIS OS 12.11: the group right after an error line came back without "root.".
        var list = ParamList.Parse("root.HTTPS.Enabled=yes\r\n# Error: Error -1 getting param in group 'Network.Filter'\r\nSystem.WebInterfaceDisabled=no\r\nroot.System.AccessLog=Off\r\n\r\n");
        Assert.Single(list.Errors);
        Assert.Contains("Network.Filter", list.Errors[0], StringComparison.Ordinal);
        Assert.False(list.Bool("System.WebInterfaceDisabled"));
        Assert.False(list.Bool("system.accesslog"));
        Assert.True(list.Bool("HTTPS.Enabled"));
        Assert.Null(list.Bool("Network.Filter.Enabled"));
        Assert.False(list.Has("Network.Filter.Enabled"));
    }

    [Fact]
    public void Values_keep_equals_signs_and_unknown_texts_are_no_bool()
    {
        var list = ParamList.Parse("root.A.B=x=y\nroot.A.C=maybe\nnot a parameter\n");
        Assert.Equal("x=y", list["A.B"]);
        Assert.Null(list.Bool("A.C"));
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Discover_lists_the_rest_apis_by_name_and_major_version()
    {
        var apis = DeviceParsers.ParseDiscover(Fixtures.Read("config-discover.json"));
        Assert.True(apis.ContainsKey("firewall/v1"));
        Assert.True(apis.ContainsKey("user-management/v2"));
        Assert.True(apis.ContainsKey("ssh/v2"));
        Assert.Equal("released", apis["snmp/v1"].State);
        Assert.Empty(DeviceParsers.ParseDiscover("<html>not json</html>"));
    }

    [Fact]
    public void Rest_answers_are_parsed()
    {
        Assert.Equal("none", DeviceParsers.ParsePasswordPolicy(Fixtures.Read("rest-user-management-v2-get.json")));
        var firewall = DeviceParsers.ParseFirewall(Fixtures.Read("rest-firewall-v1-get.json"));
        Assert.True(firewall.Activated);
        Assert.Equal("ACCEPT", firewall.DefaultPolicy);
        Assert.Equal(["LIMIT"], firewall.RuleTypes);
        Assert.True(DeviceParsers.ParseLldpActivated(Fixtures.Read("rest-lldp-v1-get.json")));
        Assert.Equal(new SnmpInfo(false, false, false, false), DeviceParsers.ParseSnmp(Fixtures.Read("rest-snmp-v1-get.json")));
        Assert.Null(DeviceParsers.ParseOidcProvider(Fixtures.Read("rest-oidcsetup-v1-get.json")));
        var error = Assert.Throws<FormatException>(() => DeviceParsers.ParseFirewall("""{"status":"error","error":{"code":2001,"message":"Not allowed"}}"""));
        Assert.Contains("Not allowed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disks_are_read_through_the_safe_xml_parser()
    {
        var disks = DeviceParsers.ParseDisks(Fixtures.Read("disks-list.xml"));
        Assert.Equal(2, disks.Count);
        Assert.True(disks[0].IsSdCard);
        Assert.True(disks[0].IsConnected);
        Assert.False(disks[0].Encrypted);
        Assert.Equal("S0", disks[0].Group);
        Assert.False(disks[1].IsConnected);
        Assert.Throws<FormatException>(() => DeviceParsers.ParseDisks("<!DOCTYPE x [<!ENTITY a \"b\">]><root>&a;</root>"));
    }
}
