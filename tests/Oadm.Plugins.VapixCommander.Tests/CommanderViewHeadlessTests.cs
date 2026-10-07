using System.Globalization;
using System.Net;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Plugins.VapixCommander.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>Headless session with the real client App, so the page renders with the host theme (OadmTheme.axaml).</summary>
public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the Commander page offscreen inside the host's core plugin page view. Set OADM_SCREENSHOT_DIR to write PNGs.</summary>
public sealed class CommanderViewHeadlessTests
{
    [Fact]
    public async Task Commander_page_renders_rollout_set_try_error_and_raw_request()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "oadm-commander-headless-" + Guid.NewGuid().ToString("N"));
        App.Options = new AppOptions { UseFake = true, DataFolder = dataFolder };
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var completed = false;
        try
        {
            var page = await session.Dispatch(PageFixture.CreateAsync, CancellationToken.None);
            await session.Dispatch(async () =>
            {
                var vm = page.Vm;
                var more = Enumerable.Range(49, 3).Select(i => new FakeDevice { Address = "10.0.0." + i, Serial = "B8A44F6313" + i }).ToList();
                page.Server.DeviceList.All.AddRange(more);
                page.Client.DeviceList.AddRange(more);
                page.Client.RaiseDevicesChanged();
                vm.Targets.Single(t => t.Address == "10.0.0.49").IsSelected = true;
                vm.Targets.Single(t => t.Address == "10.0.0.61").IsSelected = true;
                vm.Add(page.Item("common.brand.read"));
                vm.Add(page.Item("common.basicdeviceinfo.read"));
                var shift = vm.Add(page.Item("common.daynight.shiftlevel"));
                shift.Fields.Single(f => f.Name == "level").Text = "65";
                await vm.ImportJsonAsync(File.ReadAllText(Path.Combine(Samples.Directory, "Common.json")));
                var savedBrand = vm.LibraryTree[1].Children.Single().Children.First();
                vm.ActivateCommand.Execute(savedBrand);
                vm.ActivateCommand.Execute(vm.LibraryTree[0].Children.Single()); // expand Built-in > Common
                vm.SelectedRolloutItem = shift;

                var view = new CommanderView { DataContext = vm };
                var host = new CorePluginPageView { DataContext = new CorePluginPageViewModel(VapixCommanderPlugin.PluginId, "VAPIX Commander", view, hasOwnCards: true) };
                var window = new Window { Width = 1600, Height = 940, Content = new Border { Padding = new Thickness(16), Child = host } };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.True(view.Bounds.Width > 1000);

                // No count badges and no library or rollout button rows: per-row icon buttons with tooltips.
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("badge") && b.IsEffectivelyVisible);
                var buttonTexts = view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
                Assert.DoesNotContain("Add to rollout", buttonTexts);
                Assert.DoesNotContain("Export all", buttonTexts);
                Assert.DoesNotContain("Clear", buttonTexts);
                var grid = view.FindControl<DataGrid>("RolloutGrid")!;
                var upButtons = grid.GetVisualDescendants().OfType<Button>().Where(b => ToolTip.GetTip(b) as string == "Move up (Ctrl+Up)").ToList();
                Assert.Equal(4, upButtons.Count);
                Assert.Single(upButtons, b => !b.IsEffectivelyEnabled);
                Assert.Contains(grid.GetVisualDescendants().OfType<Button>(), b => ToolTip.GetTip(b) as string == "Remove all commands from the rollout set" && b.IsEffectivelyEnabled);
                Capture(window, outDir, "plugin-vapix-commander-rollout.png");

                // Try on one device with a device error (param.cgi "# Error" behind HTTP 400).
                page.Server.VapixFactory.For(page.Camera.Id).Handler = _ =>
                    FakeVapix.Text("# Error: Error setting 'root.ImageSource.I0.DayNight.ShiftLevel' to '65'!", HttpStatusCode.BadRequest);
                page.Client.ConfirmAnswers.Enqueue(true);
                await vm.TryCommand.ExecuteAsync(null);
                Assert.False(vm.TryResult!.Success);
                Dispatcher.UIThread.RunJobs();
                Capture(window, outDir, "plugin-vapix-commander-try-error.png");

                vm.CloseTryResultCommand.Execute(null);
                vm.ShowRawCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                vm.Raw.MakeField(vm.Raw.QueryRows[1]);
                Dispatcher.UIThread.RunJobs();
                Capture(window, outDir, "plugin-vapix-commander-raw.png");

                // Raw editor with errors: each one directly below its input (path, JSON body, field row), save form name.
                vm.Raw.Path = "axis-cgi/param.cgi";
                vm.Raw.BodyType = "json";
                vm.Raw.BodyText = "{ broken";
                vm.Raw.Fields[0].Label = "";
                vm.Raw.OpenSaveCommand.Execute(null);
                vm.Raw.SaveName = "ab";
                vm.Raw.ShowProblems();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("The path must be relative to the device, start with / and must not contain \"..\".", vm.Raw.ErrorOf(nameof(vm.Raw.Path)));
                Assert.StartsWith("The JSON body is not valid", vm.Raw.ErrorOf(nameof(vm.Raw.BodyText)), StringComparison.Ordinal);
                Assert.Equal("Name must have 3 to 80 characters.", vm.Raw.ErrorOf(nameof(vm.Raw.SaveName)));
                var pathBox = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible && t.Text == "axis-cgi/param.cgi");
                Assert.True(DataValidationErrors.GetHasErrors(pathBox));
                Assert.False(vm.AddRawToRolloutCommand.CanExecute(null));
                Assert.False(vm.Raw.SaveCommand.CanExecute(null));
                Capture(window, outDir, "plugin-vapix-commander-raw-errors.png");
                window.Close();
                completed = true;
            }, CancellationToken.None);
            Assert.True(completed, "the page checks did not run to the end");
        }
        finally
        {
            // The awaited dispatch may continue on the session's UI thread; Dispose waits for that thread, so dispose elsewhere.
            await Task.Run(session.Dispose);
        }

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
    public async Task Try_result_bodies_are_highlighted_json_xml_and_param_cgi()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "oadm-commander-headless-" + Guid.NewGuid().ToString("N"));
        App.Options = new AppOptions { UseFake = true, DataFolder = dataFolder };
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        try
        {
            var page = await session.Dispatch(PageFixture.CreateAsync, CancellationToken.None);
            await session.Dispatch(async () =>
            {
                var vm = page.Vm;
                var vapix = page.Server.VapixFactory.For(page.Camera.Id);
                var view = new CommanderView { DataContext = vm };
                var host = new CorePluginPageView { DataContext = new CorePluginPageViewModel(VapixCommanderPlugin.PluginId, "VAPIX Commander", view, hasOwnCards: true) };
                var window = new Window { Width = 1600, Height = 940, Content = new Border { Padding = new Thickness(16), Child = host } };
                window.Show();

                // JSON (minified on the wire): pretty-printed and highlighted.
                vm.Add(page.Item("common.basicdeviceinfo.read"));
                vapix.Handler = _ => FakeVapix.Json(
                    "{\"apiVersion\":\"1.3\",\"context\":\"oadm\",\"data\":{\"propertyList\":{\"Brand\":\"AXIS\",\"ProdNbr\":\"P3265-V\",\"ProdFullName\":\"AXIS P3265-V Dome Camera\",\"Version\":\"12.0.68\",\"SerialNumber\":\"B8A44F631348\",\"Architecture\":\"aarch64\",\"Soc\":\"Axis Artpec-8\",\"BuildDate\":\"Jun 10 2025 09:12\"},\"restricted\":false,\"retries\":3,\"temperature\":41.5,\"location\":null}}");
                await vm.TryCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                var body = view.GetVisualDescendants().OfType<CodeView>().First(c => c.IsEffectivelyVisible);
                Assert.Equal(CodeLanguage.Json, body.Document.Language);
                Assert.True(body.Document.IsHighlighted);
                Capture(window, outDir, "plugin-vapix-commander-try-json.png");

                vm.TryResult!.ShowRawCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.StartsWith("{\"apiVersion\"", body.DisplayedText, StringComparison.Ordinal);
                Capture(window, outDir, "plugin-vapix-commander-try-json-raw.png");

                // param.cgi key=value with an error line.
                vm.Add(page.Item("common.brand.read"));
                vapix.Handler = _ => FakeVapix.Text(
                    "root.Brand.Brand=AXIS\r\nroot.Brand.ProdFullName=AXIS P3265-V Dome Camera\r\nroot.Brand.ProdNbr=P3265-V\r\nroot.Brand.ProdShortName=AXIS P3265-V\r\nroot.Brand.ProdType=Dome Camera\r\nroot.Brand.ProdVariant=\r\nroot.Brand.WebURL=http://www.axis.com\r\nroot.ImageSource.I0.DayNight.ShiftLevel=50\r\nroot.Network.DNSUpdate.Enabled=yes\r\n# Error: Error -1 getting param in group 'root.Brand.Missing'\r\n");
                await vm.TryCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                body = view.GetVisualDescendants().OfType<CodeView>().First(c => c.IsEffectivelyVisible);
                Assert.Equal(CodeLanguage.KeyValue, body.Document.Language);
                Capture(window, outDir, "plugin-vapix-commander-try-paramcgi.png");

                // XML from the raw request editor.
                vm.ShowRawCommand.Execute(null);
                vm.Raw.Path = "/axis-cgi/disks/list.cgi";
                vm.Raw.QueryRows.Clear();
                vm.Raw.QueryRows.Add(new KeyValueRowViewModel { Key = "diskid", Value = "all" });
                vm.Raw.ResponseKind = ResponseKinds.Xml;
                vapix.Handler = _ => FakeVapix.Text(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><root xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" version=\"1.0\"><!-- edge storage --><disks numberofdisks=\"1\"><disk diskid=\"SD_DISK\" name=\"\" totalsize=\"30535680\" freesize=\"29967360\" cleanuplevel=\"90\" cleanupmaxage=\"7\" group=\"S0\" status=\"OK\" filesystem=\"vfat\" fullaction=\"overwrite\" readonly=\"no\" /><note><![CDATA[Card <A> & more]]></note></disks></root>",
                    HttpStatusCode.OK,
                    "text/xml");
                await vm.TryCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                body = view.GetVisualDescendants().OfType<CodeView>().First(c => c.IsEffectivelyVisible);
                Assert.Equal(CodeLanguage.Xml, body.Document.Language);
                Assert.True(body.Document.IsHighlighted);
                Capture(window, outDir, "plugin-vapix-commander-try-xml.png");
                window.Close();
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }

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
    public async Task Target_list_with_thousands_of_devices_is_virtualized()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "oadm-commander-headless-" + Guid.NewGuid().ToString("N"));
        App.Options = new AppOptions { UseFake = true, DataFolder = dataFolder };
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        try
        {
            var page = await session.Dispatch(PageFixture.CreateAsync, CancellationToken.None);
            await session.Dispatch(() =>
            {
                var vm = page.Vm;
                var devices = Enumerable.Range(0, 5000).Select(i => new FakeDevice
                {
                    Address = string.Create(CultureInfo.InvariantCulture, $"10.1.{i / 256}.{i % 256}"),
                    Serial = string.Create(CultureInfo.InvariantCulture, $"B8A44F{i:D6}"),
                    Model = i % 7 == 3 ? "AXIS C1310-E" : "AXIS P3265-V",
                    Category = i % 7 == 3 ? DeviceCategory.Speaker : DeviceCategory.Camera,
                    Apis = i % 7 == 3 ? [new DeviceApi("param-cgi", "1.0")] : i % 11 == 5 ? [] : Samples.P3265Apis,
                }).ToList();
                page.Client.DeviceList.Clear();
                page.Client.DeviceList.AddRange(devices);
                page.Client.Selection.Clear();
                page.Client.Selection.AddRange(devices.Take(1500));
                page.Client.RaiseDevicesChanged();
                vm.UseDevicesSelectionCommand.Execute(null);
                vm.Add(page.Item("common.brand.read"));
                vm.Add(page.Item("common.basicdeviceinfo.read"));

                var view = new CommanderView { DataContext = vm };
                var host = new CorePluginPageView { DataContext = new CorePluginPageViewModel(VapixCommanderPlugin.PluginId, "VAPIX Commander", view, hasOwnCards: true) };
                var window = new Window { Width = 1600, Height = 940, Content = new Border { Padding = new Thickness(16), Child = host } };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var list = view.FindControl<ItemsControl>("TargetList")!;
                var rows = list.GetVisualDescendants().OfType<CheckBox>().Count();
                Assert.InRange(rows, 1, 60);
                Assert.Equal("1,500 of 5,000 selected", vm.TargetSummary);
                Assert.NotNull(vm.CompatibilitySummary);
                Capture(window, outDir, "plugin-vapix-commander-many-devices.png");

                vm.TargetSearch = "10.1.19.";
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(136, vm.VisibleTargets.Count);
                Assert.InRange(list.GetVisualDescendants().OfType<CheckBox>().Count(), 1, 60);
                window.Close();
            }, CancellationToken.None);
        }
        finally
        {
            await Task.Run(session.Dispose);
        }

        try
        {
            Directory.Delete(dataFolder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // nothing was written
        }
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
