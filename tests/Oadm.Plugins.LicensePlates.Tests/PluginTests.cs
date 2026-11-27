using System.Text;
using System.Text.Json.Nodes;

using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>The plugin through its page methods, as the host calls them: plates, tags, import, cameras, reads, events, settings.</summary>
public sealed class PluginTests
{
    [Fact]
    public async Task Plates_are_saved_checked_renamed_and_deleted()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        var staff = await rig.TagAsync("Staff");

        var saved = await rig.PlateAsync("w-12 345 a", staff);
        Assert.Equal("W12345A", saved.Text);

        var duplicate = await rig.CallAsync<SavePlateReply>(LicensePlatesMethods.SavePlate, new SavePlateRequest(new Plate { Text = "W12345A" }));
        Assert.Equal(LicensePlateTexts.PlateExists, duplicate.Errors!["text"]);

        var dates = await rig.CallAsync<SavePlateReply>(LicensePlatesMethods.SavePlate, new SavePlateRequest(new Plate
        {
            Text = "K1",
            AllowedFrom = new DateTime(2026, 12, 1, 8, 0, 0),
            AllowedTo = new DateTime(2026, 11, 1, 8, 0, 0),
        }));
        Assert.Equal(LicensePlateTexts.AfterFrom, dates.Errors!["allowedTo"]);

