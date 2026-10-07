using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client;
using Oadm.Client.Infrastructure;
using Oadm.Plugins.Network.Client;
using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Network.Vapix;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Tests;

/// <summary>Headless Avalonia session with the real OADM client App, so the dialog renders with the host theme.</summary>
public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>
/// Renders the dialog offscreen (never on the real desktop). Set OADM_SCREENSHOT_DIR to also write PNGs.
/// </summary>
public sealed class HeadlessDialogTests
{
    [Fact]
    public async Task Dialog_renders_with_host_theme()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "oadm-network-headless-" + Guid.NewGuid().ToString("N"));
        App.Options = new AppOptions { UseFake = true, DataFolder = dataFolder };
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var current = NetworkInfoParser.WithIpv6Parameters(
            NetworkInfoParser.ParseGetNetworkInfo(Fixture.Read(Fixture.GetNetworkInfo), "10.0.0.48"),
            Fixture.Parameters(Fixture.ParamNetwork));
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

        var rows = await session.Dispatch(async () =>
        {
            // Several devices: static IPv4 and IPv6 per device in the table, host name template, DNS.
            var devices = Enumerable.Range(0, 4)
                .Select(i => (IDeviceInfo)new FakeDevice(Guid.NewGuid(), $"10.0.0.{48 + i}", Serial: $"ACCC8E00000{i + 1}"))
                .ToList();
            var vm = new NetworkSettingsViewModel(devices);
            vm.ApplyCurrent(current);
            vm.SelectedIpv4 = vm.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
            vm.SelectedIpv6 = vm.Ipv6Choices.Single(c => c.Value == Ipv6Choice.Static);
            for (var i = 0; i < devices.Count; i++)
            {
                vm.Assignment.Rows[i].NewAddress = $"10.0.0.{100 + i}";
                vm.Assignment.Rows[i].NewIpv6Address = $"2001:db8::{100 + i}";
            }

            vm.Assignment.Merge(new AddressCheckResponse([], [new("2001:db8::103", InUse: true, AnswersPing: true)]));
            vm.SelectedDns = vm.DnsChoices.Single(c => c.Value == SourceChoice.Static);
            vm.DnsSecondary = "10.0.0.2";
            vm.SelectedHostName = vm.HostNameChoices.Single(c => c.Value == SourceChoice.Static);
            vm.HostNameText = "cam-{n}";
            var window = new NetworkSettingsWindow { DataContext = vm, Width = 1100, Height = 1500 };
            window.Show();
            await PumpAsync();
            Capture(window, outDir, "network-settings-dialog-multi.png");
            Assert.True(vm.HasWarning);
            Assert.False(vm.CanApply); // 2001:db8::103 answers ping
            Assert.Null(window.FindControl<CheckBox>("Acknowledge")); // no inline acknowledgement any more

            vm.Assignment.Rows[3].NewIpv6Address = "2001:db8::110";
            await PumpAsync();
            Assert.True(vm.CanApply);

            // Apply on a risky change opens the shared confirmation window.
            MessageWindow? popup = null;
            vm.Confirm = async (title, message, confirm) =>
            {
                popup = new MessageWindow { Heading = title, Message = message, ConfirmText = confirm, CancelText = "Cancel" };
                var shown = popup.ShowDialog<bool>(window);
                await PumpAsync();
                Capture(popup, outDir, "network-settings-confirm.png");
                popup.Close(false);
                return await shown;
            };
            await vm.ApplyCommand.ExecuteAsync(null);
            Assert.NotNull(popup);
            Assert.Equal("Apply", popup!.ConfirmText);
            Assert.Null(vm.ResultJson); // declined
            window.Close();

            // One device: invalid mask shown below its field; IP address field instead of the table.
            var single = new NetworkSettingsViewModel([devices[0]]);
            single.ApplyCurrent(current);
            single.SelectedIpv4 = single.Ipv4Choices.Single(c => c.Value == Ipv4Choice.Static);
            Assert.False(single.HasErrors); // prefilled and untouched: nothing shown yet
            single.Ipv4Mask = "255.0.255.0";
            single.SelectedDns = single.DnsChoices.Single(c => c.Value == SourceChoice.Static);
            single.DnsPrimary = "10.0.0.300";
            var window2 = new NetworkSettingsWindow { DataContext = single, Height = 1100 };
            window2.Show();
            await PumpAsync();
            Capture(window2, outDir, "network-settings-dialog-single.png");
            Assert.True(single.HasErrors);
            Assert.NotNull(single.ErrorOf(nameof(single.Ipv4Mask)));
            Assert.NotNull(single.ErrorOf(nameof(single.DnsPrimary)));
            FormField maskField = window2.GetVisualDescendants().OfType<FormField>().Single(f => f.Label == "Subnet mask");
            Assert.True(maskField.HasError);
            Assert.False(single.CanApply);
            Assert.Equal(single.ErrorOf(nameof(single.Ipv4Mask)), single.ApplyBlockedReason);
            window2.Close();
            return vm.Assignment.Rows.Count;
        }, CancellationToken.None);

        Assert.Equal(4, rows);
        try
        {
            Directory.Delete(dataFolder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // nothing was written
        }
    }

    [Fact]
    public async Task Assign_ip_dialog_renders_both_pages_with_host_theme()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "oadm-network-headless-" + Guid.NewGuid().ToString("N"));
        App.Options = new AppOptions { UseFake = true, DataFolder = dataFolder };
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var current = NetworkInfoParser.WithIpv6Parameters(
            NetworkInfoParser.ParseGetNetworkInfo(Fixture.Read(Fixture.GetNetworkInfo), "10.0.0.48"),
            Fixture.Parameters(Fixture.ParamNetwork));
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));

        var (rows, conflicts) = await session.Dispatch(async () =>
        {
            var devices = Enumerable.Range(0, 5)
                .Select(i => (IDeviceInfo)new FakeDevice(Guid.NewGuid(), $"10.0.0.{48 + i}", Serial: $"ACCC8E00000{i + 1}"))
                .ToList();
            using var vm = new AssignIpViewModel(devices);
            vm.ApplyCurrent(current);
            vm.IpRange = "10.0.0.100-103,10.0.0.120";
            vm.DnsPrimary = "10.0.0.2";
            vm.DefaultRouter = "10.0.0.255";
            var window = new AssignIpWindow { DataContext = vm, Height = 900 };
            window.Show();
            await PumpAsync();
            Capture(window, outDir, "assign-ip-page1-error.png");
            Assert.False(vm.CanContinue); // error below the Default router field
            vm.DefaultRouter = "10.0.0.138";
            await PumpAsync();
            Capture(window, outDir, "assign-ip-page1.png");
            Assert.True(vm.CanContinue);

            // Page 2: what the server check found (another managed device, an address in use), one user edit with a conflict.
            vm.GoToReview();
            vm.Assignment.Merge(new AddressCheckResponse(
                [new("10.0.0.102", Guid.NewGuid(), "M3106-L Mk II ACCC8E0000AA")],
                [new("10.0.0.103", InUse: true, AnswersPing: true)]));
            vm.Assignment.Rows[4].NewAddress = "10.0.0.100";
            await PumpAsync();
            Capture(window, outDir, "assign-ip-page2.png");
            var found = vm.Assignment.Rows.Count(r => r.HasConflict);
            window.Close();

            // DHCP on one device: finishes on page 1, Finish asks in the shared confirmation window.
            using var single = new AssignIpViewModel([devices[0]]) { UseDhcp = true };
            var window2 = new AssignIpWindow { DataContext = single };
            window2.Show();
            await PumpAsync();
            Capture(window2, outDir, "assign-ip-dhcp.png");
            single.Confirm = async (title, message, confirm) =>
            {
                var popup = new MessageWindow { Heading = title, Message = message, ConfirmText = confirm, CancelText = "Cancel" };
                var shown = popup.ShowDialog<bool>(window2);
                await PumpAsync();
                Capture(popup, outDir, "assign-ip-confirm.png");
                popup.Close(true);
                return await shown;
            };
            await single.PrimaryCommand.ExecuteAsync(null);
            Assert.NotNull(single.ResultJson);
            window2.Close();
            return (vm.Assignment.Rows.Count, found);
        }, CancellationToken.None);

        Assert.Equal(5, rows);
        Assert.Equal(4, conflicts); // .102 managed, .103 in use, .100 twice
        try
        {
            Directory.Delete(dataFolder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // nothing was written
        }
    }

    private static async Task PumpAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
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
