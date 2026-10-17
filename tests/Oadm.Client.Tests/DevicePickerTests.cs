using System.Diagnostics;

using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

using Xunit.Abstractions;

namespace Oadm.Client.Tests;

/// <summary>The shared device picker (<see cref="DevicePickerViewModel"/>, <see cref="DevicePickerWindow"/>).</summary>
public sealed class DevicePickerTests(ITestOutputHelper output)
{
    [Fact]
    public void It_starts_with_the_chosen_devices_and_choices_can_be_undone()
    {
        var a = new PickDevice("10.0.0.48", "AXIS P3265-V");
        var b = new PickDevice("10.0.0.9", "AXIS M3106-L");
        var old = new PickDevice("10.0.0.20", "AXIS Q6154-E");
        var vm = new DevicePickerViewModel([a, b, old], [a.Id], d => d.Id == old.Id ? "Too old" : null);

        Assert.Equal(["10.0.0.9", "10.0.0.48", "10.0.0.20"], vm.Visible.Select(i => i.Address)); // address order, unavailable last
        Assert.Equal("P3265-V", vm.Visible[1].Model);
        Assert.Equal("1 of 3 devices chosen", vm.Summary);
        Assert.Equal((false, "Too old"), (vm.Visible[2].IsAvailable, vm.Visible[2].UnavailableReason));

        vm.SelectAllCommand.Execute(null);
        Assert.Equal([b.Id, a.Id], vm.SelectedIds); // the unavailable device is not chosen
        vm.Visible[2].IsChecked = true;
        Assert.False(vm.Visible[2].IsChecked);

        vm.Visible[1].IsChecked = false; // unchoose
        Assert.Equal([b.Id], vm.SelectedIds);
        Assert.Equal("1 of 3 devices chosen", vm.Summary);
    }

    [Fact]
    public void In_single_mode_choosing_a_device_unchooses_the_one_before()
    {
        var a = new PickDevice("10.0.0.48", "AXIS P3265-V");
        var b = new PickDevice("10.0.0.9", "AXIS M3106-L");
        var vm = new DevicePickerViewModel([a, b], DeviceChoice.Devices([a.Id]), new DevicePickerOptions { SingleDevice = true });

        vm.Visible.Single(i => i.Id == b.Id).IsChecked = true;
        Assert.Equal([b.Id], vm.SelectedIds);
        Assert.Equal("1 of 2 devices chosen", vm.Summary);
        vm.SelectAllCommand.Execute(null); // not offered in single mode; does nothing
        Assert.Equal([b.Id], vm.SelectedIds);
    }

    [Fact]
    public void Search_limits_select_all_and_none_to_what_it_shows()
    {
        var devices = new[] { new PickDevice("10.0.0.1", "AXIS P3265-V"), new PickDevice("10.0.0.2", "AXIS A9210"), new PickDevice("10.0.0.3", "AXIS P3265-V") };
        var vm = new DevicePickerViewModel(devices);

        vm.SearchText = "p3265";
        Assert.Equal(2, vm.Visible.Count);
        vm.SelectAllCommand.Execute(null);
        vm.SearchText = string.Empty;
        Assert.Equal([devices[0].Id, devices[2].Id], vm.SelectedIds);

        vm.SearchText = "10.0.0.3";
        vm.SelectNoneCommand.Execute(null);
        Assert.Equal([devices[0].Id], vm.SelectedIds);

        vm.SearchText = "nothing";
        Assert.True(vm.IsEmpty);
        Assert.Equal("No device matches the search.", vm.EmptyText);
    }

