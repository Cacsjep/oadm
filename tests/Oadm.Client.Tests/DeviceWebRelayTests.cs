using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Api;
using Oadm.Client.DeviceWeb;
using Oadm.Sdk.Client;

namespace Oadm.Client.Tests;

/// <summary>
/// The Web View relay on 127.0.0.1 against the fake server: the session cookie, the page through Exchange, request
/// bodies, WebSockets, and that other programs on the computer get nothing.
/// </summary>
public sealed class DeviceWebRelayTests
{
    private static async Task<(FakeOadmApi Api, Guid Device)> FakeAsync()
    {
        var api = new FakeOadmApi();
        var device = (await api.ListDevicesAsync(CancellationToken.None)).First(d => d.Status == Contracts.V1.DeviceStatus.Ok);
        return (api, Guid.Parse(device.Id));
    }

    private static HttpClient Browser(CookieContainer cookies) =>
        new(new SocketsHttpHandler { CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = true, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task The_start_address_hands_out_the_cookie_and_opens_the_page()
    {
        var (api, device) = await FakeAsync();
        using var relay = new DeviceWebRelay(api, device, NullLogger.Instance);
        var cookies = new CookieContainer();
        using var browser = Browser(cookies);

        var page = await browser.GetStringAsync(relay.StartAddress("/local/objectanalytics/index.html#/"));

        Assert.Contains("Web interface (fake mode): GET /local/objectanalytics/index.html", page, StringComparison.Ordinal);
        Assert.Equal(DeviceWebRelay.CookieName + "_" + relay.Origin.Port, Assert.Single(cookies.GetCookies(relay.Origin)).Name);
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(relay.Origin.Host)));

        // Later requests of the page carry the cookie.
        var other = await browser.GetStringAsync(new Uri(relay.Origin, "/axis-cgi/param.cgi?action=list"));
        Assert.Contains("GET /axis-cgi/param.cgi?action=list", other, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_relays_keep_their_cookies_apart()
    {
        // Browsers keep cookies per host, not per port: two tabs on one host would overwrite each other's session and
        // the devices' own login cookies (same names on every Axis device).
        var (api, device) = await FakeAsync();
        using var first = new DeviceWebRelay(api, device, NullLogger.Instance);
        using var second = new DeviceWebRelay(api, device, NullLogger.Instance);
        var cookies = new CookieContainer();
        using var browser = Browser(cookies);
        await browser.GetStringAsync(first.StartAddress("/"));
        await browser.GetStringAsync(second.StartAddress("/"));

        // Both tabs still work after the other one started.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(new Uri(first.Origin, "/x"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(new Uri(second.Origin, "/x"))).StatusCode);
        if (!OperatingSystem.IsMacOS())
        {
            Assert.NotEqual(first.Origin.Host, second.Origin.Host); // own cookie jar per tab
        }
    }

    [Fact]
    public async Task Without_the_cookie_or_with_a_wrong_token_or_host_nothing_is_relayed()
    {
        var (api, device) = await FakeAsync();
        using var relay = new DeviceWebRelay(api, device, NullLogger.Instance);
        using var browser = Browser(new CookieContainer());

        var direct = await browser.GetAsync(new Uri(relay.Origin, "/"));
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        // Marked, so the tab opens the page again through the start address (lost cookies).
        Assert.Contains("<title>" + DeviceWebPages.SessionLostTitle + "</title>", await direct.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var wrongToken = await browser.GetAsync(new Uri(relay.Origin, DeviceWebRelay.StartPath + "?token=guess&path=%2F"));
        Assert.Equal(HttpStatusCode.Forbidden, wrongToken.StatusCode);

        var outside = relay.StartAddress("//evil.example.com/");
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync(outside)).StatusCode);

        // DNS rebinding: another host name pointing at 127.0.0.1 is refused.
        using var request = new HttpRequestMessage(HttpMethod.Get, relay.StartAddress("/"));
        request.Headers.Host = "evil.example.com";
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Request_bodies_reach_the_device_with_a_length_or_in_chunks()
    {
        var (api, device) = await FakeAsync();
        using var relay = new DeviceWebRelay(api, device, NullLogger.Instance);
        using var browser = Browser(new CookieContainer());
        await browser.GetStringAsync(relay.StartAddress("/"));

        var form = await browser.PostAsync(new Uri(relay.Origin, "/axis-cgi/form.cgi"), new StringContent("a=1&b=2"));
        Assert.Contains("Body: 7 bytes", await form.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var big = new byte[300_000];
        using var chunked = new StreamContent(new NonSeekableStream(big));
        var upload = await browser.PostAsync(new Uri(relay.Origin, "/axis-cgi/upload.cgi"), chunked);
        Assert.Contains("Body: 300000 bytes", await upload.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_websocket_of_the_page_is_relayed_both_ways()
    {
        var (api, device) = await FakeAsync();
        using var relay = new DeviceWebRelay(api, device, NullLogger.Instance);
        var cookies = new CookieContainer();
        using (var browser = Browser(cookies))
        {
            await browser.GetStringAsync(relay.StartAddress("/"));
        }

        using var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        socket.Options.AddSubProtocol("events");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await socket.ConnectAsync(new Uri("ws://" + relay.Origin.Authority + "/vapix/ws-data-stream?sources=events"), timeout.Token);
        Assert.Equal("events", socket.SubProtocol);

        await socket.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[64];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal("hello", Encoding.UTF8.GetString(buffer, 0, result.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
    }

    [Fact]
    public async Task A_disposed_relay_stops_listening()
    {
        var (api, device) = await FakeAsync();
        var relay = new DeviceWebRelay(api, device, NullLogger.Instance);
        var port = relay.Origin.Port;
        relay.Dispose();

        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task Access_refuses_devices_the_server_refuses_and_stops_its_relays_when_disposed()
    {
        var api = new FakeOadmApi();
        var devices = await api.ListDevicesAsync(CancellationToken.None);
        var refused = devices.First(d => d.Status == Contracts.V1.DeviceStatus.CredentialsRequired);
        var ok = devices.First(d => d.Status == Contracts.V1.DeviceStatus.Ok);
        var access = new DeviceWebAccess(api, NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<DeviceWebRefusedException>(() => access.OpenAsync(Guid.Parse(refused.Id), CancellationToken.None));
        Assert.Equal(Oadm.Sdk.Devices.DeviceMessages.CredentialsRequired, ex.Message);

        var session = (DeviceWebRelay)await access.OpenAsync(Guid.Parse(ok.Id), CancellationToken.None);
        access.Dispose();
        Assert.True(session.IsDisposed);
    }

    [Theory]
    [InlineData("session=1; Path=/; Domain=10.0.0.48; Secure; HttpOnly; SameSite=None", "session=1; Path=/; HttpOnly")]
    [InlineData("a=b; SameSite=Strict", "a=b; SameSite=Strict")]
    public void Device_cookies_fit_the_relay(string device, string page) => Assert.Equal(page, DeviceWebRelay.PageCookie(device));

    /// <summary>A stream without a length: HttpClient sends it chunked.</summary>
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
