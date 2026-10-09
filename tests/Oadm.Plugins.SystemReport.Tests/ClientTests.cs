using System.IO.Compression;
using System.Net;

using Avalonia.Controls;

using Oadm.Client.Api;
using Oadm.Plugins.SystemReport.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.SystemReport.Tests;

/// <summary>The client part: the dialog's view model against the real plugin and the fake server, and the toolbar flow.</summary>
public sealed class ClientTests : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeVapixFactory _vapix = new();
    private readonly FakeRepository _devices = new();
    private readonly SystemReportPlugin _plugin = new();

    public void Dispose()
    {
        _plugin.Dispose();
        _folder.Dispose();
    }

    private async Task<Func<string, string?, CancellationToken, Task<string?>>> StartPluginAsync()
    {
        await _plugin.StartAsync(new PluginContext(_devices, _vapix, _folder.Path), CancellationToken.None);
        return _plugin.InvokeAsync;
    }

    private FakeDevice AddDevice(string address, string serial, DeviceStatus status = DeviceStatus.Ok)
    {
        var device = new FakeDevice { Address = address, Serial = serial, Status = status };
        _devices.Devices.Add(device);
        _vapix.Add(device.Id);
        return device;
    }

    [Fact]
    public async Task The_dialog_downloads_one_zip_and_shows_every_device()
    {
        var ok = AddDevice("10.0.0.48", "B8A44F631339");
        var locked = AddDevice("10.0.0.49", "ACCC8E000001", DeviceStatus.PasswordNotSet);
        var invoke = await StartPluginAsync();
        var vm = new SystemReportViewModel(invoke, [ok, locked, ok], Poll);
        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Equal("Waiting", r.StateText));
        using var output = new MemoryStream();

        await vm.RunAsync(new ReportTarget(new KeepOpen(output), "C:/reports/oadm-system-reports-2026-10-08.zip"));

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(("Done", true), (vm.Rows[0].StateText, vm.Rows[0].IsOk));
        Assert.EndsWith("KB", vm.Rows[0].Detail, StringComparison.Ordinal);
        Assert.Equal(("Failed", true, "Password not set - the device is in factory default"), (vm.Rows[1].StateText, vm.Rows[1].IsError, vm.Rows[1].Detail));
        Assert.Equal("Saved C:/reports/oadm-system-reports-2026-10-08.zip", vm.ProgressText);
        Assert.Equal("1 system report saved, 1 failed. summary.txt in the file lists the failures.", vm.ResultText);
        Assert.True(vm.HasFailures);
        Assert.Equal("2 devices · 1 done · 1 failed", vm.Summary);
        Assert.Equal("Close", vm.CancelText);
        Assert.Equal(100, vm.Progress);

        using var zip = new ZipArchive(new MemoryStream(output.ToArray()));
        Assert.Equal(2, zip.Entries.Count);
        Assert.StartsWith("10.0.0.48_B8A44F631339_P3265-V_", zip.Entries[0].FullName, StringComparison.Ordinal);
        Assert.Equal("summary.txt", zip.Entries[1].FullName);

        // The job is deleted on the server once the file is saved.
        await WaitUntilAsync(() => _plugin.Jobs!.JobCount == 0);
    }

    [Fact]
    public async Task Cancel_stops_the_job_on_the_server()
    {
        var slow = AddDevice("10.0.0.48", "B8A44F631339");
        _vapix.Cameras[slow.Id].Delay = TimeSpan.FromMinutes(5);
        var invoke = await StartPluginAsync();
        var vm = new SystemReportViewModel(invoke, [slow], Poll);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        var run = vm.RunAsync(new ReportTarget(new MemoryStream(), "x.zip"));
        await WaitUntilAsync(() => vm.Rows[0].IsAccent);
        Assert.Equal("Downloading", vm.Rows[0].StateText);
        Assert.Equal("Downloading system reports 1 of 1", vm.ProgressText);
        vm.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(closed);
        Assert.Equal("Cancelled. The file is incomplete; delete it.", vm.ProgressText);
        Assert.Equal("Not downloaded", vm.Rows[0].StateText);
        await WaitUntilAsync(() => _plugin.Jobs!.JobCount == 0);
        vm.CancelCommand.Execute(null);
        Assert.True(closed);
    }

    [Fact]
    public async Task A_server_error_shows_in_the_dialog()
    {
        var vm = new SystemReportViewModel((_, _, _) => throw new InvalidOperationException("Unknown core plugin 'oadm.system-report'."), [new FakeDevice()], Poll);

        await vm.RunAsync(new ReportTarget(new MemoryStream(), "x.zip"));

        Assert.Equal("The system reports could not be downloaded: Unknown core plugin 'oadm.system-report'.", vm.ErrorMessage);
        Assert.True(vm.HasError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Fake_mode_runs_the_same_flow()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { SystemReportDelay = TimeSpan.FromMilliseconds(20) };
        var devices = (await api.ListDevicesAsync(CancellationToken.None)).Select(d => (IDeviceInfo)new FakeDevice
        {
            Id = Guid.Parse(d.Id),
            Address = d.Address,
            Serial = d.Serial,
            Model = d.Model,
        }).ToList();
        var vm = new SystemReportViewModel((m, p, ct) => api.InvokeCorePluginAsync(SystemReportPluginInfo.PluginId, m, p, ct), devices, Poll);
        using var output = new MemoryStream();

        await vm.RunAsync(new ReportTarget(new KeepOpen(output), "fake.zip"));

        Assert.Null(vm.ErrorMessage);
        Assert.Contains(vm.Rows, r => r.IsOk);
        Assert.Contains(vm.Rows, r => r.IsError && r.Detail == "Credentials required - the device rejects the stored credentials");
        using var zip = new ZipArchive(new MemoryStream(output.ToArray()));
        Assert.Equal(vm.Rows.Count(r => r.IsOk) + 1, zip.Entries.Count);
    }

    [Fact]
    [Trait("Category", "Timing")] // time budget
    public void Five_thousand_devices_update_by_id()
    {
        var devices = Enumerable.Range(0, 5000).Select(i => (IDeviceInfo)new FakeDevice { Address = $"10.0.{i / 256}.{i % 256}", Serial = $"ACCC8E{i:X6}" }).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var vm = new SystemReportViewModel((_, _, _) => Task.FromResult<string?>(null), devices, Poll);
        var status = new JobStatus
        {
            Total = 5000,
            Finished = 5000,
            Devices = [.. devices.Select(d => new DeviceReportStatus { DeviceId = d.Id, State = DeviceReportStates.Done, Size = 300_000 })],
        };
        vm.Apply(status);
        watch.Stop();

        Assert.Equal(5000, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.True(r.IsOk));
        Assert.Equal("5,000 devices · 5000 done", vm.Summary);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task The_toolbar_flow_asks_for_the_file_first_and_reports_failures_in_the_message_window()
    {
        var device = AddDevice("10.0.0.48", "B8A44F631339");
        await StartPluginAsync();
        var ctx = new PluginToolbarContext(_plugin);
        var ui = new FakeUi();

        // Nothing selected: nothing happens.
        await SystemReportToolbarPlugin.RunAsync(ctx, ui);
        Assert.Null(ui.Suggested);

        // The save dialog is cancelled: no job.
        ctx.Selected.Add(device);
        ui.Cancel = true;
        await SystemReportToolbarPlugin.RunAsync(ctx, ui);
        Assert.Matches(@"^oadm-system-reports-\d{4}-\d{2}-\d{2}\.zip$", ui.Suggested);
        Assert.Null(ui.Shown);
        Assert.Empty(ctx.Calls);

        ui.Cancel = false;
        await SystemReportToolbarPlugin.RunAsync(ctx, ui);
        Assert.NotNull(ui.Shown);
        Assert.Equal("1 system report saved.", ui.Shown.ResultText);
        Assert.Equal(["start", "status", "read", "delete"], ctx.Calls.Distinct().ToArray());
        Assert.True(ui.Written.Length > 0);

        // A host without InvokePluginAsync: the dialog says so.
        var old = new FakeToolbarContext();
        old.Selected.Add(device);
        var oldUi = new FakeUi();
        await SystemReportToolbarPlugin.RunAsync(old, oldUi);
        Assert.Equal("The system reports could not be downloaded: This host cannot call core plugins from the toolbar.", oldUi.Shown!.ErrorMessage);

        // A failing save dialog: the message window says so.
        await SystemReportToolbarPlugin.RunAsync(old, new FailingUi());
        Assert.Equal("The system reports could not be downloaded: disk full", old.Messages.Single());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10);
        }
    }

    /// <summary>Keeps the memory stream readable after the view model disposed the target.</summary>
    internal sealed class KeepOpen(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    private sealed class FakeUi : ISystemReportUi
    {
        public MemoryStream Written { get; } = new();
        public string? Suggested { get; private set; }
        public bool Cancel { get; set; }
        public SystemReportViewModel? Shown { get; private set; }

        public Task<ReportTarget?> PickTargetAsync(string suggestedFileName)
        {
            Suggested = suggestedFileName;
            return Task.FromResult(Cancel ? null : new ReportTarget(new KeepOpen(Written), suggestedFileName));
        }

        public async Task ShowAsync(SystemReportViewModel dialog, Func<Task> run)
        {
            Shown = dialog;
            await run();
        }
    }

    private sealed class FailingUi : ISystemReportUi
    {
        public Task<ReportTarget?> PickTargetAsync(string suggestedFileName) => throw new IOException("disk full");

        public Task ShowAsync(SystemReportViewModel dialog, Func<Task> run) => run();
    }

    /// <summary>A toolbar host of an older version: no InvokePluginAsync (the SDK default throws).</summary>
    internal class FakeToolbarContext : IToolbarContext
    {
        public List<IDeviceInfo> Selected { get; } = [];
        public List<string> Messages { get; } = [];

        public IReadOnlyList<IDeviceInfo> SelectedDevices => [.. Selected];
        public IReadOnlyList<IDeviceInfo> Devices => [.. Selected];
        public IReadOnlyList<ToolbarTaskPlugin> TaskPlugins => [];
        public Window? Owner => null;

        public event EventHandler? SelectionChanged;
        public event EventHandler? DevicesChanged { add { } remove { } }
        public event EventHandler? TaskPluginsChanged { add { } remove { } }

        public void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

        public bool CanRunTask(string pluginId) => false;
        public Task<IReadOnlyList<string>?> RunTaskAsync(string pluginId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task OpenAsync(string hostPage) => Task.CompletedTask;
        public Task RemoveDevicesAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct) => Task.CompletedTask;

        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);
        public Task<string?> QueryAsync(string pluginId, Guid deviceId, string method, string? payloadJson, CancellationToken ct) => throw new NotSupportedException();
        public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A toolbar host calling the plugin in-process.</summary>
    internal sealed class PluginToolbarContext(SystemReportPlugin plugin) : FakeToolbarContext, IToolbarContext
    {
        public List<string> Calls { get; } = [];

        Task<string?> IToolbarContext.InvokePluginAsync(string pluginId, string method, string? payloadJson, CancellationToken ct)
        {
            Assert.Equal(SystemReportPluginInfo.PluginId, pluginId);
            Calls.Add(method);
            return plugin.InvokeAsync(method, payloadJson, ct);
        }
    }
}

/// <summary>A core plugin context over the fakes, with the data folder.</summary>
internal sealed class PluginContext(IDeviceRepository devices, Oadm.Sdk.Vapix.IVapixClientFactory vapix, string dataDirectory) : ICorePluginContext
{
    public IDeviceRepository Devices => devices;
    public Oadm.Sdk.Vapix.IVapixClientFactory Vapix => vapix;
    public Oadm.Sdk.Tasks.ITaskRunner Tasks => throw new NotSupportedException();
    public IPluginSettings Settings => throw new NotSupportedException();
    public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public string? DataDirectory => dataDirectory;
}
