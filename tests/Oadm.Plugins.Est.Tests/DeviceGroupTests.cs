using Oadm.Plugins.Est.Client;
using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>
/// Users limited to a device group (docs/spec/device-scope.md): the EST server is server-wide, so Save and Delete all stay
/// with users with all devices; the page shows a group user the enrollments, certificates and listeners of the group's
/// devices only; names of a device a group user added never go into its certificates.
/// </summary>
public sealed class DeviceGroupTests : IAsyncLifetime
{
    private readonly FakeCoreContext _core = new() { Pro = FakeProLicense.ActivePro() };
    private readonly FakeAuthority _ca = new();
    private readonly EstPlugin _plugin = new();
    private readonly FakeDevice _inside = new() { Serial = "B8A44F631339", Address = "10.0.0.48" };
    private readonly FakeDevice _outside = new() { Serial = "ACCC8E000001", Address = "10.0.0.49", SiteId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        _core.Add(_inside);
        _core.Add(_outside);
        _core.FakeServices.Offer<IDeviceCertificateAuthority>(_ca);
        await _core.Settings.SetAsync(Oadm.Plugins.Pki.Servers.ServerSwitch<EstService>.SettingKey, "true", CancellationToken.None); // turned on
        await _plugin.StartAsync(_core, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        _ca.Dispose();
    }

    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        await new PluginCanary(_core, _plugin, _inside, _outside)
            .WithoutDevices(EstMethods.GetState, "server-wide state; listeners and the certificate count of the user's devices")
            .WithoutDevices(EstMethods.GetEnrollments, "rows filtered to the user's devices")
            .WithoutDevices(EstMethods.GetNotRenewed, "rows filtered to the user's devices")
            .WithoutDevices(EstMethods.GetCertificate, "a serial number, only of enrollments of the user's devices", """{"serialNumber":"7DFD0549"}""")
            .AllDevicesOnly(EstMethods.Save, EstJson.Serialize(new SaveRequest(new EstConfig())))
            .AllDevicesOnly(EstMethods.ClearEnrollments)
            .RunAsync();
    }

