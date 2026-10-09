using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Controls;
using Oadm.Client.Devices;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>The Security submenu with the eight tasks of the PKI plugin, rendered with the host theme.</summary>
public sealed class SecurityMenuTests
{
    [Fact]
    public async Task Security_submenu_lists_the_pki_tasks_sorted_with_icons()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(() =>
        {
            TaskPluginInfo Plugin(string id, string name, string group, string icon) =>
                new() { Id = id, DisplayName = name, Group = group, IconKey = icon };

            TaskPluginInfo[] plugins =
            [
                Plugin("oadm.restart", "Restart", "Maintenance", "restart"),
                Plugin("oadm.network", "Network settings", "Network", "network"),
                Plugin("oadm.users", "Users", "Users", "users"),
                Plugin("oadm.pki.https-enable", "Enable HTTPS", "Security", "key"),
                Plugin("oadm.pki.https-disable", "Disable HTTPS", "Security", "cancel"),
                Plugin("oadm.pki.dot1x-enable", "Enable IEEE 802.1X", "Security", "key"),
                Plugin("oadm.pki.dot1x-disable", "Disable IEEE 802.1X", "Security", "cancel"),
                Plugin("oadm.pki.view", "View certificates", "Security", "details"),
                Plugin("oadm.pki.delete", "Delete certificates", "Security", "remove"),
                Plugin("oadm.pki.install", "Install certificates", "Security", "upload"),
                Plugin("oadm.pki.install-ca", "Install CA certificates", "Security", "upload"),
                Plugin("oadm.pki.renew", "Renew certificates", "Security", "refresh"),
            ];
            List<MenuEntryViewModel> entries =
            [
                new() { Header = "Open web interface", IconKey = "externalLink" },
                new() { Header = "Remove", IconKey = "remove" },
                MenuEntryViewModel.Separator(),
                .. DevicesViewModel.TaskMenuGroups(plugins, new CommunityToolkit.Mvvm.Input.RelayCommand(() => { })),
            ];
            MenuEntryViewModel security = entries.Single(e => e.Header == "Security");
            Assert.Equal("key", security.IconKey);

            var top = Presenter(entries);
            var sub = Presenter(security.Items!);
            var window = new Window
            {
                Width = 640,
                Height = 400,
                Content = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 4,
                    Margin = new Thickness(16),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                    Children = { top, sub },
                },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                ["Delete certificates", "Disable HTTPS", "Disable IEEE 802.1X", "Enable HTTPS", "Enable IEEE 802.1X",
                 "Install CA certificates", "Install certificates", "Renew certificates", "View certificates"],
                sub.GetVisualDescendants().OfType<MenuItem>().Select(i => i.Header as string ?? "").ToArray());

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "pki-security-menu.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private static MenuFlyoutPresenter Presenter(IEnumerable<MenuEntryViewModel> entries) =>
        new()
        {
            ItemsSource = entries.Select(Item).ToList(),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };

    private static MenuItem Item(MenuEntryViewModel entry) => new()
    {
        Header = entry.Header,
        IsEnabled = entry.IsEnabled,
        Icon = entry.IsSeparator ? null : new OadmIcon { Data = IconKeyConverter.Resolve(entry.IconKey) },
        ItemsSource = entry.Items?.Select(Item).ToList(),
    };
}
