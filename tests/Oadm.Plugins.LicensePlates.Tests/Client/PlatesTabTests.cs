using Oadm.Plugins.LicensePlates.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

/// <summary>The Plates tab, the plate editor, the import and the tags dialog against the sample server; no Avalonia.</summary>
public sealed class PlatesTabTests
{
    [Fact]
    public async Task The_grid_shows_the_plates_with_their_tags_and_marks_an_ended_plate()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var plates = page.Plates;

        Assert.Equal(Samples.Plates().Count, plates.Rows.Count);
        var anna = plates.Rows.Single(r => r.Text == "W12345A");
        Assert.Equal(["Garage Nord", "Staff"], anna.Tags.Select(t => t.Name));
        Assert.Equal((int)PlateTagColor.Blue, anna.Tags[0].Color);
        Assert.False(anna.IsEnded);
        Assert.Empty(anna.AllowedToText);

        var visitor = plates.Rows.Single(r => r.Text == "KU5521C");
        Assert.True(visitor.IsEnded); // ended 26 Nov 18:00, still in the list
        Assert.Equal("26 Nov 2026 18:00", visitor.AllowedToText);
        Assert.False(plates.Rows.Single(r => r.Text == "G4711XY").IsEnded);
        Assert.Equal("10 plates", plates.Footer);
    }

    [Fact]
    public async Task Search_finds_a_plate_from_its_start_without_spaces_or_a_name_and_the_tag_filter_narrows()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var plates = page.Plates;

        plates.SearchText = "w 12";
        Assert.Equal(["W12345A"], plates.Rows.Select(r => r.Text));
        plates.SearchText = "gruber";
        Assert.Equal(["G4711XY"], plates.Rows.Select(r => r.Text));
        plates.SearchText = "nobody";
        Assert.True(plates.ShowNoMatch);

        plates.SearchText = string.Empty;
        plates.SelectedTagFilter = plates.TagFilters.Single(f => f.Name == "Garage Süd");
        Assert.Equal(["L8820BB", "S1234K", "WU4040K"], plates.Rows.Select(r => r.Text));
        plates.SetSelection([plates.Rows[0], plates.Rows[1]]);
        Assert.Equal("3 plates · 2 selected", plates.Footer);
    }

    [Fact]
    public async Task Without_Manage_plates_the_changes_are_off_and_Export_stays()
    {
        var ctx = Samples.Context();
        ctx.Denied.Add(PermissionKeys.Of(LicensePlatesInfo.PluginId, LicensePlatePermissions.Plates));
        var (page, _, _) = await Samples.OpenAsync(ctx);
        var plates = page.Plates;
        plates.SetSelection([plates.Rows[0]]);

        Assert.False(plates.MayManage);
        Assert.False(plates.AddPlateCommand.CanExecute(null));
        Assert.False(plates.EditCommand.CanExecute(null));
        Assert.False(plates.ImportCommand.CanExecute(null));
        Assert.False(plates.ChangeTagsCommand.CanExecute(null));
        Assert.False(plates.ManageTagsCommand.CanExecute(null));
        Assert.False(plates.DeleteCommand.CanExecute(null));
        Assert.True(plates.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Delete_asks_with_the_count_in_red_and_deletes_the_selected_plates()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var plates = page.Plates;
        plates.SetSelection(plates.Rows.Take(3));

        await plates.DeleteCommand.ExecuteAsync(null);
        var (title, message, confirm) = ctx.Confirmations.Single();
        Assert.Equal(PageTexts.DeletePlatesTitle, title);
        Assert.Equal("Delete 3 plates? They are no longer allowed anywhere.", message);
        Assert.Equal("Delete", confirm); // MessageWindow makes it red
        Assert.Equal(3, ctx.LastPayload<PlateTextsRequest>(LicensePlatesMethods.DeletePlates).Plates.Count);
        Assert.Equal(7, plates.Rows.Count);
    }

    [Fact]
    public async Task Export_saves_the_servers_csv()
    {
        var (page, _, dialogs) = await Samples.OpenAsync();
        await page.Plates.ExportCommand.ExecuteAsync(null);
        Assert.Equal(PageTexts.PlatesFile, dialogs.SaveNames.Single());
        Assert.Equal("plate,name,tags\n", System.Text.Encoding.UTF8.GetString(dialogs.SavedBytes));
    }

    [Fact]
    public async Task Change_tags_adds_and_removes_only_the_clicked_tags_of_the_selected_plates()
    {
        var (page, ctx, dialogs) = await Samples.OpenAsync();
        var plates = page.Plates;
        plates.SetSelection([plates.Rows.Single(r => r.Text == "W12345A"), plates.Rows.Single(r => r.Text == "S1234K")]);
        dialogs.Handle = dialog =>
        {
            var tags = (TagsDialogViewModel)dialog;
            Assert.True(tags.IsChange);
            var nord = tags.Rows.Single(r => r.Name == "Garage Nord");
            var sued = tags.Rows.Single(r => r.Name == "Garage Süd");
            var visitors = tags.Rows.Single(r => r.Name == "Visitors");
            Assert.True(nord.IsChecked); // both have it
            Assert.Null(sued.IsChecked); // one of two
            Assert.False(visitors.IsChecked);
            Assert.Equal("1 of 2 selected have it", sued.StateText);
            sued.ToggleCommand.Execute(null); // some: becomes all
            nord.ToggleCommand.Execute(null); // all: becomes none
            tags.OkCommand.Execute(null);
            return Task.FromResult(tags.Change is not null);
        };

        await plates.ChangeTagsCommand.ExecuteAsync(null);
        var request = ctx.LastPayload<ChangePlateTagsRequest>(LicensePlatesMethods.ChangePlateTags);
        Assert.Equal(["W12345A", "S1234K"], request.Plates);
        Assert.Equal([Samples.GarageSued], request.Add);
        Assert.Equal([Samples.GarageNord], request.Remove);
    }

    [Fact]
    public async Task Manage_tags_checks_a_new_name_below_the_field_and_asks_before_deleting_a_tag_in_use()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var tags = TagsDialogViewModel.ForManage(page);
        Assert.True(tags.IsManage);
        Assert.Equal(PageTexts.Close, tags.CancelText);

        tags.StartNewTagCommand.Execute(null);
        Assert.True(tags.IsEditorOpen);
        Assert.Equal(PageTexts.CreateTag, tags.EditorButton);
        Assert.Null(tags.ErrorOf(nameof(TagsDialogViewModel.TagName))); // untouched: no error
        tags.TagName = "staff";
        Assert.Equal("A plate tag with this name exists.", tags.ErrorOf(nameof(TagsDialogViewModel.TagName)));
        Assert.False(tags.SaveTagCommand.CanExecute(null));
        tags.TagName = "Night, shift";
        Assert.Equal("A name cannot hold a comma.", tags.ErrorOf(nameof(TagsDialogViewModel.TagName)));
        tags.TagName = "Night shift";
        await tags.SaveTagCommand.ExecuteAsync(null);
        Assert.Equal("Night shift", ctx.LastPayload<SaveTagRequest>(LicensePlatesMethods.SaveTag).Tag.Name);
        Assert.False(tags.IsEditorOpen);

        await tags.DeleteCommand.ExecuteAsync(tags.Rows.Single(r => r.Name == "Garage Nord"));
        Assert.Equal("Delete the plate tag Garage Nord? 412 plates and 3 cameras use it; it is removed from them.", ctx.Confirmations.Single().Message);
        Assert.True(ctx.LastPayload<DeleteTagRequest>(LicensePlatesMethods.DeleteTag).Confirmed);
    }

    [Fact]
    public async Task A_new_plate_has_Delete_when_it_ends_on_and_it_counts_only_with_Allowed_to()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var editor = new PlateEditorViewModel(page, null, null, page.Plates.Exists);

        Assert.True(editor.IsNew);
        Assert.Equal(PageTexts.AddPlate, editor.Title);
        Assert.True(editor.DeleteWhenEnds);
        Assert.False(editor.HasAllowedTo);
        editor.Text = "w-777 ab";
        Assert.False(editor.ToPlate().DeleteWhenEnds); // no Allowed to: saved as off
        Assert.Equal("W777AB", editor.ToPlate().Text);

        editor.ToDate = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        Assert.True(editor.HasAllowedTo);
        Assert.True(editor.ToPlate().DeleteWhenEnds);
        Assert.Equal(new DateTime(2026, 12, 31, 23, 59, 0), editor.ToPlate().AllowedTo); // a day without a time: its end

        // An existing plate keeps its own choice.
        var gruber = new PlateEditorViewModel(page, Samples.Plates()[1] with { DeleteWhenEnds = false }, null, _ => false);
        Assert.False(gruber.DeleteWhenEnds);
        Assert.Equal(PageTexts.EditPlate, gruber.Title);
        Assert.Equal(new TimeSpan(18, 0, 0), gruber.ToTime);
    }

    [Fact]
    public async Task The_editor_shows_errors_below_the_fields_only_after_an_edit_or_Save()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var editor = new PlateEditorViewModel(page, null, null, page.Plates.Exists);

        Assert.Null(editor.ErrorOf(nameof(PlateEditorViewModel.Text))); // untouched
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal(PageTexts.EnterPlate, editor.SaveBlockedReason);
        editor.FieldLeft(nameof(PlateEditorViewModel.Text));
        Assert.Equal(PageTexts.EnterPlate, editor.ErrorOf(nameof(PlateEditorViewModel.Text)));

        editor.Text = "W12345A";
        Assert.Equal(LicensePlateTexts.PlateExists, editor.ErrorOf(nameof(PlateEditorViewModel.Text)));
        editor.Text = "W1";
        Assert.Null(editor.ErrorOf(nameof(PlateEditorViewModel.Text)));
        editor.Text = "W";
        Assert.Equal("A plate has 2 to 12 characters.", editor.ErrorOf(nameof(PlateEditorViewModel.Text)));
        editor.Text = "W1Ä!";
        Assert.Equal("Use only letters and digits.", editor.ErrorOf(nameof(PlateEditorViewModel.Text)));
        editor.Text = "W4242X";

        editor.FromDate = new DateTimeOffset(2026, 12, 10, 0, 0, 0, TimeSpan.Zero);
        editor.FromTime = new TimeSpan(8, 0, 0);
        editor.ToDate = new DateTimeOffset(2026, 12, 10, 0, 0, 0, TimeSpan.Zero);
        editor.ToTime = new TimeSpan(7, 0, 0);
        editor.FieldLeft(nameof(PlateEditorViewModel.ToDate));
        Assert.Equal(LicensePlateTexts.AfterFrom, editor.ErrorOf(nameof(PlateEditorViewModel.ToDate)));
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.ToTime = new TimeSpan(18, 0, 0);
        Assert.Null(editor.ErrorOf(nameof(PlateEditorViewModel.ToDate)));
        Assert.True(editor.SaveCommand.CanExecute(null));
        var closed = false;
        editor.CloseRequested += (_, ok) => closed = ok;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Equal("W4242X", editor.Saved!.Text);
    }

    [Fact]
    public async Task A_refusal_of_the_server_shows_below_its_field()
    {
        var ctx = Samples.Context();
        ctx.Answers[LicensePlatesMethods.SavePlate] = _ => Samples.Serialize(new SavePlateReply(null, new Dictionary<string, string>
        {
            ["text"] = LicensePlateTexts.PlateExists,
            ["tags"] = LicensePlateTexts.TagGone,
        }));
        var (page, _, _) = await Samples.OpenAsync(ctx);
        var editor = new PlateEditorViewModel(page, null, "W5555Q", _ => false);

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(LicensePlateTexts.PlateExists, editor.ErrorOf(nameof(PlateEditorViewModel.Text)));
        Assert.Equal(LicensePlateTexts.TagGone, editor.TagsError);
        Assert.Null(editor.Saved);
    }

    [Fact]
    public async Task Add_plate_reads_the_list_again_and_selects_the_new_plate()
    {
        var (page, ctx, dialogs) = await Samples.OpenAsync();
        dialogs.Handle = async dialog =>
        {
            var editor = (PlateEditorViewModel)dialog;
            editor.Text = "W 4242 X";
            editor.Name = "New colleague";
            editor.TagChoices.Items.Single(c => c.Name == "Staff").Chosen = true;
            await editor.SaveCommand.ExecuteAsync(null);
            return editor.Saved is not null;
        };

        await page.Plates.AddPlateCommand.ExecuteAsync(null);
        var saved = ctx.LastPayload<SavePlateRequest>(LicensePlatesMethods.SavePlate);
        Assert.Null(saved.Original);
        Assert.Equal([Samples.Staff], saved.Plate.Tags);
        Assert.Equal("W4242X", page.Plates.SelectedRow?.Text);
        Assert.Equal(11, page.Plates.Rows.Count);
    }

    [Fact]
    public async Task Import_shows_the_preview_with_refused_rows_first_and_saves_it()
    {
        var path = Path.Combine(Path.GetTempPath(), "lpc-import-" + Guid.NewGuid().ToString("N") + ".csv");
        await File.WriteAllTextAsync(path, "plate,name\nW12345A,Anna Huber\n");
        try
        {
            var (page, ctx, _) = await Samples.OpenAsync();
            var import = new ImportViewModel(page);
            Assert.Equal(PageTexts.ChooseFileFirst, import.ImportBlockedReason);
            Assert.False(import.ImportCommand.CanExecute(null));

            import.TagChoices.Items.Single(c => c.Name == "Visitors").Chosen = true;
            await import.UseFileAsync(path);
            Assert.True(import.HasPreview);
            Assert.Equal("3 new, 1 changed, 4 unchanged, 2 refused", import.CountsText);
            Assert.True(import.HasRefused);
            Assert.True(import.Rows[0].IsRefused);
            Assert.Equal("Refused", import.Rows[0].StatusText);
            Assert.Equal("A plate has 2 to 12 characters.", import.Rows[0].Error);
            Assert.Equal([Samples.Visitors], ctx.LastPayload<ImportRequest>(LicensePlatesMethods.ImportPreview).TagIds);

            Assert.True(import.ImportCommand.CanExecute(null));
            await import.ImportCommand.ExecuteAsync(null);
            Assert.Equal("preview-1", ctx.LastPayload<ImportSaveRequest>(LicensePlatesMethods.ImportSave).PreviewId);
            Assert.Equal(new ImportResult(3, 1), import.Result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_plates_event_reads_the_plates_and_tags_again()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var plates = ctx.CallsOf(LicensePlatesMethods.Plates);
        var tags = ctx.CallsOf(LicensePlatesMethods.Tags);

        page.OnEvent(LicensePlatesMethods.PlatesTopic, "{}");
        await Samples.SettleAsync(page);
        Assert.Equal(plates + 1, ctx.CallsOf(LicensePlatesMethods.Plates));
        Assert.Equal(tags + 1, ctx.CallsOf(LicensePlatesMethods.Tags));
    }
}
