using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

using Oadm.Plugins.WebView.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Plugins.WebView.Tests;

/// <summary>The Web View page: the tag tree, tabs, Close all and Back, the page select with Custom, and its look.</summary>
public sealed class WebViewTests(ITestOutputHelper output)
{
    private static (TestContext Ctx, WebViewPageViewModel Vm, TestDevice A, TestDevice B, TestDevice C) Page()
    {
        var a = new TestDevice("10.0.0.48", "P3265-V", "Building A", "PTZ");
        var b = new TestDevice("10.0.0.9", "M3106-L", "Building A") { Status = DeviceStatus.CredentialsRequired };
        var c = new TestDevice("10.0.0.120", "A9210") { Status = DeviceStatus.Unreachable, Category = DeviceCategory.IoModule };
        var ctx = new TestContext([a, b, c]);
        return (ctx, new WebViewPageViewModel(ctx, WebRuntimeState.Available), a, b, c);
    }

    [Fact]
    public void The_tree_groups_by_tag_with_No_tag_last_and_devices_by_address()
    {
        var (_, vm, a, b, c) = Page();

        var rows = vm.TreeRows.Select(r => r is DeviceTreeGroup g ? $"[{g.Name}]" : ((DeviceTreeDevice)r).Address).ToList();
        Assert.Equal(["[Building A]", "10.0.0.9", "10.0.0.48", "[PTZ]", "10.0.0.48", "[No tag]", "10.0.0.120"], rows);

        vm.SearchText = "a9210"; // the model is searched, not shown
        Assert.Equal(["[No tag]", "10.0.0.120"], vm.TreeRows.Select(r => r is DeviceTreeGroup g ? $"[{g.Name}]" : ((DeviceTreeDevice)r).Address));
        vm.SearchText = "ptz"; // tags are searched too
        Assert.Contains(vm.TreeRows, r => r is DeviceTreeGroup { Name: "PTZ" });

        vm.SearchText = string.Empty;
        vm.ToggleGroupCommand.Execute(vm.TreeRows.OfType<DeviceTreeGroup>().First());
        Assert.DoesNotContain(vm.TreeRows, r => r is DeviceTreeDevice { GroupKey: "Building A" });
        _ = (a, b, c);
    }

    [Fact]
    public async Task A_click_opens_a_tab_once_per_device_with_the_page_chosen_last()
    {
        var (ctx, vm, a, b, _) = Page();
        var rowA = vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == a.Id);

        vm.OpenDeviceCommand.Execute(rowA);
        await Task.Yield();
        var tab = Assert.Single(vm.Tabs);
        Assert.Same(tab, vm.ActiveTab);
        Assert.Equal("10.0.0.48", tab.Title);
        Assert.Equal(ctx.Web.Sessions[0].StartAddress("/"), tab.Address);
        Assert.True(rowA.IsActive);

        vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == b.Id));
        Assert.Equal(2, vm.Tabs.Count);
        vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().Last(r => r.Device.Id == a.Id)); // under PTZ: same device, its tab
        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(tab, vm.ActiveTab);
        Assert.Equal("PTZ", tab.GroupKey);

        // The select opens a preset in the active tab; new tabs use it too.
        var opened = new List<Uri>();
        tab.NavigateRequested += (_, page) => opened.Add(page);
        vm.SelectedPreset = WebPagePreset.All.Single(p => p.Name == "Object Analytics");
        Assert.Equal("/local/objectanalytics/index.html#/", tab.Path);
        Assert.Equal([new Uri(ctx.Web.Sessions[0].Origin, "/local/objectanalytics/index.html#/")], opened); // same relay
        vm.ReloadCommand.Execute(null); // Reload opens the same page again
        Assert.Equal(2, opened.Count);
        Assert.Equal(2, ctx.Web.Sessions.Count);

        // The camera's own pages keep their route after the "#".
        vm.SelectedPreset = WebPagePreset.All.Single(p => p.Name == "Privacy masks");
        Assert.Equal(new Uri(ctx.Web.Sessions[0].Origin, "/camera/index.html#/video/privacymasks"), opened[^1]);

        // Switching tabs shows that tab's page in the select without opening anything.
        vm.ActiveTab = vm.Tabs[1];
        Assert.Equal("Web interface", vm.SelectedPreset.Name);
        Assert.Equal("/", vm.Tabs[1].Path);
    }

    [Fact]
    public async Task Close_all_closes_every_tab_and_Back_asks_the_active_browser()
    {
        var (ctx, vm, a, b, _) = Page();
        vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == a.Id));
        vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == b.Id));
        await Task.Yield();
        var tab = vm.ActiveTab!;

        Assert.False(vm.GoBackCommand.CanExecute(null)); // nothing to go back to yet
        WebTab? asked = null;
        vm.GoBackRequested += (_, t) => asked = t;
        tab.CanGoBack = true; // the browser reports a history
        vm.RefreshGoBack();
        Assert.True(vm.GoBackCommand.CanExecute(null));
        vm.GoBackCommand.Execute(null);
        Assert.Same(tab, asked);

        Assert.True(vm.CloseAllTabsCommand.CanExecute(null));
        vm.CloseAllTabsCommand.Execute(null);
        Assert.Empty(vm.Tabs);
        Assert.Null(vm.ActiveTab);
        Assert.All(ctx.Web.Sessions, s => Assert.True(s.IsDisposed));
        Assert.False(vm.CloseAllTabsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Custom_opens_a_valid_path_and_shows_the_error_below_the_field()
    {
        var (_, vm, a, _, _) = Page();
        vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == a.Id));
        await Task.Yield();
        var tab = vm.ActiveTab!;

        vm.SelectedPreset = WebPagePreset.All.Single(p => p.IsCustom);
        Assert.True(vm.IsCustom);
        Assert.Equal("/", tab.Path); // nothing opened before a path is given
        vm.CustomPath = "http://other.example.com/";
        vm.ApplyCustomPathCommand.Execute(null);
        Assert.Equal(WebViewTexts.CustomPathInvalid, vm.ErrorOf(nameof(vm.CustomPath)));
        Assert.Equal("/", tab.Path);

        vm.CustomPath = "/local/myapp/index.html";
        vm.ApplyCustomPathCommand.Execute(null);
        Assert.Null(vm.ErrorOf(nameof(vm.CustomPath)));
        Assert.Equal("/local/myapp/index.html", tab.Path);
        Assert.True(tab.Preset.IsCustom);
    }

    [Fact]
    public async Task A_refused_device_shows_the_reason_and_a_removed_device_closes_its_tab()
    {
        var (ctx, vm, a, b, _) = Page();
        ctx.Web.Refused[b.Id] = DeviceMessages.CredentialsRequired;
        vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == b.Id));
        await Task.Yield();
        Assert.Equal(DeviceMessages.CredentialsRequired, vm.ActiveTab!.Error);
        Assert.Null(vm.ActiveTab.Address);

        vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == a.Id));
        await Task.Yield();
        ctx.Items.Remove(a);
        ctx.RaiseDevicesChanged();
        Assert.Single(vm.Tabs);
        Assert.True(ctx.Web.Sessions.Single(s => s.DeviceId == a.Id).IsDisposed);

        vm.Tabs[0].CloseCommand.Execute(null);
        Assert.Empty(vm.Tabs);
        Assert.Null(vm.ActiveTab);
        Assert.True(vm.ShowNoTabs);
    }

    [Fact]
    public void Without_the_web_runtime_the_page_says_what_to_install_and_opens_nothing()
    {
        var a = new TestDevice("10.0.0.48", "P3265-V");
        var ctx = new TestContext([a]);
        var vm = new WebViewPageViewModel(ctx, new WebRuntimeState(false, Client.WebRuntime.WindowsMissing));
        vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().Single());
        Assert.True(vm.IsRuntimeMissing);
        Assert.Empty(vm.Tabs);
        Assert.Empty(ctx.Web.Sessions);
        Assert.False(vm.ShowNoTabs);
    }

    [Fact]
    public void Five_thousand_devices_build_and_search() => BuildAndSearchFiveThousand();

    [Fact]
    [Trait("Category", "Perf")]
    public void Five_thousand_devices_build_and_search_fast()
    {
        var (build, search) = BuildAndSearchFiveThousand();
        output.WriteLine($"build {build} ms, search + clear {search} ms");
        Assert.True(build < 2000, $"build took {build} ms");
        Assert.True(search < 2000, $"search took {search} ms");
    }

    private static (long Build, long Search) BuildAndSearchFiveThousand()
    {
        var devices = Enumerable.Range(0, 5000)
            .Select(i => (IDeviceInfo)new TestDevice($"10.{i / 65536}.{i / 256 % 256}.{i % 256}", "P3265-V", i % 3 == 0 ? ["Building A", "PTZ"] : i % 3 == 1 ? ["Building B"] : []))
            .ToList();
        var ctx = new TestContext(devices);
        var watch = Stopwatch.StartNew();
        var vm = new WebViewPageViewModel(ctx, WebRuntimeState.Available);
        var build = watch.ElapsedMilliseconds;
        watch.Restart();
        vm.SearchText = "10.0.19";
        vm.SearchText = string.Empty;
        var search = watch.ElapsedMilliseconds;
        Assert.Equal(5000 + 1667 + 4, vm.TreeRows.Count);
        return (build, search);
    }

    [Fact]
    public async Task The_page_keeps_working_after_another_rail_page_was_shown()
    {
        var session = HeadlessEntry.Session;
        var done = await session.Dispatch(async () =>
        {
            var factory = WebViewPageView.BrowserFactory;
            var check = WebViewPage.RuntimeCheck;
            WebViewPageView.BrowserFactory = tab => new Border { DataContext = tab };
            WebViewPage.RuntimeCheck = () => WebRuntimeState.Available;
            try
            {
                var a = new TestDevice("10.0.0.48", "P3265-V");
                var b = new TestDevice("10.0.0.9", "M3106-L");
                var ctx = new TestContext([a, b]);
                var view = (WebViewPageView)new WebViewPage().CreateView(ctx);
                var vm = (WebViewPageViewModel)view.DataContext!;
                var window = Headless.Host(view);
                window.Show();
                vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == a.Id));
                await Task.Yield();
                Headless.Pump();

                // Another rail page: the host takes the view off the screen, then shows the same view again.
                var host = (Border)window.Content!;
                var page = host.Child;
                host.Child = new TextBlock { Text = "Devices" };
                Headless.Pump();
                host.Child = page;
                Headless.Pump();

                vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == b.Id));
                await Task.Yield();
                Headless.Pump();
                Assert.Equal(2, vm.Tabs.Count);
                Assert.All(vm.Tabs, t => Assert.NotNull(t.Address));
                Assert.Equal(2, view.GetVisualDescendants().OfType<Border>().Count(x => x.DataContext is WebTab && x.Parent is Panel { Name: "Pages" }));
                window.Close();
                vm.Dispose(); // the host does this when the page is removed
                return true;
            }
            finally
            {
                WebViewPageView.BrowserFactory = factory;
                WebViewPage.RuntimeCheck = check;
            }
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task The_page_renders_the_tree_tabs_and_the_toolbar()
    {
        var session = HeadlessEntry.Session;
        var done = await session.Dispatch(async () =>
        {
            var factory = WebViewPageView.BrowserFactory;
            // The native browser needs a real window: a stand-in shows where the page goes.
            WebViewPageView.BrowserFactory = tab => new Border
            {
                DataContext = tab,
                Background = Avalonia.Media.Brushes.WhiteSmoke,
                [!Avalonia.Visual.IsVisibleProperty] = new Avalonia.Data.Binding(nameof(WebTab.IsActive)),
                Child = new TextBlock { Text = "Web interface of " + tab.Title, Foreground = Avalonia.Media.Brushes.Black, Margin = new Avalonia.Thickness(24) },
            };
            try
            {
                var (_, vm, a, b, _) = Page();
                var window = Headless.Host(new WebViewPageView { DataContext = vm });
                window.Show();
                vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == b.Id));
                vm.OpenDeviceCommand.Execute(vm.TreeRows.OfType<DeviceTreeDevice>().First(r => r.Device.Id == a.Id));
                await Task.Yield();
                Headless.Pump();
                Headless.Capture(window, "web-view-page.png");

                vm.SelectedPreset = WebPagePreset.All.Single(p => p.IsCustom);
                vm.CustomPath = "local/app";
                vm.ApplyCustomPathCommand.Execute(null);
                Headless.Pump();
                Headless.Capture(window, "web-view-custom-error.png");
                window.Close();

                var missing = new WebViewPageViewModel(new TestContext([a, b]), new WebRuntimeState(false, Client.WebRuntime.LinuxMissing));
                var missingWindow = Headless.Host(new WebViewPageView { DataContext = missing });
                missingWindow.Show();
                Headless.Pump();
                Headless.Capture(missingWindow, "web-view-runtime-missing.png");
                missingWindow.Close();
                return true;
            }
            finally
            {
                WebViewPageView.BrowserFactory = factory;
            }
        }, CancellationToken.None);
        Assert.True(done);
    }
}

