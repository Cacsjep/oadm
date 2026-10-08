using System.Net.Http.Headers;
using System.Text;

using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

/// <summary>Device answers with charsets .NET does not know stay readable as text (firmware status on older AXIS OS).</summary>
public sealed class CharsetTests
{
    [Theory]
    [InlineData("utf8", "utf-8")]
    [InlineData("\"utf-8\"", "utf-8")]
    [InlineData("x-axis-unknown", "utf-8")]
    [InlineData("iso-8859-1", "iso-8859-1")]
    public async Task UnknownOrOddCharsetsBecomeReadable(string sent, string expected)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"status\":\"ok\"}"));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/json; charset={sent}");

        VapixClient.NormalizeCharset(content);

        Assert.Equal(expected, content.Headers.ContentType!.CharSet);
        Assert.Equal("{\"status\":\"ok\"}", await content.ReadAsStringAsync());
    }

    [Fact]
    public void ContentWithoutCharsetIsLeftAlone()
    {
        using var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        VapixClient.NormalizeCharset(content);
        VapixClient.NormalizeCharset(null);
        Assert.Null(content.Headers.ContentType.CharSet);
    }
}