    [Fact]
    public void Thirty_thousand_devices_are_listed_searched_and_chosen()
    {
        var devices = ThirtyThousand();
        var vm = new DevicePickerViewModel(devices, devices.Take(100).Select(d => d.Id));
        Assert.Equal("100 of 30,000 devices chosen", vm.Summary);
        vm.SearchText = "10.1.7.";
        vm.SelectAllCommand.Execute(null);
        Assert.Equal("350 of 30,000 devices chosen", vm.Summary);
        vm.SearchText = string.Empty;
        vm.SelectAllCommand.Execute(null);
        Assert.Equal("30,000 of 30,000 devices chosen", vm.Summary);
        vm.SelectNoneCommand.Execute(null);
        Assert.Equal("0 of 30,000 devices chosen", vm.Summary);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Thirty_thousand_devices_are_listed_searched_and_chosen_fast()
    {
        var devices = ThirtyThousand();
        var watch = Stopwatch.StartNew();
        var vm = new DevicePickerViewModel(devices, devices.Take(100).Select(d => d.Id));
        var created = watch.ElapsedMilliseconds;
        vm.SearchText = "10.1.7.";
        vm.SelectAllCommand.Execute(null);
        vm.SearchText = string.Empty;
        vm.SelectAllCommand.Execute(null);
        vm.SelectNoneCommand.Execute(null);
        output.WriteLine($"create {created} ms, search and select {watch.ElapsedMilliseconds - created} ms");
        Assert.Equal("0 of 30,000 devices chosen", vm.Summary);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");
    }

    private static List<PickDevice> ThirtyThousand() =>
        Enumerable.Range(0, DeviceLimits.MaxDevices).Select(i => new PickDevice($"10.1.{i / 250}.{i % 250 + 1}", "AXIS P3265-V")).ToList();

    [Fact]
    public void All_devices_the_event_device_and_tags_with_devices_are_one_choice()
    {
        var a = new PickDevice("10.0.0.1", "AXIS P3265-V") { Tags = ["Building A"] };
        var b = new PickDevice("10.0.0.2", "AXIS P3265-V") { Tags = ["building a", "PTZ"] };
        var c = new PickDevice("10.0.0.3", "AXIS A9210");
        var vm = new DevicePickerViewModel([a, b, c], DeviceChoice.EventDevice, DevicePickerOptions.Full("The device of the event"));

        Assert.True(vm.HasChoices);
        Assert.True(vm.IsEventDevice);
        Assert.Equal("The device of the event", vm.Summary);
        Assert.Equal(["Building A", "PTZ"], vm.Tags.Select(t => t.Name)); // case-insensitive, sorted
        Assert.Equal("2", vm.Tags[0].CountText);

        vm.IsAll = true;
        Assert.Equal(DeviceChoice.AllDevices, vm.Choice);

        vm.IsChosen = true;
        vm.Tags[0].IsChecked = true;
        Assert.Equal(("Tag Building A", "Tag Building A", (string?)null), (vm.Visible[0].Note, vm.Visible[1].Note, vm.Visible[2].Note));
        Assert.True(vm.Visible[1].Chosen && !vm.Visible[1].CanToggle);
        vm.Visible[2].IsChecked = true;
        Assert.Equal("1 tag and 1 device: 3 of 3 devices", vm.Summary);
        var choice = vm.Choice;
        Assert.Equal(DeviceChoiceKind.Chosen, choice.Kind);
        Assert.Equal([c.Id], choice.DeviceIds);
        Assert.Equal(["Building A"], choice.Tags);

        var plain = new DevicePickerViewModel([a, b, c], [a.Id]);
        Assert.False(plain.HasChoices); // the plain picker has no left column
        Assert.Equal(DeviceChoice.Devices([a.Id]).DeviceIds, plain.Choice.DeviceIds);
    }

    [Fact]
    public void A_chosen_tag_no_device_has_survives_ok_and_defined_tags_are_offered()
    {
        var a = new PickDevice("10.0.0.1", "AXIS P3265-V") { Tags = ["Building A"] };
        var b = new PickDevice("10.0.0.2", "AXIS A9210");
        var vm = new DevicePickerViewModel([a, b], DeviceChoice.Devices([], ["Outdoor"]), DevicePickerOptions.Full(null, ["Building C", "building a"]));

        Assert.Equal(["Building A", "Building C", "Outdoor"], vm.Tags.Select(t => t.Name)); // one entry per name
        Assert.Equal(["1", "0", "0"], vm.Tags.Select(t => t.CountText));
        Assert.True(vm.Tags.Single(t => t.Name == "Outdoor").IsChecked);
        Assert.Equal(["Outdoor"], vm.Choice.Tags); // OK keeps the tag meant for devices tagged later
        Assert.Equal("1 tag: 0 of 2 devices", vm.Summary); // no "and 0 devices"

        vm.Tags.Single(t => t.Name == "Building A").IsChecked = true;
        Assert.Equal("2 tags: 1 of 2 devices", vm.Summary);
        vm.Visible.Single(i => i.Id == b.Id).IsChecked = true;
        Assert.Equal("2 tags and 1 device: 2 of 2 devices", vm.Summary);
    }

    private sealed record PickDevice(string Address, string? Model) : IDeviceInfo
    {
        public IReadOnlyList<string> Tags { get; init; } = [];

        public Guid Id { get; } = Guid.NewGuid();

        public string Serial => "ACCC8E000001";

        public string? HostName => null;

        public string? FirmwareVersion => "12.11.77";

        public DeviceStatus Status => DeviceStatus.Ok;

        public DeviceCategory Category => DeviceCategory.Camera;

        public bool HasVideo => true;

        public IReadOnlyList<DeviceApi> Apis => [];
    }
}
