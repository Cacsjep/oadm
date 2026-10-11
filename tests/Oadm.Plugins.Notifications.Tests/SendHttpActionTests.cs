using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Notifications.Tests;

public sealed class SendHttpActionTests
{
    private static JsonObject Settings(string url, string method = "POST") => new()
    {
        ["method"] = method,
        ["url"] = url,
        ["auth"] = "none",
        ["bodyType"] = "json",
        ["body"] = string.Empty,
        ["allowUntrusted"] = false,
        ["timeout"] = 5,
    };

    [Fact]
    public async Task Json_body_query_and_headers_get_the_event_values_escaped_for_their_place()
    {
        await using var server = new MiniHttpServer();
        var settings = Settings(server.BaseUrl + "/hook/{serial}");
        settings["query"] = Run.Pairs(("object", "{objectId}"), ("site", "{tags}"));
        settings["headers"] = Run.Pairs(("X-Camera", "{mac}"));
        settings["body"] = "{\"objectID\": \"{objectId}\", \"count\": {total}, \"camera\": \"{device}\"}";
        var context = new TestContext(new Dictionary<string, string> { ["objectId"] = "a \"quoted\" & <odd>", ["total"] = "7" }, new TestDevice());

        await new SendHttpAction().ExecuteAsync(context, Run.Invocation(settings), CancellationToken.None);

        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/hook/B8A44F631339?object=a%20%22quoted%22%20%26%20%3Codd%3E&site=Building%20A%3B%20Gate", request.Target);
        Assert.Equal("B8:A4:4F:63:13:39", request.Headers["X-Camera"]);
        Assert.StartsWith("application/json", request.Headers["Content-Type"], StringComparison.Ordinal);
        using var json = JsonDocument.Parse(request.Body); // still valid JSON with the quotes in the value
        Assert.Equal("a \"quoted\" & <odd>", json.RootElement.GetProperty("objectID").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("10.0.0.48 (AXIS P3265-V)", json.RootElement.GetProperty("camera").GetString());
        Assert.Equal(["HTTP 200 OK"], context.Lines);
    }

    [Fact]
    public async Task Xml_and_form_bodies_escape_their_way()
    {
        await using var server = new MiniHttpServer();
        var context = new TestContext(new Dictionary<string, string> { ["objectId"] = "<1&2>" });
        var xml = Settings(server.BaseUrl);
        xml["bodyType"] = "xml";
        xml["body"] = "<event id=\"{objectId}\">{objectId}</event>";
        await new SendHttpAction().ExecuteAsync(context, Run.Invocation(xml), CancellationToken.None);
        var form = Settings(server.BaseUrl);
        form["bodyType"] = "form";
        form["body"] = "object={objectId}\nrule={rule}";
        await new SendHttpAction().ExecuteAsync(context, Run.Invocation(form), CancellationToken.None);

        Assert.Equal("<event id=\"&lt;1&amp;2&gt;\">&lt;1&amp;2&gt;</event>", server.Requests[0].Body);
        Assert.Equal("object=%3C1%262%3E&rule=Gate%20rule", server.Requests[1].Body);
        Assert.StartsWith("application/x-www-form-urlencoded", server.Requests[1].Headers["Content-Type"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Basic_bearer_and_digest_logins()
    {
        await using var server = new MiniHttpServer();
        var basic = Settings(server.BaseUrl, "GET");
        basic["bodyType"] = "none";
        basic["auth"] = "basic";
        basic["user"] = "oadm";
        basic["password"] = "s3cret";
        await new SendHttpAction().ExecuteAsync(new TestContext(), Run.Invocation(basic), CancellationToken.None);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("oadm:s3cret")), server.Requests[0].Headers["Authorization"]);

        var bearer = Settings(server.BaseUrl, "GET");
        bearer["bodyType"] = "none";
        bearer["auth"] = "bearer";
        bearer["token"] = "tok-123";
        await new SendHttpAction().ExecuteAsync(new TestContext(), Run.Invocation(bearer), CancellationToken.None);
        Assert.Equal("Bearer tok-123", server.Requests[1].Headers["Authorization"]);

        await using var digest = new MiniHttpServer { DigestRealm = "AXIS_B8A44F631339" };
        var settings = Settings(digest.BaseUrl + "/axis-cgi/io/virtualinput.cgi", "GET");
        settings["bodyType"] = "none";
        settings["auth"] = "digest";
        settings["user"] = "root";
        settings["password"] = "pass";
        await new SendHttpAction().ExecuteAsync(new TestContext(), Run.Invocation(settings), CancellationToken.None);
        Assert.Equal(2, digest.Requests.Count); // challenge, then the answer to it
        Assert.StartsWith("Digest username=\"root\"", digest.Requests[1].Headers["Authorization"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_untrusted_certificate_is_refused_unless_allowed()
    {
        await using var server = new MiniHttpServer(tls: true);
        var settings = Settings(server.BaseUrl);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new SendHttpAction().ExecuteAsync(new TestContext(), Run.Invocation(settings), CancellationToken.None));
        Assert.StartsWith("127.0.0.1 cannot be reached", refused.Message, StringComparison.Ordinal);

        settings["allowUntrusted"] = true;
        var context = new TestContext();
        await new SendHttpAction().ExecuteAsync(context, Run.Invocation(settings), CancellationToken.None);
        Assert.Equal(["HTTP 200 OK"], context.Lines);
    }

    [Fact]
    public async Task An_error_answer_fails_with_its_status_and_text()
    {
        await using var server = new MiniHttpServer { Status = 500, ResponseBody = "database is down" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SendHttpAction().ExecuteAsync(new TestContext(), Run.Invocation(Settings(server.BaseUrl)), CancellationToken.None));
        Assert.Equal("HTTP 500 Error: database is down", error.Message);
    }

    [Fact]
    public void Validation_checks_url_login_headers_and_json_with_placeholders()
    {
        var action = new SendHttpAction();
        var settings = Settings("ftp://x");
        settings["auth"] = "basic";
        settings["headers"] = Run.Pairs(("Bad Header", "1"));
        settings["body"] = "{\"count\": {total}, \"id\": \"{objectId}\""; // missing }
        var errors = action.Validate(Run.Element(settings), statefulRule: true);
        Assert.Equal("Enter a URL that starts with http:// or https://.", errors["url"]);
        Assert.Equal("Enter the user name.", errors["user"]);
        Assert.Contains("Bad Header", errors["headers"], StringComparison.Ordinal);
        Assert.Equal("The body is not valid JSON.", errors["body"]);

        var good = Settings("https://hooks.example.com/{serial}");
        good["body"] = "{\"count\": {total}, \"id\": \"{objectId}\"}"; // a placeholder may stand for a number
        Assert.Empty(action.Validate(Run.Element(good), statefulRule: true));
    }

    [Fact]
    public void Runs_when_the_rule_turns_off_only_when_asked()
    {
        var action = new SendHttpAction();
        var settings = Settings("https://x");
        Assert.True(action.RunsOn(ActionEdge.TurnedOn, Run.Element(settings)));
        Assert.False(action.RunsOn(ActionEdge.TurnedOff, Run.Element(settings)));
        settings["onOff"] = true;
        Assert.True(action.RunsOn(ActionEdge.TurnedOff, Run.Element(settings)));
    }

    [Fact]
    public void The_host_checks_the_rows_of_headers_and_query()
    {
        var fields = new SendHttpAction().Fields;
        var settings = ActionSettings.Defaults(fields);
        settings["url"] = "https://x";
        settings["headers"] = new JsonArray(new JsonObject { ["name"] = "", ["value"] = "1" });
        Assert.Equal("Every row with a value needs a name.", ActionSettings.Check(fields, settings, statefulRule: false)["headers"]);
        settings["headers"] = Run.Pairs(("X-A", "line1\nline2"));
        Assert.Equal("Names and values must be on one line.", ActionSettings.Check(fields, settings, statefulRule: false)["headers"]);
        settings["headers"] = Run.Pairs(("X-A", "1"));
        Assert.Empty(ActionSettings.Check(fields, settings, statefulRule: false));

        // Login fields show for their choices only.
        settings["auth"] = "digest";
        Assert.True(ActionSettings.IsVisible(fields.Single(f => f.Key == "user"), settings, false));
        Assert.False(ActionSettings.IsVisible(fields.Single(f => f.Key == "token"), settings, false));
    }
}
