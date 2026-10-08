using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Discovery;
using Oadm.Client.Infrastructure;
using Oadm.Client.Tags;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>Device tags in the client: store, rows, Tags dialog, group mode, export and import.</summary>
public sealed class DeviceTagsTests
{
    private static Device Tagged(string id, string address, params string[] tags)
    {
        Device device = TestSupport.Device(id, "ACCC8E00000" + id[^1], address, "P3265-V");
        device.Tags.AddRange(tags);
        return device;
    }

    private static DeviceTag Def(string name, TagColor color, int count = 0) =>
        new() { Id = Guid.NewGuid().ToString(), Name = name, Color = color, Defined = true, DeviceCount = count };

    /// <summary>a: Building A + PTZ, b: PTZ, c: no tag; definitions Building A (blue), PTZ (amber), Outdoor (teal).</summary>
    private static DevicesFixture Seeded(Oadm.Client.Shell.UserSession? session = null)
    {
        var fixture = new DevicesFixture(session: session);
        fixture.Store.Tags.Reset([Def("Building A", TagColor.Blue), Def("Outdoor", TagColor.Teal), Def("PTZ", TagColor.Amber)]);
        fixture.SeedDevices(Tagged("a", "10.0.0.1", "Building A", "PTZ"), Tagged("b", "10.0.0.2", "PTZ"), Tagged("c", "10.0.0.3"));
        return fixture;
    }

    private static DeviceTagsViewModel Dialog(DevicesFixture f, bool admin, Func<string, string, string, Task<bool>>? confirm = null, params string[] ids) =>
        new(f.Api, f.Store.Tags, [.. ids.Select(i => f.Store.Find(i)!)], f.Store.Devices, admin, confirm ?? ((_, _, _) => Task.FromResult(true)));

