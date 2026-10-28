using System.Text.Json;

using Oadm.Plugins.Acap.Client;
using Oadm.Sdk.Plugins;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>Several .eap files of one application, one per architecture: refusals, choice per device, old payloads.</summary>
public sealed class PackagesPerArchitectureTests : IDisposable
{
    private static readonly AcapTaskPlugin Plugin = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(200), TimeProvider.System);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-acap-multi-" + Guid.NewGuid().ToString("N"));

    public PackagesPerArchitectureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static EapManifest Manifest(string arch, string app = "hello", string version = "1.2.0") =>
        EapReader.Parse(EapBuilder.Manifest(appName: app, version: version, architecture: arch), null);

    private string WriteEap(string name, string? arch, string app = "hello", string version = "1.2.0")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, EapBuilder.FromManifest(EapBuilder.Manifest(appName: app, version: version, architecture: arch)));
        return path;
    }

    private static Task Run(RecordingContext ctx, string? payload) =>
        StepRun.RunAsync(ctx.NewRun(), () => Plugin.ExecuteAsync(ctx, new FakeDevice(), payload, CancellationToken.None));

    private static (FakeAcapDevice Device, RecordingContext Ctx, string Payload) TwoPackages(string deviceArch, string second = "armv7hf")
    {
        var device = new FakeAcapDevice { Architecture = deviceArch };
        var files = new InMemoryUploadedFiles();
        var arm64 = files.Add("hello_aarch64.eap", EapBuilder.FromManifest(EapBuilder.Manifest(architecture: "aarch64")));
        var armv7 = files.Add("hello_" + second + ".eap", EapBuilder.FromManifest(EapBuilder.Manifest(architecture: second)));
        var payload = new AcapPayload
        {
            Action = AcapAction.Install,
            Application = "hello",
            FileId = arm64.Id,
            Sha256 = arm64.Sha256,
            Files = [new(arm64.Id, arm64.Sha256, "aarch64"), new(armv7.Id, armv7.Sha256, second)],
        }.ToJson();
        return (device, new RecordingContext(device, files), payload);
    }

    [Theory]
    [InlineData("aarch64", "hello_aarch64.eap")]
    [InlineData("armv7hf", "hello_armv7hf.eap")]
    public async Task Each_device_gets_the_package_of_its_architecture(string arch, string expected)
    {
        var (device, ctx, payload) = TwoPackages(arch);

        await Run(ctx, payload);

        Assert.Equal(["upload"], device.Writes);
        Assert.Equal(expected, device.UploadedFileName);
        Assert.Equal("hello 1.2.0, aarch64 and armv7hf", StepRun.Detail(ctx.Steps, "Read package"));
        Assert.Equal($"AXIS OS 12.11.77, {arch}: {arch} package", StepRun.Detail(ctx.Steps, "Read device info"));
        Assert.Equal("Verify installation: Done", StepRun.Lines(ctx.Steps)[8]);
    }

    [Fact]
    public async Task A_device_without_a_fitting_package_is_skipped_with_a_warning()
    {
        var (device, ctx, payload) = TwoPackages("armv7hf", second: "x86_64");

        await Run(ctx, payload);

        Assert.Empty(device.Writes);
        Assert.Equal(
            ["Check compatibility: Done", "Read package: Done", "Read device info: Warning", "Read SDK version: Skipped", "Read unsigned application setting: Skipped",
             "Read installed applications: Skipped", "Check package: Skipped", "Upload package: Skipped", "Verify installation: Skipped", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("No package for armv7hf. Nothing was changed.", StepRun.Detail(ctx.Steps, "Read device info"));
        Assert.Equal("No package for armv7hf.", StepRun.Detail(ctx.Steps, "Upload package"));
        Assert.Contains("No package for armv7hf. Nothing was changed.", ctx.Warnings);
    }

    [Fact]
    public async Task The_old_single_file_payload_still_installs()
    {
        var device = new FakeAcapDevice();
        var files = new InMemoryUploadedFiles();
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest()));
        var ctx = new RecordingContext(device, files);

        await Run(ctx, $$"""{"action":"install","fileId":"{{file.Id}}","sha256":"{{file.Sha256}}","application":"hello","version":"1.2.0"}""");

        Assert.Equal(["upload"], device.Writes);
        Assert.Equal("AXIS OS 12.11.77, aarch64", StepRun.Detail(ctx.Steps, "Read device info"));
        Assert.Single(AcapPayload.Parse($$"""{"action":"install","fileId":"{{file.Id}}"}""").AllFiles());
    }

    [Fact]
    public async Task A_file_that_is_not_the_checked_architecture_fails_before_any_write()
    {
        var device = new FakeAcapDevice();
        var files = new InMemoryUploadedFiles();
        var a = files.Add("a.eap", EapBuilder.FromManifest(EapBuilder.Manifest(architecture: "aarch64")));
        var b = files.Add("b.eap", EapBuilder.FromManifest(EapBuilder.Manifest(architecture: "aarch64")));
        var ctx = new RecordingContext(device, files);
        var payload = new AcapPayload { Action = AcapAction.Install, FileId = a.Id, Files = [new(a.Id, a.Sha256, "aarch64"), new(b.Id, b.Sha256, "armv7hf")] }.ToJson();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload));

        Assert.Equal("b.eap is not the armv7hf package the dialog checked. Nothing was changed.", ex.Message);
        Assert.Empty(device.Writes);
    }

    [Theory]
    [InlineData("""{"action":"install","files":[]}""")]
    [InlineData("""{"action":"install","files":[{"fileId":""}]}""")]
    [InlineData("""{"action":"install","files":[{"fileId":"a","architecture":"aarch64"},{"fileId":"b","architecture":"arm64"}]}""")]
    [InlineData("""{"action":"install","files":[{"fileId":"a","architecture":"aarch64"},{"fileId":"b"}]}""")]
    [InlineData("""{"action":"install","files":[{"fileId":"a","architecture":"aarch64"},{"fileId":"a","architecture":"armv7hf"}]}""")]
    public void Invalid_package_lists_are_refused(string json) =>
        Assert.Throws<ArgumentException>(() => AcapPayload.Parse(json));

    [Fact]
    public void Files_win_over_the_single_file_id()
    {
        var payload = AcapPayload.Parse("""{"action":"install","fileId":"a","files":[{"fileId":"a","architecture":"aarch64"},{"fileId":"b","architecture":"armv7hf"}]}""");

        Assert.Equal(["a", "b"], payload.AllFiles().Select(f => f.FileId));
        Assert.Equal([AcapPermissions.Install], Plugin.RequiredPermissions(payload.ToJson()));
    }

    [Fact]
    public void Package_sets_are_refused_with_a_reason()
    {
        Assert.Null(AcapCompatibility.CheckPackageSet([("a.eap", Manifest("aarch64")), ("b.eap", Manifest("armv7hf"))]));
        Assert.Equal("Two files are for aarch64. Choose one file per architecture.",
            AcapCompatibility.CheckPackageSet([("a.eap", Manifest("aarch64")), ("b.eap", Manifest("arm64"))]));
        Assert.Equal("b.eap is another application (Hello World). Choose the files of one application.",
            AcapCompatibility.CheckPackageSet([("a.eap", Manifest("aarch64")), ("b.eap", Manifest("armv7hf", app: "other"))]));
        Assert.Equal("b.eap is version 1.3.0, not 1.2.0. Choose the files of one version.",
            AcapCompatibility.CheckPackageSet([("a.eap", Manifest("aarch64")), ("b.eap", Manifest("armv7hf", version: "1.3.0"))]));
        Assert.Equal("b.eap fits every architecture. Choose it on its own.",
            AcapCompatibility.CheckPackageSet([("a.eap", Manifest("aarch64")), ("b.eap", Manifest("all"))]));
    }

    [Fact]
    public void ChoosePackage_uses_the_single_package_or_the_matching_architecture()
    {
        Assert.Equal(0, AcapCompatibility.ChoosePackage(["aarch64"], "armv7hf")); // its own check refuses it
        Assert.Equal(1, AcapCompatibility.ChoosePackage(["aarch64", "armhf"], "armv7hf"));
        Assert.Equal(0, AcapCompatibility.ChoosePackage(["arm64", "armv7hf"], "aarch64"));
        Assert.Equal(-1, AcapCompatibility.ChoosePackage(["aarch64", "armv7hf"], "mips"));
        Assert.Equal(-1, AcapCompatibility.ChoosePackage(["aarch64", "armv7hf"], null));
        Assert.Equal("No package for mips.", AcapCompatibility.NoPackageFor("mips"));
    }

    [Fact]
    public async Task Dialog_shows_per_device_which_build_and_uploads_every_file()
    {
        var arm64 = WriteEap("hello_aarch64.eap", "aarch64");
        var armv7 = WriteEap("hello_armv7hf.eap", "armv7hf");
        var d1 = new FakeDevice();
        var d2 = new FakeDevice();
        var d3 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State(arch: "aarch64");
        ctx.Devices[d2.Id] = FakeDialogContext.State(arch: "armv7hf");
        ctx.Devices[d3.Id] = FakeDialogContext.State(arch: "mips");
        var vm = new AcapDialogViewModel(ctx, [d1, d2, d3], new FakeMultiPicker(arm64, armv7));
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.True(vm.HasSeveralPackages);
        Assert.Equal("2 files", vm.PackageFileName);
        Assert.Contains("Architecture aarch64, armv7hf", vm.PackageDetails, StringComparison.Ordinal);
        Assert.Equal(["aarch64 build", "armv7hf build", "No package for mips."], vm.Compatibility.Select(r => r.Details));
        Assert.Equal("No package", vm.Compatibility[2].Verdict);
        Assert.True(vm.Compatibility[2].IsWarning);
        Assert.Equal(2, vm.CompatibleCount);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal([arm64, armv7], ctx.Uploads);
        var payload = JsonSerializer.Deserialize<AcapPayload>(closed!, AcapPlugin.Json)!;
        Assert.Equal("file-1", payload.FileId);
        Assert.Equal([new AcapPackageFile("file-1", "ABCDEF", "aarch64"), new AcapPackageFile("file-2", "ABCDEF", "armv7hf")], payload.Files);
        Assert.Equal(2, AcapPayload.Parse(closed).AllFiles().Count);
    }

    [Fact]
    public async Task Dialog_refuses_a_set_that_does_not_fit_together_before_any_upload()
    {
        var a = WriteEap("a.eap", "aarch64");
        var b = WriteEap("b.eap", "aarch64", version: "1.3.0");
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State();
        var vm = new AcapDialogViewModel(ctx, [d1], new FakeMultiPicker(a, b));
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Equal("b.eap is version 1.3.0, not 1.2.0. Choose the files of one version.", vm.PackageError);
        Assert.False(vm.HasPackage);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Empty(ctx.Uploads);

        // One fitting file afterwards works as before.
        await vm.LoadPackageAsync(a);
        Assert.Null(vm.PackageError);
        Assert.Equal("a.eap", vm.PackageFileName);
        Assert.False(vm.HasSeveralPackages);
    }

    [Fact]
    public async Task Dialog_names_the_file_that_is_not_a_package()
    {
        var a = WriteEap("a.eap", "aarch64");
        var bad = Path.Combine(_dir, "bad.eap");
        await File.WriteAllTextAsync(bad, "not a package");
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State();
        var vm = new AcapDialogViewModel(ctx, [d1], new FakeMultiPicker(a, bad));
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.StartsWith("bad.eap: ", vm.PackageError, StringComparison.Ordinal);
        Assert.False(vm.HasPackage);
    }
}
