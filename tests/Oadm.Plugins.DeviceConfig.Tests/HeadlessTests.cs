using System.IO.Compression;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Plugins.DeviceConfig.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>Minimal host: dark theme variant plus the real OADM theme (styles, colors, icons), like the client app.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.DeviceConfig.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Test classes that start an Avalonia headless session run one after the other (two sessions at once hang).</summary>
[CollectionDefinition(Name)]
public sealed class HeadlessSessions
{
    public const string Name = "Headless";
}

/// <summary>The export and import windows, rendered offscreen. Set OADM_SCREENSHOT_DIR to write PNGs.</summary>
[Collection(HeadlessSessions.Name)]
public sealed class HeadlessTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("oadm-config-ui-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static readonly string[] Models = ["AXIS P3265-V", "AXIS P3265-V", "AXIS Q6135-LE", "AXIS P3265-V", "AXIS P3265-V", "AXIS M3106-L Mk II"];

    private static List<IDeviceInfo> Devices() => [.. Enumerable.Range(0, 6).Select(i => (IDeviceInfo)new FakeDevice
    {
        Address = $"10.0.0.{48 + i}",
        Serial = $"B8A44F6313{39 + i}",
        Model = Models[i],
    })];

    [Fact]
    public async Task The_windows_render_their_states()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var devices = Devices();
        var single = Path.Combine(_folder, "lobby.json");
        await File.WriteAllTextAsync(single, ImportDialogTests.FileFor("P3265-V", recipients: "r1"));
        var zip = Path.Combine(_folder, "oadm-config-2026-10-10.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var device in devices.Take(5).Where(d => d.Address != "10.0.0.51"))
            {
                using var writer = new StreamWriter(archive.CreateEntry(ConfigFileNames.ForDevice(device.Address, device.Serial, device.Model)).Open());
                writer.Write(ImportDialogTests.FileFor(ModelNames.Normalize(device.Model)));
            }
        }

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        // Func<Task<T>>: Dispatch awaits the async body (a plain Func<Task> would only start it).
        var done = await session.Dispatch(async () =>
        {
            // Export in the middle of the run: done, exporting, waiting and failed devices.
            var export = new ExportConfigViewModel((_, _) => Task.FromResult<string?>(null), devices);
            export.Rows[0].Done(58_066);
            export.Rows[1].Done(58_210);
            export.Rows[2].Failed(DeviceConfigIds.FeatureOff);
            export.Rows[3].Exporting();
            var exportWindow = new ExportConfigWindow();
            exportWindow.Attach(export);
            exportWindow.Show();
            Pump();
            Save(exportWindow, outDir, "device-config-export.png");
            exportWindow.Close();

            // Import of one file into six devices of three models.
            var vm = ImportDialogTests.Dialog(devices);
            await vm.LoadAsync(single);
            var window = new ImportConfigWindow();
            window.Attach(vm);
            window.Show();
            Pump();
            Assert.False(vm.IsZip);
            Assert.Contains("2 of 6 devices are other models and are left out.", vm.Notes);
            Save(window, outDir, "device-config-import-other-model.png");

            // The ZIP of an export: each device its own file.
            await vm.LoadAsync(zip);
            Pump();
            Assert.True(vm.IsZip);
            Assert.Equal(6, vm.Rows.Count);
            Save(window, outDir, "device-config-import-zip.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
    }
}
