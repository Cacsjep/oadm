using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Controls;
using Oadm.Client.Devices;
using Oadm.Client.Plugins;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

using Xunit.Abstractions;

namespace Oadm.Client.Tests;

/// <summary>
/// Task plugins the selection cannot run stay in the context menu, greyed out, with the reason as tooltip
/// (user decision 2026-10-08).
/// </summary>
public sealed class UnsupportedTasksMenuTests(ITestOutputHelper output)
{
    private const string OldFirmware = "Needs AXIS OS 11.11 or later (this device has 11.9.65)";
    private const string OlderFirmware = "Needs AXIS OS 11.11 or later (this device has 9.80.3)";
    private const string NoAnswer = "The device does not answer";

    [Fact]
    public void One_device_shows_its_full_reason_several_the_most_common_reason_without_device_details()
    {
        TaskPluginInfo plugin = TestSupport.Plugin("oadm.pki.https-enable", "HTTPS: Enable/Update", toolbar: false, dialog: false, "ok1", "ok2");
        AddOther(plugin, OldFirmware, 4);
        TestSupport.WithReason(plugin, OlderFirmware, "old9");
        TestSupport.WithReason(plugin, NoAnswer, "down");

        Assert.Null(TaskPluginCatalog.NotRunnableReason(plugin, ["ok1", "ok2"]));
        Assert.Null(TaskPluginCatalog.NotRunnableReason(plugin, []));
        Assert.Equal(OldFirmware, TaskPluginCatalog.NotRunnableReason(plugin, ["old"])); // not listed: the other_devices reason
        Assert.Equal(OlderFirmware, TaskPluginCatalog.NotRunnableReason(plugin, ["old9"]));
        Assert.Equal(NoAnswer, TaskPluginCatalog.NotRunnableReason(plugin, ["down"]));

        // Same general reason with different device details counts as one reason.
        Assert.Equal("Needs AXIS OS 11.11 or later: 3 of 5 selected devices", TaskPluginCatalog.NotRunnableReason(plugin, ["ok1", "ok2", "old", "old2", "old9"]));
        Assert.Equal("Needs AXIS OS 11.11 or later: 2 of 4 selected devices (+1 other reason)", TaskPluginCatalog.NotRunnableReason(plugin, ["ok1", "old", "old9", "down"]));
        Assert.Equal("Needs AXIS OS 11.11 or later: 2 of 2 selected devices", TaskPluginCatalog.NotRunnableReason(plugin, ["old", "old2"]));
    }

    [Fact]
    public void The_compact_form_takes_each_groups_count_of_the_not_runnable_ids_in_order()
    {
        var plugin = new TaskPluginInfo { Id = "oadm.datetime", DisplayName = "Date and time", RunnableOnAllExcept = true };
        plugin.NotRunnableDeviceIds.AddRange(["a", "b", "c", "d"]);
        plugin.NotRunnableGroups.Add(new NotRunnableGroup { Reason = "Needs the Time API", Count = 2 });
        plugin.NotRunnableGroups.Add(new NotRunnableGroup { Reason = NoAnswer, Count = 1 });
        plugin.NotRunnableGroups.Add(new NotRunnableGroup { Reason = "OADM cannot log in to the device: use Log in", Count = 1 });

        Assert.Null(TaskPluginCatalog.NotRunnableReason(plugin, ["x", "y"]));
        Assert.Equal("Needs the Time API", TaskPluginCatalog.NotRunnableReason(plugin, ["b"]));
        Assert.Equal(NoAnswer, TaskPluginCatalog.NotRunnableReason(plugin, ["c"]));
        Assert.Equal("Needs the Time API: 2 of 5 selected devices (+2 other reasons)", TaskPluginCatalog.NotRunnableReason(plugin, ["a", "b", "c", "d", "x"]));
    }

    [Fact]
    public void Without_reasons_from_the_server_the_default_text_is_shown()
    {
        TaskPluginInfo plugin = TestSupport.Plugin("x.old-server", "Old", toolbar: false, dialog: false, "1");

        Assert.Equal("Not supported on this device", TaskPluginCatalog.NotRunnableReason(plugin, ["2"]));
        Assert.Equal("Not supported on this device: 1 of 2 selected devices", TaskPluginCatalog.NotRunnableReason(plugin, ["1", "2"]));
    }

