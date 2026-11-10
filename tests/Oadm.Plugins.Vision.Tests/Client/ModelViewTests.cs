using System.Text.Json.Nodes;

using Avalonia.Media.Imaging;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>"What the model sees" on the page: shown for model nodes with Live view, read on selection, after a model change with the next frame.</summary>
public sealed class ModelViewTests
{
    private int _reads;
    private string? _lastNode;

    private ModelViewViewModel View(bool maySee = true, string? previewId = "p1") => new(
        (preview, node) =>
        {
            _reads++;
            _lastNode = node;
            return Task.FromResult<ModelViewReply?>(new ModelViewReply { Problem = PipelineRules.NoObjectsToClassify, InputWidth = 224, InputHeight = 224, Fit = ModelFit.Stretch });
        },
        () => previewId,
        _ => Task.FromResult<Bitmap?>(null),
        maySee);

    private static NodeViewModel Node(string id, string kind, string? model = null) =>
        new(new PipelineNode { Id = id, Kind = kind, Name = id, Settings = model is null ? [] : new JsonObject { [NodeKinds.ModelKey] = model } });

    [Fact]
    public void Only_a_model_node_shows_it_and_its_selection_reads_it_once()
    {
        var view = View();
        view.Select(Node("cam", NodeKinds.Camera));
        Assert.False(view.IsShown);
        Assert.Equal(0, _reads);

        var detect = Node("det", NodeKinds.Detect, "m1");
        view.Select(detect);
        Assert.True(view.IsShown);
        Assert.Equal(1, _reads);
        Assert.Equal("det", _lastNode);
        Assert.Equal(PipelineRules.NoObjectsToClassify, view.Problem);

        // The same node again, or a frame without a change: no read.
        view.Select(detect);
        view.FrameArrived();
        Assert.Equal(1, _reads);

        view.Select(Node("cls", NodeKinds.Classify, "m3"));
        Assert.Equal(2, _reads);
    }

    [Fact]
    public void A_new_model_is_read_with_the_next_frame()
    {
        var view = View();
        var detect = Node("det", NodeKinds.Detect, "m1");
        view.Select(detect);
        detect.Settings = new JsonObject { [NodeKinds.ModelKey] = "m2" };
        view.DraftChanged(detect);
        Assert.Equal(1, _reads); // the server has the change only with the next frame
        view.FrameArrived();
        Assert.Equal(2, _reads);
        view.FrameArrived();
        Assert.Equal(2, _reads);
    }

    [Fact]
    public async Task Without_live_view_or_a_preview_there_is_no_picture()
    {
        var hidden = View(maySee: false);
        hidden.Select(Node("det", NodeKinds.Detect, "m1"));
        Assert.False(hidden.IsShown);
        Assert.Equal(0, _reads);

        var noPreview = View(previewId: null);
        noPreview.Select(Node("det", NodeKinds.Detect, "m1"));
        await noPreview.ReadAsync();
        Assert.Equal(0, _reads);
        Assert.Equal(PipelineRules.NoPictureYet, noPreview.Problem);
    }
}
