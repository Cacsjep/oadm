using System.Diagnostics;
using System.Text;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;

using Xunit.Abstractions;

namespace Oadm.Client.Tests;

/// <summary>
/// The import's waiting list in the client: the Devices page line and the "Waiting devices" dialog against the fake server.
/// Set OADM_SCREENSHOT_DIR for client-waiting-line.png, client-waiting-dialog.png and client-waiting-checking.png.
/// </summary>
public sealed class WaitingDevicesTests(ITestOutputHelper output)
{
    private static async Task KeepAsync(FakeOadmApi api, int count)
    {
        var lines = Enumerable.Range(0, count).Select(i =>
        {
            var line = new WaitingLine { Address = $"10.99.{i / 256}.{i % 256}", UserName = "root", Password = "Secret-" + i };
            line.Tags.Add(i % 2 == 0 ? "Building A" : "PTZ");
            return line;
        }).ToList();
        await api.AddWaitingAsync(lines, "Import from file site.csv", CancellationToken.None);
    }

    [Fact]
    public void The_devices_page_line_says_how_many_wait()
    {
        var state = new WaitingDevicesState();
        Assert.False(state.HasAny);
        Assert.Equal("", state.Text);
        state.Apply(new WaitingSummary { Count = 1 });
        Assert.Equal("1 device waiting to be added", state.Text);
        state.Apply(new WaitingSummary { Count = 2412, Trying = true });
        Assert.Equal("2,412 devices waiting to be added", state.Text.Replace(' ', ','));
        bool ended = false;
        state.RoundEnded += (_, _) => ended = true;
        state.Apply(new WaitingSummary { Count = 2400 });
        Assert.True(ended);
        state.Clear();
        Assert.False(state.HasAny);
    }

    [Fact]
    public async Task The_dialog_lists_searches_tries_removes_and_exports_without_passwords()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { FakeWaitingTryTime = TimeSpan.Zero };
        await KeepAsync(api, 6);
        IDialogService dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var vm = new WaitingDevicesViewModel(api, dialogs, new WaitingDevicesState());

        await vm.LoadAsync();
        Assert.Equal(6, vm.Rows.Count);
        Assert.Equal("6 devices", vm.CountText);
        Assert.Equal("Not tried yet", vm.Rows[0].ResultText);
        Assert.Equal("Not yet", vm.Rows[0].LastTryText);
        Assert.False(vm.RemoveCommand.CanExecute(null)); // Remove needs a selection
        Assert.Equal("Try all 6 shown now", vm.TryNowTip);

        vm.SearchText = "PTZ";
        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal("3 of 6 devices", vm.CountText);
        await vm.TryNowCommand.ExecuteAsync(null);
        WaitingList after = await api.ListWaitingAsync(0, 0, CancellationToken.None);
        Assert.Equal(3, after.Devices.Count(d => d.Tries == 1)); // only the shown ones

        vm.Selected.ReplaceAll([vm.Rows[0]]);
        Assert.Equal("Try 1 selected now", vm.TryNowTip);
        await vm.RemoveCommand.ExecuteAsync(null);
        await dialogs.Received(1).ConfirmAsync("Waiting devices", "Remove 10.99.0.1 from the waiting list? OADM stops trying to add them.", "Remove");
        Assert.Equal(5, (await api.ListWaitingAsync(0, 0, CancellationToken.None)).TotalCount);
        Assert.Equal(2, vm.Rows.Count);

        vm.SearchText = "";
        byte[] csv = WaitingDevicesViewModel.WriteCsv(vm.Rows);
        string text = Encoding.UTF8.GetString(csv);
        Assert.StartsWith("﻿Address,Tags,MAC address,Last try,Result\r\n", text, StringComparison.Ordinal);
        Assert.Contains("10.99.0.0,Building A,,,Not tried yet", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret-", text, StringComparison.Ordinal);
        Discovery.DeviceImportFile reread = Discovery.DeviceImportFile.Parse("waiting.csv", csv, false);
        Assert.Equal(5, reread.Lines.Count); // the export imports again
        Assert.Equal(["Building A"], reread.Lines[0].Tags);
    }

