using System.Text.Json.Nodes;

using Avalonia;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>The editor's save, preview, eye, loops and tidy up in the corner cases (docs/spec/vision-center.md "Page", "Graph").</summary>
public sealed class EditorReviewTests
{
    private static PipelineEditorViewModel Editor(FakePageContext ctx, Pipeline pipeline) =>
        new(ctx, pipeline, false, new EngineInfo(), id => id == PageSamples.Gate.Id ? "AXIS Q1798-LE · 10.0.0.48" : "AXIS P1468-LE · 10.0.0.202",
            (_, _) => false, () => PageSamples.Models, PageSamples.Decode, PageSamples.Check);

    private static PipelineNode Node(string id, string kind, string name) => new() { Id = id, Kind = kind, Name = name, X = 5, Y = 5 };

    /// <summary>Camera, and two chains from sources: Opening hours > Open and staffed > Staffed alert, Door open > Staffed.</summary>
    private static Pipeline Sources => new()
    {
        Id = "p9",
        Name = "Sources",
        DeviceId = PageSamples.Gate.Id,
        Mode = PipelineMode.Watching,
        Fps = 8,
        Nodes =
        [
            Node("a", NodeKinds.Camera, "Camera"),
            Node("w", NodeKinds.TimeWindow, "Opening hours") with
            {
                Settings = new JsonObject { ["days"] = new JsonArray("mon"), ["from"] = "08:00", ["to"] = "18:00" },
            },
            Node("w2", NodeKinds.TimeWindow, "Staff hours") with
            {
                Settings = new JsonObject { ["days"] = new JsonArray("mon"), ["from"] = "09:00", ["to"] = "17:00" },
            },
            Node("l1", NodeKinds.Logic, "Open") with { Settings = new JsonObject { ["operator"] = "and" } },
            Node("l2", NodeKinds.Logic, "Staffed") with { Settings = new JsonObject { ["operator"] = "and" } },
            Node("x", NodeKinds.Event, "Staffed alert"),
        ],
        Links =
        [
            new("w", "l1"), new("w2", "l2"), new("l2", "x"),
            new("l1", "l2") { ToPort = NodeKinds.SecondPort },
        ],
    };

    // ---- (1) Save fingerprints what was sent ----

