using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Plugins.Est.Client;
using Oadm.Plugins.Est.Serving;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>The page against the running plugin (fake core context with a fake CA): form, errors below fields, lists, revoke.</summary>
public sealed class PageTests : IAsyncLifetime
{
    private readonly FakeCoreContext _core = new() { Pro = FakeProLicense.ActivePro() };
    private readonly FakeAuthority _ca = new();
    private readonly EstPlugin _plugin = new();
    private PageContext _ctx = null!;

    public async Task InitializeAsync()
    {
        _core.FakeServices.Offer<IDeviceCertificateAuthority>(_ca);
        await _plugin.StartAsync(_core, CancellationToken.None);
        _ctx = new PageContext(_plugin);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        _ca.Dispose();
    }

    [Fact]
    public async Task The_form_shows_the_two_ready_made_profiles_and_errors_below_the_fields()
    {
        using var vm = new EstViewModel(_ctx);
        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal("Running on port 9454", vm.StatusText);
        Assert.True(vm.StatusOk);
        Assert.Equal(["https", "dot1x"], vm.Profiles.Select(p => p.Name));
        Assert.Equal(["HTTPS", "IEEE 802.1X"], vm.Profiles.Select(p => p.Use.Label));
        Assert.True(vm.SaveCommand.CanExecute(null));

        vm.Port = "80";
        Assert.Equal("Use 1025 to 65535.", vm.ErrorOf(nameof(EstViewModel.Port)));
        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.Port = "9455";

        vm.Profiles[1].Use = EstViewModel.Uses[0];
        Assert.Equal("Another profile has this use.", vm.Profiles[1].ErrorOf(nameof(ProfileRowViewModel.Use)));
        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.Profiles[1].Use = EstViewModel.Uses[3];
        vm.Profiles[1].Name = "rtsps";

        vm.AddProfileCommand.Execute(null);
        Assert.Equal(3, vm.Profiles.Count);
        Assert.Equal(ProfileUse.Dot1x, vm.Profiles[2].Use.Value); // the first use not taken

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(vm.ActionError);
        Assert.Equal("Running on port 9455", vm.StatusText);
        Assert.Equal(["https", "rtsps", "dot1x"], _plugin.Service!.Store.Config.Profiles.Select(p => p.Name));
    }

    [Fact]
    public async Task Certificates_are_listed_and_revoked_after_a_confirmation()
    {
        var device = new FakeDevice { Address = "10.0.0.48" };
        _plugin.Service!.Store.AddCertificate(new IssuedCertificate
        {
            SerialNumber = "0A1B", DeviceId = device.Id, Device = device.Address, Profile = "https",
            IssuedUtc = DateTime.UtcNow, NotAfterUtc = DateTime.UtcNow.AddDays(365),
        });
        _plugin.Service.Store.AddEnrollment(new Enrollment { TimeUtc = DateTime.UtcNow, DeviceId = device.Id, Device = "10.0.0.48", Profile = "https", Ok = true, Reason = "Valid until 2027-10-22" });
        using var vm = new EstViewModel(_ctx);
        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal("1 certificate", vm.CertificateSummary);
        var row = Assert.Single(vm.Certificates);
        Assert.Equal(("10.0.0.48", "Valid", true), (row.Device, row.State, row.CanRevoke));
        Assert.Equal("Enrolled", Assert.Single(vm.Enrollments).Result);

        _ctx.Confirm = false;
        await vm.RevokeCommand.ExecuteAsync(row);
        Assert.Equal("Valid", vm.Certificates[0].State);
        _ctx.Confirm = true;
        await vm.RevokeCommand.ExecuteAsync(row);
        Assert.Equal("Revoked", vm.Certificates[0].State);
        Assert.True(_plugin.Service.Store.IsRevoked(device.Id, "https"));
        Assert.StartsWith("Revoke the https certificate of 10.0.0.48?", _ctx.LastConfirm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_renders()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var store = _plugin.Service!.Store;
        var now = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            var address = $"10.0.0.{40 + i}";
            store.AddCertificate(new IssuedCertificate
            {
                SerialNumber = $"0{i}AB", DeviceId = Guid.NewGuid(), Device = address, Profile = i % 2 == 0 ? "https" : "dot1x",
                IssuedUtc = now.AddDays(-i), NotAfterUtc = now.AddDays(365 - i), RevokedUtc = i == 3 ? now : null,
            });
            store.AddEnrollment(new Enrollment
            {
                TimeUtc = now.AddMinutes(-i * 7), Device = address, Profile = i % 2 == 0 ? "https" : "dot1x",
                Kind = i > 2 ? EnrollmentKind.Renewal : EnrollmentKind.New, Ok = i != 1,
                Reason = i == 1 ? EstRefusals.Revoked : "Valid until 2027-10-22",
            });
        }

        store.AddEnrollment(new Enrollment { TimeUtc = now, Device = "ACCC8E000001", Profile = "https", Reason = EstRefusals.NotManaged });

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        await session.Dispatch(async () =>
        {
            var view = new EstView();
            using var vm = new EstViewModel(_ctx);
            var window = Host(view);
            window.Show();
            view.DataContext = vm; // after attaching: no live watch, deterministic content
            await vm.LoadAsync(CancellationToken.None);
            Pump();
            Capture(window, outDir, "est-page.png");

            vm.Port = "80";
            vm.Profiles[0].Name = "a b";
            Pump();
            Capture(window, outDir, "est-page-errors.png");
            window.Close();
        }, CancellationToken.None);
        GC.KeepAlive(session);
    }

