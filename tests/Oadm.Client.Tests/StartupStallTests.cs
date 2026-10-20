using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Client.Tests.Perf;
using Oadm.Contracts.V1;

using Xunit.Abstractions;

namespace Oadm.Client.Tests;

/// <summary>
/// The start of the client like Program runs it (terms accepted, fake server, the real composition root): the splash
/// animates while the client connects and loads the devices, so the UI thread must stay free for its frames.
/// <see cref="UiStallProbe"/> measures the longest time a frame had to wait from the shown splash until it says "Ready";
/// the handover afterwards (the main window is built behind the splash, which shows the complete logo) is reported only.
/// OADM_STARTUP_PLUGINS=&lt;folder&gt; also loads the client plugins of that folder (e.g. artifacts/plugins), so their pages
/// are created during the start too.
/// </summary>
public sealed class StartupStallTests(ITestOutputHelper output)
{
    /// <summary>The sample devices of the fake server.</summary>
    internal const int FakeSampleDevices = 12;

    [Fact]
    public async Task The_start_loads_3000_devices_and_their_tasks_behind_the_splash_and_opens_the_main_window()
    {
        StartMeasure m = await RunStartAsync(output, devices: 3_000, tasks: 1_000);

        Assert.Equal(3_000 + FakeSampleDevices, m.Devices);
        Assert.True(m.Tasks >= 1_000, $"{m.Tasks} tasks");
    }

    internal sealed record StartMeasure(TimeSpan SplashShown, TimeSpan LoadingStall, TimeSpan HandoverStall, TimeSpan Ready, int Devices, int Tasks);

    /// <summary>Starts the client shell against a fake server with <paramref name="devices"/> and <paramref name="tasks"/> more.</summary>
    internal static async Task<StartMeasure> RunStartAsync(ITestOutputHelper output, int devices, int tasks)
    {
        string folder = Path.Combine(Path.GetTempPath(), "oadm-start-" + Guid.NewGuid().ToString("N"));
        string[] pluginDirs = Environment.GetEnvironmentVariable("OADM_STARTUP_PLUGINS") is { Length: > 0 } dir ? [dir] : [];
        return await HeadlessSession.Shared.Dispatch(async () =>
        {
            using ServiceProvider services = ServiceRegistration.Build(new AppOptions { UseFake = true, DataFolder = folder, PluginDirs = pluginDirs });
            var api = (FakeOadmApi)services.GetRequiredService<IOadmApi>();
            List<Device> list = ClientScale.MakeDevices(devices);
            var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            api.AddForTests(list, Enumerable.Range(0, tasks).Select(i => ClientScale.Task(i, list[i % list.Count].Id, TaskState.Done, created.AddSeconds(i))));
            services.GetRequiredService<IClientSettingsStore>().Current.AcceptedTerms = TermsViewModel.CurrentVersion;
            var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            AppShell shell = services.GetRequiredService<AppShell>();
            MainWindowViewModel main = services.GetRequiredService<MainWindowViewModel>();

            using var probe = new UiStallProbe();
            shell.Start(lifetime); // shows the splash and starts the loading behind it
            TimeSpan shown = probe.Now;
            var splash = Assert.IsType<SplashWindow>(lifetime.MainWindow);
            var splashVm = (SplashViewModel)splash.DataContext!;
            TimeSpan ready = TimeSpan.Zero;
            splashVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SplashViewModel.Status) && splashVm.Status == "Ready")
                {
                    ready = probe.Now;
                }
            };

            await TestSupport.WaitUntilAsync(() => lifetime.MainWindow is MainWindow && !splash.IsVisible);
            TimeSpan done = probe.Now;
            Assert.NotEqual(TimeSpan.Zero, ready);
            var m = new StartMeasure(shown, probe.LongestStall(shown, ready), probe.LongestStall(ready, done), ready - shown,
                main.Devices.FilteredDevices.Count, main.Devices.Tasks.Tasks.Count);

            output.WriteLine($"{devices:N0} devices, {tasks:N0} tasks: splash shown after {shown.TotalMilliseconds:F0} ms, "
                + $"Ready after {ready.TotalSeconds:F2} s, main window after {done.TotalSeconds:F2} s");
            output.WriteLine($"Longest UI stall while the splash loads: {m.LoadingStall.TotalMilliseconds:F0} ms; "
                + $"during the handover: {m.HandoverStall.TotalMilliseconds:F0} ms");
            foreach (string stall in probe.Stalls(TimeSpan.Zero, done, TimeSpan.FromMilliseconds(30)))
            {
                output.WriteLine("  stall " + stall);
            }

            main.Stop();
            lifetime.MainWindow?.Close();
            return m;
        }, CancellationToken.None);
    }
}

/// <summary>The start with 30,000 devices and 10,000 tasks: the splash keeps its frames.</summary>
[Trait("Category", "Perf")]
public sealed class StartupStallScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_splash_keeps_animating_while_30000_devices_load()
    {
        StartupStallTests.StartMeasure m = await StartupStallTests.RunStartAsync(output, ClientScale.Devices, 10_000);

        Assert.Equal(ClientScale.Devices + StartupStallTests.FakeSampleDevices, m.Devices);

        // Measured about 50 ms (about 110 ms on a cold start with the plugin pages); before the rows were built off the UI
        // thread 500 to 600 ms.
        TimeSpan budget = TimeSpan.FromMilliseconds(400);
        output.WriteLine($"Budget {budget.TotalMilliseconds:F0} ms");
        Assert.True(m.LoadingStall < budget,
            $"the UI thread stalled {m.LoadingStall.TotalMilliseconds:F0} ms while the splash loaded, budget {budget.TotalMilliseconds:F0} ms");
    }
}
