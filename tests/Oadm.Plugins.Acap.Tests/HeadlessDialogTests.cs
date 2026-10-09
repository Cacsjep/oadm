using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Plugins.Acap.Client;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>Minimal host: dark theme variant plus the real OADM theme, like the client app.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.Acap.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the dialog offscreen with the host theme. Set OADM_SCREENSHOT_DIR to write PNGs.</summary>
public sealed class HeadlessDialogTests
{
    [Fact]
    public async Task Dialog_renders_installed_apps_and_package_check()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var eapPath = Path.Combine(Path.GetTempPath(), "oadm-acap-headless-" + Guid.NewGuid().ToString("N") + ".eap");
        // Synchronous on purpose: awaiting before StartNew moves the test to a pool thread and the
        // session's Dispose then deadlocks.
        File.WriteAllBytes(eapPath,EapBuilder.FromManifest(EapBuilder.Manifest(appName: "ax_msf", friendlyName: "Missing Features", vendor: "Commend Österreich GmbH", version: "3.9.0", schema: "1.8.0", osMin: "11.11", osMax: "99")));

        var d1 = new FakeDevice { Model = "P3265-V", Address = "10.0.0.48" };
        var d2 = new FakeDevice { Model = "M3086-V", Address = "10.0.0.52" };
        var ctx = new FakeDialogContext();
        var recorded = ApplicationApiClient.ParseList(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "list-p3265v-12.11.xml")));
        ctx.Devices[d1.Id] = new ListApplicationsResult { Device = new AcapDeviceFacts { Architecture = "aarch64", FirmwareVersion = "12.11.77", AllowUnsigned = true }, Applications = recorded };
        ctx.Devices[d2.Id] = FakeDialogContext.State(arch: "armv7hf", fw: "11.11.124");

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var (rows, compat) = await session.Dispatch(async () =>
        {
            var window = new AcapWindow();
            var vm = new AcapDialogViewModel(ctx, [d1, d2], new FakePicker(eapPath));
            window.Attach(vm);
            window.Show();
            await vm.InitializeAsync();
            vm.SelectedApplication = vm.Applications.First(a => a.PackageName == "ax_msf");
            Pump();
            Capture(window, outDir, "acap-dialog-apps.png");

            await vm.PickPackageCommand.ExecuteAsync(null);
            Pump();
            Capture(window, outDir, "acap-dialog-install.png");

            vm.RemoveCommand.Execute(null);
            Pump();
            Capture(window, outDir, "acap-dialog-remove-confirm.png");
            var result = (vm.Applications.Count, vm.Compatibility.Count);
            window.Close();
            return result;
        }, CancellationToken.None);

        Assert.Equal(8, rows);
        Assert.Equal(2, compat);
        File.Delete(eapPath);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (string.IsNullOrEmpty(outDir))
        {
            frame.Dispose();
            return;
        }

        Directory.CreateDirectory(outDir);
        using (frame)
        {
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
