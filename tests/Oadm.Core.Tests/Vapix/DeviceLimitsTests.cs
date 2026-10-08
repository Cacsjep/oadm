using System.Net;
using System.Xml;

using Oadm.Core.Vapix;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Vapix;

/// <summary>Production hardening 4: size limits for device answers and XML.</summary>
public sealed class DeviceLimitsTests
{
    private static readonly Uri Base = new("https://10.0.0.48/");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAnswerLargerThan16MegabytesIsRefused(bool withLength)
    {
        var handler = new FakeHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = Body(17 * 1024 * 1024, withLength) });
        using var client = new VapixClient(Base, handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "axis-cgi/jpg/image.cgi");
        var ex = await Assert.ThrowsAsync<VapixResponseTooLargeException>(() => client.SendAsync(request, CancellationToken.None));

        Assert.Contains("16 MB", ex.Message, StringComparison.Ordinal);
        Assert.Equal(16L * 1024 * 1024, VapixClient.MaxResponseBytes);
    }

    [Fact]
    public async Task AnAnswerJustBelowTheLimitIsRead()
    {
        var handler = new FakeHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = Body((16 * 1024 * 1024) - 1, withLength: false) });
        using var client = new VapixClient(Base, handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "axis-cgi/jpg/image.cgi");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal((16 * 1024 * 1024) - 1, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public void XmlWithADtdIsRefused()
    {
        const string bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;">]>
            <reply result="ok">&lol2;</reply>
            """;
        Assert.Throws<XmlException>(() => DeviceXml.Parse(bomb));
        Assert.Throws<XmlException>(() => DeviceXml.ParseElement("<!DOCTYPE r SYSTEM \"http://evil.invalid/x.dtd\"><r/>"));
    }

    [Fact]
    public void XmlLargerThan1MegabyteIsRefused()
    {
        var big = "<r>" + new string('a', DeviceXml.MaxCharacters) + "</r>";
        Assert.Throws<XmlException>(() => DeviceXml.Parse(big));

        // Callers' own settings cannot lift the limits.
        var lax = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, MaxCharactersInDocument = 0 };
        Assert.Throws<XmlException>(() => DeviceXml.Parse("<!DOCTYPE r [<!ENTITY e \"x\">]><r>&e;</r>", lax));
    }

    [Fact]
    public void NormalXmlParses()
    {
        var root = DeviceXml.ParseElement("""<reply result="ok"><application Name="a"/></reply>""");
        Assert.Equal("reply", root.Name.LocalName);
        Assert.Single(root.Elements("application"));
    }

    private static HttpContent Body(int size, bool withLength)
    {
        var data = new byte[size];
        return withLength ? new ByteArrayContent(data) : new StreamContent(new UnknownLengthStream(data));
    }

    /// <summary>A stream that does not report its length, so the client cannot reject the answer up front.</summary>
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
