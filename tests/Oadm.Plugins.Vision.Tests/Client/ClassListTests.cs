using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Shared.Vision.Client;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>Class list profiles on the page (docs/spec/vision-center.md "Class lists"): the list, its dialogs, a model's choice.</summary>
public sealed class ClassListTests
{
    /// <summary>The dialogs answered in place: each call hands the form to the test, which says OK or Cancel.</summary>
    private sealed class Dialogs : IVisionDialogs
    {
        public Func<ClassListEditViewModel, bool> ClassList { get; set; } = _ => false;

        public Func<ModelEditViewModel, bool> Model { get; set; } = _ => false;

        public List<ClassListEditViewModel> ClassListForms { get; } = [];

        public Task<bool> EditStreamAsync(StreamSettingsViewModel form) => Task.FromResult(false);

        public Task<bool> EditModelAsync(ModelEditViewModel form) => Task.FromResult(Model(form));

        public Task<bool> EditClassListAsync(ClassListEditViewModel form)
        {
            ClassListForms.Add(form);
            return Task.FromResult(ClassList(form));
        }

        public Task<IReadOnlyList<IModelFile>?> PickModelFilesAsync() => Task.FromResult<IReadOnlyList<IModelFile>?>(null);
    }

    private static async Task<(ModelsViewModel Models, FakePageContext Ctx, Dialogs Dialogs)> LoadAsync()
    {
        var ctx = PageSamples.Context();
        var dialogs = new Dialogs();
        var models = new ModelsViewModel(ctx) { Dialogs = dialogs };
        await models.LoadAsync(CancellationToken.None);
        return (models, ctx, dialogs);
    }

    [Fact]
    public async Task The_built_in_lists_come_first_and_the_model_lines_name_their_list()
    {
        var (models, _, _) = await LoadAsync();
        Assert.Equal(["COCO (91 ids)", "COCO (80 classes)", "Gate classes"], models.ClassLists.Select(r => r.Name));
        Assert.Equal("91 names · Built-in", models.ClassLists[0].Line);
        Assert.Equal("6 names · 1 model", models.ClassLists[2].Line);
        var rf = models.Rows.OfType<ModelRow>().Single(r => r.Model.Id == "m1");
        Assert.Contains("Gate classes", rf.Line);
        Assert.DoesNotContain(VisionTexts.OwnNames, models.Rows.OfType<ModelRow>().Single(r => r.Model.Id == "m2").Line);
        Assert.Equal(VisionTexts.OwnNames, models.ClassListNameOf(PageSamples.Detr));

        Assert.Equal([VisionTexts.View], models.ActionsOf(models.ClassLists[0]).Select(a => a.Text));
        var own = models.ActionsOf(models.ClassLists[2]);
        Assert.Equal([VisionTexts.Edit, "-", VisionTexts.Delete], own.Select(a => a.Text));
        Assert.True(own[^1].IsDanger);
    }

    [Fact]
    public void The_class_list_dialog_checks_the_name_and_the_names_below_their_fields()
    {
        var form = new ClassListEditViewModel(null, name => name == "Gate classes");
        Assert.True(form.IsNew);
        Assert.Equal(VisionTexts.NewClassList, form.Title);
        Assert.False(form.HasErrors); // untouched
        Assert.False(form.OkCommand.CanExecute(null));
        form.Name = "Gate classes";
        Assert.Equal(VisionTexts.ClassListNameTaken, form.ErrorOf(nameof(ClassListEditViewModel.Name)));
        form.Name = "Dock";
        form.Names.Text = "truck\n\ncar";
        Assert.Equal(VisionTexts.WrongClassCount, form.Names.ErrorOf(nameof(ClassNamesEditor.Text)));
        form.Names.Text = "truck\ncar\n";
        Assert.Null(form.Names.ErrorOf(nameof(ClassNamesEditor.Text)));
        Assert.True(form.OkCommand.CanExecute(null));
        Assert.Equal(["truck", "car"], form.ToList().Names);

        var view = new ClassListEditViewModel(PageSamples.Coco91, _ => false, isReadOnly: true);
        Assert.Equal("COCO (91 ids)", view.Title);
        Assert.True(view.Names.IsReadOnly);
        Assert.False(view.OkCommand.CanExecute(null));
        Assert.Equal("91 names", view.Names.CountText);
    }

    [Fact]
    public async Task New_saves_the_list_and_a_refused_field_shows_its_reason_in_the_dialog_again()
    {
        var (models, ctx, dialogs) = await LoadAsync();
        var saves = 0;
        ctx.Answers[VisionMethods.SaveClassList] = json =>
        {
            var list = VisionJson.Deserialize<SaveClassListRequest>(json).ClassList!;
            return ++saves == 1
                ? VisionJson.Serialize(new SaveClassListReply { Errors = new Dictionary<string, string> { ["names"] = "Enter at most 1,000 names." } })
                : VisionJson.Serialize(new SaveClassListReply { ClassList = list with { Id = "new1" } });
        };
        dialogs.ClassList = form =>
        {
            if (dialogs.ClassListForms.Count == 1)
            {
                form.Name = "Dock";
                form.Names.Text = "truck\ncar";
            }
            else
            {
                Assert.Equal("Enter at most 1,000 names.", form.Names.ErrorOf(nameof(ClassNamesEditor.Text))); // the refusal below the field
                form.Names.Text = "truck\ncar\nbus";
            }

            return true;
        };
        await models.NewClassListCommand.ExecuteAsync(null);
        Assert.Equal(2, dialogs.ClassListForms.Count);
        Assert.Same(dialogs.ClassListForms[0], dialogs.ClassListForms[1]); // the same form, with what the user entered
        var row = Assert.Single(models.ClassLists, r => r.Name == "Dock");
        Assert.Equal("3 names", row.Line);
        Assert.Equal(2, ctx.CallsOf(VisionMethods.ListModels)); // the models that use it follow
    }

