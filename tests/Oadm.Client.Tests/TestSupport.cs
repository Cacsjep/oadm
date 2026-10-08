using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Discovery;
using Oadm.Client.Infrastructure;
using Oadm.Client.LiveView;
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

    /// <summary>Adds a reason group that lists its devices (the list form of ListTaskPlugins).</summary>
    public static TaskPluginInfo WithReason(TaskPluginInfo info, string reason, params string[] deviceIds)
    {
        var group = new NotRunnableGroup { Reason = reason, Count = deviceIds.Length };
        group.DeviceIds.AddRange(deviceIds);
        info.NotRunnableGroups.Add(group);
        return info;
    }
}

/// <summary>Builds a DevicesViewModel with its collaborators, all without Avalonia.</summary>
internal sealed class DevicesFixture : IDisposable
{
    public DevicesFixture(IOadmApi? api = null, IClientPluginRegistry? registry = null, Oadm.Client.Shell.UserSession? session = null)
    {
        Api = api ?? Substitute.For<IOadmApi>();
        Ui = new ImmediateUiDispatcher();
        Settings = new InMemoryClientSettingsStore();
        Store = new DeviceStore();
        Tasks = new TaskStore(Store);
        Dialogs = Substitute.For<IDialogService>();
        Launcher = Substitute.For<IUrlLauncher>();
        Clipboard = Substitute.For<IClipboardService>();
        Clipboard.SetTextAsync(Arg.Any<string>()).Returns(true);
        if (registry is null)
        {
            registry = Substitute.For<IClientPluginRegistry>();
            registry.FindDialog(Arg.Any<string>()).Returns((ITaskPluginDialog?)null);
            registry.FindPage(Arg.Any<string>()).Returns((ICorePluginPage?)null);
            registry.ToolbarPlugins.Returns([]);
        }

        Registry = registry;
        Catalog = new TaskPluginCatalog(Api, Ui, NullLogger<TaskPluginCatalog>.Instance) { DebounceDelay = TimeSpan.FromHours(1) };
        Runner = new TaskPluginRunner(Api, Registry, Dialogs, NullLogger<TaskPluginRunner>.Instance);
        TasksVm = new TasksViewModel(Tasks, Store, Api, Dialogs, Settings, NullLogger<TasksViewModel>.Instance);
        LiveView = new LiveViewViewModel(Api, Substitute.For<IVideoDecoderFactory>(), Ui, NullLogger<LiveViewViewModel>.Instance);
        Devices = new DevicesViewModel(Store, Catalog, Runner, Api, Dialogs, Launcher,
            mode => new AddDevicesViewModel(Api, Ui, NullLogger<AddDevicesViewModel>.Instance, mode),
            new Oadm.Client.Devices.Toolbar.DeviceToolbar(Oadm.Client.Devices.Toolbar.BuiltInToolbarPlugins.All, Registry, NullLogger<Oadm.Client.Devices.Toolbar.DeviceToolbar>.Instance),
            new ColumnLayoutViewModel(Settings), TasksVm, LiveView, NullLogger<DevicesViewModel>.Instance, session);
    }

    public IOadmApi Api { get; }
    public ImmediateUiDispatcher Ui { get; }
    public InMemoryClientSettingsStore Settings { get; }
    public DeviceStore Store { get; }
    public TaskStore Tasks { get; }
    public IDialogService Dialogs { get; }
    public IUrlLauncher Launcher { get; }
    public IClipboardService Clipboard { get; }
    public IClientPluginRegistry Registry { get; }
    public TaskPluginCatalog Catalog { get; }
    public TaskPluginRunner Runner { get; }
    public TasksViewModel TasksVm { get; }
    public LiveViewViewModel LiveView { get; }
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

    /// <summary>The main window view model with every navigation page; <paramref name="session"/> decides the role.</summary>
    public Oadm.Client.Shell.MainWindowViewModel CreateShell(Oadm.Client.Shell.ServerConnection connection, Oadm.Client.Shell.UserSession? session = null)
    {
        session ??= new Oadm.Client.Shell.UserSession();
        return new Oadm.Client.Shell.MainWindowViewModel(
            connection,
            Devices,
            new Oadm.Client.Logging.LogsViewModel(new Oadm.Client.Logging.LogStore(Ui)),
            new Oadm.Client.Settings.SettingsViewModel(Api, connection, NullLogger<Oadm.Client.Settings.SettingsViewModel>.Instance, session),
            new Oadm.Client.Settings.UsersViewModel(Api, Dialogs, session, NullLogger<Oadm.Client.Settings.UsersViewModel>.Instance),
            new Oadm.Client.Settings.CredentialsViewModel(Api, connection, Clipboard, session),
            new Oadm.Client.Settings.AboutViewModel(Api, connection),
            Catalog,
            Registry,
            Api,
            Settings,
            session,
            NullLogger<Oadm.Client.Shell.MainWindowViewModel>.Instance);
    }

    public void Dispose() => (Api as IDisposable)?.Dispose();
}

internal sealed class FakeDialog(string pluginId, string? payload) : ITaskPluginDialog
{
    public string PluginId { get; } = pluginId;
    public int ShowCount { get; private set; }

    public Task<string?> ShowAsync(Oadm.Sdk.Client.ITaskDialogContext ctx, IReadOnlyList<Oadm.Sdk.Devices.IDeviceInfo> devices, Avalonia.Controls.Window owner)
    {
        ShowCount++;
        return Task.FromResult(payload);
    }
}
