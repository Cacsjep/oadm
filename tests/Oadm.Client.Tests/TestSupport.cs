using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Discovery;
using Oadm.Client.Infrastructure;
using Oadm.Client.Plugins;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.Tests;

internal static class TestSupport
{
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time");
            }

            await Task.Delay(10);
        }
    }

    public static Device Device(string id, string serial, string address, string model, DeviceStatus status = DeviceStatus.Ok) => new()
    {
        Id = id,
        Serial = serial,
        Address = address,
        Model = model,
        FirmwareVersion = "12.11.77",
        Status = status,
        DhcpEnabled = true,
        HttpsEnabled = true,
        Dot1XEnabled = false,
        ServerName = "acs",
        Scheme = "https",
        UpnpFriendlyName = $"{model} - {serial}",
    };

    public static TaskPluginInfo Plugin(string id, string name, bool toolbar, bool dialog, params string[] runnable)
    {
        var info = new TaskPluginInfo { Id = id, DisplayName = name, ShowInToolbar = toolbar, RequiresDialog = dialog, IconKey = id };
        info.RunnableDeviceIds.AddRange(runnable);
        return info;
    }
}

/// <summary>Builds a DevicesViewModel with its collaborators, all without Avalonia.</summary>
internal sealed class DevicesFixture : IDisposable
{
    public DevicesFixture(IOadmApi? api = null, IClientPluginRegistry? registry = null)
    {
        Api = api ?? Substitute.For<IOadmApi>();
        Ui = new ImmediateUiDispatcher();
        Settings = new InMemoryClientSettingsStore();
        Store = new DeviceStore();
        Tasks = new TaskStore(Store);
        Dialogs = Substitute.For<IDialogService>();
        Launcher = Substitute.For<IUrlLauncher>();
        if (registry is null)
        {
            registry = Substitute.For<IClientPluginRegistry>();
            registry.FindDialog(Arg.Any<string>()).Returns((ITaskPluginDialog?)null);
            registry.FindPage(Arg.Any<string>()).Returns((ICorePluginPage?)null);
        }

        Registry = registry;
        Catalog = new TaskPluginCatalog(Api, Ui, NullLogger<TaskPluginCatalog>.Instance) { DebounceDelay = TimeSpan.FromHours(1) };
        Runner = new TaskPluginRunner(Api, Registry, Dialogs, NullLogger<TaskPluginRunner>.Instance);
        TasksVm = new TasksViewModel(Tasks, Store, Api, Dialogs, Settings, NullLogger<TasksViewModel>.Instance);
        Devices = new DevicesViewModel(Store, Catalog, Runner, Api, Dialogs, Launcher,
            mode => new AddDevicesWizardViewModel(Api, Ui, NullLogger<AddDevicesWizardViewModel>.Instance, mode),
            new ColumnLayoutViewModel(Settings), TasksVm, NullLogger<DevicesViewModel>.Instance);
    }

    public IOadmApi Api { get; }
    public ImmediateUiDispatcher Ui { get; }
    public InMemoryClientSettingsStore Settings { get; }
    public DeviceStore Store { get; }
    public TaskStore Tasks { get; }
    public IDialogService Dialogs { get; }
    public IUrlLauncher Launcher { get; }
    public IClientPluginRegistry Registry { get; }
    public TaskPluginCatalog Catalog { get; }
    public TaskPluginRunner Runner { get; }
    public TasksViewModel TasksVm { get; }
    public DevicesViewModel Devices { get; }

    public void SeedDevices(params Device[] devices) => Store.Reset(devices);

    public async Task SetPluginsAsync(params TaskPluginInfo[] plugins)
    {
        Api.ListTaskPluginsAsync(Arg.Any<CancellationToken>()).Returns(plugins);
        await Catalog.RefreshAsync(CancellationToken.None);
    }

    public void Select(params string[] ids)
    {
        Devices.SelectedDevices.Clear();
        foreach (string id in ids)
        {
            Devices.SelectedDevices.Add(Store.Find(id)!);
        }
    }

    public void Dispose() => (Api as IDisposable)?.Dispose();
}

internal sealed class FakeDialog(string pluginId, string? payload) : ITaskPluginDialog
{
    public string PluginId { get; } = pluginId;
    public int ShowCount { get; private set; }

    public Task<string?> ShowAsync(IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo> devices, Avalonia.Controls.Window owner)
    {
        ShowCount++;
        return Task.FromResult(payload);
    }
}