    [Fact]
    public async Task Try_now_shows_the_rows_as_checking_until_the_round_ends()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { FakeWaitingTryTime = TimeSpan.FromMilliseconds(300) };
        await KeepAsync(api, 4);
        var state = new WaitingDevicesState();
        var vm = new WaitingDevicesViewModel(api, Substitute.For<IDialogService>(), state);
        await vm.LoadAsync();
        vm.Selected.ReplaceAll([vm.Rows[1], vm.Rows[2]]);

        await vm.TryNowCommand.ExecuteAsync(null);

        Assert.Equal([false, true, true, false], vm.Rows.Select(r => r.IsChecking));
        Assert.Equal("Checking...", vm.Rows[1].ResultText);
        Assert.Equal("Checking: 2 left", vm.ScheduleText);

        // The round ends: the list is read again with the results.
        await TestSupport.WaitUntilAsync(() => !api.ListWaitingAsync(0, 0, CancellationToken.None).Result.Devices.Any(d => d.Checking));
        state.Apply(new WaitingSummary { Count = 4 });
        vm.OnRoundEnded();
        await TestSupport.WaitUntilAsync(() => vm.Rows.Count == 4 && !vm.Rows.Any(r => r.IsChecking) && vm.Rows[1].Device.Tries == 1);
        Assert.Equal("No Axis device answered at 10.99.0.1.", vm.Rows[1].ResultText);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Thirty_thousand_waiting_devices_load_and_filter_fast()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        await KeepAsync(api, 30_000);
        var vm = new WaitingDevicesViewModel(api, Substitute.For<IDialogService>(), new WaitingDevicesState());

        var watch = Stopwatch.StartNew();
        await vm.LoadAsync();
        long load = watch.ElapsedMilliseconds;
        watch.Restart();
        vm.SearchText = "10.99.12.";
        long search = watch.ElapsedMilliseconds;
        vm.SearchText = "";
        output.WriteLine($"30,000 waiting devices: loaded in {load} ms, searched in {search} ms");
        Assert.Equal(30_000, vm.Rows.Count);
        Assert.True(load < 5_000 && search < 1_000, "too slow");
    }

    [Fact]
    public async Task The_line_and_the_dialog_render_headless()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)); // disposed by the fixture
            using var fixture = new DevicesFixture(api);
            fixture.SeedDevices([.. await api.ListDevicesAsync(CancellationToken.None)]);
            await KeepAsync(api, 120);
            api.FakeWaitingTryTime = TimeSpan.Zero;
            await api.TryWaitingNowAsync([], all: true, CancellationToken.None);
            fixture.Store.Waiting.Apply(new WaitingSummary { Count = 120, NextTry = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(12)) });

            var page = new Window { Width = 1600, Height = 800, Content = new DevicesView { DataContext = fixture.Devices } };
            page.Show();
            Dispatcher.UIThread.RunJobs();
            Button link = page.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "WaitingLink");
            Assert.True(link.IsEffectivelyVisible);
            Assert.Equal("120 devices waiting to be added", link.Content);
            Capture(page, outDir, "client-waiting-line.png");
            page.Close();

            var vm = new WaitingDevicesViewModel(api, fixture.Dialogs, fixture.Store.Waiting);
            var window = new WaitingDevicesWindow();
            window.Attach(vm);
            window.Show();
            await vm.LoadAsync();
            Dispatcher.UIThread.RunJobs();
            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WaitingGrid");
            Assert.NotEmpty(grid.GetVisualDescendants().OfType<DataGridRow>());
            Assert.StartsWith("Next try at ", vm.ScheduleText, StringComparison.Ordinal);
            Capture(window, outDir, "client-waiting-dialog.png");

            // Try now on three rows: they say "Checking..." until the round ends.
            api.FakeWaitingTryTime = TimeSpan.FromSeconds(30);
            vm.Selected.ReplaceAll([vm.Rows[1], vm.Rows[2], vm.Rows[3]]);
            await vm.TryNowCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, vm.Rows.Count(r => r.IsChecking));
            Capture(window, outDir, "client-waiting-checking.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }
}
