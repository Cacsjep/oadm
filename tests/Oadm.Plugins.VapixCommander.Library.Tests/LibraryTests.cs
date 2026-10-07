using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace Oadm.Plugins.VapixCommander.Library.Tests;

/// <summary>Static checks of every Library/*.json file against the schema and the format rules.</summary>
public sealed partial class LibraryTests
{
    private static readonly string[] Categories =
    [
        "Common", "System", "Network", "Security", "Users", "Time", "Video", "Image", "PTZ", "Audio", "I/O", "Events",
        "Storage", "Applications", "Maintenance",
    ];

    // The schema registers its $id globally; load it once per test run.
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(CommandLibrary.SchemaPath)));

    public static TheoryData<string> FileNames()
    {
        var data = new TheoryData<string>();
        foreach (var f in CommandLibrary.Files)
        {
            data.Add(f.FileName);
        }

        return data;
    }

    public static TheoryData<string> CommandIds()
    {
        var data = new TheoryData<string>();
        foreach (var c in CommandLibrary.AllCommands)
        {
            data.Add(CommandLibrary.Id(c));
        }

        return data;
    }

    [Fact]
    public void Library_has_files_and_commands()
    {
        Assert.NotEmpty(CommandLibrary.Files);
        Assert.True(CommandLibrary.AllCommands.Count() >= 80, "The battery-included library should hold at least 80 commands.");
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void File_validates_against_the_schema(string fileName)
    {
        var schema = Schema.Value;
        var file = CommandLibrary.Files.Single(f => f.FileName == fileName);
        var result = schema.Evaluate(JsonDocument.Parse(file.Text).RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });

        var errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} {e.Value}"))
            .ToList();
        Assert.True(result.IsValid, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Schema_rejects_a_command_without_requires()
    {
        var file = JsonNode.Parse(CommandLibrary.Files[0].Text)!.AsObject();
        file["commands"]![0]!.AsObject().Remove("requires");
        var result = Schema.Value.Evaluate(JsonDocument.Parse(file.ToJsonString()).RootElement,
            new EvaluationOptions { RequireFormatValidation = true });
        Assert.False(result.IsValid);
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void File_name_matches_its_category_and_every_command_has_that_category(string fileName)
    {
        var file = CommandLibrary.Files.Single(f => f.FileName == fileName);
        Assert.Contains(file.Category, Categories);
        // "I/O" cannot be a file name on Windows; the file is IO.json.
        Assert.Equal(file.Category.Replace("/", string.Empty, StringComparison.Ordinal) + ".json", fileName);
        Assert.All(file.Commands, c => Assert.Equal(file.Category, c["category"]!.GetValue<string>()));
        Assert.NotEmpty(file.Commands);
    }

    [Fact]
    public void Ids_are_unique_across_all_files()
    {
        var duplicates = CommandLibrary.AllCommands.GroupBy(CommandLibrary.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Names_are_unique_across_all_files()
    {
        var duplicates = CommandLibrary.AllCommands.GroupBy(c => c["name"]!.GetValue<string>()).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Theory]
    [MemberData(nameof(CommandIds))]
    public void Every_placeholder_has_a_field_and_every_field_is_used(string id)
    {
        var command = CommandLibrary.Command(id);
        var fieldNames = CommandLibrary.Fields(command).Select(f => f["name"]!.GetValue<string>()).ToList();
        var used = CommandLibrary.RequestPlaceholders(command);

        Assert.Equal(fieldNames.Count, fieldNames.Distinct(StringComparer.Ordinal).Count());
        Assert.All(used, p => Assert.Contains(p, fieldNames));
        Assert.All(CommandLibrary.ExtractPlaceholders(command), p => Assert.Contains(p, fieldNames));
        Assert.All(fieldNames, f => Assert.Contains(f, used));
    }

    [Theory]
    [MemberData(nameof(CommandIds))]
    public void Field_defaults_are_valid(string id)
    {
        foreach (var field in CommandLibrary.Fields(CommandLibrary.Command(id)))
        {
            var name = field["name"]!.GetValue<string>();
            var type = field["type"]!.GetValue<string>();
            var def = field["default"];
            var min = field["min"]?.GetValue<double>();
            var max = field["max"]?.GetValue<double>();

            if (min is not null && max is not null)
            {
                Assert.True(min <= max, $"{name}: min > max");
            }

            switch (type)
            {
                case "enum":
                    var options = field["options"]?.AsArray() ?? [];
                    Assert.True(options.Count > 0, $"{name}: enum without options");
                    Assert.NotNull(def);
                    Assert.Contains(options, o => JsonNode.DeepEquals(o!["value"], def));
                    Assert.Equal(options.Count, options.Select(o => o!["value"]!.ToJsonString()).Distinct().Count());
                    break;
                case "integer":
                case "number":
                    Assert.NotNull(def);
                    Assert.Equal(JsonValueKind.Number, def!.GetValueKind());
                    var value = def.GetValue<double>();
                    if (type == "integer")
                    {
                        Assert.Equal(Math.Floor(value), value);
                    }

                    Assert.True(min is null || min <= value, $"{name}: default {value} below min {min}");
                    Assert.True(max is null || value <= max, $"{name}: default {value} above max {max}");
                    break;
                case "boolean":
                    Assert.NotNull(def);
                    Assert.True(def!.GetValueKind() is JsonValueKind.True or JsonValueKind.False, $"{name}: boolean default");
                    break;
                case "string":
                    Assert.NotNull(def);
                    var text = def!.GetValue<string>();
                    var pattern = field["pattern"]?.GetValue<string>();
                    _ = new Regex(pattern ?? string.Empty);
                    if (pattern is not null && text.Length > 0)
                    {
                        Assert.Matches(pattern, text);
                    }

                    break;
                case "password":
                    Assert.Null(def);
                    break;
            }

            if (type is not ("integer" or "number"))
            {
                Assert.True(min is null && max is null, $"{name}: min/max only for numbers");
            }
        }
    }

    [GeneratedRegex(@"^(get[A-Z][A-Za-z]*|get|list|listAll|status|systemready)$")]
    private static partial Regex ReadMethodRegex();

    [Theory]
    [MemberData(nameof(CommandIds))]
    public void Readers_use_GET_or_read_methods_only(string id)
    {
        var command = CommandLibrary.Command(id);
        if (CommandLibrary.Writes(command))
        {
            return;
        }

        var request = CommandLibrary.Request(command);
        var method = request["method"]!.GetValue<string>();
        var query = request["query"]?.AsObject() ?? [];
        Assert.False(query.ContainsKey("action") && query["action"]!.GetValue<string>() is not ("list" or "get" or "statusall" or "query"),
            "Reader with a write action in its query.");
        if (method == "GET")
        {
            return;
        }

        Assert.Equal("POST", method);
        switch (request["bodyType"]?.GetValue<string>())
        {
            case "json":
                var rpc = request["body"]!["method"]!.GetValue<string>();
                Assert.Matches(ReadMethodRegex(), rpc);
                break;
            case "xml":
                var xml = request["body"]!.GetValue<string>();
                Assert.Matches(@"<\w+:Get[A-Za-z]+/>", xml);
                break;
            default:
                Assert.Fail("Reader POST without a recognisable read method.");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(CommandIds))]
    public void Writers_never_use_read_only_shapes_and_dangerous_implies_writes(string id)
    {
        var command = CommandLibrary.Command(id);
        if (command["dangerous"]?.GetValue<bool>() == true)
        {
            Assert.True(CommandLibrary.Writes(command), "dangerous commands must be writes");
        }

        var request = CommandLibrary.Request(command);
        if (CommandLibrary.Writes(command) && request["path"]!.GetValue<string>() == "/axis-cgi/param.cgi")
        {
            Assert.Equal("update", request["query"]!["action"]!.GetValue<string>());
            Assert.Equal("OK", CommandLibrary.Response(command)["success"]?.GetValue<string>());
        }
    }

    [Theory]
    [MemberData(nameof(CommandIds))]
    public void Restart_factory_default_network_and_user_changes_are_dangerous(string id)
    {
        var command = CommandLibrary.Command(id);
        var category = command["category"]!.GetValue<string>();
        var body = request(command)["body"]?.ToJsonString() ?? string.Empty;
        var dangerous = command["dangerous"]?.GetValue<bool>() == true;

        if (category == "Users" && CommandLibrary.Writes(command))
        {
            Assert.True(dangerous);
        }

        foreach (var marker in new[] { "\"reboot\"", "\"factoryDefault\"", "\"rollback\"", "setHostnameConfiguration",
                     "setResolverConfiguration", "setIPv4AddressConfiguration", "setIPv6AddressConfiguration",
                     "setCaptureMode" })
        {
            if (body.Contains(marker, StringComparison.Ordinal))
            {
                Assert.True(dangerous, $"{id} contains {marker} but is not dangerous");
            }
        }

        static JsonObject request(JsonObject c) => CommandLibrary.Request(c);
    }

    [Theory]
    [MemberData(nameof(CommandIds))]
    public void Docs_link_points_to_the_axis_developer_documentation(string id)
    {
        var docs = CommandLibrary.Command(id)["docs"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(docs));
        Assert.StartsWith("https://developer.axis.com/vapix/", docs, StringComparison.Ordinal);
        Assert.EndsWith("/", docs, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CommandIds))]
    public void Commands_have_description_requires_and_sane_request(string id)
    {
        var command = CommandLibrary.Command(id);
        Assert.False(string.IsNullOrWhiteSpace(command["description"]?.GetValue<string>()));
        Assert.Matches(@"^[a-z]+\.", id);
        Assert.StartsWith(command["category"]!.GetValue<string>() switch
        {
            "I/O" => "io.",
            "Applications" => "apps.",
            var c => c.ToLowerInvariant() + ".",
        }, id, StringComparison.Ordinal);

        var requires = command["requires"]!.AsArray();
        Assert.All(requires, r => Assert.Matches(@"^[a-z0-9-]+$", r!["api"]!.GetValue<string>()));

        var request = CommandLibrary.Request(command);
        var bodyType = request["bodyType"]?.GetValue<string>() ?? "none";
        var body = request["body"];
        switch (bodyType)
        {
            case "none":
                Assert.True(body is null, "bodyType none with a body");
                break;
            case "json":
                Assert.IsType<JsonObject>(body);
                Assert.NotNull(body!["apiVersion"]);
                Assert.NotNull(body["method"]);
                Assert.Equal("json-rpc", CommandLibrary.Kind(command));
                break;
            case "form":
                Assert.IsType<JsonObject>(body);
                break;
            default:
                Assert.Equal(JsonValueKind.String, body!.GetValueKind());
                break;
        }

        Assert.DoesNotContain(request["headers"]?.AsObject() ?? [], h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        if (command["hasVideoOnly"] is null)
        {
            Assert.DoesNotContain(command["category"]!.GetValue<string>(), new[] { "Video", "Image", "PTZ" });
        }
    }

    [Theory]
    [MemberData(nameof(CommandIds))]
    public void Passwords_never_travel_in_the_url(string id)
    {
        var command = CommandLibrary.Command(id);
        var request = CommandLibrary.Request(command);
        var urlStrings = CommandLibrary.Strings(request["query"]).Append(request["path"]!.GetValue<string>()).ToList();
        foreach (var field in CommandLibrary.Fields(command).Where(f => f["type"]!.GetValue<string>() == "password"))
        {
            var token = "{{" + field["name"]!.GetValue<string>() + "}}";
            Assert.DoesNotContain(urlStrings, s => s.Contains(token, StringComparison.Ordinal));
            Assert.Equal("POST", request["method"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Readers_show_values_where_the_response_is_structured()
    {
        // param-cgi lists and JSON getters must tell the technician the current value via response.extract.
        var missing = CommandLibrary.AllCommands
            .Where(c => !CommandLibrary.Writes(c))
            .Where(c => CommandLibrary.Kind(c) == "json-rpc"
                        || (CommandLibrary.Kind(c) == "param-cgi"
                            && CommandLibrary.Request(c)["path"]!.GetValue<string>() == "/axis-cgi/param.cgi"))
            .Where(c => !CommandLibrary.Extracts(c).Any())
            .Select(CommandLibrary.Id)
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Default_rendering_produces_requests_without_leftover_placeholders()
    {
        foreach (var command in CommandLibrary.AllCommands)
        {
            var values = CommandRendering.Defaults(command);
            foreach (var pwd in CommandLibrary.Fields(command).Where(f => f["type"]!.GetValue<string>() == "password"))
            {
                values[pwd["name"]!.GetValue<string>()] = JsonValue.Create("secret");
            }

            var rendered = CommandRendering.Render(command, values);
            Assert.DoesNotContain("{{", rendered.PathAndQuery, StringComparison.Ordinal);
            Assert.DoesNotContain("%7B%7B", rendered.PathAndQuery, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", rendered.Body ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Typed_json_placeholders_keep_numbers_and_booleans_unquoted()
    {
        var command = CommandLibrary.Command("system.remotesyslog.setup");
        var values = CommandRendering.Defaults(command);
        values["address"] = JsonValue.Create("10.0.0.1");
        var body = JsonNode.Parse(CommandRendering.Render(command, values).Body!)!;
        Assert.Equal(JsonValueKind.Number, body["params"]!["servers"]![0]!["port"]!.GetValueKind());

        var ssh = CommandLibrary.Command("security.ssh.set");
        var sshBody = JsonNode.Parse(CommandRendering.Render(ssh, CommandRendering.Defaults(ssh)).Body!)!;
        Assert.Equal(JsonValueKind.False, sshBody["params"]!["enabled"]!.GetValueKind());
    }

    [Fact]
    public void Param_cgi_booleans_render_as_yes_no_or_their_custom_values()
    {
        var upnp = CommandLibrary.Command("network.upnp.set");
        var values = CommandRendering.Defaults(upnp);
        values["name"] = JsonValue.Create("Cam 1");
        Assert.Contains("Network.UPnP.Enabled=no", CommandRendering.Render(upnp, values).PathAndQuery, StringComparison.Ordinal);

        var anon = CommandLibrary.Command("security.anonymousviewer.set");
        Assert.Contains("System.BoaProtViewer=password",
            CommandRendering.Render(anon, CommandRendering.Defaults(anon)).PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void Example_from_the_format_document_is_covered_by_the_library()
    {
        var command = CommandLibrary.Command("image.daynight.shiftlevel.set");
        var rendered = CommandRendering.Render(command, CommandRendering.Defaults(command));
        Assert.Equal("/axis-cgi/param.cgi?action=update&ImageSource.I0.DayNight.ShiftLevel=50", rendered.PathAndQuery);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"{50}"), CommandRendering.Defaults(command)["level"]!.ToJsonString());
    }
}