    [Fact]
    public void Rows_resolve_shared_tags_and_a_recolor_needs_no_row_update()
    {
        using DevicesFixture f = Seeded();
        DeviceRowViewModel a = f.Store.Find("a")!;
        DeviceRowViewModel b = f.Store.Find("b")!;

        Assert.Equal(["Building A", "PTZ"], a.Tags);
        Assert.Equal("Building A; PTZ", a.TagsText);
        Assert.Same(a.TagChips[1], b.TagChips[0]);
        Assert.Equal(TagColor.Amber, b.TagChips[0].Color);
        Assert.Equal(["Building A", "Outdoor", "PTZ"], f.Store.Tags.Tags.Select(t => t.Name));

        int rowChanges = 0;
        b.PropertyChanged += (_, _) => rowChanges++;
        f.Store.Tags.Reset([Def("Building A", TagColor.Blue), Def("PTZ", TagColor.Red)]);
        Assert.Equal(TagColor.Red, b.TagChips[0].Color);
        Assert.Equal(0, rowChanges);

        // Outdoor is no longer listed: grey, not defined.
        Assert.False(f.Store.Tags.Resolve("outdoor").IsDefined);
        Assert.Equal(TagColor.Unspecified, f.Store.Tags.Resolve("Outdoor").Color);

        // A tag without definition on a device is grey until the server lists it.
        f.Store.ApplyBatch([new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = Tagged("c", "10.0.0.3", "Legacy") }]);
        Assert.Equal(TagColor.Unspecified, f.Store.Find("c")!.TagChips[0].Color);
    }

    [Fact]
    public void Search_matches_tag_names_and_unchanged_tags_raise_nothing()
    {
        using DevicesFixture f = Seeded();
        f.Devices.SearchText = "ptz";
        Assert.Equal(["a", "b"], f.Devices.FilteredDevices.Select(d => d.Id));
        f.Devices.SearchText = "building";
        Assert.Equal(["a"], f.Devices.FilteredDevices.Select(d => d.Id));

        DeviceRowViewModel a = f.Store.Find("a")!;
        IReadOnlyList<string> before = a.Tags;
        Assert.False(a.Apply(Tagged("a", "10.0.0.1", "Building A", "PTZ")));
        Assert.Same(before, a.Tags);
    }

    [Fact]
    public void Context_menu_has_Tags_and_it_opens_the_dialog()
    {
        using DevicesFixture f = Seeded();
        f.Select("a", "b");

        MenuEntryViewModel tags = f.Devices.ContextMenuEntries.Single(e => e.Header == "Tags");
        Assert.Equal("tag", tags.IconKey);
        tags.Command!.Execute(null);

        f.Dialogs.Received(1).ShowDeviceTagsAsync(Arg.Is<DeviceTagsViewModel>(d => d.Find("PTZ")!.IsChecked == true && d.Find("Building A")!.IsChecked == null));
    }

    [Fact]
    public async Task Dialog_shows_all_some_none_cycles_and_applies_in_one_call()
    {
        using DevicesFixture f = Seeded();
        f.Api.SetDeviceTagsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new SetDeviceTagsReply { DevicesChanged = 2 });
        DeviceTagsViewModel dialog = Dialog(f, admin: false, ids: ["a", "b"]);
        bool? closed = null;
        dialog.CloseRequested += (_, ok) => closed = ok;

        Assert.Equal(["Building A", "Outdoor", "PTZ"], dialog.Choices.Select(c => c.Name));
        Assert.Null(dialog.Find("Building A")!.IsChecked); // some
        Assert.True(dialog.Find("PTZ")!.IsChecked);         // all
        Assert.False(dialog.Find("Outdoor")!.IsChecked);    // none
        Assert.Equal("1 device", dialog.Find("Building A")!.CountText);
        Assert.Equal("Tags of 2 selected devices. Check a tag to add it to all of them, clear it to remove it from all.", dialog.Intro);

        // A click: some -> all, all -> none, none -> all; a second click goes back.
        dialog.Find("Building A")!.ToggleCommand.Execute(null);
        Assert.True(dialog.Find("Building A")!.IsChecked);
        dialog.Find("PTZ")!.ToggleCommand.Execute(null);
        Assert.False(dialog.Find("PTZ")!.IsChecked);
        dialog.Find("Outdoor")!.ToggleCommand.Execute(null);
        dialog.Find("Outdoor")!.ToggleCommand.Execute(null);
        Assert.False(dialog.Find("Outdoor")!.IsChecked);

        await dialog.ApplyCommand.ExecuteAsync(null);

        await f.Api.Received(1).SetDeviceTagsAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "a", "b" })),
            Arg.Is<IReadOnlyCollection<string>>(add => add.SequenceEqual(new[] { "Building A" })),
            Arg.Is<IReadOnlyCollection<string>>(remove => remove.SequenceEqual(new[] { "PTZ" })),
            Arg.Any<CancellationToken>());
        Assert.True(closed);
        Assert.Equal(2, dialog.DevicesChanged);
    }

    [Fact]
    public async Task Dialog_without_changes_closes_without_a_call_and_search_filters()
    {
        using DevicesFixture f = Seeded();
        DeviceTagsViewModel dialog = Dialog(f, admin: false, ids: ["c"]);
        bool? closed = null;
        dialog.CloseRequested += (_, ok) => closed = ok;

        dialog.SearchText = "build";
        Assert.Equal(["Building A"], dialog.Choices.Select(c => c.Name));
        dialog.SearchText = "nothing";
        Assert.True(dialog.IsEmpty);
        Assert.Equal("No tag matches the search.", dialog.EmptyText);

        await dialog.ApplyCommand.ExecuteAsync(null);
        Assert.False(closed);
        await f.Api.DidNotReceiveWithAnyArgs().SetDeviceTagsAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task New_tag_validates_below_the_field_and_is_checked_after_create()
    {
        using DevicesFixture f = Seeded();
        f.Api.CreateTagAsync("Lobby", TagColor.Pink, Arg.Any<CancellationToken>()).Returns(Def("Lobby", TagColor.Pink));
        DeviceTagsViewModel dialog = Dialog(f, admin: false, ids: ["a"]);

        dialog.StartNewTagCommand.Execute(null);
        Assert.True(dialog.IsAddingTag);
        Assert.Null(dialog.ErrorOf(nameof(DeviceTagsViewModel.NewTagName))); // untouched: no error yet
        await dialog.CreateTagCommand.ExecuteAsync(null);
        Assert.Equal("Enter a tag name.", dialog.ErrorOf(nameof(DeviceTagsViewModel.NewTagName)));

        dialog.NewTagName = "building a";
        Assert.Equal("A tag named \"building a\" already exists.", dialog.ErrorOf(nameof(DeviceTagsViewModel.NewTagName)));
        dialog.NewTagName = new string('x', 33);
        Assert.Equal("A tag name has at most 32 characters.", dialog.ErrorOf(nameof(DeviceTagsViewModel.NewTagName)));
        dialog.NewTagName = "a;b";
        Assert.Equal("A tag name cannot contain \";\".", dialog.ErrorOf(nameof(DeviceTagsViewModel.NewTagName)));

        dialog.NewTagName = " Lobby ";
        dialog.NewTagColor = TagColor.Pink;
        await dialog.CreateTagCommand.ExecuteAsync(null);

        Assert.False(dialog.IsAddingTag);
        Assert.True(dialog.Find("Lobby")!.IsChecked);
        Assert.Equal(TagColor.Pink, f.Store.Tags.Resolve("Lobby").Color);
        Assert.Equal(["Lobby"], dialog.Changes().Add);
        Assert.Empty(dialog.Changes().Remove);
    }

    [Fact]
    public async Task A_server_error_of_the_name_shows_below_the_field()
    {
        using DevicesFixture f = Seeded();
        f.Api.CreateTagAsync(Arg.Any<string>(), Arg.Any<TagColor>(), Arg.Any<CancellationToken>())
            .Returns<DeviceTag>(_ => throw new RpcException(new Status(StatusCode.AlreadyExists, "A tag named \"Lobby\" already exists.")));
        DeviceTagsViewModel dialog = Dialog(f, admin: false, ids: ["a"]);
        dialog.StartNewTagCommand.Execute(null);
        dialog.NewTagName = "Lobby";

        await dialog.CreateTagCommand.ExecuteAsync(null);

        Assert.True(dialog.IsAddingTag);
        Assert.Equal("A tag named \"Lobby\" already exists.", dialog.ErrorOf(nameof(DeviceTagsViewModel.NewTagName)));
    }

    [Fact]
    public async Task Rename_recolor_and_delete_are_for_administrators_only()
    {
        using DevicesFixture f = Seeded();
        DeviceTagsViewModel operatorDialog = Dialog(f, admin: false, ids: ["a"]);
        Assert.False(operatorDialog.IsAdmin);
        operatorDialog.StartEditCommand.Execute(operatorDialog.Find("PTZ"));
        Assert.False(operatorDialog.IsEditing);
        await operatorDialog.DeleteCommand.ExecuteAsync(operatorDialog.Find("PTZ"));
        await f.Api.DidNotReceiveWithAnyArgs().DeleteTagAsync(default!, default);

        f.Api.UpdateTagAsync("PTZ", "Dome PTZ", TagColor.Red, Arg.Any<CancellationToken>()).Returns(Def("Dome PTZ", TagColor.Red));
        string? asked = null;
        bool answer = false;
        DeviceTagsViewModel admin = Dialog(f, admin: true, confirm: (_, message, _) =>
        {
            asked = message;
            return Task.FromResult(answer);
        }, ids: ["a"]);

        admin.StartEditCommand.Execute(admin.Find("PTZ"));
        Assert.True(admin.IsEditing);
        admin.EditName = "Building A";
        Assert.Equal("A tag named \"Building A\" already exists.", admin.ErrorOf(nameof(DeviceTagsViewModel.EditName)));
        admin.EditName = "Dome PTZ";
        admin.EditColor = TagColor.Red;
        await admin.SaveEditCommand.ExecuteAsync(null);
        Assert.False(admin.IsEditing);
        Assert.NotNull(admin.Find("Dome PTZ"));
        Assert.Equal(TagColor.Red, admin.Find("Dome PTZ")!.Color);
        Assert.True(admin.Find("Dome PTZ")!.IsChecked); // the assignment stays

        // Delete asks first, naming every device with the tag; No keeps it.
        await admin.DeleteCommand.ExecuteAsync(admin.Find("Building A"));
        Assert.Equal("Remove tag Building A from 1 device?", asked);
        await f.Api.DidNotReceiveWithAnyArgs().DeleteTagAsync(default!, default);
        answer = true;
        await admin.DeleteCommand.ExecuteAsync(admin.Find("Building A"));
        await f.Api.Received(1).DeleteTagAsync("Building A", Arg.Any<CancellationToken>());
        Assert.Null(admin.Find("Building A"));
        await admin.DeleteCommand.ExecuteAsync(admin.Find("Outdoor"));
        Assert.Equal("Delete the tag Outdoor?", asked);
    }

    [Fact]
    public void Group_mode_lists_a_device_under_every_tag_and_selects_it_once()
    {
        using DevicesFixture f = Seeded();
        DevicesViewModel vm = f.Devices;

        vm.ToggleGroupByTagCommand.Execute(null);

        Assert.True(f.Settings.Current.GroupDevicesByTag);
        Assert.Equal(["Building A · 1 device", "PTZ · 2 devices", "No tag · 1 device"], vm.TagGrouping.Groups.Select(g => g.HeaderText));
        Assert.Equal(["a@Building A", "a@PTZ", "b@PTZ", "c@No tag"], vm.TagGrouping.Rows.Select(r => $"{r.Row.Id}@{r.Group.Name}"));

        // Both rows of device a selected: one device.
        vm.SelectedGridItems.ReplaceAll(vm.TagGrouping.Rows.Where(r => r.Row.Id == "a"));
        Assert.Equal(["a"], vm.SelectedDevices.Select(d => d.Id));
        Assert.Equal("3 devices, 1 selected", vm.StatusLine);
        vm.SelectedGridItems.ReplaceAll(vm.TagGrouping.Rows);
        Assert.Equal(["a", "b", "c"], vm.SelectedDevices.Select(d => d.Id));

        // A status update does not rebuild the groups; a tag change does.
        int resets = 0;
        vm.TagGrouping.Rows.CollectionChanged += (_, _) => resets++;
        Device statusOnly = Tagged("b", "10.0.0.2", "PTZ");
        statusOnly.Status = DeviceStatus.Unreachable;
        f.Store.ApplyBatch([new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = statusOnly }]);
        Assert.Equal(0, resets);
        f.Store.ApplyBatch([new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = Tagged("c", "10.0.0.3", "Outdoor") }]);
        Assert.Equal(1, resets);
        Assert.Equal(["Building A", "Outdoor", "PTZ"], vm.TagGrouping.Groups.Select(g => g.Name));

        // Search filters the groups; the mode is remembered by a new layout.
        vm.SearchText = "10.0.0.1";
        Assert.Equal(["a@Building A", "a@PTZ"], vm.TagGrouping.Rows.Select(r => $"{r.Row.Id}@{r.Group.Name}"));
        Assert.True(new ColumnLayoutViewModel(f.Settings).GroupByTag);
    }

    [Fact]
    public void Group_headers_follow_a_rename_and_keep_tag_name_order()
    {
        using DevicesFixture f = Seeded();
        f.Devices.GroupByTag = true;
        int groupChanges = 0;
        f.Devices.TagGrouping.GroupsChanged += (_, _) => groupChanges++;

        f.Store.Tags.Reset([Def("Building A", TagColor.Blue), Def("Alpha", TagColor.Amber)]);
        f.Store.ApplyBatch(
        [
            new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = Tagged("a", "10.0.0.1", "Alpha", "Building A") },
            new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = Tagged("b", "10.0.0.2", "Alpha") },
        ]);

        Assert.Equal(["Alpha · 2 devices", "Building A · 1 device", "No tag · 1 device"], f.Devices.TagGrouping.Groups.Select(g => g.HeaderText));
        Assert.Equal(TagColor.Amber, f.Devices.TagGrouping.Groups[0].Color);
        Assert.True(groupChanges >= 1);
    }

    [Fact]
    public void Export_writes_the_tags_and_import_reads_them_back()
    {
        using DevicesFixture f = Seeded();
        string csv = DeviceListCsv.Write(f.Store.Devices);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("MAC address,Status,Address,Tags,Host name,", lines[0], StringComparison.Ordinal);
        Assert.Contains(",10.0.0.1,Building A; PTZ,", lines[1], StringComparison.Ordinal);

        DeviceImportFile file = DeviceImportFile.Parse("export.csv", csv);
        Assert.Equal(["Building A", "PTZ"], file.Lines[0].Tags);
        Assert.Empty(file.Lines[2].Tags);

        DeviceImportFile own = DeviceImportFile.Parse("own.csv", "Address;Tags\n10.0.0.9;\" Lobby ; lobby;PTZ \"\n10.0.0.10;" + new string('x', 33));
        Assert.Equal(["Lobby", "PTZ"], own.Lines[0].Tags);
        Assert.Equal($"The tag \"{new string('x', 33)}\" is longer than 32 characters.", own.Lines[1].Problem);
    }

    [Fact]
    public async Task Imported_devices_get_the_tags_of_their_line_and_missing_tags_are_created()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        string csv = string.Join("\n",
            "Address,Tags",
            "camera7.example.com:8443,Lobby; Building A",
            "camera8.example.com,Lobby; Building A");
        await using var page = new AddDevicesViewModel(api, new ImmediateUiDispatcher(), NullLogger<AddDevicesViewModel>.Instance, AddDevicesMode.Import);
        page.SetImport(DeviceImportFile.Parse("site.csv", csv));
        await page.OpenAsync();
        await page.ImportCompletion!;
        await TestSupport.WaitUntilAsync(() => !page.IsScanning && page.Rows.All(r => r.IsImportPlaceholder || r.AuthState != AuthState.Pending));

        page.SelectAllAuthenticatedCommand.Execute(null);
        await page.AddCommand.ExecuteAsync(null);

        IReadOnlyList<Device> devices = await api.ListDevicesAsync(CancellationToken.None);
        Assert.All(page.AddedDeviceIds, id => Assert.Equal(["Building A", "Lobby"], devices.Single(d => d.Id == id).Tags));
        Assert.NotEmpty(page.AddedDeviceIds);
        DeviceTag lobby = (await api.ListTagsAsync(CancellationToken.None)).Single(t => t.Name == "Lobby");
        Assert.True(lobby.Defined);
        Assert.NotEqual(TagColor.Unspecified, lobby.Color);
    }

    [Fact]
    public async Task Fake_mode_has_tags_and_every_call_works_in_memory()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        IReadOnlyList<DeviceTag> tags = await api.ListTagsAsync(CancellationToken.None);
        Assert.Equal(["Building A", "Building C", "Outdoor", "PTZ"], tags.Select(t => t.Name));
        Assert.Equal([TagColor.Blue, TagColor.Green, TagColor.Teal, TagColor.Amber], tags.Select(t => t.Color));

        IReadOnlyList<Device> devices = await api.ListDevicesAsync(CancellationToken.None);
        await api.SetDeviceTagsAsync([devices[8].Id], ["Lobby"], [], CancellationToken.None);
        await api.UpdateTagAsync("Lobby", "Entrance", TagColor.Pink, CancellationToken.None);
        Assert.Equal(["Entrance"], (await api.ListDevicesAsync(CancellationToken.None))[8].Tags);
        Assert.Equal(1, await api.DeleteTagAsync("Entrance", CancellationToken.None));
        await Assert.ThrowsAsync<RpcException>(() => api.CreateTagAsync("ptz", TagColor.Red, CancellationToken.None));
    }
}
