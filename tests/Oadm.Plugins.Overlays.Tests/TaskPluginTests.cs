using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Overlays.Tests;

/// <summary>The task per camera against a fake camera that behaves like the recorded AXIS OS 12.11.</summary>
public sealed class TaskPluginTests
{
    private readonly OverlaysTaskPlugin _plugin = new();

    private static string Payload(OverlaysMode mode, params TextOverlay[] overlays) =>
        OverlaysJson.Serialize(new OverlaysPayload { Mode = mode, Overlays = overlays });

    private static TextOverlay Overlay(string text, OverlayPosition position = OverlayPosition.BottomLeft) => new() { Text = text, Position = position };

    private async Task<FakeTaskContext> RunAsync(OverlayCamera camera, string payload)
    {
        var ctx = new FakeTaskContext(camera);
        await StepRun.RunAsync(ctx.Steps, () => _plugin.ExecuteAsync(ctx, Fixtures.Camera(), payload, CancellationToken.None));
        return ctx;
    }

    [Fact]
    public async Task Add_puts_the_overlays_next_to_the_cameras_own_with_one_step_per_request()
    {
        var camera = new OverlayCamera();
        camera.Put(1, "Lobby");
        var ctx = await RunAsync(camera, Payload(OverlaysMode.Add, Overlay("%F %X"), Overlay("#n", OverlayPosition.TopRight)));

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
        Assert.Equal("Add 2 text overlays", _plugin.GetTaskName(Payload(OverlaysMode.Add, Overlay("%F %X"), Overlay("#n"))));

        // The same again changes nothing.
        var again = await RunAsync(camera, Payload(OverlaysMode.Add, Overlay("%F %X"), Overlay("#n", OverlayPosition.TopRight)));
        Assert.Equal("2 already on the camera", StepRun.Detail(again.Steps, OverlaysTaskRunner.StepCheck));
        Assert.Equal("Nothing was changed.", StepRun.Detail(again.Steps, OverlaysTaskRunner.StepVerify));
        Assert.Equal(2, camera.Added.Count);
    }

    [Fact]
    public async Task Replace_removes_first_then_adds_and_never_touches_images_or_other_view_areas()
    {
        var camera = new OverlayCamera();
        camera.Put(1, "Old");
        camera.Put(1, "%F %X", "bottomLeft");
        camera.Put(2, "Area 2");
        camera.PutImage(1);
        var ctx = await RunAsync(camera, Payload(OverlaysMode.Replace, Overlay("%F %X"), Overlay("New")));

        Assert.Single(camera.Methods, m => m == "remove");
        Assert.True(camera.Methods.IndexOf("remove") < camera.Methods.IndexOf("addText"));
        Assert.Equal(["%F %X", "Area 2", "New"], camera.Texts.Select(t => t["text"]!.GetValue<string>()));
        Assert.Contains("Remove the overlay \"Old\": Done", StepRun.Lines(ctx.Steps));
        Assert.Equal("Set 2 text overlays", _plugin.GetTaskName(Payload(OverlaysMode.Replace, Overlay("a"), Overlay("b"))));
        Assert.Equal("Remove all text overlays", _plugin.GetTaskName(Payload(OverlaysMode.Replace)));
        Assert.Equal(["addText", "list"], camera.Methods.Skip(camera.Methods.Count - 2)); // the add, then the verification's list
    }

    [Fact]
    public async Task Overlays_are_found_by_content_in_the_list_read_right_before_the_writes()
    {
        var camera = new OverlayCamera();
        camera.Put(1, "Old");
        var query = await _plugin.QueryAsync(new QueryContext(camera), Fixtures.Camera(), OverlaysJson.ListOverlaysMethod, null, CancellationToken.None);
        Assert.DoesNotContain("identity", query!, StringComparison.Ordinal); // the dialog never gets an identity
        camera.Renumber(); // as after a restart

        await RunAsync(camera, Payload(OverlaysMode.RemoveMatching, Overlay("Old")));
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
        var ctx = await RunAsync(camera, OverlaysJson.Serialize(new OverlaysPayload { Mode = OverlaysMode.RemoveAll }));
        Assert.Equal(["Area 2"], camera.Texts.Select(t => t["text"]!.GetValue<string>()));
        Assert.Equal("2 to remove", StepRun.Detail(ctx.Steps, OverlaysTaskRunner.StepCheck));
        Assert.Equal("0 text overlays in view area 1", StepRun.Detail(ctx.Steps, OverlaysTaskRunner.StepVerify));
        Assert.Equal(2, camera.Methods.Count(m => m == "remove")); // the image overlay stays
        Assert.Equal("Remove all text overlays", _plugin.GetTaskName("""{"mode":"removeAll"}"""));

        var again = await RunAsync(camera, """{"mode":"removeAll"}""");
        Assert.Equal("Nothing to change", StepRun.Detail(again.Steps, OverlaysTaskRunner.StepCheck));
    }

