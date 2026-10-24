using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
/// The page, its tabs and its dialogs rendered offscreen in the real host page with demo data (OADM_SCREENSHOT_DIR writes
/// PNGs); the checks are content and rules, never positions.
/// </summary>
[Collection(HeadlessSessions.Name)]
public sealed class HeadlessPageTests
{
    private static readonly string? OutDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");

    [Fact]
    public async Task Every_tab_renders_with_demo_data()
    {
        var server = new FakeRadiusBackend();
        var ctx = new FakePageContext(server);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = Host(view, 1280, 1000);
            window.Show();
            view.DataContext = vm; // after attaching: no live watch, deterministic content
            await vm.LoadServerAsync(CancellationToken.None);
            Pump();
            Capture(window, "radius-server.png");
            var server1 = Texts(window);

            vm.Settings.ShowAdvanced = true;
            vm.Settings.MinimumTls = SettingsViewModel.TlsVersions[1];
            vm.Settings.Port = "0";
            Pump();
            Capture(window, "radius-server-advanced.png");
            var advanced = Texts(window);
            vm.Settings.Port = "1812";

            await vm.ShowSessionsTabCommand.ExecuteAsync(null);
            Pump();
            Capture(window, "radius-sessions.png");
            var sessions = Texts(window);
            var italic = window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("guess") && t.FontStyle == Avalonia.Media.FontStyle.Italic);

            await vm.ShowLogTabCommand.ExecuteAsync(null);
            Pump();
            Capture(window, "radius-log.png");
            var log = Texts(window);

            await vm.ShowRevokedTabCommand.ExecuteAsync(null);
            Pump();
            Capture(window, "radius-revoked.png");
            var revoked = Texts(window);
            var redRevoke = window.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("danger") && b.IsEffectivelyVisible);
            window.Close();
            return (server1, advanced, sessions, italic, log, revoked, redRevoke);
        }, CancellationToken.None);

        Assert.Contains("802.1X server", checks.server1);
        Assert.Contains(RadiusPluginInfo.Subtitle, checks.server1);
        Assert.Contains("Running on port 1812", checks.server1);
        Assert.Contains("Wrong secret", checks.server1);
        Assert.Contains("Advanced", checks.server1);
        Assert.DoesNotContain("TLS_AES_128_GCM_SHA256", checks.server1); // Advanced starts collapsed
        Assert.Contains(SettingsViewModel.OldTlsHint, checks.advanced);
        Assert.Contains("TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA", checks.advanced);
        Assert.Contains("Use 1 to 65535.", checks.advanced);
        Assert.Contains("Sending server certificate: fragment 2 of 4", checks.sessions);
        Assert.True(checks.italic);
        Assert.Contains("Delete all", checks.log);
        Assert.Contains(checks.log, t => t?.Contains("Certificate of a device OADM does not manage (ACCC8E123407)", StringComparison.Ordinal) == true);
        Assert.Contains("Key compromised", checks.revoked);
        Assert.True(checks.redRevoke);
        Assert.All(checks.server1.Concat(checks.log), t => Assert.False(t?.EndsWith("...", StringComparison.Ordinal) == true || t?.EndsWith('…') == true, t));
    }

    [Fact]
    public async Task A_group_user_gets_sessions_and_log_without_changes()
    {
        var server = new FakeRadiusBackend();
        var ctx = new FakePageContext(server) { AllDevices = false };
        var texts = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new RadiusView();
            using var vm = new RadiusViewModel(ctx);
            var window = Host(view, 1280, 720);
            window.Show();
            view.DataContext = vm;
            await vm.ShowLogTabCommand.ExecuteAsync(null);
            Pump();
            Capture(window, "radius-group-user.png");
            var visibleTabs = window.GetVisualDescendants().OfType<PageTab>().Where(t => t.IsEffectivelyVisible).Select(t => t.Title).ToList();
            var deleteAll = window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Classes.Contains("danger"));
            window.Close();
            return (visibleTabs, deleteAll);
        }, CancellationToken.None);

        Assert.Equal(["Sessions", "Log"], texts.visibleTabs);
        Assert.False(texts.deleteAll);
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
        Assert.Contains("Use 16 characters or more.", checks.errors);
        Assert.True(checks.revealed);
        Assert.True(checks.saveEnabled);
        Assert.Contains("Leave empty to keep the current secret.", checks.editTexts);
        Assert.Contains("RADIUS server for this switch: 192.168.1.5, port 1812", checks.editTexts);
        Assert.Contains("Site", checks.editTexts);
    }

    [Fact]
    public async Task The_certificate_choice_the_revoke_confirmation_and_the_certificate_window_render()
    {
        var server = new FakeRadiusBackend();
        var ctx = new FakePageContext(server);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var picker = new RevokePickerViewModel(ctx);
            var window = new RevokePickerWindow();
            window.Attach(picker, _ => { });
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            await picker.LoadAsync();
            Pump();
            var list = window.GetVisualDescendants().OfType<IssuedCertificateList>().Single();
            list.Table.SelectedItems.Add(picker.Rows[0]);
            list.Table.SelectedItems.Add(picker.Rows[1]);
            Pump();
            Capture(window, "radius-revoke-choice.png");
            var texts = Texts(window);
            var rows = list.Table.ItemsSource?.Cast<object>().Count();
            var chosen = picker.Selected.Count;
            window.Close();

            var confirm = new MessageWindow
            {
                Heading = IssuedCertificateTexts.RevokeTitle([.. picker.Rows.Take(2)]),
                Message = IssuedCertificateTexts.ConfirmMessage,
                ConfirmText = "Revoke",
                ChoiceLabel = "Reason",
                Choices = [.. IssuedCertificateTexts.Reasons.Select(CertificateRevocation.ReasonText)],
                IsDanger = true,
            };
            Oadm.Client.App.ApplyCrispText(confirm);
            confirm.Show();
            Pump();
            Capture(confirm, "radius-revoke-confirm.png");
            confirm.Close();

            var certificate = new CertificateWindow { DataContext = new CertificateDetails(CertificateDescriber.Describe(FakeRadiusBackend.CameraCertificate), "Certificate · 10.0.0.45 · IEEE 802.1X", DateTime.UtcNow) };
            Oadm.Client.App.ApplyCrispText(certificate);
            certificate.Show();
            Pump();
            Capture(certificate, "radius-log-certificate.png");
            var certificateTexts = Texts(certificate);
            certificate.Close();
            return (texts, rows, chosen, certificateTexts);
        }, CancellationToken.None);

        Assert.Contains("Revoke certificates", checks.texts);
        Assert.Contains("2 certificates chosen", checks.texts);
        Assert.Equal(12, checks.rows);
        Assert.Equal(2, checks.chosen); // the grid's selection reached the view model
        Assert.Contains("Client authentication", string.Join(" ", checks.certificateTexts));
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
            await vm.ShowSessionsTabCommand.ExecuteAsync(null);
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
