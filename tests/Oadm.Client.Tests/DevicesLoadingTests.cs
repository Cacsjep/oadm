using Oadm.Client.Tests.Perf;

namespace Oadm.Client.Tests;

/// <summary>Until the first device list arrived (seconds for 30,000 devices) the Devices page says it is loading, not "No devices yet".</summary>
public sealed class DevicesLoadingTests
{
    [Fact]
    public void The_page_loads_until_the_first_list_arrived()
    {
        using var f = new DevicesFixture();
        var raised = new List<string?>();
        f.Devices.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(f.Devices.IsLoading);
        Assert.False(f.Devices.IsEmpty);

        f.Store.Reset([]);
        Assert.False(f.Devices.IsLoading);
        Assert.True(f.Devices.IsEmpty);
        Assert.Contains(nameof(f.Devices.IsLoading), raised);
        Assert.Contains(nameof(f.Devices.IsEmpty), raised);
    }

    [Fact]
    public void A_list_with_devices_is_not_empty()
    {
        using var f = new DevicesFixture();
        f.Store.Reset(ClientScale.MakeDevices(3));
        Assert.False(f.Devices.IsLoading);
        Assert.False(f.Devices.IsEmpty);
    }

    [Fact]
    public async Task The_context_menu_says_the_tasks_are_loading_until_they_arrived()
    {
        using var f = new DevicesFixture();
        f.SeedDevices(TestSupport.Device("d1", "ACCC8E000001", "10.0.0.1", "P3265-V", Oadm.Contracts.V1.DeviceStatus.Ok));
        f.Select("d1");
        f.Devices.RebuildContextMenu();
        var loading = Assert.Single(f.Devices.ContextMenuEntries, e => e.Header == Oadm.Client.Devices.DevicesViewModel.LoadingTasksText);
        Assert.False(loading.IsEnabled);

        await f.SetPluginsAsync(TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false));
        f.Devices.RebuildContextMenu();
        Assert.DoesNotContain(f.Devices.ContextMenuEntries, e => e.Header == Oadm.Client.Devices.DevicesViewModel.LoadingTasksText);
    }
}
