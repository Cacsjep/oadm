using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander.Tests;

public sealed class RendererTests
{
    private static CommandDefinition Command(string fields, string request, string kind = "raw") => Samples.Parse($$"""
        {
          "id": "test.command", "version": 1, "name": "Test command", "category": "Common",
          "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ], "writes": true,
          "fields": [ {{fields}} ],
          "request": {{request}},
          "response": { "kind": "{{kind}}" }
        }
        """);

    [Fact]
    public void Param_cgi_query_uses_defaults_and_entered_values()
    {
        var rendered = CommandRenderer.Render(Samples.ShiftLevel, Samples.Values(("level", 70)));

        Assert.Equal("GET", rendered.Method);
        Assert.Equal("axis-cgi/param.cgi?action=update&ImageSource.I0.DayNight.ShiftLevel=70", rendered.RelativeUri);
        Assert.Null(rendered.Body);
        Assert.Equal(TimeSpan.FromSeconds(15), rendered.Timeout);
    }

    [Fact]
    public void Booleans_render_yes_no_for_param_cgi_and_true_false_elsewhere()
    {
        const string Field = """{ "name": "on", "label": "On", "type": "boolean" }""";
        const string Request = """{ "method": "GET", "path": "/axis-cgi/param.cgi", "query": { "action": "update", "X.Enabled": "{{on}}" } }""";

        var param = CommandRenderer.Render(Command(Field, Request, "param-cgi"), Samples.Values(("on", true)));
        var text = CommandRenderer.Render(Command(Field, Request, "text"), Samples.Values(("on", "no")));
        var custom = CommandRenderer.Render(
            Command("""{ "name": "on", "label": "On", "type": "boolean", "trueValue": "1", "falseValue": "0" }""", Request, "param-cgi"),
            Samples.Values(("on", false)));

        Assert.EndsWith("X.Enabled=yes", param.RelativeUri, StringComparison.Ordinal);
        Assert.EndsWith("X.Enabled=false", text.RelativeUri, StringComparison.Ordinal);
        Assert.EndsWith("X.Enabled=0", custom.RelativeUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_body_substitutes_typed_values()
    {
        var command = Command(
            """
            { "name": "count", "label": "Count", "type": "integer" },
            { "name": "ratio", "label": "Ratio", "type": "number" },
            { "name": "on", "label": "On", "type": "boolean" },
            { "name": "mode", "label": "Mode", "type": "enum", "options": [ { "value": "auto", "label": "Auto" }, { "value": 3, "label": "Three" } ] },
            { "name": "mode2", "label": "Mode 2", "type": "enum", "options": [ { "value": 3, "label": "Three" } ] },
            { "name": "name", "label": "Name", "type": "string" },
            { "name": "secret", "label": "Password", "type": "password" }
            """,
            """
            { "method": "POST", "path": "/axis-cgi/x.cgi", "bodyType": "json",
              "body": { "apiVersion": "1.0", "method": "set", "params": { "count": "{{count}}", "ratio": "{{ratio}}", "on": "{{on}}",
                "mode": "{{mode}}", "mode2": "{{mode2}}", "label": "Cam {{name}} #{{count}}", "list": [ "{{name}}", 1, true ], "{{name}}": "key", "pwd": "{{secret}}" } } }
            """,
            "json-rpc");

        var rendered = CommandRenderer.Render(command, Samples.Values(
            ("count", "42"), ("ratio", 0.5), ("on", true), ("mode", "auto"), ("mode2", "3"), ("name", "Gate \"A\""), ("secret", "s3cret!")));

        var body = JsonNode.Parse(rendered.Body!)!["params"]!;
        Assert.Equal(JsonValueKind.Number, body["count"]!.GetValueKind());
        Assert.Equal(42, body["count"]!.GetValue<long>());
        Assert.Equal(0.5, body["ratio"]!.GetValue<double>());
        Assert.True(body["on"]!.GetValue<bool>());
        Assert.Equal("auto", body["mode"]!.GetValue<string>());
        Assert.Equal(3, body["mode2"]!.GetValue<long>());
        Assert.Equal("Cam Gate \"A\" #42", body["label"]!.GetValue<string>());
        Assert.Equal("Gate \"A\"", body["list"]![0]!.GetValue<string>());
        Assert.Equal("key", body["Gate \"A\""]!.GetValue<string>());
        Assert.Equal("application/json", JsonContentType(rendered));
        Assert.Contains("s3cret!", rendered.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret!", rendered.DescribeBody()!, StringComparison.Ordinal);
        Assert.Contains("***", rendered.DescribeBody()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_booleans_with_custom_values_stay_strings()
    {
        var command = Command(
            """{ "name": "on", "label": "On", "type": "boolean", "trueValue": "enabled", "falseValue": "disabled" }""",
            """{ "method": "POST", "path": "/x", "bodyType": "json", "body": { "state": "{{on}}" } }""",
            "json-rpc");

        var rendered = CommandRenderer.Render(command, Samples.Values(("on", false)));

        Assert.Equal("""{"state":"disabled"}""", rendered.Body);
    }

    [Fact]
    public void Form_text_and_xml_bodies_are_rendered_and_escaped()
    {
        const string Field = """{ "name": "user", "label": "User", "type": "string" }, { "name": "pwd", "label": "Password", "type": "password" }""";
        var form = CommandRenderer.Render(
            Command(Field, """{ "method": "POST", "path": "/axis-cgi/pwdgrp.cgi", "bodyType": "form", "body": { "action": "update", "user": "{{user}}", "pwd": "{{pwd}}" } }"""),
            Samples.Values(("user", "joe"), ("pwd", "a&b c")));
        var xml = CommandRenderer.Render(
            Command(Field, """{ "method": "POST", "path": "/vapix/services", "bodyType": "xml", "body": "<s:Envelope><u>{{user}}</u><p>{{pwd}}</p></s:Envelope>" }"""),
            Samples.Values(("user", "<joe>"), ("pwd", "x")));
        var text = CommandRenderer.Render(
            Command(Field, """{ "method": "PUT", "path": "/x", "bodyType": "text", "body": "user={{user}}" }"""),
            Samples.Values(("user", "joe"), ("pwd", "x")));

        Assert.Equal([new("action", "update"), new("user", "joe"), new("pwd", "a&b c")], form.Form!);
        Assert.Equal("action=update&user=joe&pwd=***", form.DescribeBody());
        using (var request = form.ToHttpRequest())
        {
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        }

        Assert.Equal("<s:Envelope><u>&lt;joe&gt;</u><p>x</p></s:Envelope>", xml.Body);
        Assert.StartsWith("application/soap+xml", xml.ContentType, StringComparison.Ordinal);
        Assert.Equal("user=joe", text.Body);
        Assert.StartsWith("text/plain", text.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public void Path_query_and_headers_are_escaped_and_timeout_is_set_on_the_request()
    {
        var command = Command(
            """{ "name": "app", "label": "App", "type": "string" }, { "name": "groups", "label": "Groups", "type": "string" }""",
            """{ "method": "GET", "path": "/config/rest/{{app}}/v1", "query": { "group": "{{groups}}", "q": "a b/c" }, "headers": { "X-App": "{{app}}" }, "timeoutSeconds": 120 }""");

        var rendered = CommandRenderer.Render(command, Samples.Values(("app", "my app/1"), ("groups", "Brand,Network")));
        using var request = rendered.ToHttpRequest();

        Assert.Equal("config/rest/my%20app%2F1/v1?group=Brand,Network&q=a%20b%2Fc", rendered.RelativeUri);
        Assert.Equal("my app/1", request.Headers.GetValues("X-App").Single());
        Assert.True(request.Options.TryGetValue(VapixRequestOptions.Timeout, out var timeout));
        Assert.Equal(TimeSpan.FromSeconds(120), timeout);
        Assert.False(request.RequestUri!.IsAbsoluteUri);
    }

    [Fact]
    public void Password_values_are_masked_in_the_request_line()
    {
        var command = Command(
            """{ "name": "pwd", "label": "Password", "type": "password" }""",
            """{ "method": "GET", "path": "/axis-cgi/x.cgi", "query": { "pwd": "{{pwd}}" } }""");

        var rendered = CommandRenderer.Render(command, Samples.Values(("pwd", "p w&d")));

        Assert.DoesNotContain("p%20w", rendered.Describe(), StringComparison.Ordinal);
        Assert.Equal("GET /axis-cgi/x.cgi?pwd=***", rendered.Describe());
    }

    [Fact]
    public void Optional_empty_fields_render_empty()
    {
        var command = Command(
            """{ "name": "note", "label": "Note", "type": "string", "required": false }""",
            """{ "method": "GET", "path": "/x", "query": { "note": "{{note}}" } }""");

        Assert.Equal("x?note=", CommandRenderer.Render(command, Samples.Values()).RelativeUri);
    }

    private static string? JsonContentType(RenderedRequest rendered)
    {
        using var request = rendered.ToHttpRequest();
        return request.Content!.Headers.ContentType!.MediaType;
    }

    [Theory]
    [InlineData("/\\/evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/axis-cgi//evil")]
    [InlineData("/axis-cgi\\param.cgi")]
    [InlineData("http://evil.example/axis-cgi/param.cgi")]
    [InlineData("/http://evil.example/x")]
    [InlineData("/user@evil.example/x")]
    [InlineData("/../x")]
    [InlineData("axis-cgi/param.cgi")]
    [InlineData("/axis cgi")]
    public void Paths_that_could_leave_the_device_are_rejected(string path)
    {
        Assert.False(CommandValidator.IsDevicePath(path));
        var command = Command("""{ "name": "on", "label": "On", "type": "boolean" }""", JsonSerializer.Serialize(new { method = "GET", path }));
        Assert.Contains(CommandValidator.PathProblem, CommandValidator.Validate(command));
        Assert.Throws<CommandValidationException>(() => CommandRenderer.Render(command, Samples.Values(("on", true))));
    }

    [Theory]
    [InlineData("/axis-cgi/param.cgi")]
    [InlineData("/config/rest/virtualhost/v1")]
    [InlineData("/axis-cgi/param.cgi?action=list&group=root.Brand")]
    [InlineData("/axis-cgi/x.cgi?mail=a@b")]
    public void Device_paths_are_accepted(string path) => Assert.True(CommandValidator.IsDevicePath(path));

    [Fact]
    public async Task The_client_refuses_absolute_uris_to_another_host()
    {
        using var handler = new CountingHandler();
        using var client = new Oadm.Core.Vapix.VapixClient(new Uri("https://10.0.0.48/"), handler, disposeHandler: false);
        foreach (var uri in (string[])["https://evil.example/axis-cgi/param.cgi", "//evil.example/x", "/\\evil.example/x", "http://10.0.0.48/x", "https://10.0.0.48:8443/x", "https://user@10.0.0.48/x"])
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.RelativeOrAbsolute));
            var error = await Record.ExceptionAsync(() => client.SendAsync(request, CancellationToken.None));
            Assert.True(error is ArgumentException, uri + " was not refused (" + handler.LastUri + ")");
        }

        Assert.Equal(0, handler.Calls);
        using var own = new HttpRequestMessage(HttpMethod.Get, "https://10.0.0.48/axis-cgi/param.cgi");
        using var response = await client.SendAsync(own, CancellationToken.None);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
