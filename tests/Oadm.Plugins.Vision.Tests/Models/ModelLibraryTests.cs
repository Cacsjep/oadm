using System.Security.Cryptography;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Models;

/// <summary>The stored models (docs/spec/vision-center.md "Models", "Tests"): the upload in chunks, its refusals and limits, Missing and back, delete and changes.</summary>
public sealed class ModelLibraryTests : IDisposable
{
    private readonly TempFolder _data = new();
    private readonly MemoryPluginRecords _records = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
    private readonly ModelLibrary _library;

    public ModelLibraryTests() => _library = new ModelLibrary(_records, _data.Path, _time);

    public void Dispose()
    {
        _library.Dispose();
        _data.Dispose();
    }

    private string Folder => Path.Combine(_data.Path, ModelLibrary.FolderName);

    private static byte[] Yolo(int classes = 3) => OnnxModel.Picture().Output("output0", [1, 4 + classes, 50], new float[(4 + classes) * 50]).Build();

    private static async Task<ModelCheck> UploadAsync(ModelLibrary library, byte[] file, string name = "gate-people.onnx", string? labels = null, int chunk = 100)
    {
        var id = await library.BeginUploadAsync(name, file.Length, labels, CancellationToken.None);
        for (var offset = 0; offset < file.Length; offset += chunk)
        {
            await library.AppendAsync(id, offset, file.AsMemory(offset, Math.Min(chunk, file.Length - offset)), CancellationToken.None);
        }

        return await library.FinishUploadAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task An_upload_in_chunks_is_checked_hashed_and_stored()
    {
        await _library.LoadAsync(CancellationToken.None);
        var file = Yolo();
        var changes = 0;
        _library.Changed += (_, _) => changes++;

        var check = await UploadAsync(_library, file, labels: "person\nbicycle\ncar");

        Assert.Null(check.Refusal);
        var model = check.Model!;
        Assert.Equal("gate-people", model.Name);
        Assert.Equal(ModelFormat.Yolo, model.Format);
        Assert.Equal(ModelKind.Detection, model.Kind);
        Assert.Equal(ModelFit.Letterbox, model.Fit);
        Assert.Equal(["person", "bicycle", "car"], model.ClassNames);
        Assert.Equal(file.Length, model.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(file)), model.Sha256);
        Assert.Equal(_time.GetUtcNow(), model.UploadedAt);
        Assert.Equal(32, model.Id.Length);
        Assert.Equal(file, await File.ReadAllBytesAsync(_library.FileOf(model.Id)!));
        Assert.Equal(ModelLibrary.PathOf(_data.Path, model.Id), _library.FileOf(model.Id));
        Assert.Single(await _records.ListAsync(ModelLibrary.Collection, CancellationToken.None));
        Assert.Equal([model.Id + ".onnx"], Directory.GetFiles(Folder).Select(Path.GetFileName));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task A_refused_model_is_not_kept()
    {
        var check = await UploadAsync(_library, OnnxModel.Picture().Output("output0", [1, 7, 50], new float[350]).NodeOf("com.example.custom").Build());
        Assert.Null(check.Model);
        Assert.StartsWith("This model cannot run here: it uses operators of", check.Refusal, StringComparison.Ordinal);
        Assert.Empty(_library.All);
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task An_upload_smaller_than_it_said_is_refused()
    {
        var file = Yolo();
        var id = await _library.BeginUploadAsync("m.onnx", file.Length + 10, null, CancellationToken.None);
        await _library.AppendAsync(id, 0, file, CancellationToken.None);
        Assert.Equal(ModelTexts.UploadTooShort, (await _library.FinishUploadAsync(id, CancellationToken.None)).Refusal);
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task An_upload_larger_than_it_said_ends()
    {
        var file = Yolo();
        var id = await _library.BeginUploadAsync("m.onnx", file.Length - 1, null, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => _library.AppendAsync(id, 0, file, CancellationToken.None));
        Assert.Equal(ModelTexts.UploadTooLong, error.Message);
        Assert.Equal(0, _library.Uploads);
        Assert.Empty(Directory.GetFiles(Folder));
        Assert.Equal(ModelTexts.UploadGone, (await Assert.ThrowsAsync<ArgumentException>(() => _library.FinishUploadAsync(id, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task A_lost_chunk_ends_the_upload()
    {
        var file = Yolo();
        var id = await _library.BeginUploadAsync("m.onnx", file.Length, null, CancellationToken.None);
        await _library.AppendAsync(id, 0, file.AsMemory(0, 100), CancellationToken.None);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => _library.AppendAsync(id, 200, file.AsMemory(200, 100), CancellationToken.None));
        Assert.Equal(ModelTexts.UploadOutOfOrder, error.Message);
        Assert.Equal(0, _library.Uploads);
    }

    [Fact]
    public async Task An_upload_without_chunks_ends_after_the_idle_timeout()
    {
        var file = Yolo();
        var id = await _library.BeginUploadAsync("m.onnx", file.Length, null, CancellationToken.None);
        await _library.AppendAsync(id, 0, file.AsMemory(0, 100), CancellationToken.None);

        _time.Advance(VisionLimits.UploadIdleTimeout - TimeSpan.FromSeconds(1));
        await _library.AppendAsync(id, 100, file.AsMemory(100, 100), CancellationToken.None); // a chunk starts the time anew
        _time.Advance(VisionLimits.UploadIdleTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(1, _library.Uploads);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => _library.Uploads == 0 && Directory.GetFiles(Folder).Length == 0, "the idle upload ends and its file goes");
        Assert.Equal(ModelTexts.UploadGone, (await Assert.ThrowsAsync<ArgumentException>(() => _library.AppendAsync(id, 200, file.AsMemory(200, 100), CancellationToken.None))).Message);
    }

    [Fact]
    public async Task Cancel_ends_the_upload_and_deletes_its_file()
    {
        var id = await _library.BeginUploadAsync("m.onnx", 1000, null, CancellationToken.None);
        await _library.CancelUploadAsync(id, CancellationToken.None);
        Assert.Equal(0, _library.Uploads);
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Theory]
    [InlineData("model.pt", 100, "Choose an ONNX file (.onnx).")]
    [InlineData("model.onnx", 0, "The file is empty.")]
    [InlineData("model.onnx", VisionLimits.MaxModelBytes + 1, "A model is at most 500 MB.")]
    public async Task Uploads_that_cannot_be_models_are_refused_at_once(string name, long size, string text)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => _library.BeginUploadAsync(name, size, null, CancellationToken.None));
        Assert.Equal(text, error.Message);
    }

    [Fact]
    public async Task At_most_20_models_are_kept()
    {
        for (var i = 0; i < VisionLimits.MaxModels; i++)
        {
            await _library.BeginUploadAsync("m.onnx", 1000, null, CancellationToken.None);
        }

        var error = await Assert.ThrowsAsync<ArgumentException>(() => _library.BeginUploadAsync("m.onnx", 1000, null, CancellationToken.None));
        Assert.Equal("OADM keeps at most 20 models. Delete one first.", error.Message);
    }

    [Fact]
    public async Task The_models_take_at_most_4_GB_together()
    {
        for (var i = 0; i < 8; i++)
        {
            await _library.BeginUploadAsync("m.onnx", VisionLimits.MaxModelBytes, null, CancellationToken.None); // 8 x 500 MB fits
        }

        var error = await Assert.ThrowsAsync<ArgumentException>(() => _library.BeginUploadAsync("m.onnx", 200L * 1024 * 1024, null, CancellationToken.None));
        Assert.Equal("The models take at most 4 GB together. Delete one first.", error.Message);
    }

    [Fact]
    public async Task A_labels_file_of_more_than_1000_names_is_refused()
    {
        var labels = string.Join('\n', Enumerable.Range(0, VisionLimits.MaxClasses + 1).Select(i => "c" + i));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => _library.BeginUploadAsync("m.onnx", 1000, labels, CancellationToken.None));
        Assert.Equal("A labels file has at most 1,000 names.", error.Message);
    }

    [Fact]
    public async Task The_same_file_twice_is_refused()
    {
        var file = Yolo();
        var first = (await UploadAsync(_library, file)).Model!;
        Assert.Equal(ModelTexts.AlreadyStored(first.Name), (await UploadAsync(_library, file, "again.onnx")).Refusal);
        Assert.Single(_library.All);
    }

    [Fact]
    public async Task Names_are_made_unique()
    {
        var first = (await UploadAsync(_library, Yolo(3))).Model!;
        var second = (await UploadAsync(_library, Yolo(4))).Model!;
        Assert.Equal(("gate-people", "gate-people 2"), (first.Name, second.Name));
    }

    [Fact]
    public async Task A_model_without_its_file_is_missing_and_comes_back_with_its_settings()
    {
        var file = Yolo();
        var model = (await UploadAsync(_library, file)).Model!;
        var renamed = await _library.UpdateAsync(model with { Name = "Gate people", Fit = ModelFit.Stretch, ClassNames = ["person", "bike", "car"] }, CancellationToken.None);

        // A restore on another computer: the records without the file.
        File.Delete(ModelLibrary.PathOf(_data.Path, model.Id));
        using var restored = new ModelLibrary(_records, _data.Path, _time);
        await restored.LoadAsync(CancellationToken.None);
        var missing = Assert.Single(restored.All);
        Assert.True(missing.Missing);
        Assert.Null(restored.FileOf(model.Id));
        Assert.Equal("Import the model Gate people again.", ModelTexts.UploadAgain(missing.Name));

        var back = (await UploadAsync(restored, file, "whatever.onnx")).Model!;
        Assert.Equal(model.Id, back.Id);
        Assert.False(back.Missing);
        Assert.Equal((renamed.Name, renamed.Fit), (back.Name, back.Fit));
        Assert.Equal(renamed.ClassNames, back.ClassNames);
        Assert.NotNull(restored.FileOf(model.Id));
        Assert.False(Assert.Single(restored.All).Missing);
    }

    [Fact]
    public async Task Left_over_upload_files_are_deleted_at_the_start()
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "0123.part"), [1, 2, 3]);
        await _library.LoadAsync(CancellationToken.None);
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task Delete_is_refused_while_pipelines_use_the_model()
    {
        var model = (await UploadAsync(_library, Yolo())).Model!;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _library.DeleteAsync(model.Id, ["Gate 1", "Lane 2"], CancellationToken.None));
        Assert.Equal("Used by 2 pipelines: Gate 1, Lane 2.", error.Message);
        Assert.Equal("Used by 1 pipeline: Gate 1.", ModelTexts.UsedBy(["Gate 1"]));
        Assert.Single(_library.All);

        await _library.DeleteAsync(model.Id, [], CancellationToken.None);
        Assert.Empty(_library.All);
        Assert.Empty(await _records.ListAsync(ModelLibrary.Collection, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Folder));
        Assert.Equal(ModelTexts.Gone, (await Assert.ThrowsAsync<ArgumentException>(() => _library.DeleteAsync(model.Id, [], CancellationToken.None))).Message);
    }

    [Fact]
    public async Task Changes_keep_to_the_rules()
    {
        var model = (await UploadAsync(_library, Yolo(3))).Model!;
        var other = (await UploadAsync(_library, Yolo(4), "other.onnx")).Model!;

        async Task<string> Refused(VisionModel changed) => (await Assert.ThrowsAsync<ArgumentException>(() => _library.UpdateAsync(changed, CancellationToken.None))).Message;

        Assert.Equal("Enter a name.", await Refused(model with { Name = " " }));
        Assert.Equal(ModelTexts.NameTaken(other.Name), await Refused(model with { Name = other.Name.ToUpperInvariant() }));
        Assert.Equal(ModelTexts.FormatOfOtherKind, await Refused(model with { Format = ModelFormat.Classification }));
        Assert.Equal(ModelTexts.InputSizeFixed, await Refused(model with { InputWidth = 320 }));

        var changed = await _library.UpdateAsync(model with { Name = " Gate ", Format = ModelFormat.YoloWithSuppression, Scaling = ModelScaling.ImageNet, ClassNames = ["a", " ", "c"], Sha256 = "forged", CpuMs = 1 }, CancellationToken.None);
        Assert.Equal(("Gate", ModelFormat.YoloWithSuppression, ModelScaling.ImageNet), (changed.Name, changed.Format, changed.Scaling));
        Assert.Equal(["a", "Class 1", "c"], changed.ClassNames);
        Assert.Equal((model.Sha256, model.CpuMs), (changed.Sha256, changed.CpuMs)); // what the check found stays
    }

    [Fact]
    public async Task A_free_input_size_is_a_multiple_of_32()
    {
        var model = (await UploadAsync(_library, OnnxModel.Picture(null, null).Output("output0", [1, 7, 50], new float[350]).Build())).Model!;
        Assert.Equal(ModelTexts.InputSizeRule, (await Assert.ThrowsAsync<ArgumentException>(() => _library.UpdateAsync(model with { InputWidth = 650 }, CancellationToken.None))).Message);
        var changed = await _library.UpdateAsync(model with { InputWidth = 1280, InputHeight = 736 }, CancellationToken.None);
        Assert.Equal((1280, 736), (changed.InputWidth, changed.InputHeight));
    }

    [Fact]
    public async Task The_stored_record_is_the_backup_of_the_model_list()
    {
        var model = (await UploadAsync(_library, Yolo())).Model!;
        var json = (await _records.ListAsync(ModelLibrary.Collection, CancellationToken.None))[model.Id];
        Assert.Contains(model.Sha256, json, StringComparison.Ordinal);
        Assert.DoesNotContain("missing\":true", json, StringComparison.OrdinalIgnoreCase);
    }
}