    [Fact]
    public void The_setup_dialog_offers_the_8021x_choice_only_for_cameras_with_8021x_on()
    {
        var without = new EstSetupViewModel([new FakeDevice(), new FakeDevice()]);
        Assert.False(without.ShowDot1x);
        without.ReplaceDot1x = true; // hidden: it never counts
        Assert.False(SetupPayload.Parse(without.Payload).ReplaceDot1x);

        var with = new EstSetupViewModel([new FakeDevice { Dot1xEnabled = true }, new FakeDevice { Dot1xEnabled = true }, new FakeDevice()]);
        Assert.True(with.ShowDot1x);
        Assert.Equal("Also replace the network access certificate of 2 cameras with IEEE 802.1X on", with.Dot1xText);
        Assert.False(SetupPayload.Parse(with.Payload).ReplaceDot1x); // off by default
        with.ReplaceDot1x = true;
        Assert.True(SetupPayload.Parse(with.Payload).ReplaceDot1x);
        Assert.StartsWith("Set up certificate enrollment on 3 devices?", with.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_dialog_renders()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        await session.Dispatch(() =>
        {
            var vm = new EstSetupViewModel([new FakeDevice { Dot1xEnabled = true }, new FakeDevice()]);
            var window = new EstSetupWindow { DataContext = vm };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            Pump();
            Capture(window, outDir, "est-setup-dialog.png");
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
        GC.KeepAlive(session);
    }

    private static Window Host(Control view)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(EstPluginInfo.PluginId, EstPluginInfo.DisplayName, view),
        };
        var window = new Window { Width = 1280, Height = 960, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (string.IsNullOrEmpty(outDir))
        {
            frame.Dispose();
            return;
        }

        Directory.CreateDirectory(outDir);
        using (frame)
        {
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    /// <summary>The page's view of the plugin: calls go straight to it.</summary>
    private sealed class PageContext(EstPlugin plugin) : ICorePluginClientContext
    {
        public bool Confirm { get; set; } = true;

        public string? LastConfirm { get; private set; }

        public IReadOnlyList<IDeviceInfo> Devices => [];

        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => plugin.InvokeAsync(method, payloadJson, ct);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            LastConfirm = message;
            return Task.FromResult(Confirm);
        }
    }
}

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.Est.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}
