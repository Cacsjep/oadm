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
                // Named as the server does: "<original> (<host name or address>)".
                var id = $"p{11 + copies.Count}";
                var address = ctx.DeviceList.First(d => d.Id == camera).Address;
                var copy = PageSamples.GatePeople with { Id = id, Name = $"Gate 1 people ({address})", DeviceId = camera };
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
    public async Task Each_row_has_copy_and_delete_and_copy_waits_for_unsaved_edits()
    {
        var ctx = Context();
        using var vm = await LoadAsync(ctx);
        var shown = vm.Pipelines.Single(p => p.Id == "p1");
        var other = vm.Pipelines.Single(p => p.Id == "p2");
        var actions = vm.ActionsOf(shown);
        Assert.Equal([VisionTexts.CopyToCameras, "-", VisionTexts.Delete], actions.Select(a => a.Text));
        Assert.True(actions[^1].IsDanger);
        Assert.True(vm.CopyCommand.CanExecute(shown));

        vm.Editor!.Name = "Gate 1 people (new)"; // an unsaved edit of the shown pipeline
        Assert.False(vm.CopyCommand.CanExecute(shown));
        Assert.Equal(VisionTexts.CopyToCamerasSaveFirst, vm.ActionsOf(shown)[0].Text); // off with its reason
        Assert.True(vm.CopyCommand.CanExecute(other)); // another, saved pipeline copies
        vm.Editor.Name = "Gate 1 people";
        Assert.True(vm.CopyCommand.CanExecute(shown));

        ctx.Denied.Add(VisionViewModel.ChangePermissionKey);
        ctx.RaisePermissionsChanged();
        Assert.False(vm.CopyCommand.CanExecute(shown));
    }

    [Fact]
    public async Task Delete_of_a_row_asks_first_like_the_header()
    {
        var ctx = Context();
        ctx.Answers[VisionMethods.DeletePipeline] = _ => "{}";
        using var vm = await LoadAsync(ctx);
        var other = vm.Pipelines.Single(p => p.Id == "p2");
        ctx.ConfirmAnswer = false;
        await vm.DeleteRowCommand.ExecuteAsync(other);
        Assert.Equal(VisionTexts.DeletePipeline, ctx.Confirmations[^1].Title);
        Assert.Equal(0, ctx.CallsOf(VisionMethods.DeletePipeline));
        ctx.ConfirmAnswer = true;
        await vm.DeleteRowCommand.ExecuteAsync(other);
        Assert.DoesNotContain(vm.Pipelines, p => p.Id == "p2");
        Assert.Equal("p1", vm.Editor!.Id); // the shown pipeline stays
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

        await vm.CopyCommand.ExecuteAsync(vm.Pipelines[0]);
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
        Assert.Equal("Gate 1 people (10.0.0.51)", vm.Pipelines.Single(p => p.Id == "p11").Name);
        Assert.False(vm.IsCopying);
    }

    [Fact]
    public async Task No_message_when_every_copy_worked_and_none_over_the_limit()
    {
        var ctx = Context();
        using var vm = await LoadAsync(ctx);
        vm.PickCameras = _ => Task.FromResult<IReadOnlyList<Guid>?>([.. PageSamples.MoreCameras.Select(c => c.Id)]);
        await vm.CopyCommand.ExecuteAsync(vm.Pipelines[0]);
        Assert.Empty(ctx.Messages);
        Assert.Equal(8, vm.Pipelines.Count);

        vm.PickCameras = _ => Task.FromResult<IReadOnlyList<Guid>?>([.. Enumerable.Range(0, VisionTexts.MaxCopies + 1).Select(_ => Guid.NewGuid())]);
        await vm.CopyCommand.ExecuteAsync(vm.Pipelines[0]);
        Assert.Equal(1, ctx.CallsOf(VisionMethods.CopyPipeline)); // over 100: no call
        Assert.Equal(VisionTexts.TooManyCameras, Assert.Single(ctx.Messages).Message);
    }
}
