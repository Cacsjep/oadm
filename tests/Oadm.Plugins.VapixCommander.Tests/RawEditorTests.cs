using Oadm.Plugins.VapixCommander.Client;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>The raw request editor: headers where needed, row actions, fields made and removed.</summary>
public sealed class RawEditorTests
{
    [Fact]
    public void Headers_are_hidden_for_get_unless_the_request_has_some()
    {
        var raw = new RawEditorViewModel();
        var changes = new List<string?>();
        raw.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.Equal("GET", raw.Method);
        Assert.False(raw.ShowHeaders);

        raw.Method = "POST";
        Assert.True(raw.ShowHeaders);
        Assert.Contains(nameof(RawEditorViewModel.ShowHeaders), changes);

        raw.Method = "GET";
        Assert.False(raw.ShowHeaders);

        // A command that already has headers keeps showing them (never hide data).
        changes.Clear();
        raw.HeaderRows.Add(new KeyValueRowViewModel { Key = "Accept", Value = "application/json" });
        Assert.True(raw.ShowHeaders);
        Assert.Contains(nameof(RawEditorViewModel.ShowHeaders), changes);
    }

    [Fact]
    public void A_new_request_starts_empty()
    {
        var raw = new RawEditorViewModel();

        Assert.Equal(("GET", string.Empty, BodyTypes.None), (raw.Method, raw.Path, raw.BodyType));
        Assert.Empty(raw.QueryRows);
        Assert.Empty(raw.HeaderRows);
        Assert.Empty(raw.Fields);
        Assert.Null(raw.ErrorOf(nameof(RawEditorViewModel.Path))); // untouched: no error yet
    }

    [Fact]
    public void Each_row_makes_its_own_field_and_removes_itself()
    {
        var raw = RawSamples.BrandList();
        var group = raw.QueryRows[1];

        raw.MakeRowFieldCommand.Execute(group); // the row's own button, without selecting the row
        Assert.Equal("{{group}}", group.Value);
        Assert.Equal("Brand", raw.Fields.Single().Value);

        raw.RemoveQueryRowCommand.Execute(group);
        Assert.Equal(["action"], raw.QueryRows.Select(r => r.Key));
        Assert.Empty(raw.Fields);
    }

    [Fact]
    public void Removing_a_field_puts_its_value_back()
    {
        var raw = RawSamples.BrandList();
        raw.Method = "POST";
        raw.BodyType = BodyTypes.Json;
        raw.MakeField(raw.QueryRows[0]);
        raw.BodyText = "{\"active\": \"{{active}}\", \"note\": \"x {{note}}\"}";
        var active = raw.Fields.Single(f => f.Name == "active");
        active.Type = FieldTypes.Boolean;
        active.Value = "true";
        raw.Fields.Single(f => f.Name == "note").Value = "a \"b\"";

        raw.RemoveFieldCommand.Execute(raw.Fields.Single(f => f.Name == "action"));
        raw.RemoveFieldCommand.Execute(active);
        raw.RemoveFieldCommand.Execute(raw.Fields.Single(f => f.Name == "note"));

        Assert.Equal("list", raw.QueryRows[0].Value);
        Assert.Equal("{\"active\": true, \"note\": \"x a \\\"b\\\"\"}", raw.BodyText); // typed where the placeholder was the whole value
        Assert.Empty(raw.Fields);
    }

    [Fact]
    public void A_placeholder_renamed_or_broken_while_typing_keeps_its_field_settings()
    {
        var raw = RawSamples.BrandList();
        var row = raw.QueryRows[0];
        row.Value = "{{act}}";
        var field = raw.Fields.Single();
        field.Type = FieldTypes.Integer;
        field.Value = "5";

        row.Value = "{{action}}"; // renamed
        Assert.Same(field, raw.Fields.Single());
        Assert.Equal(("action", "Action", FieldTypes.Integer, "5"), (field.Name, field.Label, field.Type, field.Value));

        row.Value = "{{action}"; // broken while typing
        Assert.Empty(raw.Fields);
        row.Value = "{{action}}";
        Assert.Same(field, raw.Fields.Single());
        Assert.Equal("5", field.Value);
    }

    [Fact]
    public void A_loaded_saved_command_keeps_what_the_editor_does_not_show()
    {
        var command = new CommandDefinition
        {
            Id = "custom.level",
            Name = "Day night level",
            Category = CommandCategories.Custom,
            Description = "Sets the level",
            Docs = "https://example.com/doc",
            Requires = [new ApiRequirement { Api = "param-cgi", MinVersion = "1.0" }],
            Writes = true,
            Fields =
            [
                new CommandField { Name = "level", Label = "Level", Type = FieldTypes.Integer, Min = 0, Max = 100, Default = System.Text.Json.JsonSerializer.SerializeToElement(40) },
                new CommandField { Name = "mode", Label = "Mode", Type = FieldTypes.Enum, Options = [new FieldOption { Label = "Auto", Value = System.Text.Json.JsonSerializer.SerializeToElement("auto") }, new FieldOption { Label = "Day", Value = System.Text.Json.JsonSerializer.SerializeToElement("day") }], Default = System.Text.Json.JsonSerializer.SerializeToElement("auto") },
            ],
            Request = new CommandRequest
            {
                Method = "POST",
                Path = "/axis-cgi/param.cgi",
                Query = new() { ["action"] = "update", ["Mode"] = "{{mode}}" },
                BodyType = BodyTypes.Json,
                Body = System.Text.Json.Nodes.JsonNode.Parse("{\"level\":\"{{level}}\"}"),
                TimeoutSeconds = 30,
            },
            Response = new CommandResponse { Kind = ResponseKinds.ParamCgi, ErrorPattern = "Error" },
        };
        var raw = new RawEditorViewModel();

        raw.Load(new CommandListItem { Source = CommandSources.Saved, Command = command });

        Assert.Equal(("POST", BodyTypes.Json, 30m, "Day night level"), (raw.Method, raw.BodyType, raw.TimeoutSeconds, raw.SaveName));
        Assert.Contains("\"{{level}}\"", raw.BodyText, StringComparison.Ordinal);
        Assert.Equal(["level", "mode"], raw.Fields.Select(f => f.Name));
        Assert.Contains(FieldTypes.Enum, raw.Fields[1].TypeOptions);
        Assert.True(raw.IsFormValid);

        raw.Fields[0].Value = "60";
        raw.Fields[1].Value = "day";
        var (saved, problems) = raw.Build(forSave: true);

        Assert.Empty(problems);
        Assert.Equal("https://example.com/doc", saved.Docs);
        Assert.Equal("Error", saved.Response.ErrorPattern);
        Assert.Equal((0d, 100d, 60L), (saved.Fields[0].Min!.Value, saved.Fields[0].Max!.Value, saved.Fields[0].Default!.Value.GetInt64()));
        Assert.Equal(2, saved.Fields[1].Options!.Count);
        Assert.Equal("day", saved.Fields[1].Default!.Value.GetString());
    }
}

/// <summary>The request most tests start from: GET param.cgi?action=list&amp;group=Brand.</summary>
internal static class RawSamples
{
    public static RawEditorViewModel BrandList() => new RawEditorViewModel().FillBrandList();

    public static RawEditorViewModel FillBrandList(this RawEditorViewModel raw)
    {
        raw.Path = "/axis-cgi/param.cgi";
        raw.QueryRows.Add(new KeyValueRowViewModel { Key = "action", Value = "list" });
        raw.QueryRows.Add(new KeyValueRowViewModel { Key = "group", Value = "Brand" });
        return raw;
    }
}
