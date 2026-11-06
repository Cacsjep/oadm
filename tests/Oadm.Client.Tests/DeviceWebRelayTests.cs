using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

using Grpc.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

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

    /// <summary>The embedded browser: like it, the requests say that they come from the relay's own page.</summary>
    private static HttpClient Browser(CookieContainer cookies)
    {
        var browser = Bare(cookies);
        browser.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        return browser;
    }

    /// <summary>A client that sends only the headers a test gives it.</summary>
    private static HttpClient Bare(CookieContainer cookies) =>
        new(new SocketsHttpHandler { CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = true, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };

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
        socket.Options.SetRequestHeader("Origin", relay.Origin.GetLeftPart(UriPartial.Authority)); // like a browser
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

    [Fact]
    public async Task On_the_shared_address_only_the_relays_own_page_gets_through()
    {
        // macOS: every relay is on 127.0.0.1 and the browser sends this relay's cookie to requests of every relay's page.
        var (api, device) = await FakeAsync();
        using var relay = new DeviceWebRelay(api, device, NullLogger.Instance, ownAddress: false);
        using var other = new DeviceWebRelay(api, device, NullLogger.Instance, ownAddress: false);
        Assert.Equal("127.0.0.1", relay.Origin.Host);
        var own = relay.Origin.GetLeftPart(UriPartial.Authority);
        var foreign = other.Origin.GetLeftPart(UriPartial.Authority);
        var cookies = new CookieContainer();
        using var browser = Bare(cookies);

        // The browser's own first load: a page, no Origin, no Sec-Fetch-Site, no Referer.
        using (var first = new HttpRequestMessage(HttpMethod.Get, relay.StartAddress("/")))
        {
            first.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            using var opened = await browser.SendAsync(first);
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        }

        (string Method, string[] Headers)[] passes =
        [
            ("GET", ["Sec-Fetch-Site: same-origin"]), // a picture, a script, fetch of the page
            ("GET", ["Sec-Fetch-Site: none", "Accept: text/html"]), // the tab opens a page itself
            ("POST", ["Origin: " + own, "Sec-Fetch-Site: same-origin"]),
            ("POST", ["Origin: " + own]),
            ("GET", ["Accept: text/html"]), // a browser without Sec-Fetch-Site opens a page itself
            ("GET", ["Referer: " + own + "/camera/index.html"]), // its page's picture
        ];
        foreach (var (method, headers) in passes)
        {
            using var response = await SendAsync(browser, relay, method, headers);
            Assert.True(response.StatusCode == HttpStatusCode.OK, method + " " + string.Join(", ", headers));
        }

        (string Method, string[] Headers)[] refused =
        [
            ("GET", []), // another relay's page without a referrer: a picture, a script
            ("GET", ["Referer: " + foreign + "/camera/index.html"]),
            ("GET", ["Accept: text/html", "Referer: " + foreign + "/camera/index.html"]), // a frame in another relay's page
            ("GET", ["Sec-Fetch-Site: same-site"]), // another port of 127.0.0.1
            ("GET", ["Sec-Fetch-Site: same-site", "Accept: text/html"]),
            ("GET", ["Sec-Fetch-Site: cross-site"]),
            ("GET", ["Sec-Fetch-Site: same-site", "Referer: " + own + "/"]),
            ("POST", ["Accept: text/html"]),
            ("POST", ["Origin: null"]),
            ("POST", ["Origin: null", "Sec-Fetch-Site: same-origin"]),
            ("POST", ["Origin: " + foreign]),
            ("POST", ["Origin: " + foreign, "Sec-Fetch-Site: same-origin"]),
        ];
        foreach (var (method, headers) in refused)
        {
            using var response = await SendAsync(browser, relay, method, headers);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, method + " " + string.Join(", ", headers));
            // Not the lost-session page: the tab must not open the page again.
            Assert.DoesNotContain(DeviceWebPages.SessionLostTitle, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A WebSocket always names its page.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var address = new Uri("ws://" + relay.Origin.Authority + "/vapix/ws-data-stream?sources=events");
        using var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        socket.Options.SetRequestHeader("Origin", own);
        await socket.ConnectAsync(address, timeout.Token);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        foreach (var origin in new[] { null, foreign })
        {
            using var stranger = new ClientWebSocket();
            stranger.Options.Cookies = cookies;
            if (origin is not null)
            {
                stranger.Options.SetRequestHeader("Origin", origin);
            }

            await Assert.ThrowsAsync<WebSocketException>(() => stranger.ConnectAsync(address, timeout.Token));
        }
    }

    [Fact]
    public async Task On_its_own_address_a_request_without_these_headers_still_passes()
    {
        if (OperatingSystem.IsMacOS())
        {
            return; // no address of its own there
        }

        var (api, device) = await FakeAsync();
        using var relay = new DeviceWebRelay(api, device, NullLogger.Instance);
        using var browser = Bare(new CookieContainer());
        await browser.GetStringAsync(relay.StartAddress("/"));

        // The session cookie is SameSite=Strict and the address is the relay's alone: no other page's request carries it.
        using var plain = await SendAsync(browser, relay, "GET", []);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        using var foreign = await SendAsync(browser, relay, "POST", ["Origin: http://127.0.0.1:1"]);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task A_refusal_before_the_answer_is_a_page_that_says_why()
    {
        // The session ended, the device left the user's devices, the plugin was turned off: the server refuses the call.
        var api = Refusing(new Status(StatusCode.NotFound, Oadm.Sdk.Devices.DeviceMessages.NoLongerAvailable(1)));
        using var relay = new DeviceWebRelay(api, Guid.NewGuid(), NullLogger.Instance);
        using var browser = Browser(new CookieContainer());

        using var response = await browser.GetAsync(relay.StartAddress("/camera/index.html"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(Oadm.Sdk.Devices.DeviceMessages.NoLongerAvailable(1), await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_log_names_the_path_without_its_query()
    {
        // VAPIX queries carry passwords (pwdgrp.cgi?...&pwd=).
        var api = Refusing(new Status(StatusCode.Unavailable, "The server is not reachable."));
        var log = new ListLogger();
        using var relay = new DeviceWebRelay(api, Guid.NewGuid(), log);
        using var browser = Browser(new CookieContainer());

        using var response = await browser.GetAsync(relay.StartAddress("/axis-cgi/pwdgrp.cgi?action=update&user=root&pwd=S3cret-pw"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var entry = Assert.Single(log.Entries);
        Assert.Contains("/axis-cgi/pwdgrp.cgi", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("S3cret-pw", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("?", entry, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient browser, DeviceWebRelay relay, string method, string[] headers)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(relay.Origin, "/axis-cgi/param.cgi?action=list"));
        if (method == "POST")
        {
            request.Content = new StringContent("a=1");
        }

        foreach (var header in headers)
        {
            var parts = header.Split(": ", 2);
            request.Headers.TryAddWithoutValidation(parts[0], parts[1]);
        }

        return await browser.SendAsync(request);
    }

    /// <summary>A server whose Exchange accepts the request and then refuses the call before any answer.</summary>
    private static IOadmApi Refusing(Status status)
    {
        var api = Substitute.For<IOadmApi>();
        api.ExchangeDeviceWeb(default).ReturnsForAnyArgs(_ => new RefusedCall(status));
        return api;
    }

    private sealed class RefusedCall(Status status) : IDeviceWebCall
    {
        public Task SendAsync(Contracts.V1.DeviceWebFrame frame, CancellationToken ct) => Task.CompletedTask;

        public Task CompleteAsync() => Task.CompletedTask;

        public async IAsyncEnumerable<Contracts.V1.DeviceWebFrame> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            if (status.StatusCode != StatusCode.OK)
            {
                throw new RpcException(status);
            }

            yield break;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ListLogger : ILogger
    {
        private readonly Lock _gate = new();
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add(formatter(state, exception));
            }
        }
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
