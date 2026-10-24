using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Plugins.Est.Client;
using Oadm.Plugins.Est.Serving;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Licensing;
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
        await _core.Settings.SetAsync(Oadm.Plugins.Pki.Servers.ServerSwitch<EstService>.SettingKey, "true", CancellationToken.None); // turned on
        await _plugin.StartAsync(_core, CancellationToken.None);
        _ctx = new PageContext(_plugin, _core);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        _ca.Dispose();
    }

    /// <summary>A CA and a camera certificate it signed, like the OADM CA's (ECC P-256, server authentication, IP and DNS names).</summary>
    private static (byte[] Leaf, string Serial) CameraCertificate(DateTimeOffset now)
    {
        using var caKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var caRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=OADM Root CA acs, O=OADM", caKey, System.Security.Cryptography.HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=10.0.0.48", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        san.AddIpAddress(System.Net.IPAddress.Parse("10.0.0.48"));
        san.AddDnsName("axis-b8a44f631339.local");
        request.CertificateExtensions.Add(san.Build());
        using var leaf = request.Create(ca, now.AddMinutes(-5), now.AddDays(365), [0x7D, 0xFD, 0x05, 0x49]);
        return (leaf.RawData, leaf.SerialNumber);
    }

    [Fact]
    public async Task Sign_in_with_shows_pki_cas_errors_below_the_field_and_saves_the_choice()
    {
        _ca.ListDefaults();
        using var vm = new EstViewModel(_ctx);
        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(["Axis device ID", "OADM Test CA, for renewals"], vm.SignIn.Select(s => s.Name)); // the CA's name, as on the PKI page
        Assert.All(vm.SignIn, s => Assert.True(s.IsChecked));
        Assert.Equal("OADM Test CA", vm.IssuingCa);
        Assert.Null(vm.SignInError);

        foreach (var row in vm.SignIn)
        {
            row.IsChecked = false;
        }

        Assert.Equal("Choose at least one.", vm.SignInError);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.SignIn[0].IsChecked = true;
        Assert.Null(vm.SignInError);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal([CertificateAuthorityInfo.AxisDeviceIdId], _plugin.Service!.Store.Config.SignInWith);
        Assert.Equal([true, false], vm.SignIn.Select(s => s.IsChecked));
    }

    [Fact]
    public async Task An_enrollment_keeps_its_certificate_and_view_certificate_shows_it_in_plain_names()
    {
        var now = DateTimeOffset.UtcNow;
        var (der, serial) = CameraCertificate(now);
        var store = _plugin.Service!.Store;
        store.AddEnrollment(new Enrollment { TimeUtc = now.UtcDateTime, Device = "10.0.0.48", Profile = "https", Ok = true, Reason = "Valid until 2027-10-22", SerialNumber = serial }, der);
        store.AddEnrollment(new Enrollment { TimeUtc = now.UtcDateTime, Device = "10.0.0.49", Profile = "https", Ok = false, Reason = EstRefusals.NotManaged });
        using var vm = new EstViewModel(_ctx);
        await vm.LoadAsync(CancellationToken.None);

        vm.SelectedEnrollment = vm.Enrollments.Single(e => e.Refused);
        Assert.False(vm.ViewCertificateCommand.CanExecute(null)); // a refusal issued nothing
        vm.SelectedEnrollment = vm.Enrollments.Single(e => e.Ok);
        CertificateDetails? shown = null;
        vm.CertificateRequested += (_, c) => shown = c;
        await store.FlushAsync(CancellationToken.None); // read back from the records
        await vm.ViewCertificateCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal("Certificate · 10.0.0.48 · https", shown.Title);
        Assert.Equal("Valid, 364 days left", shown.ValidityText);
        Assert.True(shown.IsValid);
        Assert.Equal([new CertificateNamePart("Common name", "10.0.0.48")], shown.IssuedTo);
        Assert.Equal([new CertificateNamePart("Common name", "OADM Root CA acs"), new CertificateNamePart("Organization", "OADM")], shown.IssuedBy);
        Assert.Equal(("ECC P-256", "SHA-256 with ECDSA", "3", "7D FD 05 49"), (shown.Key, shown.Signature, shown.Version, shown.SerialNumber));
        Assert.Equal("Digital signature, Server authentication", shown.Uses);
        Assert.Equal("IP 10.0.0.48" + Environment.NewLine + "DNS axis-b8a44f631339.local", shown.Names);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(der)), shown.Fingerprint.Replace(" ", "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_certificate_goes_with_its_enrollment()
    {
        var now = DateTimeOffset.UtcNow;
        var (der, serial) = CameraCertificate(now);
        var service = _plugin.Service!;
        await service.SaveAsync(new SaveRequest(service.Store.Config with { EnrollmentsKept = 100 }), CancellationToken.None);
        service.Store.AddEnrollment(new Enrollment { TimeUtc = now.UtcDateTime, Device = "10.0.0.48", Profile = "https", Ok = true, SerialNumber = serial }, der);
        await service.Store.FlushAsync(CancellationToken.None);
        Assert.Equal(der, await service.Store.IssuedAsync(serial, CancellationToken.None));

        // 100 newer enrollments push it out of the list: its certificate goes too.
        for (var i = 0; i < 100; i++)
        {
            service.Store.AddEnrollment(new Enrollment { TimeUtc = now.UtcDateTime, Device = "10.0.0.49", Profile = "https", Ok = false, Reason = EstRefusals.NotManaged });
        }

        Assert.Null(await service.Store.IssuedAsync(serial, CancellationToken.None));
        await service.Store.FlushAsync(CancellationToken.None);
        Assert.Null(await _core.PluginRecords.GetAsync(EstStore.IssuedKey, serial, CancellationToken.None));
        var gone = await Assert.ThrowsAsync<ArgumentException>(() => _plugin.InvokeAsync(EstMethods.GetCertificate, EstJson.Serialize(new CertificateQuery(serial)), CancellationToken.None));
        Assert.Equal(EstMethods.CertificateGone, gone.Message);

        // Delete all: every certificate goes.
        var (der2, serial2) = CameraCertificate(now);
        service.Store.AddEnrollment(new Enrollment { TimeUtc = now.UtcDateTime, Device = "10.0.0.48", Profile = "https", Ok = true, SerialNumber = serial2 }, der2);
        await service.Store.FlushAsync(CancellationToken.None);
        service.ClearEnrollments();
        Assert.Null(await service.Store.IssuedAsync(serial2, CancellationToken.None));
        await service.Store.FlushAsync(CancellationToken.None);
        Assert.Empty(await _core.PluginRecords.ListAsync(EstStore.IssuedKey, CancellationToken.None));
    }

    [Fact]
    public async Task The_certificate_window_renders()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var now = DateTimeOffset.UtcNow;
        var (der, _) = CameraCertificate(now);
        var row = new EnrollmentRow(new Enrollment { TimeUtc = now.UtcDateTime, Device = "10.0.0.48", Profile = "https", Ok = true });
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        await session.Dispatch(() =>
        {
            var window = new CertificateWindow { DataContext = new CertificateDetails(CertificateDescriber.Describe(der), $"Certificate · {row.Device} · {row.Profile}", now.UtcDateTime) };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            Pump();
            Capture(window, outDir, "est-certificate.png");
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Issued by", texts);
            Assert.Contains("OADM Root CA acs", texts);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
        GC.KeepAlive(session);
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
    public async Task Turned_off_the_page_shows_only_general_with_turn_on_which_waits_for_oadm_pro()
    {
        // docs/spec/pki.md "One plugin": the server is off by default and turned on on its own page.
        await _plugin.InvokeAsync(Oadm.Plugins.Pki.Servers.ServerSwitchMethods.TurnOff, null, CancellationToken.None);
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var pro = (FakeProLicense)_core.Pro!;
        static List<string?> Texts(Window window) =>
            [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];
        static Button? Button(Window window, string label) => window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label));

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var checks = await session.Dispatch(async () =>
        {
            var view = new EstView();
            using var vm = new EstViewModel(_ctx);
            var window = Host(view);
            window.Show();
            view.DataContext = vm; // after attaching: no live watch, deterministic content
            await vm.LoadServerAsync(CancellationToken.None);
            Pump();
            Capture(window, outDir, "est-page-off.png");
            var off = (Texts(window), Button(window, "Turn on")?.IsEffectivelyEnabled, Button(window, "Turn off") is null, vm.IsLoading);

            pro.Current = ProLicenseInfo.None;
            await vm.Server.LoadAsync(CancellationToken.None);
            Pump();
            Capture(window, outDir, "est-page-needs-pro.png");
            var turnOn = Button(window, "Turn on");
            var noPro = (Texts(window), turnOn?.IsEffectivelyEnabled, ToolTip.GetTip(turnOn!) as string);

            pro.Current = FakeProLicense.ActivePro().Current;
            await vm.Server.LoadAsync(CancellationToken.None);
            await vm.Server.TurnOnCommand.ExecuteAsync(null);
            await vm.LoadAsync(CancellationToken.None); // the live watch would read the cards (not started here)
            Pump();
            var on = (Texts(window), Button(window, "Turn off") is not null, Button(window, "Turn on") is null);
            window.Close();
            return (off, noPro, on);
        }, CancellationToken.None);
        GC.KeepAlive(session);

        Assert.Contains("General", checks.off.Item1);
        Assert.Contains(Oadm.Plugins.Pki.Servers.ServerSwitchMethods.OffText, checks.off.Item1);
        Assert.DoesNotContain("Profiles", checks.off.Item1);
        Assert.DoesNotContain("Enrollments", checks.off.Item1);
        Assert.True(checks.off.Item2);
        Assert.True(checks.off.Item3);
        Assert.False(checks.off.Item4);

        Assert.Contains(ProLicenseTexts.NeedsPro, checks.noPro.Item1);
        Assert.False(checks.noPro.Item2); // disabled without OADM PRO, the tooltip says why
        Assert.Equal(ProLicenseTexts.NeedsPro, checks.noPro.Item3);

        Assert.Contains("Profiles", checks.on.Item1);
        Assert.Contains("Enrollments", checks.on.Item1);
        Assert.True(checks.on.Item2);
        Assert.True(checks.on.Item3);
    }

    [Theory]
    [InlineData(1700)]
    [InlineData(1000)]
    public async Task Page_renders_with_the_enrollments_in_their_own_card(int width)
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
        var cards = await session.Dispatch(async () =>
        {
            var view = new EstView();
            using var vm = new EstViewModel(_ctx);
            var window = Host(view, width, 1000);
            window.Show();
            view.DataContext = vm; // after attaching: no live watch, deterministic content
            await vm.LoadServerAsync(CancellationToken.None); // the switch, then the cards
            Pump();
            Capture(window, outDir, $"est-page-{width}.png");

            // The card of each title: Enrollments apart from General and Profiles (docs/spec/pki.md "Pages of the services after this").
            Border? CardOf(string title) => window.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == title)
                .GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card"));
            var result = (General: CardOf("General"), Profiles: CardOf("Profiles"), Enrollments: CardOf("Enrollments"));

            vm.Port = "80";
            vm.Profiles[0].Name = "a b";
            Pump();
            Capture(window, outDir, $"est-page-{width}-errors.png");
            window.Close();
            return result;
        }, CancellationToken.None);
        GC.KeepAlive(session);

        Assert.NotNull(cards.General);
        Assert.Same(cards.General, cards.Profiles);
        Assert.NotNull(cards.Enrollments);
        Assert.NotSame(cards.General, cards.Enrollments);
        Assert.True(new Oadm.Plugins.Est.Client.EstPage().HasOwnCards);
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
    public async Task Set_up_reads_the_camera_clocks_first_and_asks_when_one_is_behind()
    {
        var behind = new FakeDevice { Address = "10.0.0.48", Dot1xEnabled = false };
        var fine = new FakeDevice { Address = "10.0.0.49", Dot1xEnabled = false };
        var cameras = new DialogContext { RunsTasks = true };
        cameras.Profiles[behind.Id] = new CameraProfileList(true, []);
        cameras.Profiles[fine.Id] = new CameraProfileList(true, []);
        var server = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        cameras.Clocks[behind.Id] = new Oadm.Plugins.DeviceClocks.ClockReading(server - new TimeSpan(0, 9, 12), server);
        cameras.Clocks[fine.Id] = new Oadm.Plugins.DeviceClocks.ClockReading(server + TimeSpan.FromMinutes(1), server);
        var runs = new List<string?>();
        cameras.Run = (devices, payload, _) =>
        {
            runs.Add(payload);
            return [.. devices.Select(d => new TaskRunState(d, "t", TaskRunStatus.Done, "Completed", ""))];
        };
        var vm = new EstSetupViewModel([behind, fine], cameras);
        await vm.LoadAsync();
        var questions = new List<string>();
        var answer = false;
        vm.AskClock = q =>
        {
            questions.Add(q);
            return Task.FromResult(answer);
        };

        // No: nothing runs.
        await vm.OkCommand.ExecuteAsync(null);
        Assert.Equal(["The time of 1 of 2 cameras is behind, by up to 9 min 12 s. The certificates become valid on them only in up to 4 min 12 s. Set the cameras to use NTP. Continue?"], questions);
        Assert.Empty(runs);
        Assert.False(vm.IsRunning);

        // Yes: the task gets the answer.
        answer = true;
        await vm.OkCommand.ExecuteAsync(null);
        Assert.True(SetupPayload.Read(Assert.Single(runs))!.ClockConfirmed);

        // In time: no question, no answer in the payload.
        cameras.Clocks[behind.Id] = new Oadm.Plugins.DeviceClocks.ClockReading(server - TimeSpan.FromMinutes(2), server);
        await vm.OkCommand.ExecuteAsync(null);
        Assert.Equal(2, questions.Count);
        Assert.False(SetupPayload.Read(runs[^1])!.ClockConfirmed);
    }

    [Fact]
    public async Task Set_up_and_remove_run_in_the_dialog_which_stays_open_shows_the_step_and_the_result_and_reads_again()
    {
        var camera = new FakeDevice { Address = "10.0.0.48", Model = "AXIS P3265-V", Dot1xEnabled = false };
        var cameras = new DialogContext { RunsTasks = true };
        cameras.Profiles[camera.Id] = new CameraProfileList(true, []);
        var vm = new EstSetupViewModel([camera], cameras);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        await vm.LoadAsync();
        Assert.Equal("Close", vm.CloseText);

        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EstSetupViewModel.RunText))
            {
                seen.Add(vm.RunText);
            }
        };
        cameras.Run = (devices, payload, progress) =>
        {
            Assert.Equal(SetupAction.Setup, SetupPayload.Read(payload)!.Action);
            cameras.Profiles[camera.Id] = new CameraProfileList(true, [new CameraProfileInfo("oadm2_https", ["WEBSERVER"], "10.0.0.17:9454/.well-known/est/https", "ENROLLED")]);
            var running = new TaskRunState(devices[0], "t1", TaskRunStatus.Running, "Step 5/7 · Wait for the https certificate", "");
            progress?.Report([running]);
            return [running with { Status = TaskRunStatus.Done, Step = "Completed" }];
        };

        await vm.OkCommand.ExecuteAsync(null);

        Assert.False(closed); // stays open
        Assert.Contains("Set up: Step 5/7 · Wait for the https certificate", seen);
        Assert.Equal(("Set up: done", true, false, false), (vm.RunText, vm.RunOk, vm.RunWarning, vm.RunError));
        Assert.Equal(["oadm2_https"], vm.Profiles.Select(p => p.Id)); // read again
        Assert.Equal(2, cameras.Queries[camera.Id]);

        // Remove that fails: the camera's message below the result.
        cameras.Run = (devices, payload, progress) =>
        {
            Assert.Equal(("oadm2_https", SetupAction.Remove), (Assert.Single(SetupPayload.Read(payload)!.Profiles), SetupPayload.Read(payload)!.Action));
            return [new TaskRunState(devices[0], "t2", TaskRunStatus.Failed, "Step 2/2 · Remove the enrollment profiles", "The camera did not answer.")];
        };
        vm.SelectedProfile = vm.Profiles[0];
        vm.RemoveCommand.Execute(null);
        vm.ConfirmRemoveCommand.Execute(null);
        await Eventually.TrueAsync(() => !vm.IsRunning && !vm.IsLoading, "the remove ended");
        Assert.Equal(("Remove oadm2_https: failed", "The camera did not answer.", true), (vm.RunText, vm.RunDetail, vm.RunError));
        Assert.False(closed);
    }

    [Fact]
    public void Several_cameras_count_while_they_run_and_name_the_ones_that_failed()
    {
        var devices = Enumerable.Range(1, 7).Select(i => new FakeDevice { Address = $"10.0.0.{i}" }).ToList();
        var vm = new EstSetupViewModel(devices);
        var states = devices.Select((d, i) => new TaskRunState(d.Id, "t" + i, i < 3 ? TaskRunStatus.Done : TaskRunStatus.Running, "", "")).ToList();
        states[3] = states[3] with { Status = TaskRunStatus.Failed, Message = "No room." };
        Assert.Equal("4 of 7 cameras done · 1 failed", EstSetupViewModel.RunningText(states));

        var ended = devices.Select((d, i) => new TaskRunState(d.Id, "t" + i, i == 0 ? TaskRunStatus.Done : TaskRunStatus.Failed, "", "Error " + i)).ToList();
        vm.ShowResult("Set up", ended);
        Assert.Equal("Set up: 6 of 7 cameras failed", vm.RunText);
        Assert.StartsWith("10.0.0.2: Error 1", vm.RunDetail, StringComparison.Ordinal);
        Assert.EndsWith("+1 more in the tasks pane", vm.RunDetail, StringComparison.Ordinal);
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

            // The run line: while a run goes, then its result.
            vm.SelectedDevice = vm.Devices[0];
            await Eventually.TrueAsync(() => !vm.IsLoading, "the first camera");
            vm.IsRunning = true;
            vm.RunText = "Set up: 1 of 3 cameras done";
            Pump();
            Capture(window, outDir, "est-setup-dialog-running.png");
            vm.IsRunning = false;
            vm.ShowResult("Set up", [
                new TaskRunState(first.Id, "a", TaskRunStatus.Done, "", ""),
                new TaskRunState(second.Id, "b", TaskRunStatus.Failed, "", "The camera has no certificate enrollment. Needs AXIS OS 12.10 or later. Nothing was changed."),
                new TaskRunState(empty.Id, "c", TaskRunStatus.Done, "", ""),
            ]);
            Pump();
            Capture(window, outDir, "est-setup-dialog-result.png");
            window.Close();
        }, CancellationToken.None);
        GC.KeepAlive(session);
    }

    /// <summary>The dialog's view of the server: the cameras' profiles as the task's query answers them.</summary>
    private sealed class DialogContext : ITaskDialogContext
    {
        public Dictionary<Guid, CameraProfileList> Profiles { get; } = [];

        public Dictionary<Guid, int> Queries { get; } = [];

        /// <summary>The camera clocks the clock check reads (none: unreadable).</summary>
        public Dictionary<Guid, Oadm.Plugins.DeviceClocks.ClockReading> Clocks { get; } = [];

        public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
        {
            if (method == Oadm.Plugins.DeviceClocks.CameraClock.QueryMethod)
            {
                return Task.FromResult(Clocks.TryGetValue(deviceId, out var clock)
                    ? EstJson.Serialize(clock)
                    : null);
            }

            Assert.Equal(Oadm.Plugins.Est.Tasks.EstTaskIds.ListProfilesMethod, method);
            Queries[deviceId] = Queries.GetValueOrDefault(deviceId) + 1;
            return Task.FromResult<string?>(EstJson.Serialize(Profiles[deviceId]));
        }

        public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();

        /// <summary>The host can run tasks from the dialog.</summary>
        public bool RunsTasks { get; init; }

        public bool CanRunTasks => RunsTasks;

        /// <summary>What a run does: the devices, the payload, the progress; returns the ended states.</summary>
        public Func<IReadOnlyList<Guid>, string?, IProgress<IReadOnlyList<TaskRunState>>?, IReadOnlyList<TaskRunState>>? Run { get; set; }

        public Task<IReadOnlyList<TaskRunState>> RunTaskAsync(IReadOnlyList<Guid> deviceIds, string? payloadJson, IProgress<IReadOnlyList<TaskRunState>>? progress, CancellationToken ct) =>
            Task.FromResult(Run!(deviceIds, payloadJson, progress));
    }

    private static Window Host(Control view, int width = 1280, int height = 960)
    {
        // The real host page with the plugin's own cards (EstPage.HasOwnCards), like in the client.
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(EstPluginInfo.PluginId, EstPluginInfo.DisplayName, view, hasOwnCards: true),
        };
        var window = new Window { Width = width, Height = height, Content = new Border { Padding = new Thickness(16), Child = page } };
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
