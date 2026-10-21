using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.VapixCommander.Client;
using Oadm.Sdk.Actions;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>File fields and multipart bodies: the Siren and light profile upload and the rules every such command follows.</summary>
public sealed class FileUploadTests
{
    private static readonly byte[] Profiles = Encoding.UTF8.GetBytes("""[ { "name": "Gate", "light": { "pattern": "Steady", "colors": ["red"], "intensity": 1 } } ]""");

    private static CommandDefinition Upload => CommandLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Library")).Commands
        .Single(c => c.Id == "siren.profiles.upload");

    private static JsonElement FileValue(string name, byte[] data) =>
        JsonSerializer.SerializeToElement(new { name, data = Convert.ToBase64String(data) });

    private static CommandDefinition Command(string fields, string body) => Samples.Parse($$"""
        {
          "id": "test.upload", "version": 1, "name": "Test upload", "category": "Common",
          "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ], "writes": true,
          "fields": [ {{fields}} ],
          "request": { "method": "POST", "path": "/axis-cgi/x.cgi", "bodyType": "multipart", "body": {{body}} },
          "response": { "kind": "json-rpc" }
        }
        """);

    [Fact]
    public async Task Upload_profiles_sends_the_request_part_and_the_file_as_multipart()
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["mode"] = JsonSerializer.SerializeToElement("overwrite"),
            ["profiles"] = FileValue("my-profiles.json", Profiles),
        };
        var rendered = CommandRenderer.Render(Upload, values);
        using var vapix = new FakeVapix { Handler = _ => FakeVapix.Json("""{ "apiVersion": "1.0", "method": "uploadProfiles", "data": {} }""") };

        var outcome = await CommandExecutor.ExecuteAsync(vapix, Upload, rendered, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Summary);
        var seen = Assert.Single(vapix.Requests);
        Assert.Equal("axis-cgi/siren_and_light.cgi", seen.Uri);
        Assert.Equal("multipart/form-data; boundary=oadm-vapix-commander-7d0a35e1", seen.Headers["Content-Type"]);
        Assert.Contains("Content-Disposition: form-data; name=\"request\"", seen.Body, StringComparison.Ordinal);
        Assert.Contains("\"method\":\"uploadProfiles\",\"params\":{\"uploadMode\":\"overwrite\"}", seen.Body, StringComparison.Ordinal);
        // The device documents the file name "profiles"; the command sends that, not the chosen file's name.
        Assert.Contains("Content-Disposition: form-data; name=\"file\"; filename=\"profiles\"", seen.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("filename*", seen.Body, StringComparison.Ordinal);
        Assert.Contains(Encoding.UTF8.GetString(Profiles), seen.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shown_request_names_the_file_and_its_size_but_not_its_content()
    {
        var rendered = CommandRenderer.Render(Upload, new Dictionary<string, JsonElement> { ["profiles"] = FileValue("my-profiles.json", Profiles) });

        var shown = rendered.DescribeBody()!;

        Assert.Contains("--- request (application/json)", shown, StringComparison.Ordinal);
        Assert.Contains($"--- file: file \"profiles\", {Profiles.Length} bytes (application/json)", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Steady", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_part_without_a_file_name_sends_the_chosen_name()
    {
        var command = Command("""{ "name": "pkg", "label": "Package", "type": "file" }""", """[ { "name": "file", "file": "{{pkg}}" } ]""");

        var part = Assert.Single(CommandRenderer.Render(command, new Dictionary<string, JsonElement> { ["pkg"] = FileValue("app.eap", [1, 2, 3]) }).Parts!);

        Assert.Equal(("app.eap", "application/octet-stream"), (part.FileName, part.ContentType));
        Assert.Equal([1, 2, 3], part.Content);
    }

    [Fact]
    public void File_values_are_checked_before_anything_is_sent()
    {
        var command = Upload;

        string Problem(JsonElement value) =>
            Assert.Throws<CommandValidationException>(() => CommandRenderer.Render(command, new Dictionary<string, JsonElement> { ["profiles"] = value })).Message;

        Assert.Contains("Profile file is required.", Assert.Throws<CommandValidationException>(() => CommandRenderer.Render(command, Samples.Values())).Message, StringComparison.Ordinal);
        Assert.Contains("Profile file: must be a file.", Problem(JsonSerializer.SerializeToElement("profiles.json")), StringComparison.Ordinal);
        Assert.Contains("Profile file: must be a file.", Problem(JsonSerializer.SerializeToElement(new { name = "x.json", data = "not base64!" })), StringComparison.Ordinal);
        Assert.Contains("Profile file: is larger than 1 MB.", Problem(FileValue("big.json", new byte[CommandLimits.MaxFileBytes + 1])), StringComparison.Ordinal);
        Assert.Contains("Profile file: has a file name that cannot be sent.", Problem(FileValue("a\"b.json", Profiles)), StringComparison.Ordinal);

        // Exactly 1 MB and the value as JSON text (the request builder's field) are fine.
        Assert.NotNull(CommandRenderer.Render(command, new Dictionary<string, JsonElement> { ["profiles"] = FileValue("max.json", new byte[CommandLimits.MaxFileBytes]) }).Parts);
        Assert.NotNull(CommandRenderer.Render(command, new Dictionary<string, JsonElement>
        {
            ["profiles"] = JsonSerializer.SerializeToElement(FileValue("p.json", Profiles).GetRawText()),
        }).Parts);
    }

    [Theory]
    [InlineData("""{ "name": "f", "label": "F", "type": "file" }""", """[ { "name": "request", "text": "{{f}}" }, { "name": "file", "file": "{{f}}" } ]""", "File field \"f\" can only be the file of a multipart part.")]
    [InlineData("""{ "name": "f", "label": "F", "type": "string", "default": "x" }""", """[ { "name": "file", "file": "{{f}}" } ]""", "Field \"f\" is the file of a multipart part, so its type must be file.")]
    [InlineData("""{ "name": "f", "label": "F", "type": "file" }""", """[ { "name": "file", "file": "{{f}}", "text": "x" } ]""", "Part \"file\" needs exactly one of json, text or file.")]
    [InlineData("""{ "name": "f", "label": "F", "type": "file" }""", """[ { "name": "file", "file": "x {{f}}" } ]""", "The file of part \"file\" must be exactly one {{field}} of a file field.")]
    [InlineData("""{ "name": "f", "label": "F", "type": "file" }""", """[ { "name": "file", "file": "{{f}}", "fileName": "../x" } ]""", "The file name of part \"file\" is not valid")]
    [InlineData("""{ "name": "f", "label": "F", "type": "file" }""", """[ { "name": "", "file": "{{f}}" } ]""", "Every multipart part needs a name")]
    [InlineData("""{ "name": "f", "label": "F", "type": "file", "pattern": "x" }""", """[ { "name": "file", "file": "{{f}}" } ]""", "Field \"f\" is a file: it takes no pattern, min, max or options.")]
    [InlineData("""{ "name": "f", "label": "F", "type": "file" }""", """{ "file": "{{f}}" }""", "A multipart body must be a list of 1 to 10 parts.")]
    public void Multipart_bodies_and_file_fields_follow_the_format(string fields, string body, string problem)
    {
        var problems = CommandValidator.Validate(Command(fields, body));

        Assert.Contains(problems, p => p.StartsWith(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_field_in_a_path_or_query_is_refused()
    {
        var command = Samples.Parse("""
            {
              "id": "test.query", "version": 1, "name": "Test query", "category": "Common",
              "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ], "writes": true,
              "fields": [ { "name": "f", "label": "F", "type": "file" } ],
              "request": { "method": "GET", "path": "/axis-cgi/x.cgi", "query": { "name": "{{f}}" } },
              "response": { "kind": "raw" }
            }
            """);

        Assert.Contains("File field \"f\" can only be the file of a multipart part.", CommandValidator.Validate(command));
    }

    [Fact]
    public void The_form_chooses_a_file_and_a_saved_copy_keeps_it()
    {
        var picked = new ActionFile("profiles.json", Profiles);
        var item = new RolloutCommandViewModel(new CommandListItem { Command = Upload }, () => Task.FromResult<ActionFile?>(picked));
        var field = item.Fields.Single(f => f.IsFile);

        Assert.False(item.IsValid); // required, no file yet
        field.ChooseFileCommand.Execute(null);

        Assert.True(item.IsValid);
        Assert.Equal(("profiles.json", $"{Profiles.Length:N0} bytes"), (field.FileName, field.FileDetails));
        Assert.NotNull(CommandRenderer.Render(Upload, item.Values()).Parts);

        // "Save with values": the file becomes the default and a new form shows it.
        var saved = item.WithValuesAsDefaults();
        var again = new RolloutCommandViewModel(new CommandListItem { Command = saved }).Fields.Single(f => f.IsFile);
        Assert.Equal("profiles.json", again.FileName);
        Assert.Equal(Profiles, ActionFile.From(saved.Fields.Single(f => f.IsFile).Default!.Value)!.Data);
    }

    [Fact]
    public void A_too_large_file_shows_its_error_below_the_input()
    {
        var item = new RolloutCommandViewModel(new CommandListItem { Command = Upload },
            () => Task.FromResult<ActionFile?>(new ActionFile("big.json", new byte[CommandLimits.MaxFileBytes + 1])));
        var field = item.Fields.Single(f => f.IsFile);

        field.ChooseFileCommand.Execute(null);

        Assert.Equal("Is larger than 1 MB.", field.ErrorOf(nameof(FieldViewModel.FileName)));
        Assert.False(item.IsValid);
    }

    [Fact]
    public void The_request_builder_loads_and_sends_a_file_field()
    {
        var raw = new RawEditorViewModel { PickFile = () => Task.FromResult<ActionFile?>(new ActionFile("p.json", Profiles)) };
        raw.Load(new CommandListItem { Command = Upload });
        var field = raw.Fields.Single(f => f.Name == "profiles");

        Assert.Equal(BodyTypes.Multipart, raw.BodyType);
        Assert.True(field.IsFile);
        Assert.Null(field.FileName);

        field.ChooseFileCommand.Execute(null);

        Assert.Equal("p.json", field.FileName);
        var (command, problems) = raw.Build(forSave: true);
        Assert.Empty(problems);
        Assert.Equal(Profiles, ActionFile.From(command.Fields.Single(f => f.IsFile).Default!.Value)!.Data);
        var (send, _) = raw.Build(forSave: false); // "Send" in the builder
        var part = CommandRenderer.Render(send, raw.Values()).Parts!.Single(p => p.FileName is not null);
        Assert.Equal(Profiles, part.Content);

        // Removing the field never pastes the file into the body.
        raw.RemoveFieldCommand.Execute(field);
        Assert.DoesNotContain("data", JsonNode.Parse(raw.BodyText)!.ToJsonString(), StringComparison.Ordinal);
    }
}