    [Fact]
    public async Task A_device_with_AXIS_OS_11_9_sees_Security_with_every_pki_task_disabled_and_its_reason()
    {
        using DevicesFixture f = CreateWithOldDevice();
        await f.SetPluginsAsync(PkiPlugins());

        f.Select("old");
        MenuEntryViewModel security = f.Devices.ContextMenuEntries.Single(e => e.Header == "Security");
        Assert.Equal(8, security.Items!.Count);
        Assert.All(security.Items!, e =>
        {
            Assert.False(e.IsEnabled);
            Assert.Equal(OldFirmware, e.ToolTip);
        });
        Assert.False(f.Devices.RunPluginCommand.CanExecute(security.Items![0].CommandParameter));
        Assert.Equal(OldFirmware, f.Devices.ToolbarContext.CannotRunTaskReason("oadm.pki.renew"));

        f.Select("new", "old", "down");
        security = f.Devices.ContextMenuEntries.Single(e => e.Header == "Security");
        Assert.All(security.Items!, e => Assert.Equal("Needs AXIS OS 11.11 or later: 1 of 3 selected devices (+1 other reason)", e.ToolTip));

        f.Select("new");
        security = f.Devices.ContextMenuEntries.Single(e => e.Header == "Security");
        Assert.All(security.Items!, e =>
        {
            Assert.True(e.IsEnabled);
            Assert.Null(e.ToolTip);
        });
        Assert.Null(f.Devices.ToolbarContext.CannotRunTaskReason("oadm.pki.renew"));
    }

