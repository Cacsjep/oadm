using Oadm.Plugins.Radius.Protocol.Text;
using Oadm.Plugins.Radius.Protocol.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>MAC addresses of every vendor format, text sanitizing, NAS-Port-Type names and the TLS alert texts.</summary>
public sealed class TextTests
{
    [Theory]
    [InlineData("AC-CC-8E-12-34-56")] // RFC 3580 3.21, Cisco IOS, Juniper
    [InlineData("ac-cc-8e-12-34-56")]
    [InlineData("AC:CC:8E:12:34:56")] // HPE Aruba, Netgear, MikroTik
    [InlineData("ac:cc:8e:12:34:56")] // Ubiquiti, Zyxel
    [InlineData("accc.8e12.3456")] // Cisco dotted
    [InlineData("ACCC.8E12.3456")]
    [InlineData("accc-8e12-3456")]
    [InlineData("accc:8e12:3456")]
    [InlineData("accc8e123456")] // no separators (HPE ProCurve, FreeRADIUS style)
    [InlineData("ACCC8E123456")]
    [InlineData("accc8e-123456")] // HPE single dash
    [InlineData("AC.CC.8E.12.34.56")]
    [InlineData(" AC-CC-8E-12-34-56 ")]
    [InlineData("\"AC-CC-8E-12-34-56\"")]
    [InlineData("'ac:cc:8e:12:34:56'")]
    [InlineData("AC-CC-8E-12-34-56:CameraNet")] // wireless "MAC:SSID" (RFC 3580 3.20 style)
    [InlineData("accc8e123456:SSID with spaces")]
    public void EveryVendorFormatIsNormalized(string value)
    {
        var mac = MacAddress.Parse(value);

        Assert.True(mac.IsValid);
        Assert.Equal("AC:CC:8E:12:34:56", mac.Normalized);
        Assert.Equal("AC:CC:8E:12:34:56", mac.Display);
        Assert.Equal("ACCC8E123456", mac.Serial);
    }

    [Theory]
    [InlineData("AC-CC-8E-12-34")] // 10 digits
    [InlineData("AC-CC-8E-12-34-56-78")] // EUI-64
    [InlineData("AC:CC:8E:12:34:56:78:9A")]
    [InlineData("ACCC8E1234567")] // 13 digits
    [InlineData("AC-CC:8E-12:34-56")] // mixed separators
    [InlineData("ACC-C8E-123-456")] // groups of 3
    [InlineData("AC-CC-8E-12-34-5G")]
    [InlineData("camera-1")]
    [InlineData("-AC-CC-8E-12-34-56")]
    [InlineData("AC--CC-8E-12-34-56")]
    [InlineData("ACCC8E123456 extra")]
    [InlineData("192.168.1.20")]
    public void AnythingElseIsShownAsSentAndNeverMatched(string value)
    {
        var mac = MacAddress.Parse(value);

        Assert.False(mac.IsValid);
        Assert.Null(mac.Normalized);
        Assert.Null(mac.Serial);
        Assert.Equal(value.Trim(), mac.Display);
    }

    [Fact]
    public void NoCallingStationIdIsEmpty()
    {
        Assert.Equal(string.Empty, MacAddress.Parse(null).Display);
        Assert.Equal(string.Empty, MacAddress.Parse(string.Empty).Display);
    }

    [Fact]
    public void AnInvalidMacIsShownSanitized()
    {
        Assert.Equal("camera", MacAddress.Parse("cam\u0007era‮").Display);
    }

    [Fact]
    public void ControlAndFormatCharactersAreRemovedAndInvalidUtf8Replaced()
    {
        Assert.Equal("ab", RadiusText.Sanitize("a\r\n\t\u0000\u001B‎⁦b"));
        Assert.Equal("Grüße 日本", RadiusText.Decode("Grüße 日本"u8));
        Assert.Equal("a�b", RadiusText.Decode([(byte)'a', 0xC3, (byte)'b']));
        Assert.Equal(string.Empty, RadiusText.Decode([]));
    }

