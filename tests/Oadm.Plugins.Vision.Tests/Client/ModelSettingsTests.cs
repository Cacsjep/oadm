using Oadm.Plugins.Shared.Vision;
using Oadm.Plugins.Shared.Vision.Client;
using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>A model's Settings dialog and the pipeline list's problem chips (docs/spec/vision-center.md "Models", "Page").</summary>
public sealed class ModelSettingsTests
{
    private sealed class Dialogs : IVisionDialogs
    {
        public Func<ModelSettingsViewModel, bool> Settings { get; set; } = _ => false;

        public List<ModelSettingsViewModel> Forms { get; } = [];

        public Task<bool> EditStreamAsync(StreamSettingsViewModel form) => Task.FromResult(false);

        public Task<bool> EditModelAsync(ModelEditViewModel form) => Task.FromResult(false);

        public Task<bool> EditClassListAsync(ClassListEditViewModel form) => Task.FromResult(false);

        public Task<bool> EditModelSettingsAsync(ModelSettingsViewModel form)
        {
            Forms.Add(form);
            return Task.FromResult(Settings(form));
        }

        public Task<IReadOnlyList<IModelFile>?> PickModelFilesAsync() => Task.FromResult<IReadOnlyList<IModelFile>?>(null);
    }

    [Fact]
    public void The_formats_follow_the_models_kind_and_the_choices_start_at_the_models_values()
    {
        var detection = new ModelSettingsViewModel(PageSamples.Detr);
        Assert.Equal(["YOLO", "YOLO with suppression", "DETR", "RF-DETR", "DETR with labels", "YOLOX"], detection.Formats.Select(f => f.Name));
        Assert.Equal(ModelFormat.Detr, detection.Format.Value);
        Assert.Equal(["Letterbox", "Stretch", "Keep shape and pad"], ModelSettingsViewModel.Fits.Select(f => f.Name));
        Assert.Equal(["0 to 1", "ImageNet", "0 to 255, BGR"], ModelSettingsViewModel.Scalings.Select(s => s.Name));
        Assert.True(detection.CanSize);
        Assert.Equal("800", detection.InputWidth);

        var classification = new ModelSettingsViewModel(PageSamples.Helmet);
        Assert.Equal(["Classification"], classification.Formats.Select(f => f.Name));
        Assert.False(new ModelSettingsViewModel(PageSamples.RfDetr).CanSize); // a fixed input
    }

    [Theory]
    [InlineData("640", null)]
    [InlineData("32", null)]
    [InlineData("2048", null)]
    [InlineData("650", "rule")]
    [InlineData("0", "rule")]
    [InlineData("2080", "rule")]
    [InlineData("abc", "rule")]
    public void The_input_size_is_a_multiple_of_32_from_32_to_2048(string width, string? error)
    {
        var form = new ModelSettingsViewModel(PageSamples.Detr) { InputWidth = width };
        Assert.Equal(error is null ? null : ModelTexts.InputSizeRule, form.ErrorOf(nameof(ModelSettingsViewModel.InputWidth)));
        Assert.Equal(error is null, form.OkCommand.CanExecute(null));
    }

    [Fact]
    public void A_fixed_input_never_checks_or_sends_a_size()
    {
        var form = new ModelSettingsViewModel(PageSamples.RfDetr) { InputWidth = "7" };
        Assert.True(form.OkCommand.CanExecute(null));
        Assert.Equal(576, form.ToModel().InputWidth);
    }

    [Fact]
    public async Task Save_sends_the_settings_and_the_row_shows_the_answer()
    {
        var ctx = PageSamples.Context();
        var dialogs = new Dialogs
        {
            Settings = form =>
            {
                form.Format = form.Formats.Single(f => f.Value == ModelFormat.Yolo);
                form.Fit = ModelSettingsViewModel.Fits.Single(f => f.Value == ModelFit.Stretch);
                form.Scaling = ModelSettingsViewModel.Scalings.Single(s => s.Value == ModelScaling.ImageNet);
                form.InputWidth = "640";
                form.InputHeight = "480";
                return true;
            },
        };
        var models = new ModelsViewModel(ctx) { Dialogs = dialogs };
        await models.LoadAsync(CancellationToken.None);
        ctx.Answers[VisionMethods.UpdateModel] = json => VisionJson.Serialize(VisionJson.Deserialize<UpdateModelRequest>(json).Model);
        var row = models.Rows.OfType<ModelRow>().Single(r => r.Model.Id == "m2");
        Assert.Equal(VisionTexts.ModelSettings, models.ActionsOf(row)[1].Text); // after Rename, before Class list
        await models.SettingsCommand.ExecuteAsync(row);

        var sent = VisionJson.Deserialize<UpdateModelRequest>(ctx.LastPayloadOf(VisionMethods.UpdateModel)).Model!;
        Assert.Equal((ModelFormat.Yolo, ModelFit.Stretch, ModelScaling.ImageNet, 640, 480), (sent.Format, sent.Fit, sent.Scaling, sent.InputWidth, sent.InputHeight));
        Assert.Equal(640, models.Rows.OfType<ModelRow>().Single(r => r.Model.Id == "m2").Model.InputWidth);
    }