    [Fact]
    public async Task Security_submenu_with_an_old_firmware_device_renders_greyed_entries_with_tooltips()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(async () =>
        {
            using DevicesFixture f = CreateWithOldDevice();
            TaskPluginInfo restart = TestSupport.Plugin("oadm.restart", "Restart", toolbar: true, dialog: false, "new", "old");
            restart.Group = "Maintenance";
            restart.IconKey = "restart";
            await f.SetPluginsAsync([restart, .. PkiPlugins()]);
            f.Select("old");

            List<MenuEntryViewModel> entries = [.. f.Devices.ContextMenuEntries];
            MenuEntryViewModel security = entries.Single(e => e.Header == "Security");
            var top = Presenter(entries);
            var sub = Presenter(security.Items!);
            // The tooltip of a greyed entry, drawn in place (a real tooltip is a popup outside the captured window).
            var tip = new ToolTip { Content = OldFirmware, Opacity = 1, IsVisible = true };
            var overlay = new Canvas { Children = { tip } };
            var window = new Window
            {
                Width = 760,
                Height = 420,
                Content = new Panel
                {
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 4,
                            Margin = new Thickness(16),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                            Children = { top, sub },
                        },
                        overlay,
                    },
                },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            MenuItem renew = sub.GetVisualDescendants().OfType<MenuItem>().Single(i => (i.Header as string) == "Renew certificates now");
            Point anchor = renew.TranslatePoint(new Point(renew.Bounds.Width / 3, renew.Bounds.Height + 2), overlay) ?? default;
            Canvas.SetLeft(tip, anchor.X);
            Canvas.SetTop(tip, anchor.Y);
            Dispatcher.UIThread.RunJobs();

            MenuItem[] items = [.. sub.GetVisualDescendants().OfType<MenuItem>()];
            Assert.Equal(8, items.Length);
            Assert.All(items, i =>
            {
                Assert.False(i.IsEffectivelyEnabled);
                Assert.Equal(OldFirmware, ToolTip.GetTip(i));
                Assert.True(ToolTip.GetShowOnDisabled(i));
            });
            Assert.True(top.GetVisualDescendants().OfType<MenuItem>().Single(i => (i.Header as string) == "Security").IsEffectivelyEnabled);

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-context-menu-unsupported.png"));
            }

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Reasons_for_5000_selected_devices_and_15_plugins_stay_fast()
    {
        const int devices = 5000;
        List<string> ids = [.. Enumerable.Range(0, devices).Select(i => $"device-{i:D5}")];
        List<TaskPluginInfo> plugins = [];
        for (int p = 0; p < 15; p++)
        {
            var plugin = new TaskPluginInfo { Id = "perf." + p, DisplayName = "Plugin " + p, RunnableOnAllExcept = true };

            // 400 old devices (one reason, details per firmware) and 100 unreachable ones per plugin.
            plugin.NotRunnableDeviceIds.AddRange(ids.Take(500));
            for (int fw = 0; fw < 4; fw++)
            {
                plugin.NotRunnableGroups.Add(new NotRunnableGroup { Reason = $"Needs AXIS OS 11.11 or later (this device has 10.{fw}.1)", Count = 100 });
            }

            plugin.NotRunnableGroups.Add(new NotRunnableGroup { Reason = NoAnswer, Count = 100 });
            plugins.Add(plugin);
        }

        var watch = Stopwatch.StartNew();
        string?[] reasons = [.. plugins.Select(p => TaskPluginCatalog.NotRunnableReason(p, ids))];
        watch.Stop();
        output.WriteLine($"Reasons of 15 plugins for 5,000 selected devices (incl. building the sets): {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.All(reasons, r => Assert.Equal("Needs AXIS OS 11.11 or later: 400 of 5000 selected devices (+1 other reason)", r));

        watch.Restart();
        reasons = [.. plugins.Select(p => TaskPluginCatalog.NotRunnableReason(p, ids))];
        watch.Stop();
        output.WriteLine($"Again with the cached sets: {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static void AddOther(TaskPluginInfo plugin, string reason, int count) =>
        plugin.NotRunnableGroups.Add(new NotRunnableGroup { Reason = reason, Count = count, OtherDevices = true });

    /// <summary>The eight PKI tasks as the server lists them: runnable on "new", "old" needs newer firmware, "down" does not answer.</summary>
    private static TaskPluginInfo[] PkiPlugins()
    {
        TaskPluginInfo Pki(string id, string name, string icon)
        {
            TaskPluginInfo info = TestSupport.Plugin(id, name, toolbar: false, dialog: false, "new");
            info.Group = "Security";
            info.IconKey = icon;
            AddOther(info, OldFirmware, 1);
            TestSupport.WithReason(info, NoAnswer, "down");
            return info;
        }

        return
        [
            Pki("oadm.pki.https-enable", "HTTPS: Enable/Update", "key"),
            Pki("oadm.pki.https-disable", "HTTPS: Disable", "cancel"),
            Pki("oadm.pki.dot1x-enable", "IEEE 802.1X: Enable/Update", "key"),
            Pki("oadm.pki.dot1x-disable", "IEEE 802.1X: Disable", "cancel"),
            Pki("oadm.pki.view", "View installed certificates", "details"),
            Pki("oadm.pki.delete", "Delete certificates", "remove"),
            Pki("oadm.pki.install", "Install certificates manually", "upload"),
            Pki("oadm.pki.renew", "Renew certificates now", "refresh"),
        ];
    }

    private static DevicesFixture CreateWithOldDevice()
    {
        var f = new DevicesFixture();
        Device old = TestSupport.Device("old", "ACCC8E000001", "10.0.0.31", "AXIS M3045-V");
        old.FirmwareVersion = "11.9.65";
        f.SeedDevices(
            TestSupport.Device("new", "B8A44F631339", "10.0.0.48", "AXIS P3265-V"),
            old,
            TestSupport.Device("down", "ACCC8E000002", "10.0.0.32", "AXIS M1065-L", DeviceStatus.Unreachable));
        return f;
    }

    private static MenuFlyoutPresenter Presenter(IEnumerable<MenuEntryViewModel> entries) =>
        new()
        {
            ItemsSource = entries.Select(Item).ToList(),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };

    /// <summary>A menu item bound like the Devices page context menu style (DevicesView.axaml).</summary>
    private static Control Item(MenuEntryViewModel entry)
    {
        if (entry.IsSeparator)
        {
            return new Separator();
        }

        var item = new MenuItem
        {
            Header = entry.Header,
            IsEnabled = entry.IsEnabled,
            Icon = new OadmIcon { Data = IconKeyConverter.Resolve(entry.IconKey) },
            ItemsSource = entry.Items?.Select(Item).ToList(),
        };
        ToolTip.SetTip(item, entry.ToolTip);
        ToolTip.SetShowOnDisabled(item, true);
        return item;
    }
}