    [Fact]
    public async Task Remove_matching_without_a_match_writes_nothing()
    {
        var camera = new OverlayCamera();
        camera.Put(1, "Keep");
        var ctx = await RunAsync(camera, Payload(OverlaysMode.RemoveMatching, Overlay("Gone")));
        Assert.Equal("1 text is not on the camera", StepRun.Detail(ctx.Steps, OverlaysTaskRunner.StepCheck));
        Assert.Equal(0, camera.WriteCount);
        Assert.Equal("Remove text overlay \"Gone\"", _plugin.GetTaskName(Payload(OverlaysMode.RemoveMatching, Overlay("Gone"))));
    }

    [Fact]
    public async Task A_camera_without_the_overlay_api_fails_before_any_request_to_it()
    {
        var camera = new OverlayCamera { Apis = [new DeviceApi("param-cgi", "1.0")] };
        var ctx = new FakeTaskContext(camera);
        var error = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() =>
            StepRun.RunAsync(ctx.Steps, () => _plugin.ExecuteAsync(ctx, Fixtures.Camera(), Payload(OverlaysMode.Add, Overlay("x")), CancellationToken.None)));
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
            StepRun.RunAsync(ctx.Steps, () => _plugin.ExecuteAsync(ctx, Fixtures.Camera(), Payload(OverlaysMode.Add, Overlay("a"), Overlay("b")), CancellationToken.None)));
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
            StepRun.RunAsync(ctx.Steps, () => _plugin.ExecuteAsync(ctx, Fixtures.Camera(), Payload(OverlaysMode.Replace, Overlay("New")), CancellationToken.None)));
        Assert.Equal("The camera does not accept this text. Already done: 1 removed, 0 added.", error.Message);

        var first = new OverlayCamera { FailNextAdd = 309 };
        var ctx2 = new FakeTaskContext(first);
        var refused = await Assert.ThrowsAsync<OverlayDeviceException>(() =>
            StepRun.RunAsync(ctx2.Steps, () => _plugin.ExecuteAsync(ctx2, Fixtures.Camera(), Payload(OverlaysMode.Add, Overlay("x")), CancellationToken.None)));
        Assert.Equal("The camera does not accept this size. Nothing was changed.", refused.Message);
    }

    [Fact]
    public async Task An_invalid_payload_never_reaches_the_camera()
    {
        var camera = new OverlayCamera();
        var ctx = new FakeTaskContext(camera);
        await Assert.ThrowsAsync<ArgumentException>(() => _plugin.ExecuteAsync(ctx, Fixtures.Camera(), Payload(OverlaysMode.Add, Overlay("  ")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _plugin.ExecuteAsync(ctx, Fixtures.Camera(), "{", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _plugin.ExecuteAsync(ctx, Fixtures.Camera(), Payload(OverlaysMode.Add, Overlay(new string('a', 513))), CancellationToken.None));
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
        Assert.Equal("Text overlays", _plugin.DisplayName);
        Assert.Equal(TaskGroups.Video, _plugin.Group);
        Assert.True(_plugin.RequiresDialog);
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
        Assert.Equal("Add text overlay \"Lobby\"", _plugin.GetTaskName(Payload(OverlaysMode.Add, Overlay("Lobby\nfloor 1"))));
        Assert.Equal("Set text overlay \"%F %X\"", _plugin.GetTaskName(Payload(OverlaysMode.Replace, Overlay("%F %X"))));
        Assert.True(_plugin.GetTaskName(Payload(OverlaysMode.RemoveMatching, Overlay(new string('x', 200)))).Length <= TaskPluginNames.MaxTaskNameLength);
        Assert.Equal("Text overlays", _plugin.GetTaskName("not json"));
    }
}
