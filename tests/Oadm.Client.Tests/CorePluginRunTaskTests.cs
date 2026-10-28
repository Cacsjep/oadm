using NSubstitute;

using Oadm.Client.Plugins;
using Oadm.Client.Shell;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Tests;

/// <summary><see cref="ICorePluginClientContext.RunTaskAsync"/>: a core plugin page runs a task plugin like the context menu.</summary>
public sealed class CorePluginRunTaskTests
{
    private const string Acap = "oadm.acap";

    private static readonly string A = Guid.NewGuid().ToString(), B = Guid.NewGuid().ToString(), C = Guid.NewGuid().ToString();

    private static async Task<DevicesFixture> CreateAsync(FakeDialog dialog, params string[] runnable)
    {
        IClientPluginRegistry registry = Substitute.For<IClientPluginRegistry>();
        registry.FindDialog(Acap).Returns(dialog);
        registry.ToolbarPlugins.Returns([]);
        var f = new DevicesFixture(registry: registry);
        f.SeedDevices(
            TestSupport.Device(A, "B8A44F631339", "10.0.0.48", "AXIS P3265-V"),
            TestSupport.Device(B, "B8A44F63133A", "10.0.0.49", "AXIS P3265-V"),
            TestSupport.Device(C, "B8A44F63133B", "10.0.0.50", "AXIS P3265-V"));
        await f.SetPluginsAsync(TestSupport.Plugin(Acap, "Applications", toolbar: true, dialog: true, runnable));
        f.Api.RunTaskAsync(default!, default!, default, default!, default).ReturnsForAnyArgs((IReadOnlyList<string>)["t1", "t2"]);
        return f;
    }

    /// <summary>A page's run through its context, like a core plugin page calls it.</summary>
    private static async Task<IReadOnlyList<string>?> RunFromPageAsync(DevicesFixture f, string pluginId, IReadOnlyList<Guid> deviceIds)
    {
        using var context = new CorePluginClientContext(f.Api, "oadm.app-management", f.Devices.ToolbarContext);
        return await context.RunTaskAsync(pluginId, deviceIds, CancellationToken.None);
    }

    [Fact]
    public async Task The_dialog_opens_for_the_known_runnable_devices_and_one_task_per_device_starts()
    {
        var dialog = new FakeDialog(Acap, "{}");
        using DevicesFixture f = await CreateAsync(dialog, A, B);
        IReadOnlyList<IDeviceInfo>? shown = null;
        f.Dialogs.ShowTaskPluginDialogAsync(dialog, Arg.Do<IReadOnlyList<IDeviceInfo>>(d => shown = d))
            .Returns(new TaskDialogResult("{}", [Guid.Parse(A), Guid.Parse(B)]));
        f.TasksVm.IsExpanded = false;

        // C cannot run the plugin, the random id is not in the client's mirror, A is given twice.
        IReadOnlyList<string>? ids = await RunFromPageAsync(f, Acap, [Guid.Parse(A), Guid.NewGuid(), Guid.Parse(C), Guid.Parse(B), Guid.Parse(A)]);

        Assert.Equal(["t1", "t2"], ids);
        Assert.Equal([A, B], shown!.Select(d => d.Id.ToString()).ToArray());
        await f.Api.Received(1).RunTaskAsync(Acap, Arg.Is<IReadOnlyCollection<string>>(x => x.SequenceEqual(new[] { A, B })), "{}",
            TaskPluginRunner.OwnerName, Arg.Any<CancellationToken>());
        Assert.True(f.TasksVm.IsExpanded); // like the toolbar: the tasks pane shows the new tasks
    }

    [Fact]
    public async Task A_start_view_of_the_page_reaches_the_dialog()
    {
        var dialog = new FakeDialog(Acap, "{}");
        using DevicesFixture f = await CreateAsync(dialog, A);
        f.Dialogs.ShowTaskPluginDialogAsync(dialog, Arg.Any<IReadOnlyList<IDeviceInfo>>(), Arg.Any<string?>())
            .Returns(new TaskDialogResult("{}", [Guid.Parse(A)]));
        using var context = new CorePluginClientContext(f.Api, "oadm.app-management", f.Devices.ToolbarContext);

        Assert.NotNull(await context.RunTaskAsync(Acap, [Guid.Parse(A)], "license:ax_msf", CancellationToken.None));
        await f.Dialogs.Received(1).ShowTaskPluginDialogAsync(dialog, Arg.Any<IReadOnlyList<IDeviceInfo>>(), "license:ax_msf");

        // The run without one: the dialog's usual start (the context menu's path).
        await context.RunTaskAsync(Acap, [Guid.Parse(A)], CancellationToken.None);
        await f.Dialogs.Received(1).ShowTaskPluginDialogAsync(dialog, Arg.Any<IReadOnlyList<IDeviceInfo>>(), null);

        // The dialog reads it from its context.
        Assert.Equal("license", new TaskDialogContext(f.Api, Acap, startView: "license").StartView);
        Assert.Null(new TaskDialogContext(f.Api, Acap, startView: " ").StartView);
    }