    [Fact]
    public async Task A_group_user_sees_only_the_enrollments_and_certificates_of_the_groups_devices()
    {
        var store = _plugin.Service!.Store;
        var now = DateTime.UtcNow;
        store.AddEnrollment(new Enrollment { TimeUtc = now, DeviceId = _inside.Id, Device = "10.0.0.48", Profile = "https", Ok = true, SerialNumber = "01" }, [1, 2, 3]);
        store.AddEnrollment(new Enrollment { TimeUtc = now, DeviceId = _outside.Id, Device = "10.0.0.49", Profile = "https", Ok = true, SerialNumber = "02" }, [4, 5, 6]);
        store.AddEnrollment(new Enrollment { TimeUtc = now, Device = "ACCC8E000099", Profile = "https", Ok = false, Reason = EstRefusals.NotManaged });
        store.AddCertificate(Issued("01", _inside.Id, now));
        store.AddCertificate(Issued("02", _outside.Id, now));
        var group = _core.CallAsGroup([_inside.Id]);
        var admin = _core.CallAsAllDevices();

        var seen = EstJson.Deserialize<EnrollmentList>(await _core.InvokeAsync(_plugin, group, EstMethods.GetEnrollments, null));
        Assert.Equal([_inside.Id], seen.Enrollments.Select(e => e.DeviceId)); // no other device, no unmanaged camera
        Assert.Equal(3, EstJson.Deserialize<EnrollmentList>(await _core.InvokeAsync(_plugin, admin, EstMethods.GetEnrollments, null)).Enrollments.Count);

        var notRenewed = EstJson.Deserialize<CertificateList>(await _core.InvokeAsync(_plugin, group, EstMethods.GetNotRenewed, null));
        Assert.Equal((1, _inside.Id), (notRenewed.Total, Assert.Single(notRenewed.Certificates).DeviceId));
        Assert.Equal(2, EstJson.Deserialize<CertificateList>(await _core.InvokeAsync(_plugin, admin, EstMethods.GetNotRenewed, null)).Total);

        Assert.Equal(1, EstJson.Deserialize<EstState>(await _core.InvokeAsync(_plugin, group, EstMethods.GetState, null)).CertificateCount);
        Assert.Equal(2, EstJson.Deserialize<EstState>(await _core.InvokeAsync(_plugin, admin, EstMethods.GetState, null)).CertificateCount);

        // The certificate of another device's enrollment: answered like one OADM no longer keeps.
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => _core.InvokeAsync(_plugin, group, EstMethods.GetCertificate, """{"serialNumber":"02"}"""));
        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => _core.InvokeAsync(_plugin, group, EstMethods.GetCertificate, """{"serialNumber":"FF"}"""));
        Assert.Equal((EstMethods.CertificateGone, EstMethods.CertificateGone), (refused.Message, unknown.Message));
        Assert.Empty(_core.RefusedAccesses);
    }

    [Fact]
    public async Task Delete_all_reaches_the_pages_of_the_groups_that_read_the_page_and_save_stays_with_all_devices()
    {
        var key = _core.FakeScopes.Add("Building B", [_inside.Id]);
        var group = FakeCallContext.WithGroup(_core, [_inside.Id], "tech2", key);
        var otherGroup = FakeCallContext.WithGroup(_core, [_outside.Id], "guard", _core.FakeScopes.Add("Lobby", [_outside.Id]));
        await _core.InvokeAsync(_plugin, group, EstMethods.GetEnrollments, null);

        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => _core.InvokeAsync(_plugin, group, EstMethods.ClearEnrollments, null));
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => _core.InvokeAsync(_plugin, group, EstMethods.Save, EstJson.Serialize(new SaveRequest(new EstConfig()))));
        await _core.InvokeAsync(_plugin, _core.CallAsAllDevices(), EstMethods.ClearEnrollments, null);

        Assert.Contains(_core.EventsSeenBy(group), e => e.Topic == EstMethods.EnrollmentsClearedTopic);
        Assert.Contains(_core.EventsSeenBy(_core.CallAsAllDevices()), e => e.Topic == EstMethods.EnrollmentsClearedTopic);
        Assert.DoesNotContain(_core.EventsSeenBy(otherGroup), e => e.Topic == EstMethods.EnrollmentsClearedTopic); // never read the page

        // A state change: each group's page gets its own state, the listener of the server's network only with a device there.
        await _core.InvokeAsync(_plugin, otherGroup, EstMethods.GetState, null);
        await _core.InvokeAsync(_plugin, _core.CallAsAllDevices(), EstMethods.Save, EstJson.Serialize(new SaveRequest(new EstConfig { EnrollmentsKept = 1000 })));
        EstState? StateOf(ICallContext watcher) => _core.EventsSeenBy(watcher).LastOrDefault(e => e.Topic == EstMethods.StateTopic) is { PayloadJson: { } json }
            ? EstJson.Deserialize<EstState>(json) : null;
        await Eventually.TrueAsync(() => StateOf(group) is not null && StateOf(otherGroup) is not null, "the state of both groups");
        Assert.Equal(["This server"], StateOf(_core.CallAsAllDevices())!.Listeners.Select(l => l.Name));
        Assert.Equal(["This server"], StateOf(group)!.Listeners.Select(l => l.Name));
        Assert.Empty(StateOf(otherGroup)!.Listeners); // its device is at a site

        _core.FakeScopes.Delete(key);
        Assert.DoesNotContain(key, _plugin.Service!.Viewers.Keys()); // a deleted group gets nothing
    }

    [Fact]
    public void A_device_a_group_user_added_gets_no_host_name_in_its_certificate()
    {
        var trusted = new FakeDevice { Serial = "B8A44F631339", Address = "cam.example.com", HostName = "cam-entrance" };
        var untrusted = trusted with { AddedByAllDevices = false };

        Assert.Equal(["cam-entrance", "cam.example.com", "axis-b8a44f631339.local"], EstEndpoint.DnsNames(trusted, inAnyGroup: false));
        Assert.Equal("cam.example.com", EstEndpoint.CommonName(trusted));
        Assert.Equal(["axis-b8a44f631339.local"], EstEndpoint.DnsNames(untrusted, inAnyGroup: false));
        Assert.Equal(["axis-b8a44f631339.local"], EstEndpoint.DnsNames(untrusted, inAnyGroup: true));
        Assert.Equal("axis-b8a44f631339.local", EstEndpoint.CommonName(untrusted));
        Assert.Equal("10.0.0.48", EstEndpoint.CommonName(untrusted with { Address = "10.0.0.48" })); // an IP address stays
    }

    [Fact]
    public void A_device_a_group_covers_gets_only_the_address_it_was_added_with_never_the_host_name_the_camera_reports()
    {
        var device = new FakeDevice { Serial = "B8A44F631339", Address = "cam.example.com", HostName = "bank.example.com" };
        Assert.False(_plugin.Service!.InAnyGroup(_inside));
        _core.FakeScopes.Add("Building B", [_inside.Id, device.Id]);

        Assert.True(_plugin.Service.InAnyGroup(_inside));
        Assert.False(_plugin.Service.InAnyGroup(_outside));
        Assert.Equal(["cam.example.com", "axis-b8a44f631339.local"], EstEndpoint.DnsNames(device, _plugin.Service.InAnyGroup(device)));

        // The camera's profile: the same names.
        var (_, json) = Tasks.EstSetupTask.Wanted(new EstProfile("https", ProfileUse.Https), device, _plugin.Service.InAnyGroup(device),
            System.Net.IPAddress.Parse("10.0.0.17"), 9454, ["OADM CA 1"], "Axis device ID");
        Assert.Equal(["DNS:cam.example.com", "DNS:axis-b8a44f631339.local"], json["subjectAlternativeNames"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("cam.example.com", json["subject"]!["cn"]!.GetValue<string>());
    }

    [Fact]
    public void A_viewed_set_covers_the_sites_of_its_devices_and_the_servers_network_only_with_a_local_device()
    {
        var site = Guid.NewGuid();
        var atSite = ViewedSet.From([new FakeDevice { SiteId = site }]);
        var local = ViewedSet.From([new FakeDevice()]);

        Assert.True(atSite.CoversSite(site));
        Assert.False(atSite.CoversSite(null));
        Assert.False(atSite.Contains((Guid?)null));
        Assert.True(local.CoversSite(null));
        Assert.False(local.CoversSite(site));
    }

    [Fact]
    public void The_page_hides_save_and_delete_all_for_a_group_user()
    {
        using var vm = new EstViewModel(new GroupPageContext());

        Assert.False(vm.MayManage);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.ClearEnrollmentsCommand.CanExecute(null));
    }

    private static IssuedCertificate Issued(string serial, Guid deviceId, DateTime now) => new()
    {
        SerialNumber = serial,
        DeviceId = deviceId,
        Device = "camera",
        Profile = "https",
        IssuedUtc = now.AddDays(-200),
        NotAfterUtc = now.AddDays(165),
        NotRenewedUtc = now.AddDays(-1),
    };

    /// <summary>A page context of a user limited to a device group whose role has every right.</summary>
    private sealed class GroupPageContext : ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices => [];

        public bool HasAllDevices => false;

        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);
    }
}