        var renamed = await rig.CallAsync<SavePlateReply>(LicensePlatesMethods.SavePlate, new SavePlateRequest(saved with { Text = "W12345B", Name = "Anna" }, Original: "W12345A"));
        Assert.Equal("W12345B", renamed.Plate!.Text);
        var page = await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest());
        Assert.Equal(["W12345B"], page.Plates.Select(p => p.Text));

        Assert.Equal(1, (await rig.CallAsync<CountReply>(LicensePlatesMethods.DeletePlates, new PlateTextsRequest(["w12345b", "NOPE"]))).Count);
        Assert.Equal(0, (await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest())).Total);
    }

    [Fact]
    public async Task The_plate_list_is_paged_searched_and_filtered_by_tag()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        var staff = await rig.TagAsync("Staff");
        await rig.PlateAsync("AA100", staff);
        await rig.PlateAsync("AB200");
        await rig.PlateAsync("BB300", staff);

        var first = await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest { Limit = 2 });
        Assert.Equal((3, "AB200"), (first.Total, first.Next));
        var second = await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest { Limit = 2, After = first.Next });
        Assert.Equal(["BB300"], second.Plates.Select(p => p.Text));
        Assert.Equal(["AA100"], (await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest { Search = "aa" })).Plates.Select(p => p.Text));
        Assert.Equal(["AA100", "BB300"], (await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest { TagId = staff.Id })).Plates.Select(p => p.Text));
        Assert.Equal(["AB200"], (await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest { Search = "car ab" })).Plates.Select(p => p.Text));
    }

    [Fact]
    public async Task Change_tags_adds_and_removes_on_the_chosen_plates()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        var staff = await rig.TagAsync("Staff");
        var visitors = await rig.TagAsync("Visitors");
        await rig.PlateAsync("AA100", staff);
        await rig.PlateAsync("BB200", staff, visitors);

        var changed = await rig.CallAsync<CountReply>(LicensePlatesMethods.ChangePlateTags, new ChangePlateTagsRequest(["AA100", "BB200"], [visitors.Id], [staff.Id]));

        Assert.Equal(2, changed.Count);
        Assert.All((await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest())).Plates, p => Assert.Equal([visitors.Id], p.Tags));
    }

    [Fact]
    public async Task A_tag_in_use_asks_first_and_is_removed_from_plates_and_cameras()
    {
        await using var rig = await Rig.StartAsync();
        var staff = await rig.TagAsync("Staff");
        await rig.PlateAsync("AA100", staff);
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0)]));
        await rig.CallAsync(LicensePlatesMethods.SetAllowedTags, new AllowedTagsRequest([rig.Id(0)], Set: [staff.Id]));

        var asked = await rig.CallAsync<DeleteTagReply>(LicensePlatesMethods.DeleteTag, new DeleteTagRequest(staff.Id));
        Assert.Equal(new DeleteTagReply(false, 1, 1), asked);

        var deleted = await rig.CallAsync<DeleteTagReply>(LicensePlatesMethods.DeleteTag, new DeleteTagRequest(staff.Id, Confirmed: true));
        Assert.True(deleted.Deleted);
        Assert.Empty((await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest())).Plates[0].Tags);
        Assert.Empty((await rig.CallAsync<CamerasReply>(LicensePlatesMethods.Cameras)).Cameras[0].AllowedTags);
    }

    [Fact]
    public async Task Tag_names_are_unique_and_hold_no_comma()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        await rig.TagAsync("Staff");

        Assert.Equal("A plate tag with this name exists.", (await rig.CallAsync<SaveTagReply>(LicensePlatesMethods.SaveTag, new SaveTagRequest(new PlateTag { Name = "staff" }))).Errors!["name"]);
        Assert.Equal("A name cannot hold a comma.", (await rig.CallAsync<SaveTagReply>(LicensePlatesMethods.SaveTag, new SaveTagRequest(new PlateTag { Name = "A,B" }))).Errors!["name"]);
    }

    [Fact]
    public async Task An_import_shows_new_changed_and_refused_rows_and_saves_in_one_transaction()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        var staff = await rig.TagAsync("Staff");
        var visitors = await rig.TagAsync("Visitors");
        await rig.PlateAsync("AA100", staff);
        var call = rig.Admin;
        var csv = "Plate,Name,Tags,Allowed from,Allowed to,Delete when it ends\r\n"
            + "AA100,Anna,Staff,,,\r\n"
            + "BB 200,Ben,Visitors,2026-12-01 08:00,2026-12-31 18:00,\r\n"
            + "CC300,Carl,Nobody,,,\r\n"
            + "D,Too short,,,,\r\n"
            + "BB200,Twice,,,,\r\n";
        var upload = ((FakeUploadedFiles)call.Uploads).Add("plates.csv", Encoding.UTF8.GetBytes(csv));
        var writesBefore = rig.Ctx.PluginRecords.WrittenRecords;

        var preview = await rig.CallAsync<ImportPreview>(LicensePlatesMethods.ImportPreview, new ImportRequest(upload.Id), call);

        Assert.Equal((ImportFormat.Oadm, 1, 1, 0, 3), (preview.Format, preview.New, preview.Changed, preview.Unchanged, preview.Refused));
        var rows = (await rig.CallAsync<ImportRowsPage>(LicensePlatesMethods.ImportRows, new ImportRowsRequest(preview.PreviewId), call)).Rows;
        Assert.Equal("No plate tag is named Nobody.", rows.Single(r => r.Plate == "CC300").Error);
        Assert.Equal("This plate is already in the file.", rows.Single(r => r.Line == 6).Error);
        Assert.True(rows.Single(r => r.Plate == "BB200" && r.Status == ImportRowStatus.New).DeleteWhenEnds);
        Assert.Equal(writesBefore, rig.Ctx.PluginRecords.WrittenRecords); // nothing saved by the preview

        var result = await rig.CallAsync<ImportResult>(LicensePlatesMethods.ImportSave, new ImportSaveRequest(preview.PreviewId), call);

        Assert.Equal(new ImportResult(1, 1), result);
        Assert.Equal(writesBefore + 2, rig.Ctx.PluginRecords.WrittenRecords);
        var plates = (await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest())).Plates;
        Assert.Equal("Anna", plates.Single(p => p.Text == "AA100").Name);
        Assert.Equal([visitors.Id], plates.Single(p => p.Text == "BB200").Tags);

        // A preview is saved once and only by its session.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.CallAsync(LicensePlatesMethods.ImportSave, new ImportSaveRequest(preview.PreviewId), call));
    }

    [Fact]
    public async Task ALPVs_export_gives_the_name_and_keeps_a_listed_plates_times_and_tags()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        var staff = await rig.TagAsync("Staff");
        var visitors = await rig.TagAsync("Visitors");
        await rig.CallAsync(LicensePlatesMethods.SavePlate, new SavePlateRequest(new Plate { Text = "AA100", Tags = [staff.Id], AllowedTo = new DateTime(2027, 1, 1) }));
        var call = rig.Admin;
        var upload = ((FakeUploadedFiles)call.Uploads).Add("alpv.csv", Encoding.UTF8.GetBytes("AA100,2026-01-01,Anna\nZZ999,,Delivery\n"));

        var preview = await rig.CallAsync<ImportPreview>(LicensePlatesMethods.ImportPreview, new ImportRequest(upload.Id, [visitors.Id]), call);
        Assert.Equal((ImportFormat.Alpv, 1, 1), (preview.Format, preview.New, preview.Changed));
        await rig.CallAsync(LicensePlatesMethods.ImportSave, new ImportSaveRequest(preview.PreviewId), call);

        var plates = (await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest())).Plates;
        var listed = plates.Single(p => p.Text == "AA100");
        Assert.Equal(("Anna", new DateTime(2027, 1, 1)), (listed.Name, listed.AllowedTo!.Value));
        Assert.Equal([staff.Id, visitors.Id], listed.Tags.Order().ToList(), new GuidOrder(staff.Id, visitors.Id));
        Assert.Equal([visitors.Id], plates.Single(p => p.Text == "ZZ999").Tags);
    }

    private sealed class GuidOrder(Guid a, Guid b) : IEqualityComparer<IEnumerable<Guid>>
    {
        public bool Equals(IEnumerable<Guid>? x, IEnumerable<Guid>? y) => x!.ToHashSet().SetEquals(y!) && x!.ToHashSet().SetEquals([a, b]);

        public int GetHashCode(IEnumerable<Guid> obj) => 0;
    }

    [Fact]
    public async Task Delete_when_it_ends_deletes_the_plate_within_a_minute_after_allowed_to()
    {
        var audit = new FakePluginAudit();
        await using var rig = await Rig.StartAsync(cameras: 0, setup: ctx => ctx.Audit = audit);
        var end = new DateTime(2026, 11, 27, 12, 1, 0);
        await rig.CallAsync(LicensePlatesMethods.SavePlate, new SavePlateRequest(new Plate { Text = "AA100", AllowedTo = end, DeleteWhenEnds = true }));
        await rig.CallAsync(LicensePlatesMethods.SavePlate, new SavePlateRequest(new Plate { Text = "BB200", AllowedTo = end, DeleteWhenEnds = false }));

        rig.Time.Advance(TimeSpan.FromSeconds(90));

        // One audit entry for the sweep, with the count and never the plate (personal data).
        await Eventually.TrueAsync(async () => (await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest())).Plates.Select(p => p.Text).SequenceEqual(["BB200"])
            && audit.Entries.SequenceEqual(["Deleted 1 plate whose time ended"]), "the ended plate is deleted with one audit entry, the other stays");
    }

    [Fact]
    public async Task A_camera_gets_its_own_name_unique_in_the_plugin_and_the_device_keeps_its_own()
    {
        await using var rig = await Rig.StartAsync(cameras: 2);
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0), rig.Id(1)]));
        var before = (await rig.CallAsync<CamerasReply>(LicensePlatesMethods.Cameras)).Cameras;
        var deviceName = rig.Ctx.DeviceList[0].HostName ?? rig.Ctx.DeviceList[0].Address;
        Assert.Equal(deviceName, before.Single(c => c.DeviceId == rig.Id(0)).Name); // by default the device's name

        Assert.Null((await rig.CallAsync<RenameCameraReply>(LicensePlatesMethods.RenameCamera, new RenameCameraRequest(rig.Id(0), "  Main gate "))).Error);
        Assert.Equal(LicensePlateTexts.CameraNameTaken, (await rig.CallAsync<RenameCameraReply>(LicensePlatesMethods.RenameCamera, new RenameCameraRequest(rig.Id(1), "MAIN GATE"))).Error);
        Assert.Equal("Enter a name.", (await rig.CallAsync<RenameCameraReply>(LicensePlatesMethods.RenameCamera, new RenameCameraRequest(rig.Id(1), " "))).Error);
        Assert.Equal("Use at most 64 characters.", (await rig.CallAsync<RenameCameraReply>(LicensePlatesMethods.RenameCamera, new RenameCameraRequest(rig.Id(1), new string('x', 65)))).Error);

        var after = (await rig.CallAsync<CamerasReply>(LicensePlatesMethods.Cameras)).Cameras;
        Assert.Equal("Main gate", after.Single(c => c.DeviceId == rig.Id(0)).Name);
        Assert.Equal("Main gate", rig.Plugin.CameraName(rig.Id(0))); // the log, the stats, the events and the tools
        Assert.Equal("Main gate", rig.Plugin.Store!.Camera(rig.Id(0))!.Name); // kept with the camera in the records
        Assert.Equal(deviceName, rig.Ctx.DeviceList[0].HostName ?? rig.Ctx.DeviceList[0].Address); // never the device in OADM
        Assert.True(LicensePlatesMethods.IsCameraChange(LicensePlatesMethods.RenameCamera)); // the Set up cameras permission, audited
    }

    [Fact]
    public async Task A_camera_whose_first_check_fails_is_checked_again_before_it_connects()
    {
        await using var rig = await Rig.StartAsync();
        var camera = rig.Camera(0);
        camera.FailAppReads = 1;
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0)]));

        // Time moves on (the wait after a failure) until the camera is connected; it never connects unchecked.
        var connectedUnchecked = false;
        await Eventually.TrueAsync(() =>
        {
            var state = rig.Plugin.Runtime!.StateOf(rig.Id(0));
            connectedUnchecked |= camera.IsConnected && state.Checked is null;
            if (state.Status != CameraStatus.Connected)
            {
                rig.Time.Advance(TimeSpan.FromSeconds(1));
                return false;
            }

            return state.Checked is not null;
        }, "connected after a check");
        Assert.False(connectedUnchecked);
        Assert.Equal(0, camera.FailAppReads);
        Assert.Equal("3.1.7", rig.Plugin.Runtime!.StateOf(rig.Id(0)).AlpvVersion);
    }

    [Fact]
    public async Task After_a_connected_session_breaks_the_first_wait_is_1_second_again()
    {
        await using var rig = await Rig.StartAsync();
        var camera = rig.Camera(0);
        camera.Refuse = new HttpRequestException("No route to host");
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0)]));
        await Eventually.TrueAsync(() => rig.Plugin.Runtime!.StateOf(rig.Id(0)).LostSince is not null, "a refused connection");

        camera.Refuse = null;
        await Eventually.TrueAsync(() =>
        {
            if (camera.IsConnected && rig.Plugin.Runtime!.StateOf(rig.Id(0)).Status == CameraStatus.Connected)
            {
                return true;
            }

            rig.Time.Advance(TimeSpan.FromSeconds(1));
            return false;
        }, "connected after the failures");

        // The session breaks: one second later the camera is connected again (not the wait after earlier failures).
        var connections = camera.Connections;
        camera.Drop();
        await Eventually.TrueAsync(() => !camera.IsConnected, "the session broke");
        await Eventually.TrueAsync(() => rig.Plugin.Runtime!.StateOf(rig.Id(0)).LostSince is not null, "the loss is seen");
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => camera.IsConnected && camera.Connections > connections, "connected again after 1 s");
    }

    [Fact]
    public async Task A_read_marked_at_the_same_time_as_a_state_change_never_undoes_it()
    {
        await using var rig = await Rig.StartAsync();
        rig.Camera(0).Refuse = new HttpRequestException("No route to host"); // the connection waits quietly (time stands still)
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0)]));
        await Eventually.TrueAsync(() => rig.Plugin.Runtime!.StateOf(rig.Id(0)).LostSince is not null, "the camera waits");
        var runtime = rig.Plugin.Runtime!;
        var id = rig.Id(0);
        const int Count = 200_000;
        var undone = 0;

        using var stop = new CancellationTokenSource();
        var marks = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                runtime.MarkRead(id, rig.Time.GetUtcNow());
            }
        });
        var changes = Task.Run(() =>
        {
            for (var i = 0; i <= Count; i++)
            {
                var text = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                runtime.Update(id, s => s with { Detail = text });
                if (runtime.StateOf(id).Detail != text)
                {
                    undone++; // a mark wrote back the state from before the change
                }
            }
        });
        await changes;
        await stop.CancelAsync();
        await marks;

        Assert.Equal(0, undone);
    }

    [Fact]
    public async Task A_read_a_wildcard_plate_decides_logs_the_car_and_names_the_pattern()
    {
        await using var rig = await Rig.StartAsync();
        var staff = await rig.TagAsync("Staff");
        await rig.CallAsync(LicensePlatesMethods.SavePlate, new SavePlateRequest(new Plate { Text = "axis 12**", Name = "Company cars", Tags = [staff.Id] }));
        var camera = rig.Camera(0);
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0)]));
        await rig.CallAsync(LicensePlatesMethods.SetAllowedTags, new AllowedTagsRequest([rig.Id(0)], Set: [staff.Id]));
        await Eventually.TrueAsync(() => camera.IsConnected && rig.Plugin.Runtime!.StateOf(rig.Id(0)).Status == CameraStatus.Connected, "the camera is connected");

        await camera.SendAsync(Fixture.Notify("New", "1", "AXIS1234", 0.91));

        await Eventually.TrueAsync(() => rig.Publisher.Published.Any(e => e.TypeId == LicensePlatesInfo.PlateAllowedType), "Plate allowed fires");
        var allowed = rig.Publisher.Published.Single(e => e.TypeId == LicensePlatesInfo.PlateAllowedType);
        Assert.Equal(("AXIS1234", "AXIS12**", "", "Company cars", "Staff"),
            (allowed.Data["plate"], allowed.Data["listEntry"], allowed.Data["readAs"], allowed.Data["name"], allowed.Data["tags"]));
        await Eventually.TrueAsync(() => rig.Plugin.Log!.Store!.Written == 1, "the read is stored");
        var read = Assert.Single((await rig.CallAsync<LogPage>(LicensePlatesMethods.Log, new LogPageRequest())).Rows);
        Assert.Equal(("AXIS1234", "AXIS12**", (string?)null, ReadResult.Allowed), (read.Plate, read.ListEntry, read.ReadAs, read.Result));
    }

    [Fact]
    public async Task The_log_search_by_name_finds_the_reads_of_the_plates_with_that_name_now()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        await rig.Plugin.Store!.WritePlatesAsync(
        [
            new Plate { Text = "W12345A", Name = "Anna Gruber" },
            new Plate { Text = "AXIS12**", Name = "Gruber fleet" },
            new Plate { Text = "K99", Name = "Ben" },
        ], [], CancellationToken.None);
        var store = rig.Plugin.Log!.Store!;
        PlateRead Row(string plate, string? entry, string storedName) => new()
        {
            Id = store.NextId(), Time = rig.Time.GetUtcNow().AddMinutes(-1), CameraId = Guid.NewGuid(), Camera = "gate", Plate = plate,
            ListEntry = entry, Name = storedName, Result = ReadResult.Allowed,
        };
        store.Insert(new Log.LogInsert(Row("W12345A", "W12345A", "Anna Huber"), null)); // the name at the time of the read
        store.Insert(new Log.LogInsert(Row("AXIS1234", "AXIS12**", "Gruber fleet"), null));
        store.Insert(new Log.LogInsert(Row("K99", "K99", "Gruber old"), null)); // a stored name never counts
        await store.FlushAsync();

        var page = await rig.CallAsync<LogPage>(LicensePlatesMethods.Log, new LogPageRequest { Query = new LogQuery { Search = "gruber" } });
        Assert.Equal(["AXIS1234", "W12345A"], page.Rows.Select(r => r.Plate).Order());
        Assert.Equal("Anna Huber", page.Rows.Single(r => r.Plate == "W12345A").Name); // the row keeps its stored name
        Assert.False(page.TooManyNames);
        Assert.Equal(2, await rig.CallAsync<CountReply>(LicensePlatesMethods.LogCount, new LogQuery { Search = "gruber" }) is { } count ? count.Count : -1);
    }

    [Fact]
    public async Task More_than_1000_plates_with_the_name_say_so()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        await rig.Plugin.Store!.WritePlatesAsync(
            [.. Enumerable.Range(0, LicensePlateLimits.MaxNamedPlates + 1).Select(i => new Plate { Text = "F" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), Name = "Fleet car" })], [], CancellationToken.None);

        var page = await rig.CallAsync<LogPage>(LicensePlatesMethods.Log, new LogPageRequest { Query = new LogQuery { Search = "fleet" } });
        Assert.True(page.TooManyNames);
        Assert.Equal("More than 1,000 names match. Type more.", LicensePlateTexts.TooManyNames);
    }

    [Fact]
    public async Task The_list_keeps_at_most_1000_wildcard_plates()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        await rig.Plugin.Store!.WritePlatesAsync(
            [.. Enumerable.Range(0, LicensePlateLimits.MaxWildcardPlates).Select(i => new Plate { Text = "FL" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "**" })], [], CancellationToken.None);
        Assert.Equal(LicensePlateLimits.MaxWildcardPlates, rig.Plugin.Store.WildcardCount);

        var refused = await rig.CallAsync<SavePlateReply>(LicensePlatesMethods.SavePlate, new SavePlateRequest(new Plate { Text = "ZZ99**" }));
        Assert.Equal(LicensePlateTexts.TooManyWildcards, refused.Errors!["text"]);
        var plain = await rig.CallAsync<SavePlateReply>(LicensePlatesMethods.SavePlate, new SavePlateRequest(new Plate { Text = "ZZ9999" }));
        Assert.Null(plain.Errors); // plates on their own still go in
        var changed = await rig.CallAsync<SavePlateReply>(LicensePlatesMethods.SavePlate, new SavePlateRequest(new Plate { Text = "FL0001**", Name = "Fleet" }, "FL0001**"));
        Assert.Null(changed.Errors); // a wildcard plate changed is no new one
    }

    [Fact]
    public void The_audit_text_of_a_sweep_counts_the_plates()
    {
        Assert.Equal("Deleted 1 plate whose time ended", LicensePlatesPlugin.EndedAuditDetail(1));
        Assert.Equal("Deleted 1,200 plates whose time ended", LicensePlatesPlugin.EndedAuditDetail(1200));
    }

    [Fact]
    public async Task A_camera_takes_part_and_an_allowed_plate_fires_its_events_and_is_logged()
    {
        await using var rig = await Rig.StartAsync();
        var staff = await rig.TagAsync("Staff");
        await rig.PlateAsync("W12345A", staff);
        var camera = rig.Camera(0);

        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0)]));
        await rig.CallAsync(LicensePlatesMethods.SetAllowedTags, new AllowedTagsRequest([rig.Id(0)], Set: [staff.Id]));
        await Eventually.TrueAsync(() => camera.IsConnected && rig.Plugin.Runtime!.StateOf(rig.Id(0)).Status == CameraStatus.Connected, "the camera is connected");

        var filter = JsonNode.Parse(camera.Filters[0])!["params"]!["eventFilterList"]!.AsArray().Select(f => f!["topicFilter"]!.GetValue<string>());
        Assert.Equal(Reads.AlpvEvent.Topics, filter);
        Assert.Contains(rig.Plugin.EventTypes, t => t.Id == LicensePlatesInfo.PlateAllowedType && t.DeviceIds!.Contains(rig.Id(0)));

        await camera.SendAsync(Fixture.Notify("New", "1", "W12345A", 0.93, image: Fixture.JpegBase64));

        await Eventually.TrueAsync(() => rig.Publisher.Published.Count(e => e.TypeId is LicensePlatesInfo.PlateAllowedType or LicensePlatesInfo.PlateReadType) == 2, "Plate allowed and Plate read fire");
        var allowed = rig.Publisher.Published.Single(e => e.TypeId == LicensePlatesInfo.PlateAllowedType);
        Assert.Equal(rig.Id(0), allowed.DeviceId);
        Assert.Equal(("W12345A", "", "Staff", "In", "93"), (allowed.Data["plate"], allowed.Data["readAs"], allowed.Data["tags"], allowed.Data["direction"], allowed.Data["confidence"]));
        Assert.Equal("Allowed", rig.Publisher.Published.Single(e => e.TypeId == LicensePlatesInfo.PlateReadType).Data["result"]);

        await Eventually.TrueAsync(() => rig.Plugin.Log!.Store!.Written == 1, "the read is stored");
        var page = await rig.CallAsync<LogPage>(LicensePlatesMethods.Log, new LogPageRequest());
        var read = Assert.Single(page.Rows);
        Assert.Equal(("W12345A", ReadResult.Allowed, "Car W12345A", true), (read.Plate, read.Result, read.Name, read.HasPicture));
        Assert.NotNull((await rig.CallAsync<PictureReply>(LicensePlatesMethods.Picture, new ReadIdRequest(read.Id))).Jpeg);
    }

    [Fact]
    public async Task A_camera_with_ALPV_stopped_or_events_off_shows_it_and_raises_camera_problem()
    {
        await using var rig = await Rig.StartAsync(cameras: 2);
        rig.Camera(0).AppsXml = Fixture.AppsWith(status: "Stopped");
        rig.Camera(1).EventsJson = Fixture.Events.Replace("\"New\":\"true\"", "\"New\":\"false\"", StringComparison.Ordinal);

        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0), rig.Id(1)]));

        await Eventually.TrueAsync(() => rig.Plugin.Runtime!.StateOf(rig.Id(0)).Status == CameraStatus.AlpvStopped
            && rig.Plugin.Runtime!.StateOf(rig.Id(1)).Status == CameraStatus.EventsOff, "both problems show");
        Assert.False(rig.Camera(0).IsConnected);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => rig.Publisher.Published.Any(e => e.TypeId == LicensePlatesInfo.CameraProblemType && e.DeviceId == rig.Id(0) && e.IsOn == true)
            && rig.Publisher.Published.Any(e => e.TypeId == LicensePlatesInfo.CameraProblemType && e.DeviceId == rig.Id(1) && e.IsOn == true)
            && rig.Publisher.Published.Any(e => e.TypeId == LicensePlatesInfo.AnyCameraProblemType && e.IsOn == true && e.Data["count"] == "2"), "Camera problem for both and Any camera problem");
        var cameras = await rig.CallAsync<CamerasReply>(LicensePlatesMethods.Cameras);
        Assert.Equal(("ALPV is stopped", "Events are off in ALPV"), (cameras.Cameras[0].StatusText, cameras.Cameras[1].StatusText));
        Assert.Equal(1, cameras.Connected);
    }

    [Fact]
    public async Task A_connection_lost_for_30_seconds_is_shown_with_its_time_and_becomes_a_gap_row()
    {
        await using var rig = await Rig.StartAsync();
        var camera = rig.Camera(0);
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0)]));
        await Eventually.TrueAsync(() => rig.Plugin.Runtime!.StateOf(rig.Id(0)).Status == CameraStatus.Connected, "connected");

        camera.Refuse = new HttpRequestException("No route to host");
        camera.Drop();
        await Eventually.TrueAsync(() => rig.Plugin.Runtime!.StateOf(rig.Id(0)).LostSince is not null, "the loss is seen");
        rig.Time.Advance(TimeSpan.FromSeconds(31));
        await Eventually.TrueAsync(() => rig.Plugin.Runtime!.StateOf(rig.Id(0)).Status == CameraStatus.ConnectionLost, "Connection lost after 30 s");

        camera.Refuse = null;
        await Eventually.TrueAsync(() =>
        {
            rig.Time.Advance(TimeSpan.FromSeconds(10)); // the reconnect waits
            return rig.Plugin.Runtime!.StateOf(rig.Id(0)).Status == CameraStatus.Connected;
        }, "connected again");
        await Eventually.TrueAsync(() => rig.Plugin.Log!.Store!.Written == 1, "the gap is stored");
        var gap = Assert.Single((await rig.CallAsync<LogPage>(LicensePlatesMethods.Log, new LogPageRequest())).Rows);
        Assert.True(gap.Gap);
        Assert.True(gap.Until > gap.Time);
    }

    [Fact]
    public async Task Unknown_plates_are_not_logged_when_the_setting_is_off_but_still_fire()
    {
        await using var rig = await Rig.StartAsync();
        var settings = PluginJson.Parse<LicensePlateSettings>(await rig.CallAsync(LicensePlatesMethods.Settings), LicensePlatesJson.Options);
        Assert.Empty((await rig.CallAsync<SaveSettingsReply>(LicensePlatesMethods.SaveSettings, settings with { KeepUnknown = false })).Errors ?? new Dictionary<string, string>());
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0)]));
        await Eventually.TrueAsync(() => rig.Camera(0).IsConnected && rig.Plugin.Runtime!.StateOf(rig.Id(0)).Status == CameraStatus.Connected, "connected");

        await rig.Camera(0).SendAsync(Fixture.Notify("New", "9", "K99999", 0.8));
        await rig.Camera(0).SendAsync(Fixture.Notify("Lost", "9", "K99999", 0.8, state: "lost"));

        await Eventually.TrueAsync(() => rig.Publisher.Published.Any(e => e.TypeId == LicensePlatesInfo.UnknownPlateType), "Unknown plate fires");
        await rig.Plugin.Log!.Store!.FlushAsync();
        Assert.Empty((await rig.CallAsync<LogPage>(LicensePlatesMethods.Log, new LogPageRequest())).Rows);
    }

    [Fact]
    public async Task Settings_are_checked_and_the_folder_is_outside_the_backup()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);

        var reply = await rig.CallAsync<SaveSettingsReply>(LicensePlatesMethods.SaveSettings, new LicensePlateSettings
        {
            Deviation = 3,
            WaitSeconds = 11,
            KeepDays = 0,
            MaxGigabytes = 0,
            Folder = rig.Ctx.DataDirectory,
        });

        Assert.Equal(["deviation", "folder", "keepDays", "maxGigabytes", "waitSeconds"], reply.Errors!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Choose a folder on the server.", (await rig.CallAsync<SaveSettingsReply>(LicensePlatesMethods.SaveSettings, new LicensePlateSettings { Folder = "relative/log" })).Errors!["folder"]);
    }

    [Fact]
    public async Task A_folder_the_servers_folder_checks_refuse_is_not_saved()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        var loose = Path.Combine(rig.Folder, "loose");
        var safe = Path.Combine(rig.Folder, "safe");
        Directory.CreateDirectory(loose);
        Directory.CreateDirectory(safe);
        var folders = new FakeServerFolderCheck().Refuse(loose, ServerFolderTexts.OthersCanChange);
        rig.Ctx.FolderCheck = folders;
        var before = PluginJson.Parse<LicensePlateSettings>(await rig.CallAsync(LicensePlatesMethods.Settings), LicensePlatesJson.Options);

        var refused = await rig.CallAsync<SaveSettingsReply>(LicensePlatesMethods.SaveSettings, before with { Folder = loose });

        Assert.Equal(ServerFolderTexts.OthersCanChange, Assert.Single(refused.Errors!, e => e.Key == "folder").Value);
        Assert.Null(refused.Settings);
        Assert.Equal(before.Folder, PluginJson.Parse<LicensePlateSettings>(await rig.CallAsync(LicensePlatesMethods.Settings), LicensePlatesJson.Options).Folder);

        var saved = await rig.CallAsync<SaveSettingsReply>(LicensePlatesMethods.SaveSettings, before with { Folder = safe });

        Assert.Empty(saved.Errors ?? new Dictionary<string, string>());
        Assert.Equal(safe, saved.Settings!.Folder);
        Assert.Equal([loose, safe], folders.Checked);

        // The plugin's own checks come first: a relative path never reaches the host.
        await rig.CallAsync<SaveSettingsReply>(LicensePlatesMethods.SaveSettings, before with { Folder = "relative/log" });
        Assert.Equal([loose, safe], folders.Checked);
    }

    [Fact]
    public async Task Save_never_creates_a_folder_and_writes_nothing_where_the_host_refuses()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        var missing = Path.Combine(rig.Folder, "not-there");
        var system = Path.Combine(rig.Folder, "system");
        Directory.CreateDirectory(system);
        var folders = new FakeServerFolderCheck().Refuse(system, ServerFolderTexts.SystemFolder);
        rig.Ctx.FolderCheck = folders;
        var before = PluginJson.Parse<LicensePlateSettings>(await rig.CallAsync(LicensePlatesMethods.Settings), LicensePlatesJson.Options);

        var refused = await rig.CallAsync<SaveSettingsReply>(LicensePlatesMethods.SaveSettings, before with { Folder = missing });
        Assert.Equal(LicensePlateTexts.FolderMissing, refused.Errors!["folder"]);
        Assert.False(Directory.Exists(missing)); // new folders only with the picker's New folder

        var system1 = await rig.CallAsync<SaveSettingsReply>(LicensePlatesMethods.SaveSettings, before with { Folder = system });
        Assert.Equal(ServerFolderTexts.SystemFolder, system1.Errors!["folder"]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(system));
        Assert.Equal([system], folders.Checked);
    }

    [Fact]
    public async Task The_rights_of_the_methods()
    {
        await using var plugin = new LicensePlatesPlugin();

        Assert.Equal(LicensePlatePermissions.Log, plugin.RequiredPermission(LicensePlatesMethods.Log));
        Assert.Equal(LicensePlatePermissions.Log, plugin.RequiredPermission(LicensePlatesMethods.Stats));
        Assert.Equal(LicensePlatePermissions.Export, plugin.RequiredPermission(LicensePlatesMethods.ExportLog));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(LicensePlatesMethods.Plates));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(LicensePlatesMethods.Cameras));
        Assert.Equal(LicensePlatePermissions.Plates, plugin.RequiredPermission(LicensePlatesMethods.ImportSave));
        Assert.Equal(LicensePlatePermissions.Cameras, plugin.RequiredPermission(LicensePlatesMethods.SaveSetup));
        Assert.Null(plugin.RequiredPermission(LicensePlatesMethods.SaveSettings));
        Assert.Equal(UserRole.Admin, plugin.RequiredRole(LicensePlatesMethods.SaveSettings));
        Assert.True(plugin.RequiresAllDevices(LicensePlatesMethods.SaveSettings));
        Assert.True(plugin.IsAudited(LicensePlatesMethods.SavePlate));
        Assert.True(plugin.IsAudited(LicensePlatesMethods.ExportLog));
        Assert.False(plugin.IsAudited(LicensePlatesMethods.Log));
        Assert.Equal(["show", "log", "export", "plates", "cameras"], plugin.Permissions.Select(p => p.Key));
        Assert.Equal(CorePluginGroup.Integrations, plugin.Group);
        Assert.DoesNotContain("W12345", plugin.AuditDetail(LicensePlatesMethods.SavePlate, """{"plate":{"text":"W12345"}}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_method_reaches_a_device_outside_the_users_group()
    {
        await using var rig = await Rig.StartAsync(cameras: 2);
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0), rig.Id(1)]));
        // Adding starts each camera's check and connection in the background: the canary's snapshots begin only after both
        // finished their first reads (connected), so none of them lands between its snapshots.
        await Eventually.TrueAsync(() => Enumerable.Range(0, 2).All(i => rig.Camera(i).IsConnected
            && rig.Plugin.Runtime!.StateOf(rig.Id(i)) is { Status: CameraStatus.Connected, Checked: not null }), "both cameras checked and connected");
        var inside = rig.Ctx.DeviceList[0];
        var outside = rig.Ctx.DeviceList[1];

        await new PluginCanary(rig.Ctx, rig.Plugin, inside, outside)
            .Method(LicensePlatesMethods.AddCameras, """{"deviceIds":["{inside}","{device}"]}""")
            .Method(LicensePlatesMethods.RemoveCameras, """{"deviceIds":["{device}"]}""")
            .Method(LicensePlatesMethods.SetAllowedTags, """{"deviceIds":["{device}"],"set":[]}""")
            .Method(LicensePlatesMethods.RefreshCameras, """{"deviceIds":["{device}"]}""")
            .Method(LicensePlatesMethods.ReadSetup, """{"deviceId":"{device}"}""")
            .Method(LicensePlatesMethods.SaveSetup, """{"deviceId":"{device}","setup":{}}""")
            .Method(LicensePlatesMethods.CopySettings, """{"sourceId":"{inside}","targetIds":["{device}"]}""")
            .Method(LicensePlatesMethods.UseRecommendedImageSettings, """{"deviceIds":["{device}"]}""")
            .WithoutDevices(LicensePlatesMethods.Cameras, "the cameras of the caller's group")
            .WithoutDevices(LicensePlatesMethods.Log, "the reads of the caller's cameras", "{}")
            .WithoutDevices(LicensePlatesMethods.LogCount, "the reads of the caller's cameras", "{}")
            .WithoutDevices(LicensePlatesMethods.LogStatus, "the reads of the caller's cameras")
            .WithoutDevices(LicensePlatesMethods.Picture, "a read of the caller's cameras", """{"id":1}""")
            .WithoutDevices(LicensePlatesMethods.Stats, "the reads of the caller's cameras", """{"from":"2026-11-27T00:00:00Z","to":"2026-11-27T12:00:00Z"}""")
            .WithoutDevices(LicensePlatesMethods.Plates, "the one plate list of the server", "{}")
            .WithoutDevices(LicensePlatesMethods.Tags, "the one tag set of the server")
            .WithoutDevices(LicensePlatesMethods.SavePlate, "the one plate list of the server", """{"plate":{"text":"AB123"}}""")
            .WithoutDevices(LicensePlatesMethods.DeletePlates, "the one plate list of the server", """{"plates":["AB123"]}""")
            .WithoutDevices(LicensePlatesMethods.ChangePlateTags, "the one plate list of the server", """{"plates":[],"add":[],"remove":[]}""")
            .WithoutDevices(LicensePlatesMethods.SaveTag, "the one tag set of the server", """{"tag":{"name":"Canary"}}""")
            .AllDevicesOnly(LicensePlatesMethods.Settings)
            .AllDevicesOnly(LicensePlatesMethods.SaveSettings, "{}")
            .RunAsync();
    }

    [Fact]
    public async Task A_group_user_sees_only_the_groups_cameras_and_their_reads()
    {
        await using var rig = await Rig.StartAsync(cameras: 2);
        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0), rig.Id(1)]));
        foreach (var i in new[] { 0, 1 })
        {
            await Eventually.TrueAsync(() => rig.Camera(i).IsConnected && rig.Plugin.Runtime!.StateOf(rig.Id(i)).Status == CameraStatus.Connected, "connected");
            await rig.Camera(i).SendAsync(Fixture.Notify("New", "1", "K" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "0000", 0.9));
            await rig.Camera(i).SendAsync(Fixture.Notify("Lost", "1", "", 0.9, state: "lost"));
        }

        await Eventually.TrueAsync(() => rig.Plugin.Log!.Store!.Written == 2, "both reads are stored");
        var group = rig.Ctx.CallAsGroup([rig.Id(1)]);

        var cameras = PluginJson.Parse<CamerasReply>(await rig.Ctx.InvokeAsync(rig.Plugin, group, LicensePlatesMethods.Cameras, null), LicensePlatesJson.Options);
        Assert.Equal([rig.Id(1)], cameras.Cameras.Select(c => c.DeviceId));
        var log = PluginJson.Parse<LogPage>(await rig.Ctx.InvokeAsync(rig.Plugin, group, LicensePlatesMethods.Log, "{}"), LicensePlatesJson.Options);
        Assert.Equal(["K10000"], log.Rows.Select(r => r.Plate));
        Assert.Empty(rig.Ctx.RefusedAccesses);
    }

    [Fact]
    public async Task The_export_needs_the_log_right_and_holds_only_the_callers_reads()
    {
        await using var rig = await Rig.StartAsync();
        var withoutLog = new DeniedCall(rig.Admin, PermissionKeys.Of(LicensePlatesInfo.PluginId, LicensePlatePermissions.Log));

        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.Plugin.OpenDownloadAsync(withoutLog, LicensePlatesMethods.ExportLog, "{}", CancellationToken.None));

        await using var download = await rig.Ctx.OpenDownloadAsync(rig.Plugin, rig.Admin, LicensePlatesMethods.ExportLog, """{"format":"csv"}""");
        Assert.EndsWith(".csv", download.FileName, StringComparison.Ordinal);
        Assert.Equal("Exported plate log (0 reads)", download.AuditDetail);
    }

    /// <summary>A call whose role lacks <paramref name="denied"/>.</summary>
    private sealed class DeniedCall(ICallContext inner, params string[] denied) : ICallContext
    {
        public string UserName => inner.UserName;

        public string SessionId => inner.SessionId;

        public string DeviceScopeKey => inner.DeviceScopeKey;

        public bool HasAllDevices => inner.HasAllDevices;

        public Oadm.Sdk.Devices.IDeviceRepository Devices => inner.Devices;

        public Oadm.Sdk.Vapix.IVapixClientFactory Vapix => inner.Vapix;

        public Oadm.Sdk.Tasks.ITaskRunner Tasks => inner.Tasks;

        public Oadm.Sdk.Devices.IDeviceEventStreams? EventStreams => inner.EventStreams;

        public bool Has(string permission) => !denied.Contains(permission) && inner.Has(permission);

        public bool Contains(Oadm.Sdk.Devices.IDeviceInfo device) => inner.Contains(device);
    }

    [Fact]
    public async Task The_tools_read_plates_cameras_and_the_log_and_save_a_plate_with_a_preview()
    {
        await using var rig = await Rig.StartAsync(cameras: 0);
        await rig.TagAsync("Staff");
        var tools = rig.Plugin.Tools;
        Assert.Contains(tools, t => t.Name == "plates" && t.Paged);
        Assert.Contains(tools, t => t.Name == "plate_save" && t.Changes);
        Assert.Contains(tools, t => t.Name == "log" && t.Paged);

        var preview = JsonNode.Parse((await rig.Plugin.InvokeAsync(rig.Admin, "tool.plate_save.preview", """{"plate":"w 1","tags":["staff"]}""", CancellationToken.None))!)!;
        Assert.Equal("W1", preview["plate"]!["plate"]!.GetValue<string>());
        Assert.Equal(0, (await rig.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest())).Total);

        await rig.Plugin.InvokeAsync(rig.Admin, "tool.plate_save", """{"plate":"w 1","tags":["staff"]}""", CancellationToken.None);
        var list = JsonNode.Parse((await rig.Plugin.InvokeAsync(rig.Admin, "tool.plates", """{"limit":50}""", CancellationToken.None))!)!;
        Assert.Equal(1, list["total"]!.GetValue<int>());
        Assert.Equal("Staff", list["plates"]![0]!["tags"]![0]!.GetValue<string>());

        var errors = JsonNode.Parse((await rig.Plugin.InvokeAsync(rig.Admin, "tool.plate_save.preview", """{"plate":"W1"}""", CancellationToken.None))!)!;
        Assert.Equal("plate", errors["errors"]![0]!["field"]!.GetValue<string>());

        var log = JsonNode.Parse((await rig.Plugin.InvokeAsync(rig.Admin, "tool.log", """{"limit":10}""", CancellationToken.None))!)!;
        Assert.False(log["more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_event_types_follow_the_cameras()
    {
        await using var rig = await Rig.StartAsync(cameras: 1);
        var changed = 0;
        rig.Plugin.EventTypesChanged += (_, _) => changed++;
        Assert.All(rig.Plugin.EventTypes.Where(t => t.Scope == EventScope.Device), t => Assert.Empty(t.DeviceIds!));

        await rig.CallAsync(LicensePlatesMethods.AddCameras, new DevicesRequest([rig.Id(0)]));

        Assert.Equal(1, changed);
        Assert.All(rig.Plugin.EventTypes.Where(t => t.Scope == EventScope.Device), t => Assert.Equal([rig.Id(0)], t.DeviceIds!));
        Assert.Equal(["Plate allowed", "Plate not allowed", "Unknown plate", "Plate read", "Camera problem", "Any camera problem"], rig.Plugin.EventTypes.Select(t => t.DisplayName));
        Assert.Contains(rig.Plugin.EventTypes.Single(t => t.Id == LicensePlatesInfo.PlateReadType).Fields!, f => f.Name == "result" && f.Type == EventFieldType.Choice);
    }
}