    [Fact]
    public void A_device_page_opens_only_through_the_host_and_only_on_the_device()
    {
        var opened = new List<(Guid, string)>();
        var api = Substitute.For<Oadm.Client.Api.IOadmApi>();
        var host = Substitute.For<IToolbarContext>();
        using var context = new CorePluginClientContext(api, "oadm.app-management", host, openDevicePage: (id, path) =>
        {
            opened.Add((id, path));
            return true;
        });
        var device = Guid.NewGuid();

        Assert.True(context.OpenDevicePage(device, "/local/ax_msf/redirect.html"));
        Assert.False(context.OpenDevicePage(device, "http://evil.example/")); // the path must stay on the device
        Assert.False(context.OpenDevicePage(device, "/local/../axis-cgi/x"));
        Assert.Equal([(device, "/local/ax_msf/redirect.html")], opened);

        // The Web View page gets the request through its context.
        DevicePageRequest? seen = null;
        context.OpenDevicePageRequested += (_, r) => seen = r;
        context.RequestOpenDevicePage(new DevicePageRequest(device, "/local/x.html"));
        Assert.Equal(new DevicePageRequest(device, "/local/x.html"), seen);

        // A host without Web View (no opener): nothing opens.
        using var bare = new CorePluginClientContext(api, "oadm.app-management", host);
        Assert.False(bare.OpenDevicePage(device, "/local/x.html"));
    }

    [Fact]
    public async Task A_closed_dialog_starts_nothing()
    {
        var dialog = new FakeDialog(Acap, null);
        using DevicesFixture f = await CreateAsync(dialog, A);
        f.Dialogs.ShowTaskPluginDialogAsync(dialog, Arg.Any<IReadOnlyList<IDeviceInfo>>()).Returns((TaskDialogResult?)null);

        Assert.Null(await RunFromPageAsync(f, Acap, [Guid.Parse(A)]));
        await f.Api.DidNotReceiveWithAnyArgs().RunTaskAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task A_task_plugin_the_role_cannot_run_or_that_is_off_starts_nothing()
    {
        var dialog = new FakeDialog(Acap, "{}");
        using DevicesFixture f = await CreateAsync(dialog, A);

        // Not in the catalog: the server leaves out plugins that are off or that the role cannot run.
        Assert.Null(await RunFromPageAsync(f, "oadm.firmware", [Guid.Parse(A)]));

        await f.Dialogs.DidNotReceiveWithAnyArgs().ShowTaskPluginDialogAsync(default!, default!);
        await f.Api.DidNotReceiveWithAnyArgs().RunTaskAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task Without_a_known_runnable_device_nothing_opens()
    {
        var dialog = new FakeDialog(Acap, "{}");
        using DevicesFixture f = await CreateAsync(dialog, A);

        Assert.Null(await RunFromPageAsync(f, Acap, [Guid.NewGuid(), Guid.Parse(C)]));
        Assert.Null(await RunFromPageAsync(f, Acap, []));

        await f.Dialogs.DidNotReceiveWithAnyArgs().ShowTaskPluginDialogAsync(default!, default!);
    }

    [Fact]
    public async Task A_closed_page_starts_nothing()
    {
        var dialog = new FakeDialog(Acap, "{}");
        using DevicesFixture f = await CreateAsync(dialog, A);
        var context = new CorePluginClientContext(f.Api, "oadm.app-management", f.Devices.ToolbarContext);
        context.Dispose();

        Assert.Null(await context.RunTaskAsync(Acap, [Guid.Parse(A)], CancellationToken.None));
        await f.Dialogs.DidNotReceiveWithAnyArgs().ShowTaskPluginDialogAsync(default!, default!);
    }

    [Fact]
    public async Task A_page_uploads_through_the_devices_page_and_a_closed_page_uploads_nothing()
    {
        var host = Substitute.For<IToolbarContext>();
        var file = new Oadm.Sdk.Plugins.UploadedFile("f1", "hello.eap", 10, "AA");
        host.UploadAsync("C:/x/hello.eap", null, Arg.Any<CancellationToken>()).Returns(file);
        var context = new CorePluginClientContext(Substitute.For<Oadm.Client.Api.IOadmApi>(), "oadm.app-management", host);

        Assert.Same(file, await ((ICorePluginClientContext)context).UploadAsync("C:/x/hello.eap", null, CancellationToken.None));

        context.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.UploadAsync("C:/x/hello.eap", null, CancellationToken.None));
        await host.Received(1).UploadAsync(Arg.Any<string>(), Arg.Any<IProgress<double>?>(), Arg.Any<CancellationToken>());
    }
}