    [Fact]
    public async Task An_edit_while_saving_stays_unsaved_with_its_undo()
    {
        var ctx = PageSamples.Context();
        var answer = new TaskCompletionSource<string?>();
        ctx.AsyncAnswers[VisionMethods.SavePipeline] = _ => answer.Task;
        using var editor = Editor(ctx, PageSamples.GatePeople);
        editor.Name = "Gate 1 people today";
        var save = editor.SaveCommand.ExecuteAsync(null);
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.SavePipeline) == 1, "the save call");
        var sent = VisionJson.Deserialize<SavePipelineRequest>(ctx.LastPayloadOf(VisionMethods.SavePipeline)).Pipeline!;

        editor.Canvas.Add(NodeKinds.EventKind, editor.Canvas.Find("f")!); // an edit while the server saves
        answer.SetResult(VisionJson.Serialize(new SavePipelineReply { Pipeline = sent }));
        await save;

        Assert.True(editor.IsDirty);
        Assert.True(editor.Canvas.UndoCommand.CanExecute(null));
        editor.Canvas.UndoCommand.Execute(null);
        Assert.False(editor.IsDirty); // back to what was saved
    }

    [Fact]
    public async Task A_save_without_edits_meanwhile_is_clean_and_forgets_the_undo()
    {
        var ctx = PageSamples.Context();
        ctx.Answers[VisionMethods.SavePipeline] = json =>
            VisionJson.Serialize(new SavePipelineReply { Pipeline = VisionJson.Deserialize<SavePipelineRequest>(json).Pipeline });
        using var editor = Editor(ctx, PageSamples.GatePeople);
        editor.Canvas.Add(NodeKinds.EventKind, editor.Canvas.Find("f")!);
        Assert.True(editor.Canvas.UndoCommand.CanExecute(null));
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.False(editor.IsDirty);
        Assert.False(editor.Canvas.UndoCommand.CanExecute(null));
    }

    // ---- (2) Loops over control links ----

    [Fact]
    public void A_link_that_closes_a_loop_over_a_control_link_is_refused()
    {
        var canvas = new CanvasViewModel(Sources.Nodes, Sources.Links);
        var open = canvas.Find("l1")!;
        var staffed = canvas.Find("l2")!;
        Assert.True(canvas.IsBefore(open, staffed)); // Open feeds Staffed's second input
        Assert.False(canvas.TryPort(staffed, open, out _, NodeKinds.SecondPort));
        Assert.False(canvas.FitsPort(staffed, open, NodeKinds.SecondPort));
        Assert.True(canvas.TryPort(canvas.Find("w2")!, open, out _, NodeKinds.SecondPort)); // no loop: fits
    }

    // ---- (3) A viewer's eye ----

    [Fact]
    public void A_viewers_eye_redraws_the_video_and_never_makes_the_draft_unsaved()
    {
        var ctx = PageSamples.Context();
        ctx.Denied.Add(VisionViewModel.ChangePermissionKey);
        using var editor = Editor(ctx, PageSamples.GatePeople);
        var detect = editor.Canvas.Find("b")!;
        var changed = 0;
        editor.Canvas.Changed += (_, _) => changed++;
        editor.Canvas.ToggleEye(detect);
        Assert.True(detect.ShowOnVideo);
        Assert.Equal(0, changed);
        Assert.False(editor.IsDirty);
    }

    // ---- (4) The preview's sends ----

    [Fact]
    public async Task A_quick_hide_and_show_sends_the_new_preview_at_once_and_ignores_the_old_ones_answer()
    {
        var ctx = PageSamples.Context();
        var first = new TaskCompletionSource<string?>();
        var calls = 0;
        ctx.AsyncAnswers[VisionMethods.StartPreview] = _ => ++calls == 1 ? first.Task : Task.FromResult<string?>(VisionJson.Serialize(new PreviewReply()));
        ctx.Answers[VisionMethods.StopPreview] = _ => "{}";
        using var editor = Editor(ctx, PageSamples.GatePeople);
        editor.PreviewTime = new FakeTimeProvider(PageSamples.Time); // no keep-alive in this test
        editor.StartPreview();
        var old = editor.PreviewId!;
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.StartPreview) == 1, "the first start");

        editor.StopPreview();
        editor.StartPreview();
        var now = editor.PreviewId!;
        first.SetException(new InvalidOperationException("The camera does not answer.")); // the old call ends late, failing

        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.StartPreview) == 2, "the new preview's start");
        Assert.Equal(now, VisionJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(VisionMethods.StartPreview)).PreviewId);
        Assert.Null(editor.PreviewProblem); // the old preview's failure is not the new one's
        Assert.NotEqual(old, now);
        editor.StopPreview();
    }

    [Fact]
    public async Task A_start_that_lands_after_its_stop_is_stopped_again()
    {
        var ctx = PageSamples.Context();
        var first = new TaskCompletionSource<string?>();
        ctx.AsyncAnswers[VisionMethods.StartPreview] = _ => first.Task;
        ctx.Answers[VisionMethods.StopPreview] = _ => "{}";
        using var editor = Editor(ctx, PageSamples.GatePeople);
        editor.PreviewTime = new FakeTimeProvider(PageSamples.Time);
        editor.StartPreview();
        var id = editor.PreviewId!;
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.StartPreview) == 1, "the start");
        editor.StopPreview();
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.StopPreview) == 1, "the stop");

        first.SetResult(VisionJson.Serialize(new PreviewReply())); // the server answers the start after the stop
        await Eventually.TrueAsync(() => ctx.CallsOf(VisionMethods.StopPreview) == 2, "the second stop");
        Assert.Equal(id, VisionJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(VisionMethods.StopPreview)).PreviewId);
    }

    // ---- (5) Tidy up of chains from sources ----

    [Fact]
    public void Tidy_up_lays_out_the_chain_of_a_source_from_top_to_bottom()
    {
        var canvas = new CanvasViewModel(Sources.Nodes, Sources.Links) { ViewportSize = new Size(900, 500) };
        canvas.TidyUpCommand.Execute(null);
        double Row(string id) => canvas.Find(id)!.Y;
        Assert.True(Row("w2") < Row("l2") && Row("l2") < Row("x")); // Staff hours > Staffed > Staffed alert, top to bottom
        Assert.True(Row("w") < Row("l1"));
        var rects = canvas.Nodes.Select(n => n.Rect).ToList();
        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                Assert.False(rects[i].Intersects(rects[j]), $"{canvas.Nodes[i].Name} and {canvas.Nodes[j].Name} overlap");
            }
        }
    }
}
