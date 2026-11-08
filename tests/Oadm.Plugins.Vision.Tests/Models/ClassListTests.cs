using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Plugins.Vision.Tests.Pipelines;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Models;

/// <summary>Class list profiles chosen on the model (docs/spec/vision-center.md "Class lists"): the built-in COCO tables, the choice at the check, the user's lists, their rules and methods.</summary>
public sealed class ClassListTests : IDisposable
{
    private readonly TempFolder _data = new();
    private readonly MemoryPluginRecords _records = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
    private readonly ModelLibrary _library;

    public ClassListTests() => _library = new ModelLibrary(_records, _data.Path, _time);

    public void Dispose()
    {
        _library.Dispose();
        _data.Dispose();
    }

    private static byte[] Yolo(int classes, string? names = null)
    {
        var model = OnnxModel.Picture().Output("output0", [1, 4 + classes, 200], new float[(4 + classes) * 200]);
        if (names is not null)
        {
            model.Metadata("names", names);
        }

        return model.Build();
    }

    private static byte[] Detr(int classes) =>
        OnnxModel.Picture(800, 800).Output("pred_logits", [1, 10, classes + 1], new float[10 * (classes + 1)]).Output("pred_boxes", [1, 10, 4], new float[40]).Build();

    private async Task<VisionModel> UploadAsync(byte[] file, string name = "model.onnx", string? labels = null)
    {
        var id = await _library.BeginUploadAsync(name, file.Length, labels, CancellationToken.None);
        await _library.AppendAsync(id, 0, file, CancellationToken.None);
        var check = await _library.FinishUploadAsync(id, CancellationToken.None);
        return check.Model ?? throw new InvalidOperationException(check.Refusal);
    }

    private static ClassList List(string name, params string[] names) => new() { Name = name, Names = names };

    [Fact]
    public void Coco_91_follows_the_coco_2017_category_ids()
    {
        var names = ClassLists.Coco91.Names;
        Assert.Equal(91, names.Count);
        Assert.Equal(("person", "car", "toothbrush"), (names[1], names[3], names[90]));
        Assert.Equal(("traffic light", "stop sign", "dining table", "tv", "book", "hair drier"), (names[10], names[13], names[67], names[72], names[84], names[89]));
        Assert.Equal([0, 12, 26, 29, 30, 45, 66, 68, 69, 71, 83], Enumerable.Range(0, 91).Where(i => names[i] == VisionLimits.NoClassName));
        Assert.True(ClassLists.Coco91.BuiltIn);
        Assert.Equal(("coco-91", "COCO for DETR (91)"), (ClassLists.Coco91.Id, ClassLists.Coco91.Name));
    }

    [Fact]
    public void Coco_80_holds_the_classes_in_a_row()
    {
        var names = ClassLists.Coco80.Names;
        Assert.Equal(80, names.Count);
        Assert.Equal(("person", "bicycle", "toothbrush"), (names[0], names[1], names[79]));
        Assert.Equal(("traffic light", "stop sign", "tv"), (names[9], names[11], names[62]));
        Assert.DoesNotContain(VisionLimits.NoClassName, names);
        Assert.Equal(80, names.Distinct().Count());
        Assert.Equal(("coco-80", "COCO for YOLO (80)", true), (ClassLists.Coco80.Id, ClassLists.Coco80.Name, ClassLists.Coco80.BuiltIn));
    }

    [Fact]
    public void Coco_90_holds_rf_detrs_slots_by_category_id()
    {
        var names = ClassLists.Coco90.Names;
        Assert.Equal(90, names.Count);
        Assert.Equal(("person", "car", "dog", "hair drier"), (names[1], names[3], names[18], names[89]));
        Assert.Equal([0, 12, 26, 29, 30, 45, 66, 68, 69, 71, 83], Enumerable.Range(0, 90).Where(i => names[i] == VisionLimits.NoClassName));
        Assert.Equal(ClassLists.Coco91.Names.Take(90), names);
        Assert.Equal(("coco-90", "COCO for RF-DETR (90)", true), (ClassLists.Coco90.Id, ClassLists.Coco90.Name, ClassLists.Coco90.BuiltIn));
        Assert.Equal(["coco-91", "coco-90", "coco-80"], ClassLists.BuiltIn.Select(l => l.Id));
    }

    [Fact]
    public async Task An_rf_detr_model_of_90_classes_gets_its_coco_list()
    {
        var rfDetr = OnnxModel.Picture(576, 576).Output("dets", [1, 300, 4], new float[1200]).Output("labels", [1, 300, 90], new float[300 * 90]).Build();
        var model = await UploadAsync(rfDetr, "rf-detr-medium.onnx");
        Assert.Equal((ModelFormat.RfDetr, ClassLists.Coco90Id), (model.Format, model.ClassListId));
        Assert.Equal("dog", model.ClassNames[18]);
    }

