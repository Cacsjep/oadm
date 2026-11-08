using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>Copy to cameras (decision 2026-11-08): independent copies of a saved pipeline on other cameras.</summary>
public sealed class CopyPipelineTests
{
    /// <summary>The fake server: copies get ids p11, p12, ... and open with getPipeline; <paramref name="refuse"/> by camera.</summary>
    internal static FakePageContext Context(Dictionary<Guid, string>? refuse = null)
    {
        var ctx = PageSamples.Context();
        var copies = new Dictionary<string, Pipeline>(StringComparer.Ordinal);
        ctx.Answers[VisionMethods.CopyPipeline] = json =>
        {
            var request = VisionJson.Deserialize<CopyPipelineRequest>(json);
            var created = new List<Pipeline>();
            foreach (var camera in request.DeviceIds.Where(id => refuse?.ContainsKey(id) != true))
            {
                var id = $"p{11 + copies.Count}";
                var copy = PageSamples.GatePeople with { Id = id, Name = $"Gate 1 people {copies.Count + 2}", DeviceId = camera };
                copies[id] = copy;
                created.Add(copy);
            }

            return VisionJson.Serialize(new CopyPipelineReply
            {
                Created = created,
                Refused = refuse?.Where(r => request.DeviceIds.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value) ?? [],
            });
        };
        var get = ctx.Answers[VisionMethods.GetPipeline];
        ctx.Answers[VisionMethods.GetPipeline] = json =>
            copies.TryGetValue(VisionJson.Deserialize<PipelineRequest>(json).PipelineId, out var copy) ? VisionJson.Serialize(copy) : get(json);
        return ctx;
    }

    private static async Task<VisionViewModel> LoadAsync(FakePageContext ctx)
    {
        var vm = new VisionViewModel(ctx) { Check = PageSamples.Check };
        await vm.LoadAsync(CancellationToken.None);
        return vm;
    }

    [Fact]
    public async Task Copy_is_on_for_a_saved_pipeline_without_unsaved_edits()
    {
        var ctx = Context();
        using var vm = await LoadAsync(ctx);
        Assert.True(vm.CopyCommand.CanExecute(null));
        Assert.Equal(VisionTexts.CopyToCameras, vm.CopyTip); // an icon button: its name as the tooltip

        vm.Editor!.Name = "Gate 1 people (new)"; // an unsaved edit
        Assert.False(vm.CopyCommand.CanExecute(null));
        Assert.Equal(VisionTexts.SaveFirst, vm.CopyTip);
        vm.Editor.Name = "Gate 1 people";
        Assert.True(vm.CopyCommand.CanExecute(null));

        vm.PickCamera = _ => Task.FromResult<Guid?>(PageSamples.Dock.Id);
        await vm.NewCommand.ExecuteAsync(null); // a new pipeline: save it first
        Assert.False(vm.CopyCommand.CanExecute(null));

        ctx.Denied.Add(VisionViewModel.ChangePermissionKey);
        ctx.RaisePermissionsChanged();
        Assert.False(vm.MayChange);
        Assert.False(vm.CopyCommand.CanExecute(null));
    }

    [Fact]
    public async Task One_call_the_copies_join_the_list_the_first_shows_and_refusals_come_in_one_message()
    {
        var refused = PageSamples.MoreCameras[2].Id;
        var ctx = Context(new Dictionary<Guid, string> { [refused] = "At most 50 pipelines." });
        using var vm = await LoadAsync(ctx);
        IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo>? offered = null;
        vm.PickCameras = targets =>
        {
            offered = targets;
            return Task.FromResult<IReadOnlyList<Guid>?>([PageSamples.MoreCameras[0].Id, PageSamples.MoreCameras[1].Id, refused]);
        };

        await vm.CopyCommand.ExecuteAsync(null);
        Assert.DoesNotContain(offered!, d => d.Id == PageSamples.Gate.Id); // never the pipeline's own camera
        Assert.Equal(1, ctx.CallsOf(VisionMethods.CopyPipeline));
        var request = VisionJson.Deserialize<CopyPipelineRequest>(ctx.LastPayloadOf(VisionMethods.CopyPipeline));
        Assert.Equal("p1", request.PipelineId);
        Assert.Equal(3, request.DeviceIds.Count);

        Assert.Equal(5, vm.Pipelines.Count);
        Assert.Equal("p11", vm.Editor!.Id); // the first copy shows
        Assert.Same(vm.Pipelines.Single(p => p.Id == "p11"), vm.SelectedPipeline);
        var (title, message) = Assert.Single(ctx.Messages);
        Assert.Equal(VisionTexts.CopyToCameras, title);
        Assert.Equal("AXIS P1468-LE · 10.0.0.53: At most 50 pipelines.", message);
        Assert.False(vm.IsCopying);
    }

    [Fact]
    public async Task No_message_when_every_copy_worked_and_none_over_the_limit()
    {
        var ctx = Context();
        using var vm = await LoadAsync(ctx);
        vm.PickCameras = _ => Task.FromResult<IReadOnlyList<Guid>?>([.. PageSamples.MoreCameras.Select(c => c.Id)]);
        await vm.CopyCommand.ExecuteAsync(null);
        Assert.Empty(ctx.Messages);
        Assert.Equal(8, vm.Pipelines.Count);

        vm.PickCameras = _ => Task.FromResult<IReadOnlyList<Guid>?>([.. Enumerable.Range(0, VisionTexts.MaxCopies + 1).Select(_ => Guid.NewGuid())]);
        await vm.CopyCommand.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(VisionMethods.CopyPipeline)); // over 100: no call
        Assert.Equal(VisionTexts.TooManyCameras, Assert.Single(ctx.Messages).Message);
    }
}
