using System.Text.Json.Nodes;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Notifications.Tests;

/// <summary>
/// The addresses of Send HTTP request and Send TCP message (docs/spec/device-scope.md): the actions declare where they send,
/// and check every address (after the placeholders, also each redirect) with <see cref="IActionContext.IsDestinationAllowed"/>
/// before they contact it; rules of all devices are not limited.
/// </summary>
public sealed class DestinationTests
{
    private static JsonObject Http(string url, string auth = "none") => new()
    {
        ["method"] = "POST",
        ["url"] = url,
        ["auth"] = auth,
        ["user"] = "u",
        ["password"] = "p",
        ["bodyType"] = "json",
        ["body"] = "{\"a\": 1}",
        ["timeout"] = 30,
    };

    [Fact]
    public void The_actions_declare_where_they_send()
    {
        Assert.Equal([new ActionDestination("vms.example.com", 8443)], new SendHttpAction().Destinations(Run.Element(Http("https://vms.example.com:8443/hook"))));
        Assert.Equal([new ActionDestination("vms.example.com", 80)], new SendHttpAction().Destinations(Run.Element(Http("http://vms.example.com/hook"))));
        Assert.Equal([new ActionDestination("http://{address}/x", 0)], new SendHttpAction().Destinations(Run.Element(Http("http://{address}/x"))));
        Assert.Equal([new ActionDestination("alarm.local", 9000)], new SendTcpAction().Destinations(Run.Element(new JsonObject { ["host"] = " alarm.local ", ["port"] = 9000 })));
        Assert.Equal([new ActionDestination("alarm.local", 8000)], new SendTcpAction().Destinations(Run.Element(new JsonObject { ["host"] = "alarm.local" })));
    }

    [Fact]
    public async Task An_http_request_to_an_address_that_is_not_allowed_is_never_sent()
    {
        await using var server = new MiniHttpServer();
        var context = new TestContext { Allowed = (_, _) => false };
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SendHttpAction().ExecuteAsync(context, Run.Invocation(Http(server.BaseUrl + "/hook")), CancellationToken.None));
        Assert.StartsWith(ActionDestination.NotAllowedText, refused.Message, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        Assert.Equal([("127.0.0.1", new Uri(server.BaseUrl).Port)], context.Asked);
    }

    [Fact]
    public async Task A_redirect_to_an_address_that_is_not_allowed_is_not_followed()
    {
        await using var server = new MiniHttpServer { Redirect = path => path == "/hook" ? "http://192.0.2.10:81/elsewhere" : null };
        var port = new Uri(server.BaseUrl).Port;
        var context = new TestContext { Allowed = (host, p) => host == "127.0.0.1" && p == port };
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SendHttpAction().ExecuteAsync(context, Run.Invocation(Http(server.BaseUrl + "/hook")), CancellationToken.None));
        Assert.Contains("192.0.2.10 was not contacted", refused.Message, StringComparison.Ordinal);
        Assert.Single(server.Requests);
        Assert.Equal([("127.0.0.1", port), ("192.0.2.10", 81)], context.Asked);
    }

    [Fact]
    public async Task An_allowed_redirect_is_followed_like_before_without_the_login()
    {
        await using var server = new MiniHttpServer { Redirect = path => path == "/hook" ? "/moved" : null };
        var context = new TestContext();
        await new SendHttpAction().ExecuteAsync(context, Run.Invocation(Http(server.BaseUrl + "/hook", auth: "basic")), CancellationToken.None);

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(("POST", "/hook"), (server.Requests[0].Method, server.Requests[0].Target));
        Assert.True(server.Requests[0].Headers.ContainsKey("Authorization"));
        Assert.Equal(("GET", "/moved"), (server.Requests[1].Method, server.Requests[1].Target)); // a 302 after a POST is a GET
        Assert.False(server.Requests[1].Headers.ContainsKey("Authorization"));
        Assert.Equal(["HTTP 200 OK"], context.Lines);
        Assert.Equal(2, context.Asked.Count);
    }

    [Fact]
    public async Task A_tcp_message_to_an_address_that_is_not_allowed_is_never_sent()
    {
        var context = new TestContext(new Dictionary<string, string> { ["host"] = "10.1.2.3" }) { Allowed = (host, port) => host == "alarm.local" && port == 9000 };
        var settings = new JsonObject { ["host"] = "{host}", ["port"] = 9000, ["format"] = "text", ["message"] = "x", ["timeout"] = 30 };
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new SendTcpAction().ExecuteAsync(context, Run.Invocation(settings), CancellationToken.None));
        Assert.Equal(ActionDestination.NotAllowedText + " 10.1.2.3 was not contacted.", refused.Message);
        Assert.Equal([("10.1.2.3", 9000)], context.Asked); // checked after the placeholders were filled
    }
}
