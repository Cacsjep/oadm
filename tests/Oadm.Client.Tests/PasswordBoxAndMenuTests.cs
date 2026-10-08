using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Controls;
using Oadm.Client.Devices;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>The shared password field and the grouped device context menu, rendered with the host theme.</summary>
public sealed class PasswordBoxAndMenuTests
{
    [Fact]
    public async Task Password_box_masks_and_the_eye_button_reveals_and_hides_the_text()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(() =>
        {
            var password = new PasswordBox { Text = "s3cret!", Width = 280 };
            var plain = new TextBox { Text = "root", Width = 280 };
            var window = new Window { Width = 400, Height = 200, Content = new StackPanel { Spacing = 8, Children = { plain, password } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // A text box with the theme's look: same height as any other text field, masked by default.
            Assert.IsAssignableFrom<TextBox>(password);
            Assert.Equal(PasswordBox.Mask, password.PasswordChar);
            Assert.False(password.RevealPassword);
            Assert.Equal(plain.Bounds.Height, password.Bounds.Height);
            Assert.Equal(plain.Padding, password.Padding);
            Assert.Same(password.RevealButton, password.InnerRightContent);
            Assert.False(password.RevealButton.Focusable);
            Assert.Equal(PasswordBox.ShowTip, ToolTip.GetTip(password.RevealButton));
            Geometry? eye = ((OadmIcon)password.RevealButton.Content!).Data;
            Assert.NotNull(eye);

            password.RevealButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(password.RevealPassword);
            Assert.Equal(PasswordBox.HideTip, ToolTip.GetTip(password.RevealButton));
            Assert.NotSame(eye, ((OadmIcon)password.RevealButton.Content!).Data); // eye-off icon
            Assert.Equal("s3cret!", password.Text);

            password.RevealButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(password.RevealPassword);
            Assert.Equal(PasswordBox.ShowTip, ToolTip.GetTip(password.RevealButton));
            Assert.Same(eye, ((OadmIcon)password.RevealButton.Content!).Data);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Grouped_context_menu_renders_submenus_with_icons_and_readable_width()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(() =>
        {
            TaskPluginInfo Plugin(string id, string name, string group, string icon) =>
                new() { Id = id, DisplayName = name, Group = group, IconKey = icon };

            // The installed task plugins, as the server reports them (older names with "..." are stripped).
            TaskPluginInfo[] plugins =
            [
                Plugin("oadm.restart", "Restart", "Maintenance", "restart"),
                Plugin("oadm.firmware", "Upgrade firmware", "Maintenance", "firmware"),
                Plugin("oadm.acap", "Applications (ACAP)...", "Applications", "plugin"),
                Plugin("oadm.users", "Users", "Users", "users"),
                Plugin("oadm.network", "Network settings", "Network", "network"),
                Plugin("oadm.network.assign-ip", "Assign IP address", "Network", "network"),
            ];
            List<MenuEntryViewModel> entries =
            [
                new() { Header = "Open web interface", IconKey = "externalLink" },
                new() { Header = "Remove", IconKey = "remove" },
                MenuEntryViewModel.Separator(),
                .. DevicesViewModel.TaskMenuGroups(plugins, new CommunityToolkit.Mvvm.Input.RelayCommand(() => { })),
            ];

            // The menu as the device grid shows it, plus the open Network submenu next to it.
            MenuFlyoutPresenter top = Presenter(entries);
            MenuEntryViewModel network = entries.Single(e => e.Header == "Network");
            MenuFlyoutPresenter sub = Presenter(network.Items!);
            var window = new Window
            {
                Width = 640,
                Height = 360,
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

            List<MenuItem> items = top.GetVisualDescendants().OfType<MenuItem>().ToList();
            Assert.Equal(["Open web interface", "Remove", "-", "Applications", "Maintenance", "Network", "Users"],
                items.Select(i => i.Header as string ?? "").ToArray());
            Assert.All(items.Skip(3), i => Assert.True(i.HasSubMenu));
            Assert.DoesNotContain(items, i => ((string?)i.Header ?? "").EndsWith("...", StringComparison.Ordinal));
            Assert.All(items.Where(i => (string?)i.Header != "-"), i => Assert.IsType<OadmIcon>(i.Icon));
            Assert.Equal(["Assign IP address", "Network settings"],
                sub.GetVisualDescendants().OfType<MenuItem>().Select(i => i.Header as string ?? "").ToArray());

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-context-menu-groups.png"));
            }

            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>Menu items built like the device grid's ContextMenu item style (header, icon, submenu entries).</summary>
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
