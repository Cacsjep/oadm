extern alias fwclient;

using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using ClientDirection = fwclient::Oadm.Plugins.Firmware.FirmwareDirection;
using ClientMode = fwclient::Oadm.Plugins.Firmware.FactoryDefaultMode;
using ClientPayload = fwclient::Oadm.Plugins.Firmware.FirmwarePayload;
using ClientStatus = fwclient::Oadm.Plugins.Firmware.FirmwareStatusInfo;
using ClientVerdict = fwclient::Oadm.Plugins.Firmware.FirmwareVerdict;
using DialogViewModel = fwclient::Oadm.Plugins.Firmware.Client.FirmwareDialogViewModel;
using DialogWindow = fwclient::Oadm.Plugins.Firmware.Client.FirmwareDialogWindow;
using FileSource = fwclient::Oadm.Plugins.Firmware.Client.IFirmwareFileSource;
using TaskDialog = fwclient::Oadm.Plugins.Firmware.Client.FirmwareTaskDialog;

// Own namespace: inside Oadm.Plugins.Firmware.* the server's copies of the shared types would win.
namespace Oadm.FirmwareDialogTests;

internal sealed class Device(string address, string? model, string? version, DeviceStatus status = DeviceStatus.Ok) : IDeviceInfo
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Serial => "B8A44F63" + Address[^4..].Replace(".", "0", StringComparison.Ordinal);
    public string Address { get; } = address;
    public string? HostName => null;
    public string? Model { get; } = model;
    public string? FirmwareVersion { get; } = version;
    public DeviceStatus Status { get; } = status;
    public DeviceCategory Category => DeviceCategory.Camera;
    public bool HasVideo => true;
    public IReadOnlyList<Oadm.Sdk.Vapix.DeviceApi> Apis { get; } = [new("fwmgr", "1.10")];
}

internal sealed class FakeDialogContext : ITaskDialogContext
{
    public Dictionary<Guid, string?> Status { get; } = [];
    public List<string> Queries { get; } = [];
    public List<string> Uploads { get; } = [];
    public Exception? UploadError { get; set; }
    public TaskCompletionSource? UploadGate { get; set; }

    public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
    {
        Queries.Add(method);
        return Status.TryGetValue(deviceId, out var json) ? Task.FromResult(json) : throw new NotSupportedException("Plugin queries are not available yet.");
    }

    public async Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        Uploads.Add(localPath);
        if (UploadGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        if (UploadError is { } error)
        {
            throw error;
        }

        progress?.Report(0.5);
        progress?.Report(1.0);
        return new UploadedFile("file-1", Path.GetFileName(localPath), 2 * 1024 * 1024, "AB");
    }
}

internal sealed class FakeFileSource(long size = 87 * 1024 * 1024) : FileSource
{
    public string? Picked { get; set; }

    public Task<string?> PickAsync() => Task.FromResult(Picked);

    public Task<long> GetSizeAsync(string path, CancellationToken ct)
    {
        if (path.Contains("missing", StringComparison.Ordinal))
        {
            throw new FileNotFoundException("not found", path);
        }

        return Task.FromResult(size);
    }
}

public sealed class FirmwareDialogViewModelTests
{
    private static readonly Device[] Devices =
    [
        new("10.0.0.48", "P3265-V", "11.11.160"),
        new("10.0.0.49", "P3265-V", "12.11.77"),
        new("10.0.0.50", "Q6135-LE", "11.11.160"),
        new("10.0.0.51", "P3265-V", "12.20.10"),
    ];

    private static DialogViewModel Create(FakeDialogContext? ctx = null, FakeFileSource? files = null) =>
        new(ctx ?? new FakeDialogContext(), Devices, files ?? new FakeFileSource());

    [Fact]
    public void Starts_without_file_and_cannot_start()
    {
        using var vm = Create();

        Assert.Equal(4, vm.Devices.Count);
        Assert.Equal("No file selected", vm.FileName);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.All(vm.Devices, d => Assert.Equal("Choose a file", d.VerdictText));
        Assert.Equal(ClientMode.None, vm.SelectedMode.Mode);
        Assert.False(vm.HasModeWarning);
    }

