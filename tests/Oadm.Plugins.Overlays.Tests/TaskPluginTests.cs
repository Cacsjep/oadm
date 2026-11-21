using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Overlays.Tests;

/// <summary>The task per camera against a fake camera that behaves like the recorded AXIS OS 12.11.</summary>
public sealed class TaskPluginTests
{
    private readonly OverlaysTaskPlugin _plugin = new AddOverlaysTask();

    /// <summary>The task of a menu entry.</summary>
    internal static OverlaysTaskPlugin For(OverlaysMode mode) => mode switch
    {
        OverlaysMode.Replace => new ReplaceOverlaysTask(),
        OverlaysMode.RemoveMatching => new RemoveMatchingOverlaysTask(),
        OverlaysMode.RemoveAll => new RemoveAllOverlaysTask(),
        _ => new AddOverlaysTask(),
    };

    private static string Payload(params TextOverlay[] overlays) => OverlaysJson.Serialize(new OverlayListPayload { Overlays = overlays });

    private static TextOverlay Overlay(string text, OverlayPosition position = OverlayPosition.BottomLeft) => new() { Text = text, Position = position };

    private static async Task<FakeTaskContext> RunAsync(OverlayCamera camera, OverlaysMode mode, string payload)
    {
        var ctx = new FakeTaskContext(camera);
        await StepRun.RunAsync(ctx.Steps, () => For(mode).ExecuteAsync(ctx, Fixtures.Camera(), payload, CancellationToken.None));
        return ctx;
    }

