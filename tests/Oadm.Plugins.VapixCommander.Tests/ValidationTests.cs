using System.Text.Json;

namespace Oadm.Plugins.VapixCommander.Tests;

public sealed class ValidationTests
{
    private static CommandDefinition Valid() => Samples.ShiftLevel;

    [Fact]
    public void Sample_library_commands_are_valid()
    {
        var library = Samples.Library();

        Assert.Empty(library.Problems);
        Assert.Equal(3, library.Commands.Count);
        Assert.All(library.Commands, c => Assert.Empty(CommandValidator.Validate(c)));
    }

    [Theory]
    [InlineData("Id", "Bad Id")]
    [InlineData("Name", "ab")]
    [InlineData("Category", "Whatever")]
    public void Definition_problems_are_reported(string property, string value)
    {
        var command = Valid();
        typeof(CommandDefinition).GetProperty(property)!.SetValue(command, value);

        Assert.NotEmpty(CommandValidator.Validate(command));
    }

    [Fact]
    public void Custom_category_is_allowed()
    {
        var command = Valid();
        command.Category = CommandCategories.Custom;

        Assert.Empty(CommandValidator.Validate(command));
    }

    [Fact]
    public void Requires_is_mandatory_and_versions_are_checked()
    {
        var command = Valid();
        command.Requires.Clear();
        Assert.Contains(CommandValidator.Validate(command), p => p.Contains("requires", StringComparison.Ordinal));

        command.Requires.Add(new ApiRequirement { Api = "param-cgi", MinVersion = "1" });
        Assert.Contains(CommandValidator.Validate(command), p => p.Contains("1.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Undeclared_placeholders_unsafe_paths_and_auth_headers_are_rejected()
    {
        var command = Valid();
        command.Request.Query!["extra"] = "{{missing}}";
        command.Request.Path = "/../etc";
        command.Request.Headers = new() { ["Authorization"] = "Basic x" };
        command.Request.TimeoutSeconds = 601;

        var problems = CommandValidator.Validate(command);

        Assert.Contains(problems, p => p.Contains("{{missing}}", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("path", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Authorization", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("timeout", StringComparison.Ordinal));
    }

    [Fact]
    public void Body_must_match_its_type()
    {
        var command = Valid();
        command.Request.BodyType = "form";
        command.Request.Body = System.Text.Json.Nodes.JsonNode.Parse("""{ "a": 1 }""");

        Assert.Contains(CommandValidator.Validate(command), p => p.Contains("form", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_properties_fail_strict_parsing()
    {
        Assert.Throws<JsonException>(() => CommandJson.ParseLibraryFile("""{ "formatVersion": 1, "category": "Common", "commands": [], "extra": 1 }"""));
    }

    [Theory]
    [InlineData("level", "abc", "Shift level: must be a whole number.")]
    [InlineData("level", 101, "Shift level: must be at most 100.")]
    [InlineData("level", -1, "Shift level: must be at least 0.")]
    [InlineData("channel", 1.5, "Image channel: must be a whole number.")]
    public void Values_are_checked_before_sending(string field, object value, string expected)
    {
        var ex = Assert.Throws<CommandValidationException>(() => CommandRenderer.Render(Valid(), Samples.Values((field, value))));

        Assert.Equal(expected, ex.Message);
    }

    [Fact]
    public void Required_fields_without_value_or_default_fail()
    {
        var command = Valid();
        command.Fields[1].Default = null;

        var ex = Assert.Throws<CommandValidationException>(() => CommandRenderer.Render(command, Samples.Values()));

        Assert.Equal("Shift level is required.", ex.Message);
    }

    [Fact]
    public void Types_patterns_and_enums_are_validated_without_echoing_secrets()
    {
        var command = Samples.Parse("""
            {
              "id": "test.types", "version": 1, "name": "Types", "category": "Common",
              "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ], "writes": false,
              "fields": [
                { "name": "ratio", "label": "Ratio", "type": "number", "min": 0, "max": 1 },
                { "name": "on", "label": "On", "type": "boolean" },
                { "name": "mode", "label": "Mode", "type": "enum", "options": [ { "value": "a", "label": "A" } ] },
                { "name": "host", "label": "Host", "type": "string", "pattern": "[a-z]+" },
                { "name": "pwd", "label": "Password", "type": "password", "max": 4 }
              ],
              "request": { "method": "GET", "path": "/x", "query": { "r": "{{ratio}}", "o": "{{on}}", "m": "{{mode}}", "h": "{{host}}", "p": "{{pwd}}" } },
              "response": { "kind": "text" }
            }
            """);

        var ex = Assert.Throws<CommandValidationException>(() => CommandRenderer.Render(command, Samples.Values(
            ("ratio", "2"), ("on", "maybe"), ("mode", "b"), ("host", "ABC"), ("pwd", "toolongsecret"))));

        Assert.Equal(
            [
                "Ratio: must be at most 1.", "On: must be yes or no.", "Mode: is not one of the allowed values.",
                "Host: does not have the expected format.", "Password: allows at most 4 characters.",
            ],
            ex.Problems);
        Assert.DoesNotContain("toolongsecret", ex.Message, StringComparison.Ordinal);

        var ok = CommandRenderer.Render(command, Samples.Values(("ratio", "0.25"), ("on", "yes"), ("mode", "a"), ("host", "abc"), ("pwd", "pw")));
        Assert.Equal("x?r=0.25&o=true&m=a&h=abc&p=pw", ok.RelativeUri);
    }
}
