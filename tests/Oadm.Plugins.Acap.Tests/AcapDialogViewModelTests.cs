using System.Text.Json;

using Oadm.Plugins.Acap.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Acap.Tests;

internal sealed class FakeDialogContext : ITaskDialogContext
{
    public Dictionary<Guid, ListApplicationsResult> Devices { get; } = [];
    public Dictionary<Guid, Exception> Failures { get; } = [];
    public List<(Guid Device, string Method)> Queries { get; } = [];
    public List<string> Uploads { get; } = [];
    public Exception? UploadFailure { get; set; }

    public static ListApplicationsResult State(string arch = "aarch64", string fw = "12.11.77", params InstalledApplication[] apps) =>
        new() { Device = new AcapDeviceFacts { Architecture = arch, FirmwareVersion = fw, AllowUnsigned = true }, Applications = apps };

    public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
    {
        Queries.Add((deviceId, method));
        if (Failures.TryGetValue(deviceId, out var ex))
        {
            return Task.FromException<string?>(ex);
        }

        return Task.FromResult<string?>(Devices[deviceId].ToJson());
    }

    public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        Uploads.Add(localPath);
        if (UploadFailure is not null)
        {
            return Task.FromException<UploadedFile>(UploadFailure);
        }

        progress?.Report(0.5);
        progress?.Report(1.0);
        return Task.FromResult(new UploadedFile("file-1", Path.GetFileName(localPath), new FileInfo(localPath).Length, "ABCDEF"));
    }
}

internal sealed class FakePicker(string? path) : IEapFilePicker
{
    public Task<string?> PickEapAsync() => Task.FromResult(path);
}

