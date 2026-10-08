using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Oadm.Plugins.VapixCommander.Tests;

public sealed class InterpreterTests
{
    private static CommandResponse Spec(string kind, string? success = null, string? errorPattern = null, params ResponseExtract[] extract) =>
        new() { Kind = kind, Success = success, ErrorPattern = errorPattern, Extract = extract.Length == 0 ? null : [.. extract] };

    private static Interpretation Run(CommandResponse spec, string body, int status = 200, string? contentType = null) =>
        ResponseInterpreter.Interpret(spec, status, null, contentType, body);

    [Fact]
    public void Param_cgi_list_parses_parameters_and_extracts()
    {
        var result = Run(
            Spec(ResponseKinds.ParamCgi, extract: [new ResponseExtract { Label = "Product", Param = "\"root.Brand.ProdNbr\"" }]),
            "root.Brand.Brand=AXIS\nroot.Brand.ProdNbr=P3265-V\nroot.Brand.ProdType=Dome Camera\nroot.Brand.WebURL=http://www.axis.com\n");

        Assert.True(result.Success);
        Assert.Equal("Product: P3265-V", result.Summary);
        Assert.Equal(4, result.Parameters.Count);
    }

    [Fact]
    public void Param_cgi_errors_show_the_device_text()
    {
        var result = Run(Spec(ResponseKinds.ParamCgi, "OK"), "# Error: Error setting 'root.ImageSource.I9.DayNight.ShiftLevel' to '50'!\n");

        Assert.False(result.Success);
        Assert.Equal("Error: Error setting 'root.ImageSource.I9.DayNight.ShiftLevel' to '50'!", result.Summary);
    }

    [Fact]
    public void Error_text_with_http_200_is_a_failure_for_param_cgi()
    {
        Assert.Equal("Error: Invalid PTZ command", Run(Spec(ResponseKinds.ParamCgi), "Error: Invalid PTZ command").Summary);
        Assert.True(Run(Spec(ResponseKinds.ParamCgi, "OK"), "OK").Success);
        Assert.False(Run(Spec(ResponseKinds.ParamCgi, "OK"), "Something else").Success);
    }

    [Fact]
    public void Error_pattern_detects_errors_in_text_responses()
    {
        var spec = Spec(ResponseKinds.Text, errorPattern: "^Error");

        var failed = Run(spec, "line one\nError: camera not found\n");
        var ok = Run(spec, "Moved to preset 1");

        Assert.False(failed.Success);
        Assert.Equal("Error: camera not found", failed.Summary);
        Assert.True(ok.Success);
        Assert.Equal("Moved to preset 1", ok.Summary);
    }

    [Fact]
    public void Json_rpc_success_extracts_paths_with_indices_and_ignores_api_version()
    {
        var spec = Spec(
            ResponseKinds.JsonRpc,
            extract:
            [
                new ResponseExtract { Label = "Model", Path = "data.propertyList.ProdNbr" },
                new ResponseExtract { Label = "First", Path = "data.items.0.name" },
                new ResponseExtract { Label = "Second", Path = "data.items[1].name" },
                new ResponseExtract { Label = "Missing", Path = "data.nope" },
            ]);

        var result = Run(spec, """{"apiVersion":"1.3","method":"x","data":{"propertyList":{"ProdNbr":"P3265-V"},"items":[{"name":"a"},{"name":"b"}]}}""");

        Assert.True(result.Success);
        Assert.Equal("Model: P3265-V · First: a · Second: b · Missing: (not found)", result.Summary);
    }

    [Fact]
    public void Json_rpc_error_shows_message_and_code()
    {
        var result = Run(Spec(ResponseKinds.JsonRpc), """{"apiVersion":"1.0","error":{"code":2104,"message":"Invalid parameter value"}}""");

        Assert.False(result.Success);
        Assert.Equal("Invalid parameter value (code 2104)", result.Summary);
    }

    [Fact]
    public void Json_rpc_invalid_json_is_an_error_and_empty_body_is_ok()
    {
        Assert.StartsWith("Invalid JSON response: ", Run(Spec(ResponseKinds.JsonRpc), "<html>").Summary, StringComparison.Ordinal);
        Assert.True(Run(Spec(ResponseKinds.JsonRpc), string.Empty, 204).Success);
    }