    [Fact]
    public async Task A_refusal_of_a_field_shows_below_it_and_the_dialog_opens_again()
    {
        var ctx = PageSamples.Context();
        var calls = 0;
        var dialogs = new Dialogs { Settings = _ => ++calls == 1 };
        var models = new ModelsViewModel(ctx) { Dialogs = dialogs };
        await models.LoadAsync(CancellationToken.None);
        ctx.Answers[VisionMethods.UpdateModel] = _ => throw new InvalidOperationException(ModelTexts.OutputsDoNotFit);
        await models.SettingsCommand.ExecuteAsync(models.Rows.OfType<ModelRow>().Single(r => r.Model.Id == "m2"));

        Assert.Equal(2, dialogs.Forms.Count);
        Assert.Same(dialogs.Forms[0], dialogs.Forms[1]);
        Assert.Equal(ModelTexts.OutputsDoNotFit, dialogs.Forms[0].ErrorOf(nameof(ModelSettingsViewModel.Format)));
        Assert.Empty(ctx.Messages);

        var form = new ModelSettingsViewModel(PageSamples.Detr);
        Assert.True(form.ShowRefusal(ModelTexts.InputSizeRule));
        Assert.Equal(ModelTexts.InputSizeRule, form.ErrorOf(nameof(ModelSettingsViewModel.InputWidth)));
        Assert.False(form.ShowRefusal("Something else."));
    }

    [Fact]
    public async Task Another_refusal_goes_to_the_hosts_message()
    {
        var ctx = PageSamples.Context();
        var dialogs = new Dialogs { Settings = _ => true };
        var models = new ModelsViewModel(ctx) { Dialogs = dialogs };
        await models.LoadAsync(CancellationToken.None);
        ctx.Answers[VisionMethods.UpdateModel] = _ => throw new InvalidOperationException("The model no longer exists.");
        await models.SettingsCommand.ExecuteAsync(models.Rows.OfType<ModelRow>().Single(r => r.Model.Id == "m2"));
        Assert.Single(dialogs.Forms);
        Assert.Equal("The model no longer exists.", Assert.Single(ctx.Messages).Message);
    }

    [Fact]
    public async Task Each_kind_of_problem_has_its_short_chip_and_the_whole_text_stays_the_tooltip()
    {
        var ctx = PageSamples.Context();
        var list = PageSamples.List;
        string[] problems =
        [
            PipelineRules.TooManyWatching(24), DeviceMessages.TimedOut, DeviceMessages.CredentialsRequired, CameraFrameTexts.ResolutionNotOffered,
            PipelineRules.Off, PipelineRules.NotRunning, PipelineRules.Starting, PipelineRules.ModelsFail,
        ];
        ctx.Answers[VisionMethods.ListPipelines] = _ => VisionJson.Serialize(list with
        {
            Pipelines = [.. problems.Select((p, i) => list.Pipelines[0] with { Id = "x" + i, Name = "P" + i, Problem = p })],
        });
        using var vm = new VisionViewModel(ctx) { Check = PageSamples.Check, DecodePicture = PageSamples.Decode };
        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(
            [ProblemChips.LimitReached, ProblemChips.NotReachable, ProblemChips.LoginRefused, ProblemChips.StreamError,
                ProblemChips.NotInDeviceGroup, ProblemChips.NotInstalled, ProblemChips.Starting, VisionTexts.NotRunning],
            vm.Pipelines.OrderBy(p => p.Row.Name, StringComparer.Ordinal).Select(p => p.ChipText));
        Assert.All(vm.Pipelines, p => Assert.True(p.IsError));
        Assert.Equal(PipelineRules.ModelsFail, vm.Pipelines.Single(p => p.Row.Name == "P7").Problem);
    }
}