/// <summary>The shared device icon: category icon, status color and the hover text.</summary>
public sealed class DeviceIconTests
{
    [Theory]
    [InlineData(DeviceStatus.Ok, false, null)]
    [InlineData(DeviceStatus.Unreachable, false, "Unreachable")]
    [InlineData(DeviceStatus.CredentialsRequired, true, DeviceMessages.CredentialsRequired)]
    [InlineData(DeviceStatus.PasswordNotSet, true, DeviceMessages.PasswordNotSet)]
    [InlineData(DeviceStatus.CertificateChanged, true, DeviceMessages.CertificateChanged)]
    public void The_status_decides_color_and_hover_text(DeviceStatus status, bool warning, string? message)
    {
        Assert.Equal(warning, Oadm.Sdk.Client.Controls.DeviceIcon.IsWarning(status));
        Assert.Equal(message, Oadm.Sdk.Client.Controls.DeviceIcon.StatusMessage(status));
    }

    [Fact]
    public void Every_category_has_an_icon_and_a_text()
    {
        Assert.Equal("device.camera", Oadm.Sdk.Client.Controls.DeviceIcon.IconKey(DeviceCategory.Camera));
        Assert.Equal("device.io", Oadm.Sdk.Client.Controls.DeviceIcon.IconKey(DeviceCategory.IoModule));
        Assert.Equal("device.generic", Oadm.Sdk.Client.Controls.DeviceIcon.IconKey(DeviceCategory.Unknown));
        Assert.Equal("I/O module", Oadm.Sdk.Client.Controls.DeviceIcon.CategoryText(DeviceCategory.IoModule));
    }
}
