using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

public class VapixParsersTests
{
    [Fact]
    public void ParseBasicDeviceInfoReadsRecordedP3265()
    {
        var info = VapixParsers.ParseBasicDeviceInfo(Fixtures.Read("basicdeviceinfo-getAllProperties.json"));

        Assert.Equal("B8A44F631339", info.SerialNumber);
        Assert.Equal("P3265-V", info.ProdNbr);
        Assert.Equal("Dome Camera", info.ProdType);
        Assert.Equal("AXIS P3265-V", info.ProdShortName);
        Assert.Equal("AXIS P3265-V Dome Camera", info.ProdFullName);
        Assert.Equal("12.11.77", info.Version);
        Assert.Equal("931.11", info.HardwareId);
        Assert.Equal("aarch64", info.Architecture);
    }

    [Fact]
    public void ParseBasicDeviceInfoThrowsOnErrorResponse()
    {
        const string json = """{"apiVersion":"1.3","error":{"code":4002,"message":"bad"}}""";
        var ex = Assert.Throws<VapixException>(() => VapixParsers.ParseBasicDeviceInfo(json));
        Assert.Contains("4002", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseBasicDeviceInfoThrowsOnInvalidJson()
    {
        Assert.Throws<VapixException>(() => VapixParsers.ParseBasicDeviceInfo("<html>"));
    }

    [Theory]
    [InlineData("401-http-headers.txt")]
    [InlineData("401-https-headers.txt")]
    public void SerialFromRecordedRealm(string headerFile)
    {
        var values = Fixtures.WwwAuthenticate(headerFile);
        Assert.NotEmpty(values);
        Assert.Equal("B8A44F631339", VapixParsers.ParseSerialFromRealm(values));
    }

    [Fact]
    public void RecordedRealmsShowDigestOnHttpAndBasicOnHttps()
    {
        Assert.StartsWith("Digest ", Fixtures.WwwAuthenticate("401-http-headers.txt")[0], StringComparison.Ordinal);
        Assert.StartsWith("Basic ", Fixtures.WwwAuthenticate("401-https-headers.txt")[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Digest realm=\"axis_accc8e012345\", nonce=\"x\"", null)]
    [InlineData("Basic realm=\"Synology\"", null)]
    [InlineData("Digest realm=\"AXIS_accc8e012345\", nonce=\"x\"", "ACCC8E012345")]
    [InlineData(null, null)]
    public void SerialFromRealmHandlesOtherRealms(string? header, string? expected)
    {
        Assert.Equal(expected, VapixParsers.ParseSerialFromRealm(header));
    }

    [Fact]
    public void ParseParameterListStripsRootPrefix()
    {
        var p = VapixParsers.ParseParameterList(Fixtures.Read("param-list-networkinfo.txt"));

        Assert.Equal("dhcp", p["Network.BootProto"]);
        Assert.Equal("yes", p["HTTPS.Enabled"]);
        Assert.Equal("443", p["HTTPS.Port"]);
        Assert.Equal("no", p["Network.Interface.I0.dot1x.Enabled"]);
        Assert.Equal("AXIS P3265-V - B8A44F631339", p["Network.UPnP.FriendlyName"]);
        Assert.DoesNotContain(p.Keys, k => k.StartsWith("root.", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseParameterListKeepsEqualsInValues()
    {
        var p = VapixParsers.ParseParameterList(Fixtures.Read("param-list-network.txt"));
        Assert.Equal("videocodec=h264", p["Network.RTP.R0.AlwaysMulticastProfile"]);
        Assert.Equal("B8:A4:4F:63:13:39", p["Network.eth0.MACAddress"]);
        Assert.True(p.Count > 100);
    }

    [Fact]
    public void ParseParameterListReportsPerGroupErrors()
    {
        var p = VapixParsers.ParseParameterList(Fixtures.Read("param-list-mixed-error.txt"), out var errors);

        Assert.Equal(2, p.Count);
        Assert.Equal("dhcp", p["Network.BootProto"]);
        Assert.Equal("yes", p["HTTPS.Enabled"]);
        var error = Assert.Single(errors);
        Assert.Contains("Does.Not.Exist", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseNetworkInfoFromRecordedParameters()
    {
        var info = VapixParsers.ParseNetworkInfo(VapixParsers.ParseParameterList(Fixtures.Read("param-list-networkinfo.txt")));

        Assert.Equal(new NetworkInfo(true, true, false, "AXIS P3265-V - B8A44F631339"), info);
    }

    [Fact]
    public void ParseNetworkInfoWithMissingParametersYieldsNulls()
    {
        var info = VapixParsers.ParseNetworkInfo(new Dictionary<string, string> { ["Network.BootProto"] = "none" });
        Assert.Equal(new NetworkInfo(false, null, null, null), info);
    }

    [Fact]
    public void ParseSystemReadyFromRecording()
    {
        var ready = VapixParsers.ParseSystemReady(Fixtures.Read("systemready.json"));

        Assert.True(ready.SystemReady);
        Assert.False(ready.NeedSetup);
        Assert.Equal("none", ready.PassphrasePolicy);
        Assert.Equal(12484, ready.UptimeSeconds);
        Assert.Equal("93bfbb4a-9c3f-4075-a0c5-8702dfccfc04", ready.BootId);
    }

    [Fact]
    public void ParseSystemReadyFactoryDefault()
    {
        const string json = """{"apiVersion":"1.5","method":"systemready","data":{"systemready":"yes","needsetup":"yes"}}""";
        var ready = VapixParsers.ParseSystemReady(json);
        Assert.True(ready.NeedSetup);
        Assert.Null(ready.PassphrasePolicy);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("No", false)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("maybe", null)]
    [InlineData(null, null)]
    public void ParseBool(string? value, bool? expected)
    {
        Assert.Equal(expected, VapixParsers.ParseBool(value));
    }

    [Theory]
    [InlineData("Created account root.", true)]
    [InlineData("# Error: account already exists", false)]
    public void PwdgrpResult(string body, bool expected)
    {
        Assert.Equal(expected, VapixParsers.IsPwdgrpSuccess(body));
    }

    [Fact]
    public void NormalizeSerialRemovesSeparators()
    {
        Assert.Equal("B8A44F631339", VapixParsers.NormalizeSerial("b8:a4:4f:63:13:39"));
    }

    [Fact]
    public void ApiDiscoveryRecordingListsNetworkSettingsAndSystemReady()
    {
        // Evidence for the API surface of AXIS OS 12.11 used by this client.
        var json = Fixtures.Read("apidiscovery-getApiList.json");
        Assert.Contains("\"id\": \"systemready\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\": \"param-cgi\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\": \"basic-device-info\"", json, StringComparison.Ordinal);
    }
}