    [Theory]
    [InlineData(15u, "Ethernet")]
    [InlineData(19u, "Wireless")]
    [InlineData(18u, "Wireless")]
    [InlineData(5u, "Virtual")]
    [InlineData(21u, "FDDI")]
    [InlineData(40u, "Type 40")]
    public void NasPortTypesHaveTheirNames(uint value, string name)
    {
        Assert.Equal(name, NasPortTypes.Name(value));
        Assert.Null(NasPortTypes.Name(null));
    }

    [Theory]
    [InlineData(48, TlsAlertDirection.SentByDevice, "The device does not trust the server certificate (TLS alert 48, unknown CA)")]
    [InlineData(48, TlsAlertDirection.SentByOadm, "Client certificate not issued by a trusted CA (TLS alert 48, unknown CA)")]
    [InlineData(10, TlsAlertDirection.SentByDevice, "Unexpected message (TLS alert 10, unexpected message)")]
    [InlineData(20, TlsAlertDirection.SentByOadm, "Message damaged on the way (TLS alert 20, bad record MAC)")]
    [InlineData(40, TlsAlertDirection.SentByOadm, "No common security settings (TLS alert 40, handshake failure)")]
    [InlineData(42, TlsAlertDirection.SentByDevice, "The device rejected the server certificate (TLS alert 42, bad certificate)")]
    [InlineData(42, TlsAlertDirection.SentByOadm, "Client certificate rejected (TLS alert 42, bad certificate)")]
    [InlineData(43, TlsAlertDirection.SentByOadm, "Client certificate type not supported (TLS alert 43, unsupported certificate)")]
    [InlineData(44, TlsAlertDirection.SentByOadm, "Client certificate revoked (TLS alert 44, certificate revoked)")]
    [InlineData(45, TlsAlertDirection.SentByDevice, "The device says the server certificate expired (TLS alert 45, certificate expired)")]
    [InlineData(46, TlsAlertDirection.SentByOadm, "Client certificate not accepted (TLS alert 46, certificate unknown)")]
    [InlineData(47, TlsAlertDirection.SentByDevice, "Invalid value in the handshake (TLS alert 47, illegal parameter)")]
    [InlineData(49, TlsAlertDirection.SentByOadm, "Access denied (TLS alert 49, access denied)")]
    [InlineData(50, TlsAlertDirection.SentByOadm, "Message could not be read (TLS alert 50, decode error)")]
    [InlineData(51, TlsAlertDirection.SentByOadm, "Signature check failed (TLS alert 51, decrypt error)")]
    [InlineData(70, TlsAlertDirection.SentByOadm, "TLS version not supported (TLS alert 70, protocol version)")]
    [InlineData(71, TlsAlertDirection.SentByDevice, "Security settings too weak (TLS alert 71, insufficient security)")]
    [InlineData(80, TlsAlertDirection.SentByDevice, "Internal error (TLS alert 80, internal error)")]
    [InlineData(86, TlsAlertDirection.SentByOadm, "Fallback refused (TLS alert 86, inappropriate fallback)")]
    [InlineData(90, TlsAlertDirection.SentByDevice, "Canceled (TLS alert 90, user canceled)")]
    [InlineData(109, TlsAlertDirection.SentByOadm, "Required extension missing (TLS alert 109, missing extension)")]
    [InlineData(110, TlsAlertDirection.SentByDevice, "Extension not supported (TLS alert 110, unsupported extension)")]
    [InlineData(112, TlsAlertDirection.SentByDevice, "Server name not known (TLS alert 112, unrecognized name)")]
    [InlineData(116, TlsAlertDirection.SentByOadm, "No client certificate sent (TLS alert 116, certificate required)")]
    [InlineData(99, TlsAlertDirection.SentByOadm, "TLS alert 99")]
    [InlineData(0, TlsAlertDirection.SentByDevice, "TLS alert 0")]
    public void EveryAlertHasItsTextInBothDirections(byte alert, TlsAlertDirection direction, string text)
    {
        Assert.Equal(text, TlsAlertTexts.Describe(alert, direction));
        Assert.Equal(text, new TlsAlert(alert, direction).Text);
    }
}
