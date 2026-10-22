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

/// <summary>The page against the running plugin (fake core context with a fake CA): form, errors below fields, lists.</summary>
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
        _ctx = new PageContext(_plugin, _core);
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
    public async Task Only_certificates_the_cameras_did_not_renew_are_listed()
    {
        var device = new FakeDevice { Address = "10.0.0.48" };
        var other = new FakeDevice { Address = "10.0.0.49" };
        var now = DateTime.UtcNow;
        var store = _plugin.Service!.Store;
        store.AddCertificate(new IssuedCertificate { SerialNumber = "01", DeviceId = device.Id, Device = device.Address, Profile = "https", IssuedUtc = now.AddDays(-200), NotAfterUtc = now.AddDays(165) });
        store.AddCertificate(new IssuedCertificate { SerialNumber = "02", DeviceId = other.Id, Device = other.Address, Profile = "https", IssuedUtc = now, NotAfterUtc = now.AddDays(365) });
        store.MarkNotRenewed("01", now);
        store.AddEnrollment(new Enrollment { TimeUtc = now, DeviceId = device.Id, Device = "10.0.0.48", Profile = "https", Ok = true, Reason = "Valid until 2027-10-22" });
        using var vm = new EstViewModel(_ctx);
        await vm.LoadAsync(CancellationToken.None);

        Assert.True(vm.HasNotRenewed);
        Assert.Equal("1 certificate", vm.NotRenewedSummary);
        var row = Assert.Single(vm.NotRenewed);
        Assert.Equal(("10.0.0.48", "https", "Not renewed"), (row.Device, row.Profile, row.State));
        Assert.Equal("Enrolled", Assert.Single(vm.Enrollments).Result);
    }

    [Fact]
    public async Task Five_thousand_enrollments_are_searched_and_deleted_after_a_confirmation()
    {
        var service = _plugin.Service!;
        await service.SaveAsync(new SaveRequest(service.Store.Config with { EnrollmentsKept = 5000 }), CancellationToken.None);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5000; i++)
        {
            service.Store.AddEnrollment(new Enrollment
            {
                TimeUtc = now.AddSeconds(i), Device = $"10.1.{i / 250}.{i % 250}", Profile = i % 2 == 0 ? "https" : "dot1x",
                Kind = i % 3 == 0 ? EnrollmentKind.Renewal : EnrollmentKind.New, Ok = i % 10 != 0,
                Reason = i % 10 == 0 ? EstRefusals.NotManaged : "Valid until 2027-10-22",
            });
        }

        using var vm = new EstViewModel(_ctx);
        await vm.LoadAsync(CancellationToken.None);
        Assert.Equal(5000, vm.Enrollments.Count);
        Assert.Null(vm.EnrollmentsEmptyText);

        vm.EnrollmentSearch = "does not manage";
        Assert.Equal(500, vm.Enrollments.Count);
        vm.EnrollmentSearch = "10.1.3.249";
        Assert.Equal("10.1.3.249", Assert.Single(vm.Enrollments).Device);
        vm.EnrollmentSearch = "nothing like this";
        Assert.Empty(vm.Enrollments);
        Assert.Equal("No enrollment matches the search.", vm.EnrollmentsEmptyText);
        vm.EnrollmentSearch = string.Empty;
        Assert.Equal(5000, vm.Enrollments.Count);

        _ctx.Confirm = false;
        await vm.ClearEnrollmentsCommand.ExecuteAsync(null);
        Assert.Equal(5000, vm.Enrollments.Count);
        _ctx.Confirm = true;
        await vm.ClearEnrollmentsCommand.ExecuteAsync(null);
        Assert.Empty(vm.Enrollments);
        Assert.Empty(service.Store.Enrollments());
        Assert.False(vm.ClearEnrollmentsCommand.CanExecute(null));
        Assert.Equal("No enrollments yet. Set up a camera with Certificate enrollment.", vm.EnrollmentsEmptyText);
        Assert.StartsWith("Delete every entry of the enrollments list?", _ctx.LastConfirm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_enrollments_appear_live_under_the_search_and_delete_all_of_another_client_empties_the_list()
    {
        var service = _plugin.Service!;
        service.Store.AddEnrollment(new Enrollment { TimeUtc = DateTime.UtcNow, Device = "10.0.0.48", Profile = "https", Ok = true, Reason = "Valid until 2027-10-22" });
        using var vm = new EstViewModel(_ctx);
        vm.Activate();
        await Eventually.TrueAsync(() => vm.Enrollments.Count == 1, "the page read the list");
        while (_core.Published.Reader.TryRead(out _))
        {
            // events of the start: the page already read the state
        }

        vm.EnrollmentSearch = "10.0.0.49";
        _core.Publish(EstMethods.EnrollmentsTopic, EstJson.Serialize(new EnrollmentList(
        [
            new Enrollment { TimeUtc = DateTime.UtcNow, Device = "10.0.0.49", Profile = "https", Ok = true, Reason = "Valid until 2027-10-22" },
            new Enrollment { TimeUtc = DateTime.UtcNow, Device = "10.0.0.50", Profile = "https", Ok = true, Reason = "Valid until 2027-10-22" },
        ])));
        await Eventually.TrueAsync(() => vm.Enrollments.Count == 1, "the matching new row");
        Assert.Equal("10.0.0.49", vm.Enrollments[0].Device);
        vm.EnrollmentSearch = string.Empty;
        Assert.Equal(3, vm.Enrollments.Count);

        service.ClearEnrollments(); // Delete all on another client
        await Eventually.TrueAsync(() => vm.Enrollments.Count == 0, "the list emptied");
        vm.Deactivate();
    }

    [Fact]
    public async Task Without_not_renewed_certificates_the_section_is_hidden()
    {
        var now = DateTime.UtcNow;
        _plugin.Service!.Store.AddCertificate(new IssuedCertificate { SerialNumber = "02", DeviceId = Guid.NewGuid(), Device = "10.0.0.49", Profile = "https", IssuedUtc = now, NotAfterUtc = now.AddDays(365) });
        using var vm = new EstViewModel(_ctx);
        await vm.LoadAsync(CancellationToken.None);

        Assert.False(vm.HasNotRenewed);
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
                IssuedUtc = now.AddDays(-i), NotAfterUtc = now.AddDays(365 - i),
            });
            store.AddEnrollment(new Enrollment
            {
                TimeUtc = now.AddMinutes(-i * 7), Device = address, Profile = i % 2 == 0 ? "https" : "dot1x",
                Kind = i > 2 ? EnrollmentKind.Renewal : EnrollmentKind.New, Ok = i != 1,
                Reason = i == 1 ? EstRefusals.NotManaged : "Valid until 2027-10-22",
            });
        }

        store.AddEnrollment(new Enrollment { TimeUtc = now, Device = "ACCC8E000001", Profile = "https", Reason = EstRefusals.NotManaged });
        var renewing = Guid.NewGuid();
        store.AddCertificate(new IssuedCertificate { SerialNumber = "10AB", DeviceId = renewing, Device = "10.0.0.50", Profile = "https", IssuedUtc = now.AddDays(-400), NotAfterUtc = now.AddDays(-35) });
        store.AddCertificate(new IssuedCertificate { SerialNumber = "11AB", DeviceId = renewing, Device = "10.0.0.50", Profile = "https", IssuedUtc = now.AddDays(-340), NotAfterUtc = now.AddDays(25) });
        store.MarkNotRenewed("11AB", now);

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
    public async Task The_dialog_lists_the_chosen_cameras_profiles_and_removes_one_after_a_confirmation()
    {
        var first = new FakeDevice { Address = "10.0.0.48", Model = "AXIS P3265-V" };
        var second = new FakeDevice { Address = "10.0.0.49", Model = "AXIS M3215-LVE" };
        var cameras = new DialogContext();
        cameras.Profiles[first.Id] = new CameraProfileList(true,
        [
            new CameraProfileInfo("oadm_https", ["WEBSERVER"], "10.0.0.17:9454/.well-known/est/https", "ENROLLED"),
            new CameraProfileInfo("corp_dot1x", ["NETAUTH"], "pki.example.com:443/.well-known/est/dot1x", "CONNECTED"),
        ]);
        cameras.Profiles[second.Id] = new CameraProfileList(false, []);
        var vm = new EstSetupViewModel([first, second], cameras);
        string? payload = null;
        vm.CloseRequested += (_, ok) => payload = ok ? vm.Payload : null;

        await vm.LoadAsync();
        Assert.Equal(["oadm_https", "corp_dot1x"], vm.Profiles.Select(p => p.Id));
        Assert.Equal([("HTTPS", "OADM", "Enrolled"), ("IEEE 802.1X", "Other", "No certificate")], vm.Profiles.Select(p => (p.Uses, p.SetUpBy, p.Status)));
        Assert.Equal("Set up and Remove apply to all 2 selected cameras.", vm.ScopeText);
        Assert.False(vm.RemoveCommand.CanExecute(null));

        vm.SelectedDevice = vm.Devices[1];
        await Eventually.TrueAsync(() => !vm.IsLoading && vm.HasError, "the second camera was read");
        Assert.Equal("The camera has no certificate enrollment. Needs AXIS OS 12.10 or later.", vm.ErrorMessage);
        vm.SelectedDevice = vm.Devices[0];
        await Eventually.TrueAsync(() => vm.Profiles.Count == 2, "the first camera again, from the cache");
        Assert.Equal(1, cameras.Queries[first.Id]);

        // Back and forth while a read runs: the list shows the camera once.
        vm.SelectedDevice = vm.Devices[1];
        vm.SelectedDevice = vm.Devices[0];
        await Eventually.TrueAsync(() => !vm.IsLoading, "the reads ended");
        Assert.Equal(2, vm.Profiles.Count);

        vm.SelectedProfile = vm.Profiles[1];
        vm.RemoveCommand.Execute(null);
        Assert.True(vm.ConfirmRemoveVisible);
        Assert.Equal("Remove corp_dot1x from all 2 selected cameras? OADM did not set it up: the cameras stop renewing the certificates of that EST server. Cameras with IEEE 802.1X on drop off the network when that certificate expires.", vm.ConfirmRemoveText);
        vm.CancelRemoveCommand.Execute(null);
        Assert.Null(payload);

        vm.RemoveCommand.Execute(null);
        Assert.EndsWith("Cameras with IEEE 802.1X on drop off the network when that certificate expires.", vm.ConfirmRemoveText, StringComparison.Ordinal);

        // Another row closes the confirmation: Remove never takes a profile the text did not name.
        vm.SelectedProfile = vm.Profiles[0];
        Assert.False(vm.ConfirmRemoveVisible);
        vm.ConfirmRemoveCommand.Execute(null);
        Assert.Null(payload);

        vm.SelectedProfile = vm.Profiles[1];
        vm.RemoveCommand.Execute(null);
        vm.ConfirmRemoveCommand.Execute(null);
        var remove = SetupPayload.Read(payload)!;
        Assert.Equal((SetupAction.Remove, "corp_dot1x"), (remove.Action, Assert.Single(remove.Profiles)));
    }

    [Fact]
    public void Set_up_offers_the_8021x_choice_only_for_cameras_with_8021x_on()
    {
        string? payload = null;
        var without = new EstSetupViewModel([new FakeDevice { Dot1xEnabled = false }, new FakeDevice { Dot1xEnabled = false }]);
        without.CloseRequested += (_, _) => payload = without.Payload;
        Assert.False(without.ShowDot1x);
        without.ReplaceDot1x = true; // hidden: it never counts
        without.OkCommand.Execute(null);
        Assert.Equal((SetupAction.Setup, false), (SetupPayload.Read(payload)!.Action, SetupPayload.Read(payload)!.ReplaceDot1x));

        Assert.True(new EstSetupViewModel([new FakeDevice()]).ShowDot1x); // not read yet: counts as on
        var with = new EstSetupViewModel([new FakeDevice { Dot1xEnabled = true }, new FakeDevice { Dot1xEnabled = true }, new FakeDevice { Dot1xEnabled = false }]);
        with.CloseRequested += (_, _) => payload = with.Payload;
        Assert.True(with.ShowDot1x);
        Assert.Equal("Also replace the network access certificate of 2 cameras with IEEE 802.1X on", with.Dot1xText);
        with.OkCommand.Execute(null);
        Assert.False(SetupPayload.Read(payload)!.ReplaceDot1x); // off by default
        with.ReplaceDot1x = true;
        with.OkCommand.Execute(null);
        Assert.True(SetupPayload.Read(payload)!.ReplaceDot1x);
    }

    [Fact]
    public async Task Setup_dialog_renders()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var first = new FakeDevice { Address = "10.0.0.48", Model = "AXIS P3265-V", Dot1xEnabled = true };
        var second = new FakeDevice { Address = "10.0.0.49", Model = "AXIS M3215-LVE", Dot1xEnabled = false };
        var empty = new FakeDevice { Address = "10.0.0.50", Model = "AXIS Q1656", Dot1xEnabled = false };
        var cameras = new DialogContext();
        cameras.Profiles[first.Id] = new CameraProfileList(true,
        [
            new CameraProfileInfo("oadm_https", ["WEBSERVER"], "10.0.0.17:9454/.well-known/est/https", "ENROLLED"),
            new CameraProfileInfo("corp_dot1x", ["NETAUTH"], "pki.example.com:443/.well-known/est/dot1x", "UNCONNECTED"),
        ]);
        cameras.Profiles[second.Id] = new CameraProfileList(false, []);
        cameras.Profiles[empty.Id] = new CameraProfileList(true, []);
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        await session.Dispatch(async () =>
        {
            var vm = new EstSetupViewModel([first, second, empty], cameras);
            var window = new EstSetupWindow { DataContext = vm };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            await vm.LoadAsync();
            Pump();
            Capture(window, outDir, "est-setup-dialog.png");

            vm.SelectedProfile = vm.Profiles[1];
            vm.RemoveCommand.Execute(null);
            Pump();
            Capture(window, outDir, "est-setup-dialog-remove.png");

            vm.CancelRemoveCommand.Execute(null);
            vm.SelectedDevice = vm.Devices[2];
            await Eventually.TrueAsync(() => !vm.IsLoading && vm.IsEmpty, "the empty camera");
            Pump();
            Capture(window, outDir, "est-setup-dialog-empty.png");

            vm.SelectedDevice = vm.Devices[1];
            await Eventually.TrueAsync(() => !vm.IsLoading && vm.HasError, "the camera without enrollment");
            Pump();
            Capture(window, outDir, "est-setup-dialog-unsupported.png");
            window.Close();
        }, CancellationToken.None);
        GC.KeepAlive(session);
    }

    /// <summary>The dialog's view of the server: the cameras' profiles as the task's query answers them.</summary>
    private sealed class DialogContext : ITaskDialogContext
    {
        public Dictionary<Guid, CameraProfileList> Profiles { get; } = [];

        public Dictionary<Guid, int> Queries { get; } = [];

        public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
        {
            Assert.Equal(Oadm.Plugins.Est.Tasks.EstTaskIds.ListProfilesMethod, method);
            Queries[deviceId] = Queries.GetValueOrDefault(deviceId) + 1;
            return Task.FromResult<string?>(EstJson.Serialize(Profiles[deviceId]));
        }

        public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
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
    private sealed class PageContext(EstPlugin plugin, FakeCoreContext core) : ICorePluginClientContext
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

        /// <summary>The events the plugin publishes, as the host streams them to the page.</summary>
        public IAsyncEnumerable<PluginEvent> WatchEventsAsync(CancellationToken ct) => core.Published.Reader.ReadAllAsync(ct);

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
