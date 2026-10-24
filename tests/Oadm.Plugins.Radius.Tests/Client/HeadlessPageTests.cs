using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Servers;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Radius.Tests.Client;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.Radius.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    private static readonly Lazy<HeadlessUnitTestSession> Shared = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry)));

    /// <summary>One headless application per test process (a second one fails to register its resources); never disposed.</summary>
    public static HeadlessUnitTestSession Session => Shared.Value;

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>The headless tests share one Avalonia session and run one after another.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessSessions
{
    public const string Name = "Avalonia headless";
}

/// <summary>
/// The page, its cards and its dialogs rendered offscreen in the real host page with demo data (OADM_SCREENSHOT_DIR writes
/// PNGs); the checks are content and rules, never positions.
/// </summary>
[Collection(HeadlessSessions.Name)]
public sealed class HeadlessPageTests
{
    private static readonly string? OutDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");

    [Fact]
    public async Task Turned_off_the_page_shows_only_the_server_card_with_turn_on_which_waits_for_oadm_pro()
    {
        // docs/spec/pki.md "One plugin": the server is off by default and turned on on its own page; nothing else is read.
        var server = new FakeRadiusBackend { Switch = new ServerSwitchState(false, false, true, ServerSwitchMethods.OffText) };
        var ctx = new FakePageContext(server);
        static Button? Button(Window window, string label) => window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label));
        static int Cards(Window window) => window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("card") && b.IsEffectivelyVisible);

        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = Host(view, 1600, 900);
            window.Show();
            view.DataContext = vm; // after attaching: no live watch, deterministic content
            await vm.LoadAllAsync(CancellationToken.None);
            Pump();
            Capture(window, "radius-page-off.png");
            var off = (Cards(window), Texts(window), Button(window, "Turn on")?.IsEffectivelyEnabled, server.CountOf(RadiusMethods.GetState) + server.CountOf(RadiusMethods.ListLog));

            server.Switch = server.Switch with { ProActive = false, StatusText = Oadm.Sdk.Licensing.ProLicenseTexts.NeedsPro };
            await vm.Server.LoadAsync(CancellationToken.None);
            Pump();
            Capture(window, "radius-page-needs-pro.png");
            var noPro = (Texts(window), Button(window, "Turn on")?.IsEffectivelyEnabled);

            server.Switch = server.Switch with { ProActive = true, StatusText = ServerSwitchMethods.OffText };
            await vm.Server.LoadAsync(CancellationToken.None);
            await vm.Server.TurnOnCommand.ExecuteAsync(null);
            await vm.LoadPageAsync(CancellationToken.None); // the live watch would read the page (not started here)
            Pump();
            Capture(window, "radius-page-turned-on.png");
            var on = (Cards(window), Button(window, "Turn off") is not null, Button(window, "Turn on") is null);
            window.Close();
            return (off, noPro, on);
        }, CancellationToken.None);

        Assert.Equal(1, checks.off.Item1);
        Assert.Contains("Server", checks.off.Item2);
        Assert.Contains(ServerSwitchMethods.OffText, checks.off.Item2);
        Assert.DoesNotContain("Switches", checks.off.Item2);
        Assert.DoesNotContain("Sessions", checks.off.Item2);
        Assert.True(checks.off.Item3);
        Assert.Equal(0, checks.off.Item4); // nothing of the server is read while it is off

        Assert.Contains(Oadm.Sdk.Licensing.ProLicenseTexts.NeedsPro, checks.noPro.Item1);
        Assert.False(checks.noPro.Item2);

        Assert.Equal(3, checks.on.Item1); // Server, Switches, Sessions (in progress, then the log)
        Assert.True(checks.on.Item2);
        Assert.True(checks.on.Item3);
    }

    [Fact]
    public async Task The_page_renders_every_card_with_demo_data()
    {
        var server = new FakeRadiusBackend();
        var ctx = new FakePageContext(server);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = Host(view, 1920, 1500);
            window.Show();
            view.DataContext = vm; // after attaching: no live watch, deterministic content
            await vm.LoadAllAsync(CancellationToken.None);
            Pump();
            Capture(window, "radius-page.png");
            var texts = Texts(window);
            var checkBoxes = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Select(c => c.Content as string).ToList();
            var italic = window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("guess") && t.FontStyle == Avalonia.Media.FontStyle.Italic);
            var tabs = window.GetVisualDescendants().OfType<PageTab>().Count(t => t.IsEffectivelyVisible);
            var primaries = window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));
            var labels = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)
                .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)).ToList();
            window.Close();
            return (texts, checkBoxes, italic, tabs, primaries, labels);
        }, CancellationToken.None);

        Assert.Contains("802.1X server", checks.texts);
        Assert.Contains(RadiusPluginInfo.Subtitle, checks.texts);
        Assert.Equal(0, checks.tabs); // one page, no tabs
        foreach (var title in new[] { "Server", "Switches", "Sessions" })
        {
            Assert.Contains(title, checks.texts);
        }

        foreach (var gone in new[] { "Trusted CAs", "Policies", "Default VLAN" })
        {
            Assert.DoesNotContain(gone, checks.texts); // the rules are PKI's policies
        }

        Assert.Contains("Running on port 1812", checks.texts);
        Assert.Contains("Last request", checks.texts);
        Assert.DoesNotContain("Wrong secret", checks.texts); // no State column of the switches (decision 2026-10-25)
        Assert.DoesNotContain("Advanced", checks.labels);
        Assert.Contains("6 policies applied", checks.texts); // the link to the policies, left in the Save row
        Assert.Contains("20", checks.texts); // the VLAN column shows the number, the policy that gave it in its tooltip
        Assert.DoesNotContain("VLAN 20 by PTZ", checks.texts);
        Assert.DoesNotContain("Authenticated with VLAN 20 by PTZ", checks.texts); // an ended session is a row of the log, not in progress
        Assert.Contains("Authenticated", checks.texts); // its row of the log
        Assert.Contains("Add switch", checks.labels);
        Assert.Equal(1, checks.primaries); // one accent button: the Server card's Save
        Assert.Contains("Sending server certificate: fragment 2 of 4", checks.texts);
        Assert.False(checks.italic); // no Device column, so no guessed device in italics (decision 2026-10-25)
        Assert.Contains("Delete all", checks.labels);
        Assert.DoesNotContain("Revoked", checks.texts);
        Assert.Contains(checks.texts, t => t?.Contains("Certificate of a device OADM does not manage (ACCC8E123407)", StringComparison.Ordinal) == true);
        Assert.All(checks.texts.Concat(checks.labels), t => Assert.False(t?.EndsWith("...", StringComparison.Ordinal) == true || t?.EndsWith('…') == true, t));
    }

    [Fact]
    public async Task A_group_user_gets_sessions_and_log_without_changes()
    {
        var server = new FakeRadiusBackend();
        var ctx = new FakePageContext(server) { AllDevices = false };
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = Host(view, 1280, 900);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAllAsync(CancellationToken.None);
            Pump();
            Capture(window, "radius-group-user.png");
            var texts = Texts(window);
            var deleteAll = window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Classes.Contains("danger"));
            window.Close();
            return (texts, deleteAll);
        }, CancellationToken.None);

        Assert.Contains("Sessions", checks.texts);
        Assert.DoesNotContain("Switches", checks.texts);
        Assert.DoesNotContain("6 policies applied", checks.texts);
        Assert.False(checks.deleteAll);
    }

    [Fact]
    public async Task The_switch_dialog_renders_with_errors_below_the_fields_and_a_generated_secret()
    {
        var server = new FakeRadiusBackend();
        var ctx = new FakePageContext(server);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            using var page = new RadiusViewModel(ctx);
            await page.LoadServerAsync(CancellationToken.None);
            var dialog = page.Switches.NewDialog(null);
            var window = new SwitchWindow();
            window.Attach(dialog, _ => { });
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            dialog.Name = "Floor 2";
            dialog.Address = "bad host";
            dialog.Secret = "short";
            Pump();
            Capture(window, "radius-switch-errors.png");
            var errors = Texts(window);

            dialog.Name = "Floor 3";
            dialog.Address = "10.0.3.2";
            await dialog.GenerateCommand.ExecuteAsync(null);
            Pump();
            Capture(window, "radius-switch-generated.png");
            var box = window.GetVisualDescendants().OfType<PasswordBox>().Single();
            var revealed = box.RevealPassword;
            var saveEnabled = dialog.SaveCommand.CanExecute(null);
            window.Close();

            page.Switches.Selected = page.Switches.Rows.Single(r => r.Name == "Gate house");
            var edit = page.Switches.NewDialog(page.Switches.Selected.Source);
            var editWindow = new SwitchWindow();
            editWindow.Attach(edit, _ => { });
            Oadm.Client.App.ApplyCrispText(editWindow);
            editWindow.Show();
            Pump();
            Capture(editWindow, "radius-switch-edit.png");
            var editTexts = Texts(editWindow);
            editWindow.Close();
            return (errors, revealed, saveEnabled, editTexts);
        }, CancellationToken.None);

        Assert.Contains("Another switch has this name.", checks.errors);
        Assert.Contains("Enter an IP address or a host name.", checks.errors);
        Assert.Contains("Use 8 characters or more.", checks.errors);
        Assert.True(checks.revealed);
        Assert.True(checks.saveEnabled);
        Assert.Contains("Leave empty to keep the current secret.", checks.editTexts);
        Assert.DoesNotContain(checks.editTexts, t => t?.StartsWith("RADIUS server for this switch", StringComparison.Ordinal) == true);
        Assert.Contains("Site", checks.editTexts);
    }

    [Fact]
    public async Task The_certificate_window_of_a_log_row_renders()
    {
        var texts = await HeadlessEntry.Session.Dispatch(() =>
        {
            var certificate = new CertificateWindow { DataContext = new CertificateDetails(CertificateDescriber.Describe(FakeRadiusBackend.CameraCertificate), "Certificate · 10.0.0.45 · IEEE 802.1X", DateTime.UtcNow) };
            Oadm.Client.App.ApplyCrispText(certificate);
            certificate.Show();
            Pump();
            Capture(certificate, "radius-log-certificate.png");
            var result = Texts(certificate);
            certificate.Close();
            return Task.FromResult(result);
        }, CancellationToken.None);

        Assert.Contains("Client authentication", string.Join(" ", texts));
    }

    [Fact]
    public async Task A_sessions_reread_and_the_log_choice_over_live_rows_render()
    {
        var server = new FakeRadiusBackend();
        var now = DateTime.UtcNow;
        server.Log = [.. Enumerable.Range(0, RadiusLimits.MaxLogPage).Select(i => FakeRadiusBackend.LogEntry(now.AddSeconds(-i), "AC:CC:8E:12:34:01", LogResults.Authenticated, null, null, null, "10.0.0.41"))];
        var ctx = new FakePageContext(server);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = Host(view, 1280, 900);
            window.Show();
            view.DataContext = vm;

            // The sessions read again (the page came back): the rows stay, the progress row shows.
            await vm.LoadAllAsync(CancellationToken.None);
            vm.Sessions.MarkStale();
            var hold = server.Hold(RadiusMethods.ListSessions);
            var reread = vm.Sessions.LoadAsync(CancellationToken.None);
            Pump();
            Capture(window, "radius-sessions-reread.png");
            var sessions = Texts(window);
            var rowsWhileReading = vm.Sessions.Rows.Count;
            hold.SetResult();
            await reread;

            // The log's chosen row stays chosen in the grid when a live row pushes the full page.
            Pump();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "SessionsGrid");
            var chosen = vm.Log.Rows[3];
            grid.SelectedItem = chosen;
            Pump();
            vm.Log.Add([FakeRadiusBackend.LogEntry(now.AddSeconds(1), "AC:CC:8E:12:34:02", LogResults.Failed, "Timed out", null, null, null)]);
            Pump();
            var kept = ReferenceEquals(grid.SelectedItem, chosen) && ReferenceEquals(vm.Log.Selected, chosen);
            window.Close();
            return (sessions, rowsWhileReading, kept);
        }, CancellationToken.None);

        Assert.Contains("Reading the sessions", checks.sessions);
        Assert.True(checks.rowsWhileReading > 0);
        Assert.True(checks.kept);
    }

    internal static Window Host(Control view, int width, int height)
    {
        // The real host page (title, ui:PageHeader subtitle, own cards), like in the client.
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(RadiusPluginInfo.PluginId, RadiusPluginInfo.DisplayName, view, hasOwnCards: true),
        };
        var window = new Window { Width = width, Height = height, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
    }

    internal static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    internal static void Capture(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            if (!string.IsNullOrEmpty(OutDir))
            {
                Directory.CreateDirectory(OutDir);
                frame.Save(Path.Combine(OutDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
    }

    private static List<string?> Texts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];
}

/// <summary>The Sessions grid with 10,000 rows renders only what it shows (virtualized) and stays fast.</summary>
[Collection(HeadlessSessions.Name)]
[Trait("Category", "Perf")]
public sealed class HeadlessPerfTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Ten_thousand_sessions_render_virtualized()
    {
        var server = new FakeRadiusBackend();
        var now = DateTime.UtcNow;
        server.Sessions.Clear();
        server.Sessions.AddRange(Enumerable.Range(0, RadiusPageLimits.MaxSessions).Select(i =>
            FakeRadiusBackend.Session(now.AddMilliseconds(-i), "AC:CC:8E:" + (i >> 16).ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + ":" + ((i >> 8) & 0xFF).ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + ":" + (i & 0xFF).ToString("X2", System.Globalization.CultureInfo.InvariantCulture),
                "receivingClientCertificate", "Receiving client certificate: fragment 3 of 5", guess: i % 2 == 0)));
        var ctx = new FakePageContext(server);
        var (elapsed, realized, batch) = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = HeadlessPageTests.Host(view, 1280, 900);
            window.Show();
            view.DataContext = vm;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await vm.Server.LoadAsync(CancellationToken.None); // the server runs: the cards show
            await vm.Sessions.LoadAsync(CancellationToken.None);
            HeadlessPageTests.Pump();
            var shown = watch.Elapsed;
            watch.Restart();
            vm.Sessions.Apply([.. server.Sessions.Take(500).Select(s => s with { State = SessionStates.Authenticated, StateText = "Authenticated with VLAN 20" })]);
            HeadlessPageTests.Pump();
            var applied = watch.Elapsed;
            var rows = window.GetVisualDescendants().OfType<Avalonia.Controls.DataGridRow>().Count(r => r.IsEffectivelyVisible);
            window.Close();
            return (shown, rows, applied);
        }, CancellationToken.None);

        output.WriteLine($"10,000 sessions: tab shown in {elapsed.TotalMilliseconds:F0} ms with {realized} rows realized; a batch of 500 rendered in {batch.TotalMilliseconds:F0} ms");
        Assert.InRange(realized, 1, 200);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"showing took {elapsed.TotalMilliseconds:F0} ms");
        Assert.True(batch < TimeSpan.FromSeconds(5), $"the batch took {batch.TotalMilliseconds:F0} ms");
    }
}