    [Fact]
    public void Rest_errors_and_problem_json()
    {
        var rest = Run(Spec(ResponseKinds.Rest), """{"status":"error","error":{"code":400,"message":"Value out of range"}}""");
        var problem = Run(Spec(ResponseKinds.Rest), """{"type":"about:blank","title":"Bad Request","status":400,"detail":"level must be 0-100"}""", 400, "application/problem+json");
        var ok = Run(Spec(ResponseKinds.Rest, extract: [new ResponseExtract { Label = "Level", Path = "data.level" }]), """{"status":"success","data":{"level":50}}""");

        Assert.Equal("Value out of range (code 400)", rest.Summary);
        Assert.Equal("Bad Request - HTTP 400: Bad Request: level must be 0-100", problem.Summary);
        Assert.Equal("Level: 50", ok.Summary);
    }

    [Fact]
    public void Text_kind_uses_success_marker_and_first_line()
    {
        Assert.Equal("Unexpected thing", Run(Spec(ResponseKinds.Text, "OK"), "Unexpected thing\nmore").Summary);
        Assert.True(Run(Spec(ResponseKinds.Text, "OK"), "Result OK").Success);
    }

    [Fact]
    public void Xml_faults_and_general_errors()
    {
        const string Fault = """<?xml version="1.0"?><SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope"><SOAP-ENV:Body><SOAP-ENV:Fault><SOAP-ENV:Code><SOAP-ENV:Value>SOAP-ENV:Sender</SOAP-ENV:Value></SOAP-ENV:Code><SOAP-ENV:Reason><SOAP-ENV:Text xml:lang="en">Invalid argument</SOAP-ENV:Text></SOAP-ENV:Reason></SOAP-ENV:Fault></SOAP-ENV:Body></SOAP-ENV:Envelope>""";
        const string General = """<reply result="error"><GeneralError><ErrorCode>10</ErrorCode><ErrorDescription>Unknown action</ErrorDescription></GeneralError></reply>""";
        const string Ok = """<reply result="ok"><application Name="vmd" Status="Running"/></reply>""";

        Assert.Equal("Invalid argument", Run(Spec(ResponseKinds.Xml), Fault).Summary);
        Assert.Equal("Unknown action (code 10)", Run(Spec(ResponseKinds.Xml), General).Summary);
        var ok = Run(Spec(ResponseKinds.Xml, extract: [new ResponseExtract { Label = "Status", Path = "reply.application.@Status" }]), Ok);
        Assert.True(ok.Success);
        Assert.Equal("Status: Running", ok.Summary);
        Assert.StartsWith("Invalid XML response", Run(Spec(ResponseKinds.Xml), "<a><b></a>").Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_kind_shows_an_excerpt()
    {
        var result = Run(Spec(ResponseKinds.Raw), new string('x', 400));

        Assert.True(result.Success);
        Assert.Equal(303, result.Summary.Length);
    }

    [Theory]
    [InlineData(400, "Bad Request - HTTP 400")]
    [InlineData(401, "Unauthorized - HTTP 401 (check the credentials)")]
    [InlineData(403, "Forbidden - HTTP 403 (administrator rights are required)")]
    [InlineData(404, "Not Found - HTTP 404 (API not available on this firmware)")]
    [InlineData(500, "Server error - HTTP 500")]
    [InlineData(503, "Server error - HTTP 503")]
    public void Http_statuses_map_to_readable_texts(int status, string expected)
    {
        Assert.Equal(expected, Run(Spec(ResponseKinds.ParamCgi), string.Empty, status).Summary);
    }

    [Fact]
    public void Http_errors_append_the_device_body_text_but_not_html_pages()
    {
        Assert.Equal("Server error - HTTP 500: Error -1: could not set day/night level", Run(Spec(ResponseKinds.Text), "Error -1: could not set day/night level\n", 500).Summary);
        Assert.Equal("Bad Request - HTTP 400: Error: invalid group", Run(Spec(ResponseKinds.ParamCgi), "# Error: invalid group", 400).Summary);
        Assert.Equal("Not Found - HTTP 404 (API not available on this firmware): 404 Not Found",
            Run(Spec(ResponseKinds.Raw), "<html><head><title>404 Not Found</title></head><body>x</body></html>", 404, "text/html").Summary);
        Assert.Equal("Unauthorized - HTTP 401 (check the credentials)",
            Run(Spec(ResponseKinds.Raw), "<html><body>no</body></html>", 401, "text/html").Summary);
    }

    [Fact]
    public void Transport_errors_map_to_readable_texts()
    {
        var timeout = TimeSpan.FromSeconds(15);
        Assert.Equal("Timeout after 15 s", TransportErrors.Describe(new TaskCanceledException("x", new TimeoutException()), timeout));
        Assert.Equal("Unreachable - Connection refused", TransportErrors.Describe(
            new HttpRequestException(HttpRequestError.ConnectionError, "x", new SocketException((int)SocketError.ConnectionRefused)), timeout));
        Assert.Equal("Unreachable - no route to the device", TransportErrors.Describe(
            new HttpRequestException(HttpRequestError.ConnectionError, "x", new SocketException((int)SocketError.HostUnreachable)), timeout));
        Assert.Equal("TLS/certificate error: The remote certificate is invalid", TransportErrors.Describe(
            new HttpRequestException(HttpRequestError.SecureConnectionError, "x", new AuthenticationException("The remote certificate is invalid")), timeout));
        Assert.Equal("Host not found (name resolution failed)", TransportErrors.Describe(new HttpRequestException(HttpRequestError.NameResolutionError, "x"), timeout));
    }

    [Fact]
    public async Task Executor_maps_transport_failures_and_masks_secrets()
    {
        var vapix = new FakeVapix { Handler = _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "x", new SocketException((int)SocketError.ConnectionRefused)) };
        var command = Samples.ShiftLevel;
        var rendered = CommandRenderer.Render(command, Samples.Values());

        var outcome = await CommandExecutor.ExecuteAsync(vapix, command, rendered, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal("Unreachable - Connection refused", outcome.Summary);
        Assert.Null(outcome.StatusCode);

        vapix.Handler = _ => throw new TaskCanceledException("t", new TimeoutException());
        Assert.Equal("Timeout after 15 s", (await CommandExecutor.ExecuteAsync(vapix, command, rendered, CancellationToken.None)).Summary);
    }

    [Fact]
    public async Task Executor_decodes_utf8_with_unknown_charset_and_describes_binary_bodies()
    {
        var vapix = new FakeVapix();
        vapix.Handler = _ =>
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"apiVersion":"1.3","data":{"propertyList":{"ProdNbr":"P3265-V","Version":"12.11.77"}}}"""));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf8");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        var command = Samples.BasicDeviceInfo;
        var outcome = await CommandExecutor.ExecuteAsync(vapix, command, CommandRenderer.Render(command, Samples.Values()), CancellationToken.None);

        Assert.True(outcome.Success, outcome.Summary);
        Assert.Equal("Model: P3265-V · AXIS OS: 12.11.77", outcome.Summary);
        Assert.Contains("\n", outcome.Body!, StringComparison.Ordinal); // pretty-printed

        vapix.Handler = _ =>
        {
            var content = new ByteArrayContent(new byte[2048]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        var raw = Samples.Parse("""
            { "id": "test.snapshot", "version": 1, "name": "Snapshot", "category": "Common", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false, "fields": [], "request": { "method": "GET", "path": "/axis-cgi/jpg/image.cgi" }, "response": { "kind": "raw" } }
            """);
        var image = await CommandExecutor.ExecuteAsync(vapix, raw, CommandRenderer.Render(raw, Samples.Values()), CancellationToken.None);

        Assert.True(image.Success);
        Assert.Equal("image/jpeg, 2.0 KB", image.Summary);
    }

    [Fact]
    public async Task Extract_paths_may_contain_placeholders()
    {
        var command = Samples.Parse("""
            { "id": "test.extract", "version": 1, "name": "Extract", "category": "Common", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false, "fields": [ { "name": "channel", "label": "Channel", "type": "integer", "default": 1 } ],
              "request": { "method": "GET", "path": "/axis-cgi/param.cgi", "query": { "action": "list", "group": "ImageSource.I{{channel}}" } },
              "response": { "kind": "param-cgi", "extract": [ { "label": "Level", "param": "root.ImageSource.I{{channel}}.DayNight.ShiftLevel" } ] } }
            """);
        var vapix = new FakeVapix { Handler = _ => FakeVapix.Text("root.ImageSource.I0.DayNight.ShiftLevel=10\nroot.ImageSource.I1.DayNight.ShiftLevel=70\n") };

        var outcome = await CommandExecutor.ExecuteAsync(vapix, command, CommandRenderer.Render(command, Samples.Values()), CancellationToken.None);

        Assert.Equal("Level: 70", outcome.Summary);
    }
}