    [Fact]
    public async Task Selecting_a_file_previews_every_device()
    {
        using var vm = Create();

        await vm.SelectFileAsync("C:\\fw\\P3265-V_12_11_77.bin", CancellationToken.None);

        Assert.Equal("P3265-V_12_11_77.bin", vm.FileName);
        Assert.Contains("87.0 MB", vm.FileDetails, StringComparison.Ordinal);
        Assert.Contains("for P3265-V, AXIS OS 12.11.77", vm.FileDetails, StringComparison.Ordinal);
        Assert.Null(vm.FileError);
        Assert.Equal(ClientVerdict.Upgrade, vm.Devices[0].Check!.Verdict);
        Assert.True(vm.Devices[0].IsOk);
        Assert.Equal(ClientVerdict.AlreadyUpToDate, vm.Devices[1].Check!.Verdict);
        Assert.Equal(ClientVerdict.WrongProduct, vm.Devices[2].Check!.Verdict);
        Assert.True(vm.Devices[2].IsError);
        Assert.Equal(ClientVerdict.DowngradeBlocked, vm.Devices[3].Check!.Verdict);
        Assert.Equal("1 of 4 device(s) will be updated, 1 already up to date, 2 not possible.", vm.Summary);
        Assert.Equal(ClientDirection.Upgrade, vm.Direction());
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Allow_downgrade_and_factory_default_change_the_preview_and_warn()
    {
        using var vm = Create();
        await vm.SelectFileAsync("P3265-V_12_11_77.bin", CancellationToken.None);

        vm.AllowDowngrade = true;
        Assert.Equal(ClientVerdict.DowngradeBlocked, vm.Devices[3].Check!.Verdict);

        vm.SelectedMode = vm.Modes[2];
        Assert.Equal(ClientVerdict.Downgrade, vm.Devices[3].Check!.Verdict);
        Assert.True(vm.Devices[3].IsWarn);
        Assert.True(vm.HasModeWarning);
        Assert.Contains("IP address", vm.ModeWarning, StringComparison.Ordinal);

        vm.SelectedMode = vm.Modes[1];
        Assert.Contains("passwords are reset", vm.ModeWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_or_unreadable_file_blocks_start()
    {
        using var vm = Create(files: new FakeFileSource(size: 1000));
        await vm.SelectFileAsync("P3265-V_12_11_77.bin", CancellationToken.None);
        Assert.Contains("smaller than 1 MB", vm.FileError, StringComparison.Ordinal);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal(vm.FileError, vm.FileRowError); // at the file row
        Assert.Equal(vm.FileError, vm.StartBlockedReason); // and the Upgrade tooltip says why

        await vm.SelectFileAsync("C:\\missing\\a.bin", CancellationToken.None);
        Assert.Contains("could not be read", vm.FileError, StringComparison.Ordinal);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal(vm.FileError, vm.StartBlockedReason);
    }

    [Fact]
    public async Task Browse_uses_the_picker()
    {
        var files = new FakeFileSource { Picked = "P3265-V_12_11_77.bin" };
        using var vm = Create(files: files);

        await vm.BrowseCommand.ExecuteAsync(null);

        Assert.Equal("P3265-V_12_11_77.bin", vm.FileName);
    }

    [Fact]
    public async Task Start_uploads_and_returns_payload_with_file_id_and_options()
    {
        var ctx = new FakeDialogContext();
        using var vm = Create(ctx);
        string? closed = "not closed";
        vm.CloseRequested += (_, payload) => closed = payload;
        await vm.SelectFileAsync("C:\\fw\\P3265-V_12_11_77.bin", CancellationToken.None);
        vm.AllowDowngrade = true;
        vm.SelectedMode = vm.Modes[1];

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(["C:\\fw\\P3265-V_12_11_77.bin"], ctx.Uploads);
        Assert.NotNull(vm.Result);
        Assert.Equal(vm.Result, closed);
        var payload = ClientPayload.Parse(vm.Result);
        Assert.Equal("file-1", payload.FileId);
        Assert.Equal("P3265-V_12_11_77.bin", payload.FileName);
        Assert.Equal(ClientMode.Soft, payload.FactoryDefaultMode);
        Assert.True(payload.AllowDowngrade);
        Assert.Equal(100, vm.UploadProgress);
        Assert.False(vm.IsUploading);

        // The server part parses the same JSON.
        var server = Oadm.Plugins.Firmware.FirmwarePayload.Parse(vm.Result);
        Assert.Equal(Oadm.Plugins.Firmware.FactoryDefaultMode.Soft, server.FactoryDefaultMode);

        // One device upgrades, another downgrades: the task name does not claim either.
        Assert.Equal(ClientDirection.Unknown, payload.Direction);
        Assert.Equal("Install firmware 12.11.77 (factory default)", server.TaskName());
    }

    [Fact]
    public async Task Upload_failure_keeps_the_dialog_open_with_error()
    {
        var ctx = new FakeDialogContext { UploadError = new InvalidOperationException("disk full") };
        using var vm = Create(ctx);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        await vm.SelectFileAsync("P3265-V_12_11_77.bin", CancellationToken.None);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Null(vm.Result);
        Assert.Contains("disk full", vm.Error, StringComparison.Ordinal);
        Assert.Equal(vm.Error, vm.FileRowError); // shown at the file row, not below the options
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Null(vm.StartBlockedReason);
    }

    [Fact]
    public async Task Cancel_during_upload_cancels_the_upload_and_keeps_the_dialog()
    {
        var ctx = new FakeDialogContext { UploadGate = new TaskCompletionSource() };
        using var vm = Create(ctx);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        await vm.SelectFileAsync("P3265-V_12_11_77.bin", CancellationToken.None);

        var start = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsUploading);
        Assert.False(vm.BrowseCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await start;

        Assert.False(closed);
        Assert.Equal("Upload cancelled.", vm.Error);
    }

    [Fact]
    public void Cancel_without_upload_closes_with_null()
    {
        using var vm = Create();
        string? closed = "x";
        vm.CloseRequested += (_, payload) => closed = payload;

        vm.CancelCommand.Execute(null);

        Assert.Null(closed);
    }

    [Fact]
    public async Task Status_query_updates_versions_and_rollback_state_and_tolerates_failures()
    {
        var ctx = new FakeDialogContext();
        ctx.Status[Devices[0].Id] = new ClientStatus { Supported = true, ActiveVersion = "12.11.77", InactiveVersion = "11.11.160", IsCommitted = true }.ToJson();
        ctx.Status[Devices[1].Id] = new ClientStatus { Supported = false, ActiveVersion = "12.11.77" }.ToJson();
        ctx.Status[Devices[2].Id] = new ClientStatus { Supported = true, ActiveVersion = "11.11.160", InactiveVersion = "11.11.100", IsCommitted = false, TimeToRollback = 120 }.ToJson();
        using var vm = Create(ctx);
        await vm.SelectFileAsync("P3265-V_12_11_77.bin", CancellationToken.None);

        await vm.LoadStatusAsync(CancellationToken.None);

        Assert.Equal(4, ctx.Queries.Count);
        Assert.All(ctx.Queries, q => Assert.Equal("status", q));
        Assert.Equal("12.11.77", vm.Devices[0].CurrentVersion);
        Assert.Equal("Rollback to 11.11.160 possible", vm.Devices[0].FirmwareState);
        Assert.Equal(ClientVerdict.AlreadyUpToDate, vm.Devices[0].Check!.Verdict);
        Assert.False(vm.Devices[1].WillInstall);
        Assert.Equal("This device's firmware cannot be updated from OADM.", vm.Devices[1].Message);
        Assert.Equal("Not committed, rolls back to 11.11.100 in 120 s", vm.Devices[2].FirmwareState);
        Assert.Equal("-", vm.Devices[3].FirmwareState);
        Assert.Equal("12.20.10", vm.Devices[3].CurrentVersion);
    }

    [Fact]
    public void Dialog_plugin_id_matches_server_plugin() =>
        Assert.Equal(Oadm.Plugins.Firmware.FirmwareTaskPlugin.PluginId, new TaskDialog().PluginId);
}

/// <summary>Headless Avalonia app with the real host theme (OadmTheme.axaml from Oadm.Client).</summary>
public sealed class ThemeTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Client/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class ThemeTestEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ThemeTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the dialog offscreen with the host theme. Set OADM_SCREENSHOT_DIR to write a PNG.</summary>
public sealed class FirmwareDialogHeadlessTests
{
    [Fact]
    public async Task Dialog_renders_with_host_theme()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestEntry));

        var rows = await session.Dispatch(async () =>
        {
            var ctx = new FakeDialogContext();
            Device[] devices =
            [
                new("10.0.0.48", "P3265-V", "11.11.160"),
                new("10.0.0.49", "P3265-V", "12.11.77"),
                new("10.0.0.50", "Q6135-LE", "11.11.160"),
                new("10.0.0.51", "P3265-V", "12.20.10"),
            ];
            ctx.Status[devices[0].Id] = new ClientStatus { Supported = true, ActiveVersion = "11.11.160", InactiveVersion = "11.11.100", IsCommitted = true }.ToJson();
            using var vm = new DialogViewModel(ctx, devices, new FakeFileSource());
            var window = new DialogWindow { DataContext = vm };
            window.Show();
            await vm.SelectFileAsync("C:\\Downloads\\P3265-V_12_11_77.bin", CancellationToken.None);
            await vm.LoadStatusAsync(CancellationToken.None);
            vm.SelectedMode = vm.Modes[1];
            await PumpAsync();
            Capture(window, outDir, "firmware-dialog.png");

            vm.SelectedMode = vm.Modes[0];
            vm.IsUploading = true;
            vm.UploadProgress = 42;
            vm.UploadText = "Uploading firmware to the server (42 %)";
            await PumpAsync();
            Capture(window, outDir, "firmware-dialog-uploading.png");
            window.Close();
            return vm.Devices.Count;
        }, CancellationToken.None);

        Assert.Equal(4, rows);
    }

    private static async Task PumpAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }

    private static void Capture(Avalonia.Controls.Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
