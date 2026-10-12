using System.Net;

using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

/// <summary>The login of WebSocket upgrades of the Web View relay (the handler does not answer those challenges).</summary>
public sealed class WebSocketLoginTests
{
    private static readonly NetworkCredential Mufasa = new("Mufasa", "Circle Of Life");

    [Fact]
    public void DigestMatchesTheRfcExample()
    {
        // RFC 2617 section 3.5 (GET /dir/index.html, qop auth, nc 00000001, cnonce 0a4f113b).
        var header = WebSocketLogin.Answer(
            ["Digest realm=\"testrealm@host.com\", qop=\"auth,auth-int\", nonce=\"dcd98b7102dd2f0e8b11d0f600bfb0c093\", opaque=\"5ccc069c403ebaf9f0171e9517f40e41\""],
            "/dir/index.html", Mufasa, https: false, cnonce: "0a4f113b");

        Assert.NotNull(header);
        Assert.Contains("response=\"6629fae49393a05397450978507c4ef1\"", header, StringComparison.Ordinal);
        Assert.Contains("opaque=\"5ccc069c403ebaf9f0171e9517f40e41\"", header, StringComparison.Ordinal);
        Assert.Contains("qop=auth, nc=00000001, cnonce=\"0a4f113b\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Sha256DigestIsSupported()
    {
        var header = WebSocketLogin.Answer(["Digest realm=\"AXIS_ACCC8E000001\", nonce=\"abc\", algorithm=SHA-256, qop=\"auth\""], "/ws", Mufasa, https: false, cnonce: "c");
        var ha1 = WebSocketLogin.Hash("SHA-256", "Mufasa:AXIS_ACCC8E000001:Circle Of Life");
        var ha2 = WebSocketLogin.Hash("SHA-256", "GET:/ws");
        Assert.Contains($"response=\"{WebSocketLogin.Hash("SHA-256", $"{ha1}:abc:00000001:c:auth:{ha2}")}\"", header!, StringComparison.Ordinal);
    }

    [Fact]
    public void BasicIsSentOnlyOverHttps()
    {
        Assert.Null(WebSocketLogin.Answer(["Basic realm=\"AXIS\""], "/ws", Mufasa, https: false));
        Assert.Equal("Basic " + Convert.ToBase64String("Mufasa:Circle Of Life"u8.ToArray()), WebSocketLogin.Answer(["Basic realm=\"AXIS\""], "/ws", Mufasa, https: true));
        // Digest wins when both are offered.
        Assert.StartsWith("Digest ", WebSocketLogin.Answer(["Basic realm=\"AXIS\"", "Digest realm=\"r\", nonce=\"n\""], "/ws", Mufasa, https: true), StringComparison.Ordinal);
    }
}

/// <summary>The cookies of the relay's second try.</summary>
public sealed class RelayCookieTests
{
    [Fact]
    public void NewDeviceCookiesReplaceThePagesOnes() =>
        Assert.Equal("lang=en; _axis=new", VapixClient.MergeCookies("_axis=old; lang=en", ["_axis=new; Path=/; HttpOnly"]));

    [Fact]
    public void WithoutPageCookiesOnlyTheNewOnesGo() =>
        Assert.Equal("_axis=x", VapixClient.MergeCookies(null, ["_axis=x; Path=/"]));
}