    [Fact]
    public async Task Add_puts_the_overlays_next_to_the_cameras_own_with_one_step_per_request()
    {
        var camera = new OverlayCamera();
        camera.Put(1, "Lobby");
        var ctx = await RunAsync(camera, OverlaysMode.Add, Payload(Overlay("%F %X"), Overlay("#n", OverlayPosition.TopRight)));

        Assert.Equal(
            [
                "Check the overlay support: Done", "Read the overlay limits: Done", "Read the overlays: Done", "Check the overlays: Done",
                "Add the overlay \"%F %X\": Done", "Add the overlay \"#n\": Done", "Verify the overlays: Done", "Completed: Done",
            ],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("2 to add", StepRun.Detail(ctx.Steps, OverlaysTaskRunner.StepCheck));
        Assert.Equal("8 slots per view area, text up to 512 characters, sizes 16 to 200", StepRun.Detail(ctx.Steps, OverlaysTaskRunner.StepReadLimits));
        Assert.Equal(["Lobby", "%F %X", "#n"], camera.Texts.Select(t => t["text"]!.GetValue<string>()));
        Assert.All(camera.Added, a => Assert.Equal(1, a["camera"]!.GetValue<int>()));
        Assert.All(camera.Added, a => Assert.False(a.ContainsKey("reference") || a.ContainsKey("scrollSpeed")));
        Assert.Equal("Add 2 text overlays", For(OverlaysMode.Add).GetTaskName(Payload(Overlay("%F %X"), Overlay("#n"))));

        // The same again changes nothing.
        var again = await RunAsync(camera, OverlaysMode.Add, Payload(Overlay("%F %X"), Overlay("#n", OverlayPosition.TopRight)));
        Assert.Equal("2 already on the camera", StepRun.Detail(again.Steps, OverlaysTaskRunner.StepCheck));
        Assert.Equal("Nothing was changed.", StepRun.Detail(again.Steps, OverlaysTaskRunner.StepVerify));
        Assert.Equal(2, camera.Added.Count);
    }

    [Fact]
    public async Task Replace_adds_first_then_removes_and_never_touches_images_or_other_view_areas()
    {
        var camera = new OverlayCamera();
        camera.Put(1, "Old");
        camera.Put(1, "%F %X", "bottomLeft");
        camera.Put(2, "Area 2");
        camera.PutImage(1);
        var ctx = await RunAsync(camera, OverlaysMode.Replace, Payload(Overlay("%F %X"), Overlay("New")));

        Assert.Single(camera.Methods, m => m == "remove");
        Assert.True(camera.Methods.IndexOf("addText") < camera.Methods.IndexOf("remove"));
        Assert.Equal(["%F %X", "Area 2", "New"], camera.Texts.Select(t => t["text"]!.GetValue<string>()));
        Assert.Equal(
            [
                "Check the overlay support: Done", "Read the overlay limits: Done", "Read the overlays: Done", "Check the overlays: Done",
                "Add the overlay \"New\": Done", "Remove the overlay \"Old\": Done", "Verify the overlays: Done", "Completed: Done",
            ],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("Set 2 text overlays", For(OverlaysMode.Replace).GetTaskName(Payload(Overlay("a"), Overlay("b"))));
        Assert.Equal(["remove", "list"], camera.Methods.Skip(camera.Methods.Count - 2)); // the removal, then the verification's list
    }

    [Fact]
    public async Task Replace_removes_first_when_the_free_slots_do_not_take_the_new_overlays()
    {
        var camera = new OverlayCamera();
        for (var i = 0; i < 8; i++)
        {
            camera.Put(1, "x" + i);
        }

        var ctx = await RunAsync(camera, OverlaysMode.Replace, Payload(Overlay("New")));

        Assert.True(camera.Methods.IndexOf("remove") < camera.Methods.IndexOf("addText"));
        Assert.Equal(["New"], camera.Texts.Select(t => t["text"]!.GetValue<string>()));
        Assert.Equal("Remove the overlay \"x0\": Done", StepRun.Lines(ctx.Steps)[4]);
        Assert.Equal("Add the overlay \"New\": Done", StepRun.Lines(ctx.Steps)[12]);
        Assert.Equal("Verify the overlays: Done", StepRun.Lines(ctx.Steps)[^2]);
    }

    [Fact]
    public async Task Overlays_are_found_by_content_in_the_list_read_right_before_the_writes()
    {
        var camera = new OverlayCamera();
        camera.Put(1, "Old");
        var query = await _plugin.QueryAsync(new QueryContext(camera), Fixtures.Camera(), OverlaysJson.ListOverlaysMethod, null, CancellationToken.None);
        Assert.DoesNotContain("identity", query!, StringComparison.Ordinal); // the dialog never gets an identity
        camera.Renumber(); // as after a restart

        await RunAsync(camera, OverlaysMode.RemoveMatching, Payload(Overlay("Old")));
        Assert.Empty(camera.Texts);
    }

    [Fact]
    public async Task Remove_all_removes_every_text_overlay_of_view_area_1_and_nothing_else()
    {
        var camera = new OverlayCamera();
        camera.Put(1, "Lobby");
        camera.Put(1, "%F %X");
        camera.Put(2, "Area 2");
        camera.PutImage(1);
        var ctx = await RunAsync(camera, OverlaysMode.RemoveAll, "{}");
        Assert.Equal(["Area 2"], camera.Texts.Select(t => t["text"]!.GetValue<string>()));
        Assert.Equal("2 to remove", StepRun.Detail(ctx.Steps, OverlaysTaskRunner.StepCheck));
        Assert.Equal("0 text overlays in view area 1", StepRun.Detail(ctx.Steps, OverlaysTaskRunner.StepVerify));
        Assert.Equal(2, camera.Methods.Count(m => m == "remove")); // the image overlay stays
        Assert.Equal("Remove all text overlays", For(OverlaysMode.RemoveAll).GetTaskName(null));

        var again = await RunAsync(camera, OverlaysMode.RemoveAll, "{}");
        Assert.Equal("Nothing to change", StepRun.Detail(again.Steps, OverlaysTaskRunner.StepCheck));
    }

    [Fact]
    public async Task Remove_matching_without_a_match_writes_nothing()
    {
        var camera = new OverlayCamera();
        camera.Put(1, "Keep");
        var ctx = await RunAsync(camera, OverlaysMode.RemoveMatching, Payload(Overlay("Gone")));
        Assert.Equal("1 text is not on the camera", StepRun.Detail(ctx.Steps, OverlaysTaskRunner.StepCheck));
        Assert.Equal(0, camera.WriteCount);
        Assert.Equal("Remove text overlay \"Gone\"", For(OverlaysMode.RemoveMatching).GetTaskName(Payload(Overlay("Gone"))));
    }

    [Fact]
    public async Task A_camera_without_the_overlay_api_fails_before_any_request_to_it()
    {
        var camera = new OverlayCamera { Apis = [new DeviceApi("param-cgi", "1.0")] };
        var ctx = new FakeTaskContext(camera);
        var error = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() =>
            StepRun.RunAsync(ctx.Steps, () => For(OverlaysMode.Add).ExecuteAsync(ctx, Fixtures.Camera(), Payload(Overlay("x")), CancellationToken.None)));
        Assert.Equal(OverlaysTaskPlugin.NotSupportedNothingChanged, error.Message);
        Assert.Empty(camera.Methods);
    }

    [Fact]
    public async Task A_full_view_area_fails_before_the_first_write()
    {
        var camera = new OverlayCamera();
        for (var i = 0; i < 7; i++)
        {
            camera.Put(1, "x" + i);
        }

        var ctx = new FakeTaskContext(camera);
        var error = await Assert.ThrowsAsync<OverlayDeviceException>(() =>
            StepRun.RunAsync(ctx.Steps, () => For(OverlaysMode.Add).ExecuteAsync(ctx, Fixtures.Camera(), Payload(Overlay("a"), Overlay("b")), CancellationToken.None)));
        Assert.Equal("The camera has room for 1 more overlay in view area 1, not 2. Nothing was changed.", error.Message);
        Assert.Equal(0, camera.WriteCount);
        Assert.Equal("Check the overlays: Failed", StepRun.Lines(ctx.Steps).Single(l => l.StartsWith("Check the overlays", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_refused_write_says_what_was_already_done()
    {
        var camera = new OverlayCamera { FailNextAdd = 304 };
        camera.Put(1, "Old");
        var ctx = new FakeTaskContext(camera);
        var error = await Assert.ThrowsAsync<OverlayDeviceException>(() =>
            StepRun.RunAsync(ctx.Steps, () => For(OverlaysMode.Replace).ExecuteAsync(ctx, Fixtures.Camera(), Payload(Overlay("New")), CancellationToken.None)));
        Assert.Equal("The camera does not accept this text. Nothing was changed.", error.Message);
        Assert.Equal(["Old"], camera.Texts.Select(t => t["text"]!.GetValue<string>())); // the add came first: the old overlay stays
        Assert.DoesNotContain("remove", camera.Methods);

        var full = new OverlayCamera();
        for (var i = 0; i < 8; i++)
        {
            full.Put(1, "x" + i);
        }

        full.FailNextAdd = 304;
        var ctxFull = new FakeTaskContext(full);
        var afterRemovals = await Assert.ThrowsAsync<OverlayDeviceException>(() =>
            StepRun.RunAsync(ctxFull.Steps, () => For(OverlaysMode.Replace).ExecuteAsync(ctxFull, Fixtures.Camera(), Payload(Overlay("New")), CancellationToken.None)));
        Assert.Equal("The camera does not accept this text. Already done: 8 removed, 0 added.", afterRemovals.Message);

        var first = new OverlayCamera { FailNextAdd = 309 };
        var ctx2 = new FakeTaskContext(first);
        var refused = await Assert.ThrowsAsync<OverlayDeviceException>(() =>
            StepRun.RunAsync(ctx2.Steps, () => For(OverlaysMode.Add).ExecuteAsync(ctx2, Fixtures.Camera(), Payload(Overlay("x")), CancellationToken.None)));
        Assert.Equal("The camera does not accept this size. Nothing was changed.", refused.Message);
    }

    [Fact]
    public async Task An_invalid_payload_never_reaches_the_camera()
    {
        var camera = new OverlayCamera();
        var ctx = new FakeTaskContext(camera);
        await Assert.ThrowsAsync<ArgumentException>(() => For(OverlaysMode.Add).ExecuteAsync(ctx, Fixtures.Camera(), Payload(Overlay("  ")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _plugin.ExecuteAsync(ctx, Fixtures.Camera(), "{", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => For(OverlaysMode.Add).ExecuteAsync(ctx, Fixtures.Camera(), Payload(Overlay(new string('a', 513))), CancellationToken.None));
        Assert.Empty(camera.Methods);
    }

    [Fact]
    public async Task The_query_reads_view_area_1_and_the_limits_and_changes_nothing()
    {
        var camera = new OverlayCamera();
        camera.Put(1, "%F %X", new JsonArray(-0.25, -0.5));
        camera.Put(2, "Area 2");
        var result = OverlaysJson.ParseQueryResult(await _plugin.QueryAsync(new QueryContext(camera), Fixtures.Camera(), OverlaysJson.ListOverlaysMethod, null, CancellationToken.None))!;
        Assert.True(result.Supported);
        Assert.Equal(200, result.Capabilities!.MaxFontSize);
        var only = Assert.Single(result.Overlays);
        Assert.Equal(OverlayPosition.Custom, only.Position);
        Assert.Equal(0, camera.WriteCount);

        var old = new OverlayCamera { Apis = [] };
        var unsupported = OverlaysJson.ParseQueryResult(await _plugin.QueryAsync(new QueryContext(old), Fixtures.Camera(), OverlaysJson.ListOverlaysMethod, null, CancellationToken.None))!;
        Assert.False(unsupported.Supported);
        await Assert.ThrowsAsync<NotSupportedException>(() => _plugin.QueryAsync(new QueryContext(camera), Fixtures.Camera(), "addText", null, CancellationToken.None));
    }

    [Fact]
    public void The_menu_entry_needs_the_api_and_a_working_login()
    {
        Assert.Equal(["Add overlays", "Replace overlays", "Remove matching overlays", "Remove all overlays"],
            new[] { OverlaysMode.Add, OverlaysMode.Replace, OverlaysMode.RemoveMatching, OverlaysMode.RemoveAll }.Select(m => For(m).DisplayName));
        Assert.All(new[] { OverlaysMode.Add, OverlaysMode.Replace, OverlaysMode.RemoveMatching, OverlaysMode.RemoveAll }.Select(For),
            t => Assert.Equal((TaskGroups.Overlays, true), (t.Group, t.RequiresDialog)));
        Assert.True(((ITaskPlugin)For(OverlaysMode.RemoveAll)).IsDestructive); // last in its group, after a separator
        Assert.False(((ITaskPlugin)For(OverlaysMode.RemoveMatching)).IsDestructive);
        Assert.Equal("oadm.overlays.remove-all", For(OverlaysMode.RemoveAll).Id);
        Assert.True(_plugin.CanRun(Fixtures.Camera()));
        var noApi = new FakeDevice { Apis = [new DeviceApi("param-cgi", "1.0")], FirmwareVersion = "9.80.1" };
        Assert.False(_plugin.CanRun(noApi));
        Assert.Equal("Needs text overlays (this device has 9.80.1)", _plugin.NotSupportedReason(noApi));
        Assert.False(_plugin.CanRun(Fixtures.Camera() with { Status = DeviceStatus.Unreachable }));
        Assert.False(_plugin.CanRun(new FakeDevice { Apis = [new DeviceApi("dynamicoverlay", "2.0")] })); // another major version is another API
    }

    [Fact]
    public void Task_names_stay_short_and_name_one_overlay()
    {
        Assert.Equal("Add text overlay \"Lobby floor 1\"", For(OverlaysMode.Add).GetTaskName(Payload(Overlay("Lobby\nfloor 1"))));
        Assert.Equal("Set text overlay \"SPECIAL BLDG NAME FLOOR 3 NORTH ENTRANCE %F %X\"",
            For(OverlaysMode.Replace).GetTaskName(Payload(Overlay("SPECIAL BLDG NAME FLOOR 3 NORTH ENTRANCE %F %X")))); // the whole text
        var longName = For(OverlaysMode.RemoveMatching).GetTaskName(Payload(Overlay(new string('x', 200))));
        Assert.Equal(TaskPluginNames.MaxTaskNameLength, longName.Length); // shortened only past the limit
        Assert.EndsWith("…\"", longName, StringComparison.Ordinal);
        Assert.Equal("Set text overlay \"%F %X\"", For(OverlaysMode.Replace).GetTaskName(Payload(Overlay("%F %X"))));
        Assert.Equal("Add overlays", _plugin.GetTaskName("not json"));
    }
}