    [Fact]
    public async Task Delete_asks_first_and_a_refusal_stays_in_the_row()
    {
        var (models, ctx, _) = await LoadAsync();
        ctx.Answers[VisionMethods.DeleteClassList] = _ => throw new InvalidOperationException("Used by 1 model: RF-DETR medium.");
        var row = models.ClassLists.Single(r => r.Name == "Gate classes");
        ctx.ConfirmAnswer = false;
        await models.DeleteClassListCommand.ExecuteAsync(row);
        Assert.Equal(0, ctx.CallsOf(VisionMethods.DeleteClassList));
        Assert.Equal(VisionTexts.DeleteClassList, ctx.Confirmations[^1].Title);
        ctx.ConfirmAnswer = true;
        await models.DeleteClassListCommand.ExecuteAsync(row);
        Assert.Equal("Used by 1 model: RF-DETR medium.", row.Refusal);
        Assert.Contains(row, models.ClassLists);

        ctx.Answers[VisionMethods.DeleteClassList] = _ => "{}";
        await models.DeleteClassListCommand.ExecuteAsync(row);
        Assert.DoesNotContain(models.ClassLists, r => r.Name == "Gate classes");
    }

    [Fact]
    public void A_models_class_list_offers_the_lists_that_fit_its_class_count_and_its_own_names()
    {
        var form = ModelEditViewModel.ForClassList(PageSamples.RfDetr, PageSamples.ClassLists.Lists);
        Assert.Equal([VisionTexts.OwnNames, "Gate classes"], form.Choices.Select(c => c.Name)); // 91 and 80 names do not fit 6 classes
        Assert.Equal("gate6", form.SelectedChoice!.Id);
        Assert.False(form.IsOwn);
        Assert.Equal(PageSamples.GateClasses.Names, form.ClassNames);
        Assert.Contains("lorry", form.ChosenNames);

        form.SelectedChoice = form.Choices[0];
        Assert.True(form.IsOwn);
        Assert.Null(form.ClassListId);
        form.Own.Text = "person\ncar";
        Assert.Equal(VisionTexts.WrongClassCount, form.Own.ErrorOf(nameof(ClassNamesEditor.Text)));
        Assert.False(form.OkCommand.CanExecute(null));
        form.Own.Text = string.Join("\n", PageSamples.Coco);
        Assert.True(form.OkCommand.CanExecute(null));
    }

    [Fact]
    public async Task Choosing_a_list_updates_the_model_with_its_id_and_names()
    {
        var (models, ctx, dialogs) = await LoadAsync();
        ctx.Answers[VisionMethods.UpdateModel] = json => VisionJson.Serialize(VisionJson.Deserialize<UpdateModelRequest>(json).Model);
        dialogs.Model = form =>
        {
            form.SelectedChoice = form.Choices.Single(c => c.Id == "gate6");
            return true;
        };
        var detr = models.Rows.OfType<ModelRow>().Single(r => r.Model.Id == "m2");
        await models.ClassListCommand.ExecuteAsync(detr);
        var sent = VisionJson.Deserialize<UpdateModelRequest>(ctx.LastPayloadOf(VisionMethods.UpdateModel)).Model!;
        Assert.Equal("gate6", sent.ClassListId);
        Assert.Equal(PageSamples.GateClasses.Names, sent.ClassNames);
        Assert.Contains("Gate classes", models.Rows.OfType<ModelRow>().Single(r => r.Model.Id == "m2").Line);
    }

    [Fact]
    public async Task Detect_objects_shows_its_models_class_list_below_the_model_field()
    {
        var ctx = PageSamples.Context();
        using var vm = new VisionViewModel(ctx) { Check = PageSamples.Check };
        await vm.LoadAsync(CancellationToken.None);
        var editor = vm.Editor!;
        editor.Canvas.SelectedNode = editor.Canvas.Find("b");
        var model = Assert.IsType<ModelFieldViewModel>(editor.Form!.Fields[0]);
        Assert.Equal("Class list: Gate classes", model.ClassListText);
        model.Selected = model.Models.Single(m => m.Id == "m2");
        Assert.Equal("Class list: Own names", model.ClassListText);
    }

    [Fact]
    public void Without_a_fitting_list_the_dialog_says_why_and_offers_own_names()
    {
        var form = ModelEditViewModel.ForClassList(PageSamples.Helmet, PageSamples.ClassLists.Lists);
        Assert.Equal([VisionTexts.OwnNames], form.Choices.Select(c => c.Name));
        Assert.True(form.IsOwn);
        Assert.Equal("No class list has 2 names. Create one or use own names.", form.NoFittingList);
        Assert.True(form.OkCommand.CanExecute(null));
        Assert.Null(ModelEditViewModel.ForClassList(PageSamples.RfDetr, PageSamples.ClassLists.Lists).NoFittingList);
    }
}