public sealed class AcapDialogViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-acap-vm-" + Guid.NewGuid().ToString("N"));

    public AcapDialogViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static InstalledApplication App(string name, string version, string status = "Stopped", bool bundled = false) =>
        new() { Name = name, NiceName = name + " app", Vendor = "Acme", Version = version, Status = status, Bundled = bundled, License = "None" };

    private string WriteEap(string manifest)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".eap");
        File.WriteAllBytes(path, EapBuilder.FromManifest(manifest));
        return path;
    }

    private static AcapPayload Payload(string? json) => JsonSerializer.Deserialize<AcapPayload>(json!, AcapPlugin.Json)!;

    [Fact]
    public async Task Loads_the_applications_of_the_first_device()
    {
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State(apps: [App("hello", "1.0.0", "Running"), App("abc", "2.0")]);
        var vm = new AcapDialogViewModel(ctx, [d1], new FakePicker(null));

        await vm.InitializeAsync();

        Assert.Equal(2, vm.Applications.Count);
        Assert.False(vm.ShowDevicePicker);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsEmpty);
        Assert.Equal(("hello", AcapPlugin.ListApplicationsMethod), (vm.Applications.Single(a => a.IsRunning).PackageName, ctx.Queries[0].Method));
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Device_picker_switches_the_table()
    {
        var d1 = new FakeDevice();
        var d2 = new FakeDevice { Address = "10.0.0.49" };
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State(apps: [App("hello", "1.0.0")]);
        ctx.Devices[d2.Id] = FakeDialogContext.State(apps: []);
        var vm = new AcapDialogViewModel(ctx, [d1, d2], new FakePicker(null));
        await vm.InitializeAsync();

        Assert.True(vm.ShowDevicePicker);
        Assert.Contains("all 2 selected devices", vm.ScopeText, StringComparison.Ordinal);
        vm.SelectedDevice = vm.Devices[1];
        await TestWait.UntilAsync(() => !vm.IsLoading);

        Assert.Empty(vm.Applications);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public async Task Query_failure_is_shown()
    {
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Failures[d1.Id] = new InvalidOperationException("Device unreachable");
        var vm = new AcapDialogViewModel(ctx, [d1], new FakePicker(null));

        await vm.InitializeAsync();

        Assert.True(vm.HasError);
        Assert.Contains("Device unreachable", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task Start_and_stop_complete_with_payload()
    {
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State(apps: [App("hello", "1.0.0")]);
        var vm = new AcapDialogViewModel(ctx, [d1], new FakePicker(null));
        await vm.InitializeAsync();
        string? closed = "unset";
        vm.CloseRequested += (_, p) => closed = p;

        vm.SelectedApplication = vm.Applications[0];
        Assert.True(vm.StartCommand.CanExecute(null));
        vm.StartCommand.Execute(null);

        var payload = Payload(closed);
        Assert.Equal(AcapAction.Start, payload.Action);
        Assert.Equal("hello", payload.Application);
        Assert.Equal(closed, vm.Result);
        Assert.Equal(AcapAction.Start, AcapPayload.Parse(closed).Action);
    }

    [Fact]
    public async Task Remove_asks_for_confirmation_first()
    {
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State(apps: [App("hello", "1.0.0")]);
        var vm = new AcapDialogViewModel(ctx, [d1], new FakePicker(null));
        await vm.InitializeAsync();
        var closes = new List<string?>();
        vm.CloseRequested += (_, p) => closes.Add(p);
        vm.SelectedApplication = vm.Applications[0];

        vm.RemoveCommand.Execute(null);
        Assert.True(vm.ConfirmRemoveVisible);
        Assert.Contains("Remove hello app from", vm.ConfirmRemoveText, StringComparison.Ordinal);
        Assert.Empty(closes);

        vm.CancelRemoveCommand.Execute(null);
        Assert.False(vm.ConfirmRemoveVisible);
        Assert.Empty(closes);

        vm.RemoveCommand.Execute(null);
        vm.ConfirmRemoveCommand.Execute(null);
        var payload = Payload(Assert.Single(closes));
        Assert.Equal(AcapAction.Remove, payload.Action);
        Assert.Equal("hello", payload.Application);
    }

    [Fact]
    public async Task Bundled_applications_cannot_be_removed()
    {
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State(apps: [App("objectanalytics", "1.26.205", bundled: true)]);
        var vm = new AcapDialogViewModel(ctx, [d1], new FakePicker(null));
        await vm.InitializeAsync();
        var closes = new List<string?>();
        vm.CloseRequested += (_, p) => closes.Add(p);
        vm.SelectedApplication = vm.Applications[0];

        vm.RemoveCommand.Execute(null);
        Assert.Contains("cannot be removed", vm.ConfirmRemoveText, StringComparison.Ordinal);
        vm.ConfirmRemoveCommand.Execute(null);

        Assert.Empty(closes);
        Assert.False(vm.ConfirmRemoveVisible);
    }

    [Fact]
    public async Task Picked_package_is_checked_per_device_and_uploaded()
    {
        var ok = new FakeDevice { Address = "10.0.0.48" };
        var wrongArch = new FakeDevice { Address = "10.0.0.60" };
        var older = new FakeDevice { Address = "10.0.0.61" };
        var ctx = new FakeDialogContext();
        ctx.Devices[ok.Id] = FakeDialogContext.State(apps: [App("hello", "1.0.0")]);
        ctx.Devices[wrongArch.Id] = FakeDialogContext.State(arch: "armv7hf");
        ctx.Devices[older.Id] = FakeDialogContext.State(fw: "11.11.124");
        var path = WriteEap(EapBuilder.Manifest(appName: "hello", version: "1.2.0", schema: "1.7.1"));
        var vm = new AcapDialogViewModel(ctx, [ok, wrongArch, older], new FakePicker(path));
        await vm.InitializeAsync();
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.True(vm.HasPackage);
        Assert.Equal("Hello World 1.2.0", vm.PackageTitle);
        Assert.Contains("Architecture aarch64", vm.PackageDetails, StringComparison.Ordinal);
        Assert.Equal(3, vm.Compatibility.Count);
        Assert.True(vm.HasPackageFile);
        Assert.Equal(Path.GetFileName(path), vm.PackageFileName);
        Assert.EndsWith("KB", vm.PackageFileDetails, StringComparison.Ordinal);
        Assert.True(vm.Compatibility[0].IsOk);
        Assert.False(vm.Compatibility[0].IsWarning);
        Assert.Equal("Upgrade from 1.0.0", vm.Compatibility[0].Result);
        Assert.Equal("Upgrade from 1.0.0", vm.Compatibility[0].Verdict);
        Assert.True(vm.Compatibility[1].IsError);
        Assert.Equal("Not compatible", vm.Compatibility[1].Verdict);
        Assert.Contains("armv7hf", vm.Compatibility[1].Result, StringComparison.Ordinal);
        Assert.Contains("armv7hf", vm.Compatibility[1].Details, StringComparison.Ordinal);
        Assert.True(vm.Compatibility[2].IsError);
        Assert.Contains("needs AXIS OS 12.0", vm.Compatibility[2].Result, StringComparison.Ordinal);
        Assert.Equal(1, vm.CompatibleCount);
        Assert.Equal("1 of 3 device(s) can install this package.", vm.CompatibilitySummary);
        Assert.True(vm.InstallCommand.CanExecute(null));

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal([path], ctx.Uploads);
        var payload = Payload(closed);
        Assert.Equal(AcapAction.Install, payload.Action);
        Assert.Equal("file-1", payload.FileId);
        Assert.Equal("ABCDEF", payload.Sha256);
        Assert.Equal("hello", payload.Application);
        Assert.Equal("1.2.0", payload.Version);
        Assert.True(payload.StartAfterInstall);
        Assert.False(payload.AllowDowngrade);
    }

    [Fact]
    public async Task Downgrade_option_rechecks_compatibility()
    {
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State(apps: [App("hello", "2.0.0")]);
        var vm = new AcapDialogViewModel(ctx, [d1], new FakePicker(WriteEap(EapBuilder.Manifest(version: "1.2.0"))));
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Contains("downgrade option", vm.Compatibility[0].Result, StringComparison.Ordinal);

        vm.AllowDowngrade = true;
        await TestWait.UntilAsync(() => vm.CompatibleCount == 1);
        Assert.True(vm.InstallCommand.CanExecute(null));
        Assert.Equal("Downgrade from 2.0.0", vm.Compatibility[0].Result);
        Assert.Equal("Downgrade from 2.0.0", vm.Compatibility[0].Verdict);
        Assert.True(vm.Compatibility[0].IsWarning);
    }

    [Fact]
    public async Task Invalid_package_shows_an_error_and_cannot_install()
    {
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State();
        var path = Path.Combine(_dir, "broken.eap");
        await File.WriteAllTextAsync(path, "not an eap");
        var vm = new AcapDialogViewModel(ctx, [d1], new FakePicker(path));
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.False(vm.HasPackage);
        Assert.True(vm.HasPackageError);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Empty(ctx.Uploads);
    }

    [Fact]
    public async Task Upload_failure_keeps_the_dialog_open()
    {
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext { UploadFailure = new NotSupportedException("Uploads are not available yet.") };
        ctx.Devices[d1.Id] = FakeDialogContext.State();
        var vm = new AcapDialogViewModel(ctx, [d1], new FakePicker(WriteEap(EapBuilder.Manifest())));
        await vm.InitializeAsync();
        var closes = 0;
        vm.CloseRequested += (_, _) => closes++;

        await vm.PickPackageCommand.ExecuteAsync(null);
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(0, closes);
        Assert.Contains("Upload failed: Uploads are not available yet.", vm.UploadStatus, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Cancel_closes_without_payload()
    {
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State();
        var vm = new AcapDialogViewModel(ctx, [d1], new FakePicker(null));
        await vm.InitializeAsync();
        string? closed = "unset";
        vm.CloseRequested += (_, p) => closed = p;

        vm.CancelCommand.Execute(null);

        Assert.Null(closed);
        Assert.Null(vm.Result);
    }
}

internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time");
            }

            await Task.Delay(10);
        }
    }
}