    [Fact]
    public async Task Rf_detr_with_the_outputs_of_rf_detr_onnx_gets_the_coco_91_list()
    {
        // rf-detr-medium.onnx (RealModels): pred_boxes first, then pred_logits with 91 columns, the COCO category ids.
        var rfDetr = OnnxModel.Picture(576, 576).Output("pred_boxes", [1, 300, 4], new float[1200]).Output("pred_logits", [1, 300, 91], new float[300 * 91]).Build();
        var model = await UploadAsync(rfDetr, "rf-detr-medium.onnx");
        Assert.Equal((ModelFormat.RfDetr, ModelFit.Stretch, ClassLists.Coco91Id), (model.Format, model.Fit, model.ClassListId));
        Assert.Equal(("person", "car", "toothbrush"), (model.ClassNames[1], model.ClassNames[3], model.ClassNames[90]));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Stored_models_of_90_generated_names_get_the_rf_detr_list_at_the_start(bool generated, bool migrated)
    {
        // As stored before the list existed: no list, 90 names.
        var names = Enumerable.Range(0, 90).Select(i => generated ? "Class " + i : "Mine " + i).ToList();
        var stored = new VisionModel { Name = "rf-detr", Kind = ModelKind.Detection, Format = ModelFormat.RfDetr, InputWidth = 576, InputHeight = 576, FixedInput = true, ClassNames = names, Sha256 = "ab" };
        await _records.SetManyAsync(ModelLibrary.Collection, [new("0123456789abcdef0123456789abcdef", VisionJson.Serialize(stored))], CancellationToken.None);

        await _library.LoadAsync(CancellationToken.None);

        var model = Assert.Single(_library.All);
        Assert.Equal(migrated ? ClassLists.Coco90Id : null, model.ClassListId);
        Assert.Equal(migrated ? "dog" : "Mine 18", model.ClassNames[18]);
        var json = (await _records.ListAsync(ModelLibrary.Collection, CancellationToken.None))[model.Id];
        Assert.Equal(migrated, json.Contains(ClassLists.Coco90Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_without_names_gets_the_built_in_list_of_its_class_count()
    {
        var detr = await UploadAsync(Detr(91), "detr.onnx");
        Assert.Equal(ClassLists.Coco91Id, detr.ClassListId);
        Assert.Equal(ClassLists.Coco91.Names, detr.ClassNames);

        var yolo = await UploadAsync(Yolo(80), "yolo.onnx");
        Assert.Equal(ClassLists.Coco80Id, yolo.ClassListId);
        Assert.Equal("person", yolo.ClassNames[0]);

        var other = await UploadAsync(Yolo(3), "three.onnx");
        Assert.Null(other.ClassListId);
        Assert.Equal(["Class 0", "Class 1", "Class 2"], other.ClassNames);
    }

    [Fact]
    public async Task Names_of_the_model_or_its_labels_file_win()
    {
        var names = "{" + string.Join(", ", Enumerable.Range(0, 80).Select(i => $"{i}: 'thing {i}'")) + "}";
        var named = await UploadAsync(Yolo(80, names), "named.onnx");
        Assert.Null(named.ClassListId);
        Assert.Equal("thing 0", named.ClassNames[0]);

        var labelled = await UploadAsync(Detr(91), "labelled.onnx", string.Join('\n', Enumerable.Range(0, 91).Select(i => "label " + i)));
        Assert.Null(labelled.ClassListId);
        Assert.Equal("label 90", labelled.ClassNames[90]);
    }

    [Fact]
    public async Task A_list_of_another_count_cannot_be_chosen()
    {
        var detr = await UploadAsync(Detr(91));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => _library.UpdateAsync(detr with { ClassListId = ClassLists.Coco80Id }, CancellationToken.None));
        Assert.Equal("This class list has 80 names, the model has 91 classes.", error.Message);
    }

    [Fact]
    public async Task Choosing_a_list_takes_its_names_and_own_names_can_be_edited_again()
    {
        var model = await UploadAsync(Yolo(3));
        var list = await _library.SaveClassListAsync(List("Gate", "person", "car", "bike"), CancellationToken.None);

        var chosen = await _library.UpdateAsync(model with { ClassListId = list.Id, ClassNames = ["ignored"] }, CancellationToken.None);
        Assert.Equal(list.Id, chosen.ClassListId);
        Assert.Equal(["person", "car", "bike"], chosen.ClassNames);

        var own = await _library.UpdateAsync(chosen with { ClassListId = null, ClassNames = ["a", "b", "c"] }, CancellationToken.None);
        Assert.Null(own.ClassListId);
        Assert.Equal(["a", "b", "c"], own.ClassNames);
        Assert.Equal(ModelTexts.ClassListGone, (await Assert.ThrowsAsync<ArgumentException>(() => _library.UpdateAsync(own with { ClassListId = "0123456789abcdef0123456789abcdef" }, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task A_changed_list_refreshes_the_names_of_its_models()
    {
        var model = await UploadAsync(Yolo(2));
        var list = await _library.SaveClassListAsync(List("Lane", "car", "truck"), CancellationToken.None);
        await _library.UpdateAsync(model with { ClassListId = list.Id }, CancellationToken.None);
        var changes = 0;
        _library.Changed += (_, _) => changes++;

        await _library.SaveClassListAsync(list with { Name = "Lane cars", Names = ["car", "lorry"] }, CancellationToken.None);

        Assert.Equal(["car", "lorry"], _library.Find(model.Id)!.ClassNames);
        Assert.Equal(1, changes);
        Assert.Contains("lorry", (await _records.ListAsync(ModelLibrary.Collection, CancellationToken.None))[model.Id], StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<ClassListFieldException>(() => _library.SaveClassListAsync(list with { Names = ["car"] }, CancellationToken.None));
        Assert.Equal((ClassListFieldException.NamesField, "Used by models with 2 classes. Keep 2 names."), (error.Field, error.Message));
    }

    [Fact]
    public async Task Saving_keeps_to_the_rules()
    {
        async Task<(string Field, string Text)> Refused(ClassList list)
        {
            var error = await Assert.ThrowsAsync<ClassListFieldException>(() => _library.SaveClassListAsync(list, CancellationToken.None));
            return (error.Field, error.Message);
        }

        await _library.SaveClassListAsync(List("Gate", "a"), CancellationToken.None);
        Assert.Equal(("name", "Enter a name."), await Refused(List(" ", "a")));
        Assert.Equal(("name", "A name has at most 64 characters."), await Refused(List(new string('x', 65), "a")));
        Assert.Equal(("name", "A class list named Gate already exists."), await Refused(List("GATE", "a")));
        Assert.Equal(("name", "A class list named COCO for YOLO (80) already exists."), await Refused(List("coco for yolo (80)", "a")));
        Assert.Equal(("names", "Enter at least one name."), await Refused(List("Empty")));
        Assert.Equal(("names", "A class list has at most 1,000 names."), await Refused(new ClassList { Name = "Many", Names = [.. Enumerable.Range(0, 1001).Select(i => "c" + i)] }));
        Assert.Equal(("names", "A name holds no line break."), await Refused(List("Break", "a\nb")));
        Assert.Equal(("names", "A class name has at most 64 characters."), await Refused(List("Long", new string('x', 65))));

        var saved = await _library.SaveClassListAsync(List(" Lane ", " car ", "", "N/A"), CancellationToken.None);
        Assert.Equal(("Lane", false, 32), (saved.Name, saved.BuiltIn, saved.Id.Length));
        Assert.Equal(["car", "Class 1", "N/A"], saved.Names);
        Assert.Equal(ModelTexts.ClassListBuiltInChanged, (await Assert.ThrowsAsync<ArgumentException>(() => _library.SaveClassListAsync(ClassLists.Coco80 with { Name = "Mine" }, CancellationToken.None))).Message);
        Assert.Equal(["COCO for DETR (91)", "COCO for RF-DETR (90)", "COCO for YOLO (80)", "Gate", "Lane"], _library.ClassLists.Select(l => l.Name));
    }

    [Fact]
    public async Task At_most_50_lists_are_kept()
    {
        for (var i = 0; i < ModelLimits.MaxClassLists; i++)
        {
            await _library.SaveClassListAsync(List("List " + i, "a"), CancellationToken.None);
        }

        var error = await Assert.ThrowsAsync<ArgumentException>(() => _library.SaveClassListAsync(List("One more", "a"), CancellationToken.None));
        Assert.Equal("OADM keeps at most 50 class lists. Delete one first.", error.Message);
    }

    [Fact]
    public async Task Delete_is_refused_for_built_in_lists_and_while_models_use_one()
    {
        Assert.Equal("A built-in class list cannot be deleted.", (await Assert.ThrowsAsync<InvalidOperationException>(() => _library.DeleteClassListAsync(ClassLists.Coco91Id, CancellationToken.None))).Message);

        var list = await _library.SaveClassListAsync(List("Gate", "a", "b"), CancellationToken.None);
        var first = await UploadAsync(Yolo(2), "gate people.onnx");
        var second = await UploadAsync(Yolo(2, "{0: 'x', 1: 'y'}"), "lane cars.onnx");
        await _library.UpdateAsync(first with { ClassListId = list.Id }, CancellationToken.None);
        await _library.UpdateAsync(second with { ClassListId = list.Id }, CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _library.DeleteClassListAsync(list.Id, CancellationToken.None));
        Assert.Equal("Used by 2 models: gate people, lane cars.", error.Message);

        await _library.UpdateAsync(_library.Find(first.Id)! with { ClassListId = null }, CancellationToken.None);
        await _library.UpdateAsync(_library.Find(second.Id)! with { ClassListId = null }, CancellationToken.None);
        await _library.DeleteClassListAsync(list.Id, CancellationToken.None);
        Assert.Equal(3, _library.ClassLists.Count);
        Assert.Empty(await _records.ListAsync(ModelLibrary.ListCollection, CancellationToken.None));
        Assert.Equal(ModelTexts.ClassListGone, (await Assert.ThrowsAsync<ArgumentException>(() => _library.DeleteClassListAsync(list.Id, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task Lists_are_in_the_records_and_a_missing_model_keeps_its_list()
    {
        var file = Yolo(2);
        var list = await _library.SaveClassListAsync(List("Gate", "a", "b"), CancellationToken.None);
        var model = await UploadAsync(file);
        await _library.UpdateAsync(model with { ClassListId = list.Id }, CancellationToken.None);

        // A restore on another computer: the records (models and lists) without the model's file.
        File.Delete(ModelLibrary.PathOf(_data.Path, model.Id));
        using var restored = new ModelLibrary(_records, _data.Path, _time);
        await restored.LoadAsync(CancellationToken.None);
        Assert.Equal(["COCO for DETR (91)", "COCO for RF-DETR (90)", "COCO for YOLO (80)", "Gate"], restored.ClassLists.Select(l => l.Name));
        Assert.True(restored.Find(model.Id)!.Missing);

        var id = await restored.BeginUploadAsync("again.onnx", file.Length, null, CancellationToken.None);
        await restored.AppendAsync(id, 0, file, CancellationToken.None);
        var back = (await restored.FinishUploadAsync(id, CancellationToken.None)).Model!;
        Assert.Equal((list.Id, false), (back.ClassListId, back.Missing));
        Assert.Equal(["a", "b"], back.ClassNames);
    }

    [Fact]
    public void Saving_and_deleting_lists_need_the_models_right_and_all_devices_and_are_audited()
    {
        var plugin = new VisionHarness().Plugin;
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(VisionMethods.ListClassLists));
        Assert.False(plugin.IsAudited(VisionMethods.ListClassLists));
        Assert.False(plugin.RequiresAllDevices(VisionMethods.ListClassLists));
        foreach (var method in new[] { VisionMethods.SaveClassList, VisionMethods.DeleteClassList })
        {
            Assert.Equal(VisionInfo.ModelsPermission, plugin.RequiredPermission(method));
            Assert.True(plugin.RequiresAllDevices(method));
            Assert.True(plugin.IsAudited(method));
        }
    }

    [Fact]
    public async Task The_methods_list_save_and_delete_with_their_audit_names()
    {
        var context = new FakeCoreContext { DataDirectory = _data.Path };
        var plugin = new VisionPlugin(new VisionParts
        {
            Models = ctx => new ModelLibrary(ctx.Records!, _data.Path, _time),
            Inference = _ => new FakeInference(),
            Frames = _ => new FakeFrames(),
            Time = _time,
        });
        await plugin.StartAsync(context, CancellationToken.None);
        try
        {
            async Task<T> Call<T>(string method, object payload) =>
                VisionJson.Deserialize<T>(await context.InvokeAsync(plugin, context.CallAsAllDevices(), method, VisionJson.Serialize(payload)));

            var lists = await Call<ClassListList>(VisionMethods.ListClassLists, new { });
            Assert.Equal([ClassLists.Coco91Id, ClassLists.Coco90Id, ClassLists.Coco80Id], lists.Lists.Select(l => l.Id));

            var refused = await Call<SaveClassListReply>(VisionMethods.SaveClassList, new SaveClassListRequest { ClassList = List(" ", "a") });
            Assert.Null(refused.ClassList);
            Assert.Equal("Enter a name.", refused.Errors["name"]);

            var request = new SaveClassListRequest { ClassList = List("Gate", "person", "car") };
            var saved = (await Call<SaveClassListReply>(VisionMethods.SaveClassList, request)).ClassList!;
            Assert.Equal("Saved class list Gate", plugin.AuditDetail(VisionMethods.SaveClassList, VisionJson.Serialize(request)));
            Assert.Equal(4, (await Call<ClassListList>(VisionMethods.ListClassLists, new { })).Lists.Count);

            var delete = new ClassListRequest { ClassListId = saved.Id };
            await Call<object>(VisionMethods.DeleteClassList, delete);
            Assert.Equal("Deleted class list Gate", plugin.AuditDetail(VisionMethods.DeleteClassList, VisionJson.Serialize(delete)));
            Assert.Equal(3, (await Call<ClassListList>(VisionMethods.ListClassLists, new { })).Lists.Count);
        }
        finally
        {
            await plugin.StopAsync(CancellationToken.None);
        }
    }
}
