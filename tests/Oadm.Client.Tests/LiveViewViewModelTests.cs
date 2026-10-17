using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.LiveView;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>The side panel around the shared live video: open, switch and close from the device icon, the title, the remembered channel.</summary>
public sealed class LiveViewViewModelTests : IDisposable
{
    // The panel, its video's posted updates and the checks run on one thread, like on the client's UI thread (see UiThread).
    private readonly UiThread _ui = new();

    private readonly LiveVideoFakes _fakes = new();
    private readonly DeviceRowViewModel _camA = new(TestSupport.Device("a", "B8A44F000001", "10.0.0.48", "P3265-V"));
    private readonly DeviceRowViewModel _camB = new(TestSupport.Device("b", "B8A44F000002", "10.0.0.49", "M3106-L Mk II"));

    private LiveViewViewModel CreateVm() => new(_fakes.Api, _fakes.Decoders, _ui, NullLogger<LiveViewViewModel>.Instance);

    public void Dispose() => _ui.Dispose();

    [Fact]
    public Task The_icon_opens_the_device_and_the_title_names_model_and_channel() => _ui.RunAsync(async () =>
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        Assert.True(vm.IsOpen);
        var stream = await _fakes.StreamAsync(0);
        Assert.Equal("a", stream.Request.DeviceId);
        await TestSupport.WaitUntilAsync(() => vm.Title == "P3265-V  ·  View Area 1");
        stream.Send();
        await TestSupport.WaitUntilAsync(() => vm.Video.State == LiveViewState.Live);

        vm.Video.SelectSourceCommand.Execute(vm.Video.Sources[1]);
        Assert.Equal("P3265-V  ·  View Area 2", vm.Title);
    });

    [Fact]
    public Task One_channel_shows_only_the_model() => _ui.RunAsync(async () =>
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camB);
        await TestSupport.WaitUntilAsync(() => vm.Video.Sources.Count == 1);
        Assert.Equal("M3106-L Mk II", vm.Title);
    });

    [Fact]
    public Task The_same_icon_closes_another_switches() => _ui.RunAsync(async () =>
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        var first = await _fakes.StreamAsync(0);

        vm.ToggleCommand.Execute(_camB);
        Assert.Equal("b", vm.Device!.Id);
        Assert.Equal("b", (await _fakes.StreamAsync(1)).Request.DeviceId);
        await TestSupport.WaitUntilAsync(() => first.Cancelled);

        vm.ToggleCommand.Execute(_camB);
        Assert.False(vm.IsOpen);
        Assert.Null(vm.Device);
        Assert.Equal(LiveViewState.Closed, vm.Video.State);
        await TestSupport.WaitUntilAsync(() => _fakes.Streams[1].Cancelled);
    });

    [Fact]
    public Task The_chosen_channel_is_remembered_per_device_for_the_session() => _ui.RunAsync(async () =>
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        await _fakes.StreamAsync(0); // the channels are applied and the first stream runs
        vm.Video.SelectSourceCommand.Execute(vm.Video.Sources[1]);
        Assert.Equal(2, (await _fakes.StreamAsync(1)).Request.Camera);

        vm.ToggleCommand.Execute(_camB);     // another device: its own first channel
        Assert.Equal(1, (await _fakes.StreamAsync(2)).Request.Camera);

        vm.ToggleCommand.Execute(_camA);     // back: view area 2 again
        Assert.Equal(2, (await _fakes.StreamAsync(3)).Request.Camera);
        Assert.True(vm.Video.Sources[1].IsSelected);
    });

    [Fact]
    public Task Without_the_live_view_permission_the_icon_does_nothing_and_the_panel_closes() => _ui.RunAsync(() =>
    {
        using var vm = CreateVm();
        vm.ToggleCommand.Execute(_camA);
        vm.IsAllowed = false;
        Assert.False(vm.IsOpen);
        Assert.False(vm.ToggleCommand.CanExecute(_camA));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Removing_the_viewed_device_closes_the_panel() => _ui.RunAsync(async () =>
    {
        var api = Substitute.For<IOadmApi>();
        api.WatchLiveViewAsync(Arg.Any<LiveViewRequest>(), Arg.Any<CancellationToken>()).Returns(new LiveVideoFakes.FakeStream(new LiveViewRequest()).ReadAsync(CancellationToken.None));
        api.ListLiveViewSourcesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([LiveVideoFakes.Source(1, "Camera")]);
        using var fixture = new DevicesFixture(api);
        fixture.SeedDevices(TestSupport.Device("a", "B8A44F000001", "10.0.0.48", "P3265-V"));
        DeviceRowViewModel row = fixture.Store.Find("a")!;
        fixture.Devices.LiveView.Open(row);
        Assert.True(fixture.Devices.LiveView.IsOpen);

        fixture.SeedDevices();
        await TestSupport.WaitUntilAsync(() => !fixture.Devices.LiveView.IsOpen);
    });

    [Theory]
    [InlineData(DeviceCategory.Camera, true)]
    [InlineData(DeviceCategory.Encoder, true)]
    [InlineData(DeviceCategory.Intercom, true)]
    [InlineData(DeviceCategory.Unknown, true)]
    [InlineData(DeviceCategory.Speaker, false)]
    [InlineData(DeviceCategory.DoorController, false)]
    [InlineData(DeviceCategory.IoModule, false)]
    [InlineData(DeviceCategory.Radar, false)]
    public void Live_view_support_follows_the_device_category(DeviceCategory category, bool expected)
    {
        var device = TestSupport.Device("x", "B8A44F000009", "10.0.0.9", "any");
        device.Category = category;
        device.HasVideo = category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom;
        Assert.Equal(expected, LiveViewSupport.IsSupported(new DeviceRowViewModel(device)));
    }
}
