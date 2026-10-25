using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.DeviceClocks;
using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Est.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Est.Tests;

/// <summary>
/// "Certificate enrollment" (set up and remove) against a camera that answers like 10.0.0.48
/// (AXIS OS 12.11.77: its device ID certificates, its cert-est answers; recorded in Fixtures).
/// </summary>
public sealed class TaskTests : IAsyncLifetime
{
    private readonly FakeAuthority _ca = new();
    private readonly EstCamera _camera = new();
    private readonly FakeDevice _device = new() { Serial = "B8A44F631339", Address = "10.0.0.48", FirmwareVersion = "12.11.77" };
    private EstService _est = null!;
    private Oadm.Plugins.Pki.Policies.PolicyStore _policies = null!; // a new server's policies: issuing, Enrollment "All devices", Sign-in CA

    /// <summary>The settings with every profile naming the first issuing policy of its kind (as the EST server page saves them).</summary>
    private SaveRequest Named(EstConfig config) => new(config with
    {
        Profiles = [.. config.Profiles.Select(p => p.PolicyId is null ? p with { PolicyId = _policies.FirstOf(EstProfile.PolicyKindOf(p.Use))!.Id } : p)],
    });

    public async Task InitializeAsync()
    {
        _policies = await TestPolicies.EstStoreAsync();
        // No device group covers the device: the host name the camera reports goes into its profile.
        _est = new EstService(new EstHost(new FakeSettings(), new NoDevices(), new FakePluginNetwork(), () => _ca, () => true) { Scopes = new FakeCoreContext().FakeScopes, Policies = () => _policies });
        await _est.StartAsync(CancellationToken.None);
        _camera.Server = _est;
        _camera.Device = _device;
    }

    public async Task DisposeAsync()
    {
        await _est.DisposeAsync();
        _ca.Dispose();
    }

    [Fact]
    public async Task Set_up_writes_the_https_profile_with_the_device_id_and_a_second_run_writes_nothing()
    {
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        var task = new EstSetupTask(() => _est);
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, _device, "{}", CancellationToken.None));

        Assert.All(StepRun.Lines(ctx.Steps), line => Assert.EndsWith(": Done", line, StringComparison.Ordinal));
        var profile = Assert.Single(_camera.Profiles.Values);
        Assert.Equal("oadm_https", profile["id"]!.GetValue<string>());
        Assert.Equal("10.0.0.17:9454/.well-known/est/https", profile["server"]!.GetValue<string>());
        Assert.Equal("Axis device ID ECC-P256 (802.1AR)", profile["certificate"]!.GetValue<string>()); // the ECC profile signs in with the ECC device ID
        Assert.Equal(["WEBSERVER"], profile["services"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["IP:10.0.0.48", "DNS:axis-b8a44f631339.local"], profile["subjectAlternativeNames"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("10.0.0.48", profile["subject"]!["cn"]!.GetValue<string>());
        var caAlias = Assert.Single(profile["caCertificates"]!.AsArray())!.GetValue<string>();
        Assert.StartsWith(EstTaskIds.CaAliasPrefix, caAlias, StringComparison.Ordinal);
        Assert.Contains(caAlias, _camera.CaCertificates.Keys);

        var writes = _camera.Writes;
        var again = Context();
        await StepRun.RunAsync(again.Steps, () => task.ExecuteAsync(again, _device, "{}", CancellationToken.None));
        Assert.Equal(writes, _camera.Writes);
        Assert.Contains("Info: " + EstTaskIds.AlreadySetUp, again.LogLines);
    }

    [Fact]
    public async Task A_camera_behind_is_refused_before_any_write_unless_the_user_continued()
    {
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        var task = new EstSetupTask(() => _est);
        _camera.ClockOffset = -TimeSpan.FromMinutes(12);

        var ctx = Context();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => task.ExecuteAsync(ctx, _device, "{}", CancellationToken.None));
        Assert.EndsWith(" Set the device to use NTP. Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, _camera.Writes);
        Assert.Empty(_camera.Profiles);

        var yes = Context();
        await StepRun.RunAsync(yes.Steps, () => task.ExecuteAsync(yes, _device, "{\"clockConfirmed\":true}", CancellationToken.None));
        Assert.Single(_camera.Profiles);
        Assert.Contains(yes.LogLines, l => l.EndsWith("The user chose to continue.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Both_profiles_are_set_up_https_last_and_only_a_profile_with_the_same_service_is_replaced()
    {
        _camera.Profiles["other"] = new JsonObject { ["id"] = "other", ["services"] = new JsonArray("NETAUTH"), ["status"] = "ENROLLED" };
        var task = new EstSetupTask(() => _est);
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, _device, "{}", CancellationToken.None));

        Assert.Equal(["oadm_dot1x", "oadm_https"], _camera.Profiles.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["NETAUTH"], _camera.Profiles["oadm_dot1x"]["services"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("other", StepRun.Detail(ctx.Steps, EstTaskIds.RemoveOldStep));
        var adds = _camera.RequestLog.Where(r => r == "POST config/rest/cert-est/v1/profiles").Count();
        Assert.Equal(2, adds);
        Assert.True(StepRun.Lines(ctx.Steps).ToList().FindIndex(l => l.StartsWith(EstTaskIds.AddStep("dot1x"), StringComparison.Ordinal))
            < StepRun.Lines(ctx.Steps).ToList().FindIndex(l => l.StartsWith(EstTaskIds.AddStep("https"), StringComparison.Ordinal)), "HTTPS last");
        var order = _camera.RequestLog.Where(r => r.StartsWith("POST config/rest/cert-est", StringComparison.Ordinal) || r.StartsWith("GET config/rest/cert-est/v1/profiles", StringComparison.Ordinal)).ToList();
        Assert.True(order.IndexOf("POST config/rest/cert-est/v1/profiles") < order.LastIndexOf("GET config/rest/cert-est/v1/profiles"), "waits before the next profile");
    }

    [Fact]
    public async Task With_ieee_8021x_on_the_8021x_profile_is_skipped_with_a_warning()
    {
        var device = _device;
        _camera.Parameters["Network.Interface.I0.dot1x.Enabled"] = "yes";
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, device, "{}", CancellationToken.None));

        Assert.Equal(["oadm_https"], _camera.Profiles.Keys);
        Assert.Contains(EstTaskIds.Dot1xSkipped, ctx.Warnings);
    }

    [Fact]
    public async Task With_ieee_8021x_on_the_8021x_profile_is_written_when_the_user_chose_it()
    {
        var device = _device;
        _camera.Parameters["Network.Interface.I0.dot1x.Enabled"] = "yes";
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, device, EstJson.Serialize(new SetupPayload(ReplaceDot1x: true)), CancellationToken.None));

        Assert.Equal(["oadm_dot1x", "oadm_https"], _camera.Profiles.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(ctx.Warnings);
        Assert.Contains("Info: " + EstTaskIds.Dot1xReplaced, ctx.LogLines);
    }

    [Fact]
    public async Task Setting_up_again_without_the_8021x_choice_keeps_the_cameras_8021x_profile()
    {
        // Set up earlier with "Also replace the network access certificate": the camera has both profiles.
        var device = _device;
        _camera.Parameters["Network.Interface.I0.dot1x.Enabled"] = "yes";
        var task = new EstSetupTask(() => _est);
        var first = Context();
        await StepRun.RunAsync(first.Steps, () => task.ExecuteAsync(first, device, EstJson.Serialize(new SetupPayload(ReplaceDot1x: true)), CancellationToken.None));
        Assert.Equal(["oadm_dot1x", "oadm_https"], _camera.Profiles.Keys.Order(StringComparer.Ordinal));

        var again = Context();
        await StepRun.RunAsync(again.Steps, () => task.ExecuteAsync(again, device, "{}", CancellationToken.None));

        Assert.Equal(["oadm_dot1x", "oadm_https"], _camera.Profiles.Keys.Order(StringComparer.Ordinal)); // no lock-out
        Assert.Contains(EstTaskIds.Dot1xSkipped, again.Warnings);
    }

    [Fact]
    public async Task With_ieee_8021x_on_a_profile_that_also_serves_it_stays_and_its_other_use_is_skipped()
    {
        _camera.Parameters["Network.Interface.I0.dot1x.Enabled"] = "yes";
        _camera.Profiles["corp"] = new JsonObject { ["id"] = "corp", ["services"] = new JsonArray("WEBSERVER", "NETAUTH"), ["status"] = "ENROLLED" };
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https), new EstProfile("mqtt", ProfileUse.Mqtt)] }), CancellationToken.None);
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None));

        Assert.Equal(["corp", "oadm_mqtt"], _camera.Profiles.Keys.Order(StringComparer.Ordinal)); // never removed: no lock-out
        Assert.Contains(EstTaskIds.ServedByNetworkAccessProfile("https", "corp"), ctx.Warnings);
    }

    [Fact]
    public async Task Ieee_8021x_the_camera_does_not_report_counts_as_on()
    {
        _camera.Parameters.Remove("Network.Interface.I0.dot1x.Enabled");
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device with { Dot1xEnabled = false }, "{}", CancellationToken.None));

        Assert.Equal(["oadm_https"], _camera.Profiles.Keys);
        Assert.Contains(EstTaskIds.Dot1xSkipped, ctx.Warnings);
    }

    [Fact]
    public async Task With_a_profile_that_stays_the_profile_that_replaces_another_one_is_chosen()
    {
        // HTTPS and IEEE 802.1X wanted; "mqtt" stays, "other" serves IEEE 802.1X: only one fits, the one replacing "other".
        _camera.Profiles["other"] = new JsonObject { ["id"] = "other", ["services"] = new JsonArray("NETAUTH"), ["status"] = "ENROLLED" };
        _camera.Profiles["mqtt"] = new JsonObject { ["id"] = "mqtt", ["services"] = new JsonArray("MQTT"), ["status"] = "ENROLLED" };
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None));

        Assert.Equal(["mqtt", "oadm_dot1x"], _camera.Profiles.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Set_up_follows_the_enrollment_policies_before_the_first_write()
    {
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https), new EstProfile("mqtt", ProfileUse.Mqtt)] }), CancellationToken.None);
        var all = _policies.PoliciesOf(Oadm.Plugins.Pki.Policies.PolicyKinds.Enrollment).Single();

        // Enrollment for mqtt only: https is skipped with the policy's reason.
        await TestPolicies.AddAsync(_policies, all with { ProfileScope = Oadm.Plugins.Pki.Policies.PolicyProfiles.Chosen, Profiles = ["mqtt"] });
        var ctx = Context();
        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device with { Dot1xEnabled = false }, "{}", CancellationToken.None));
        Assert.Equal(["oadm_mqtt"], _camera.Profiles.Keys);
        Assert.Contains(EstTexts.ProfileNotAllowed(all.Name, "https"), ctx.Warnings);

        // No enrollment: nothing allowed, nothing written.
        var writes = _camera.Writes;
        await TestPolicies.AddAsync(_policies, _policies.PoliciesOf(Oadm.Plugins.Pki.Policies.PolicyKinds.Enrollment).Single() with { ProfileScope = Oadm.Plugins.Pki.Policies.PolicyProfiles.None }); // as stored now
        var none = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => new EstSetupTask(() => _est).ExecuteAsync(Context(), _device, "{}", CancellationToken.None));
        Assert.Equal(EstTaskIds.NoAllowedProfile, none.Message);

        // A camera no Enrollment policy holds is not set up.
        await TestPolicies.AddAsync(_policies, _policies.PoliciesOf(Oadm.Plugins.Pki.Policies.PolicyKinds.Enrollment).Single() with { ProfileScope = Oadm.Plugins.Pki.Policies.PolicyProfiles.All, Devices = new Oadm.Plugins.Pki.Policies.PolicyDevices { Serials = ["ACCC8E000001"] } });
        var held = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => new EstSetupTask(() => _est).ExecuteAsync(Context(), _device, "{}", CancellationToken.None));
        Assert.Equal(EstTaskIds.NoEnrollmentPolicy, held.Message);
        Assert.Equal(writes, _camera.Writes);
    }

    [Fact]
    public async Task A_camera_with_two_profiles_that_stay_is_not_touched()
    {
        _camera.Profiles["a"] = new JsonObject { ["id"] = "a", ["services"] = new JsonArray("MQTT") };
        _camera.Profiles["b"] = new JsonObject { ["id"] = "b", ["services"] = new JsonArray("RTSPS") };
        var writes = _camera.Writes;

        var error = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => new EstSetupTask(() => _est).ExecuteAsync(Context(), _device, "{}", CancellationToken.None));

        Assert.Equal(EstTaskIds.NoRoom, error.Message);
        Assert.Equal(writes, _camera.Writes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"action":2}""")]
    [InlineData("""{"action":"Remove","profiles":"oadm_https"}""")]
    public async Task Options_that_cannot_be_read_change_nothing(string payload)
    {
        var writes = _camera.Writes;

        var error = await Assert.ThrowsAsync<ArgumentException>(() => new EstSetupTask(() => _est).ExecuteAsync(Context(), _device, payload, CancellationToken.None));

        Assert.StartsWith(EstTaskIds.BadOptions, error.Message, StringComparison.Ordinal);
        Assert.Equal(writes, _camera.Writes);
    }

    [Fact]
    public async Task A_profile_of_oadm_that_set_up_removes_stops_the_renewal_check_of_its_certificate()
    {
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("web", ProfileUse.Https)] }), CancellationToken.None);
        _camera.Profiles["oadm_https"] = new JsonObject { ["id"] = "oadm_https", ["services"] = new JsonArray("WEBSERVER"), ["status"] = "ENROLLED" };
        var now = DateTime.UtcNow;
        _est.Store.AddCertificate(new IssuedCertificate { SerialNumber = "01", DeviceId = _device.Id, Device = _device.Address, Profile = "https", IssuedUtc = now, NotAfterUtc = now.AddDays(365) });
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None)); // the profile was renamed to "web"

        Assert.Equal(["oadm_web"], _camera.Profiles.Keys);
        Assert.NotNull(_est.Store.FindCertificate("01")!.RenewalStoppedUtc);
    }

    [Fact]
    public async Task A_camera_at_a_site_gets_the_nodes_address_and_the_site_relay_opens()
    {
        var sites = new FakeSites();
        var site = sites.Add("Building B");
        site.AllowedPorts.Add((Sdk.Sites.SiteProtocol.Tcp, EstPluginInfo.DefaultPort));
        await using var est = new EstService(new EstHost(new FakeSettings(), new NoDevices(), new FakePluginNetwork(sites), () => _ca, () => true) { Sites = sites, Policies = () => _policies });
        await est.StartAsync(CancellationToken.None);
        await est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        var device = _device with { Address = "192.168.5.20", SiteId = site.Id };
        _camera.Server = est;
        _ca.NameAlso("192.168.5.2"); // the node's address is in the EST server's certificate
        var ctx = new FakeTaskContext(_camera) { Network = new FakeDeviceNetwork(site.Id) { LocalAddress = IPAddress.Parse("192.168.5.2") } };

        Assert.Equal(Sdk.Sites.SiteSupport.Full, new EstSetupTask(() => est).SiteSupport);
        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => est).ExecuteAsync(ctx, device, "{}", CancellationToken.None));

        Assert.Equal("192.168.5.2:9454/.well-known/est/https", _camera.Profiles["oadm_https"]["server"]!.GetValue<string>());
        Assert.Equal(Sdk.Sites.RelayState.Running, est.SiteRelay(site.Id)!.State);
        Assert.Equal(["192.168.5.2"], est.Store.SiteAddresses(site.Id));
    }

    [Fact]
    public async Task A_camera_without_the_est_api_or_too_old_is_not_touched()
    {
        var task = new EstSetupTask(() => _est);
        var old = _device with { FirmwareVersion = "12.9.60" };
        Assert.False(task.CanRun(old));
        Assert.Equal("Needs AXIS OS 12.10 or later (this device has 12.9.60)", task.NotSupportedReason(old));

        _camera.HasEstApi = false;
        var ctx = Context();
        var error = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => task.ExecuteAsync(ctx, _device, "{}", CancellationToken.None));
        Assert.Equal(EstTaskIds.NeedsApis, error.Message);
        Assert.Equal(0, _camera.Writes);
    }

    [Fact]
    public async Task A_refusal_of_the_est_server_ends_the_wait_with_its_reason()
    {
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        _camera.Enroll = false;
        _camera.OnProfileAdded = () => _est.Store.AddEnrollment(new Enrollment
        {
            TimeUtc = DateTime.UtcNow, DeviceId = _device.Id, Device = _device.Address, Profile = "https", Reason = EstRefusals.NotManaged,
        });
        var ctx = Context();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None));

        Assert.Equal("The EST server refused the camera: " + EstRefusals.NotManaged, error.Message);
    }

    [Fact]
    public async Task A_profile_set_up_again_after_remove_takes_the_other_name_and_the_older_certificate_goes()
    {
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        var task = new EstSetupTask(() => _est);
        var first = Context();
        await StepRun.RunAsync(first.Steps, () => task.ExecuteAsync(first, _device, "{}", CancellationToken.None));
        Assert.Equal(["oadm_https"], _camera.Profiles.Keys);

        // Remove: the profile goes, its certificate stays with the web server (like 10.0.0.48).
        var remove = Context();
        await StepRun.RunAsync(remove.Steps, () => task.ExecuteAsync(remove, _device, EstJson.Serialize(new SetupPayload { Action = SetupAction.Remove, Profiles = ["oadm_https"] }), CancellationToken.None));
        Assert.Empty(_camera.Profiles);
        Assert.Equal("WEBSERVER", _camera.Certificates["oadm_https"].Use);

        var again = Context();
        await StepRun.RunAsync(again.Steps, () => task.ExecuteAsync(again, _device, "{}", CancellationToken.None));

        Assert.All(StepRun.Lines(again.Steps), line => Assert.EndsWith(": Done", line, StringComparison.Ordinal));
        Assert.Equal(["oadm2_https"], _camera.Profiles.Keys);
        Assert.Equal(["oadm2_https"], _camera.Certificates.Keys); // the new one took the web server, the older one was deleted
        Assert.Contains("Info: Older certificate \"oadm_https\" deleted.", again.LogLines);

        // Removed and set up once more: back to the first name.
        var remove2 = Context();
        await StepRun.RunAsync(remove2.Steps, () => task.ExecuteAsync(remove2, _device, EstJson.Serialize(new SetupPayload { Action = SetupAction.Remove, Profiles = ["oadm2_https"] }), CancellationToken.None));
        var third = Context();
        await StepRun.RunAsync(third.Steps, () => task.ExecuteAsync(third, _device, "{}", CancellationToken.None));
        Assert.Equal(["oadm_https"], _camera.Profiles.Keys);
        Assert.Equal(["oadm_https"], _camera.Certificates.Keys);

        // Its second run changes nothing: OADM's profile keeps whichever name it has.
        var writes = _camera.Writes;
        var fourth = Context();
        await StepRun.RunAsync(fourth.Steps, () => task.ExecuteAsync(fourth, _device, "{}", CancellationToken.None));
        Assert.Equal(writes, _camera.Writes);
    }

    [Fact]
    public async Task An_older_certificate_no_use_holds_is_deleted_and_its_name_taken()
    {
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("mqtt", ProfileUse.Mqtt)] }), CancellationToken.None);
        _camera.Certificates["oadm_mqtt"] = new(null, "old"); // left over, no use holds it
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None));

        Assert.Equal(["oadm_mqtt"], _camera.Profiles.Keys);
        Assert.NotEqual("old", _camera.Certificates["oadm_mqtt"].Fingerprint);
        Assert.Contains("Info: Older certificate \"oadm_mqtt\" deleted.", ctx.LogLines);
    }

    [Fact]
    public async Task Both_names_held_by_uses_fail_that_profile_and_the_other_profile_is_still_set_up()
    {
        _camera.Certificates["oadm_dot1x"] = new("NETAUTH", "a");
        _camera.Certificates["oadm2_dot1x"] = new("MQTT", "b");
        var ctx = Context();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None)));

        Assert.Equal(EstTaskIds.NoFreeName("dot1x"), error.Message);
        Assert.Equal(["oadm_https"], _camera.Profiles.Keys);
    }

    [Fact]
    public async Task A_camera_that_reports_enrolled_but_keeps_its_older_certificate_says_so()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var est = new EstService(new EstHost(new FakeSettings(), new NoDevices(), new FakePluginNetwork(), () => _ca, () => true) { Time = time, Policies = () => _policies });
        await est.StartAsync(CancellationToken.None);
        await est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        _camera.Server = est;
        _camera.KeepsOlderCertificate = true; // the server issues, the camera answers ENROLLED and stores nothing
        _camera.Certificates["oadm_https"] = new(null, "old");
        _camera.Certificates["oadm2_https"] = new(null, "old");
        var ctx = Context();

        var running = new EstSetupTask(() => est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None);
        while (!running.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(1);
        }

        var error = await Assert.ThrowsAsync<TimeoutException>(() => running);
        Assert.Equal(EstTaskIds.NotStored, error.Message);
    }

    [Theory]
    [InlineData("CONNECTED", false)]
    [InlineData("UNCONNECTED", true)]
    public async Task A_camera_without_a_certificate_after_two_minutes_says_whether_it_reached_the_server(string status, bool unreachable)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var est = new EstService(new EstHost(new FakeSettings(), new NoDevices(), new FakePluginNetwork(), () => _ca, () => true) { Time = time, Policies = () => _policies });
        await est.StartAsync(CancellationToken.None);
        await est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        _camera.Enroll = false;
        _camera.ProfileStatus = status;
        var ctx = Context();

        var running = new EstSetupTask(() => est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None);
        while (!running.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(3)); // the camera is asked every 3 s for 2 minutes
            await Task.Delay(1);
        }

        var error = await Assert.ThrowsAsync<TimeoutException>(() => running);
        Assert.Equal(unreachable ? EstTaskIds.NotReached(est.Store.Config.Port) : EstTaskIds.NoCertificate, error.Message);
    }

    [Fact]
    public async Task A_written_profile_needs_a_new_certificate_the_previous_one_does_not_count()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var est = new EstService(new EstHost(new FakeSettings(), new NoDevices(), new FakePluginNetwork(), () => _ca, () => true) { Time = time, Policies = () => _policies });
        await est.StartAsync(CancellationToken.None);
        await est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        _camera.Server = null; // the camera says ENROLLED with the certificate it had, nothing new reaches the server
        var ctx = Context();

        var running = new EstSetupTask(() => est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None);
        while (!running.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Yield();
        }

        var error = await Assert.ThrowsAsync<TimeoutException>(() => running);
        Assert.Equal(EstTaskIds.KeptOldCertificate, error.Message);
    }

    [Fact]
    public async Task Setting_up_again_after_one_profile_changed_rewrites_only_that_one()
    {
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https), new EstProfile("mqtt", ProfileUse.Mqtt)] }), CancellationToken.None);
        var task = new EstSetupTask(() => _est);
        var first = Context();
        await StepRun.RunAsync(first.Steps, () => task.ExecuteAsync(first, _device, "{}", CancellationToken.None));
        Assert.Equal(["oadm_https", "oadm_mqtt"], _camera.Profiles.Keys.Order(StringComparer.Ordinal));

        await _est.SaveAsync(Named(_est.Store.Config with { Profiles = [new EstProfile("https", ProfileUse.Https), new EstProfile("mqtt", ProfileUse.Mqtt, Enabled: false)] }), CancellationToken.None);
        var again = Context();
        await StepRun.RunAsync(again.Steps, () => task.ExecuteAsync(again, _device, "{}", CancellationToken.None));

        Assert.Equal(["oadm_https"], _camera.Profiles.Keys);
        Assert.Equal("oadm_mqtt", StepRun.Detail(again.Steps, EstTaskIds.RemoveOldStep));
        Assert.DoesNotContain(StepRun.Lines(again.Steps), l => l.StartsWith(EstTaskIds.AddStep("https"), StringComparison.Ordinal)); // no new certificate, no restart
    }

    [Fact]
    public async Task A_changed_profile_of_oadm_comes_back_under_its_other_name_with_a_new_certificate()
    {
        var task = new EstSetupTask(() => _est);
        var first = Context();
        await StepRun.RunAsync(first.Steps, () => task.ExecuteAsync(first, _device, "{}", CancellationToken.None));
        var issued = _est.Store.NewestOf(_device.Id, "https")!.SerialNumber;

        var renamed = _device with { HostName = "cam-renamed" };
        var again = Context();
        await StepRun.RunAsync(again.Steps, () => task.ExecuteAsync(again, renamed, "{}", CancellationToken.None));

        // The camera never stores a certificate over an older one of the same name (also not after a change in place).
        Assert.DoesNotContain(_camera.RequestLog, r => r.StartsWith("PATCH", StringComparison.Ordinal));
        Assert.Equal("oadm_dot1x, oadm_https", StepRun.Detail(again.Steps, EstTaskIds.RemoveOldStep)); // both name the camera
        Assert.Equal(["oadm2_dot1x", "oadm2_https"], _camera.Profiles.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("DNS:cam-renamed", _camera.Profiles["oadm2_https"]["subjectAlternativeNames"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.NotEqual(issued, _est.Store.NewestOf(_device.Id, "https")!.SerialNumber); // a new certificate
        Assert.Equal(["oadm2_dot1x", "oadm2_https"], _camera.Certificates.Keys.Order(StringComparer.Ordinal)); // the new ones took the uses, the older ones were deleted
        Assert.All(StepRun.Lines(again.Steps), line => Assert.EndsWith(": Done", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replace_chosen_but_no_8021x_profile_written_keeps_the_cameras_8021x_profile()
    {
        // IEEE 802.1X on, "replace" ticked, but the EST server's IEEE 802.1X profile is off: nothing replaces it.
        _camera.Parameters["Network.Interface.I0.dot1x.Enabled"] = "yes";
        _camera.Profiles["oadm_dot1x"] = new JsonObject { ["id"] = "oadm_dot1x", ["services"] = new JsonArray("NETAUTH"), ["status"] = "ENROLLED" };
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https), new EstProfile("dot1x", ProfileUse.Dot1x, Enabled: false)] }), CancellationToken.None);
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, EstJson.Serialize(new SetupPayload(ReplaceDot1x: true)), CancellationToken.None));

        Assert.Equal(["oadm_dot1x", "oadm_https"], _camera.Profiles.Keys.Order(StringComparer.Ordinal)); // no lock-out
    }

    [Fact]
    public async Task Remove_removes_the_chosen_profiles_also_of_another_est_server_and_names_the_task_after_them()
    {
        _camera.Profiles["oadm_https"] = new JsonObject { ["id"] = "oadm_https", ["services"] = new JsonArray("WEBSERVER") };
        _camera.Profiles["other"] = new JsonObject { ["id"] = "other", ["services"] = new JsonArray("MQTT") };
        _camera.Profiles["keep"] = new JsonObject { ["id"] = "keep", ["services"] = new JsonArray("RTSPS") };
        var task = new EstSetupTask(() => _est);
        var payload = EstJson.Serialize(new SetupPayload { Action = SetupAction.Remove, Profiles = ["oadm_https", "other", "missing"] });
        var ctx = Context();

        await task.ExecuteAsync(ctx, _device, payload, CancellationToken.None);

        Assert.Equal(["keep"], _camera.Profiles.Keys);
        Assert.Equal("oadm_https, other", StepRun.Detail(ctx.Steps, EstTaskIds.RemoveStep));
        Assert.Equal("Remove 3 enrollment profiles", ((ITaskPlugin)task).GetTaskName(payload));
        Assert.Equal("Remove enrollment profile other", ((ITaskPlugin)task).GetTaskName(EstJson.Serialize(new SetupPayload { Action = SetupAction.Remove, Profiles = ["other"] })));
        Assert.Equal(EstTaskIds.SetupName, ((ITaskPlugin)task).GetTaskName("{}"));
        Assert.Equal(EstTaskIds.MenuName, task.DisplayName);

        // A camera without any of them: nothing written.
        var writes = _camera.Writes;
        var again = Context();
        await task.ExecuteAsync(again, _device, payload, CancellationToken.None);
        Assert.Equal(writes, _camera.Writes);
        Assert.Contains(EstTaskIds.NothingToRemove, again.Warnings);
    }

    [Fact]
    public async Task The_dialogs_query_lists_the_cameras_profiles_and_never_writes()
    {
        _camera.Profiles["oadm_https"] = new JsonObject { ["id"] = "oadm_https", ["services"] = new JsonArray("WEBSERVER"), ["status"] = "ENROLLED", ["server"] = "10.0.0.17:9454/.well-known/est/https" };
        var query = new FakeQueryContext(_camera);

        var json = await new EstSetupTask(() => _est).QueryAsync(query, _device, EstTaskIds.ListProfilesMethod, null, CancellationToken.None);

        var list = EstJson.Deserialize<CameraProfileList>(json);
        Assert.True(list.Supported);
        var profile = Assert.Single(list.Profiles);
        Assert.Equal(("oadm_https", "ENROLLED", "10.0.0.17:9454/.well-known/est/https"), (profile.Id, profile.Status, profile.Server));
        Assert.Equal(["WEBSERVER"], profile.Services);
        Assert.Equal(0, _camera.Writes);
        Assert.Equal((null, null), (list.RadiusServerCa, list.NetworkAccessCa)); // a PKI without the list of CAs

        // The CAs of the dot1x profile's IEEE 802.1X policy, for the dialog's hint (Set up replaces the RADIUS server CA).
        _ca.ListDefaults();
        var named = EstJson.Deserialize<CameraProfileList>(await new EstSetupTask(() => _est).QueryAsync(query, _device, EstTaskIds.ListProfilesMethod, null, CancellationToken.None));
        Assert.Equal(("OADM Test CA", "OADM Test CA"), (named.RadiusServerCa, named.NetworkAccessCa));
        Assert.Equal("These cameras will check the RADIUS server with OADM Test CA. Only when your RADIUS server trusts OADM Test CA. Otherwise these cameras drop off the network.",
            Client.EstSetupViewModel.Dot1xHintFor(named.RadiusServerCa, named.NetworkAccessCa));
        Assert.Equal(Client.EstSetupViewModel.Dot1xHint, Client.EstSetupViewModel.Dot1xHintFor(null, null));

        _camera.HasEstApi = false;
        Assert.False(EstJson.Deserialize<CameraProfileList>(await new EstSetupTask(() => _est).QueryAsync(query, _device, EstTaskIds.ListProfilesMethod, null, CancellationToken.None)).Supported);
        await Assert.ThrowsAsync<ArgumentException>(() => new EstSetupTask(() => _est).QueryAsync(query, _device, "write", null, CancellationToken.None));
    }

    [Fact]
    public async Task Set_up_gives_the_camera_the_issuer_of_the_est_servers_certificate_not_simply_the_oadm_ca()
    {
        // After New OADM CA the EST server policy may still pick a certificate of the previous (or an imported) CA.
        using var previous = TestPki.Root("Previous OADM CA");
        _ca.ServerIssuer = [previous.RawData];
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None));

        var alias = Assert.Single(_camera.Profiles["oadm_https"]["caCertificates"]!.AsArray())!.GetValue<string>();
        using var trusted = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(_camera.CaCertificates[alias]);
        Assert.Equal(previous.RawData, trusted.RawData);
        Assert.Single(_camera.CaCertificates); // the OADM CA is not what the camera checks the server with
    }

    [Fact]
    public async Task Set_up_refuses_before_any_write_when_the_est_server_cannot_show_a_usable_certificate()
    {
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        var task = new EstSetupTask(() => _est);

        _ca.ServerIssuer = []; // PKI does not know its issuer
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(Context(), _device, "{}", CancellationToken.None));
        Assert.Equal(EstTaskIds.ServerIssuerUnknown, unknown.Message);

        _ca.ServerIssuer = null;
        _ca.Revoke(_ca.ServerSerial());
        var revoked = await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(Context(), _device, "{}", CancellationToken.None));
        Assert.Equal("The EST server cannot use its certificate: it is revoked. Nothing was changed.", revoked.Message);
        Assert.Equal(0, _camera.Writes);
        Assert.Empty(_camera.Profiles);
    }

    [Fact]
    public async Task Set_up_refuses_an_address_that_is_not_in_the_est_servers_certificate()
    {
        await _est.SaveAsync(Named(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        var ctx = new FakeTaskContext(_camera) { Network = new FakeDeviceNetwork { LocalAddress = IPAddress.Parse("10.0.0.99") } };

        var error = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None));

        Assert.Equal(EstTaskIds.NotInCertificate("10.0.0.99"), error.Message);
        Assert.Equal(0, _camera.Writes);
    }

    [Fact]
    public async Task Set_up_sets_the_radius_server_ca_before_the_https_profile_and_a_second_run_finds_it_set()
    {
        _camera.Dot1xOn("old RADIUS CA");
        var task = new EstSetupTask(() => _est);
        var payload = EstJson.Serialize(new SetupPayload(ReplaceDot1x: true));
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, _device, payload, CancellationToken.None));

        var written = Assert.Single(_camera.Dot1xCaWrites);
        var alias = Assert.Single(written);
        Assert.Equal(_ca.Chain[0], System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(_camera.CaCertificates[alias]).RawData);
        var lines = StepRun.Lines(ctx.Steps).ToList();
        Assert.Contains(EstTaskIds.RadiusCaStep + ": Done", lines);
        Assert.True(lines.FindIndex(l => l.StartsWith(EstTaskIds.RadiusCaStep, StringComparison.Ordinal)) < lines.FindIndex(l => l.StartsWith(EstTaskIds.AddStep("https"), StringComparison.Ordinal)),
            "the RADIUS server CA before the HTTPS profile restarts the web server");

        var again = Context();
        await StepRun.RunAsync(again.Steps, () => task.ExecuteAsync(again, _device, payload, CancellationToken.None));
        Assert.StartsWith("Already set: ", StepRun.Detail(again.Steps, EstTaskIds.RadiusCaStep), StringComparison.Ordinal);
        Assert.Single(_camera.Dot1xCaWrites);
    }

    [Fact]
    public async Task The_radius_server_ca_step_is_skipped_without_the_network_settings_api_or_with_8021x_off()
    {
        _camera.Parameters["Network.Interface.I0.dot1x.Enabled"] = "yes"; // the parameter says on, the network settings API is not listed
        var payload = EstJson.Serialize(new SetupPayload(ReplaceDot1x: true));
        var ctx = Context();
        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, payload, CancellationToken.None));
        Assert.Equal("The camera has no network settings API", StepRun.Detail(ctx.Steps, EstTaskIds.RadiusCaStep));

        _camera.Dot1xOn();
        _camera.NetworkInfo!["devices"]![0]!["wired"]!["8021X"]!["enabled"] = false;
        var off = Context();
        await StepRun.RunAsync(off.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(off, _device, payload, CancellationToken.None));
        Assert.Equal("IEEE 802.1X with certificates is off on the camera", StepRun.Detail(off.Steps, EstTaskIds.RadiusCaStep));
        Assert.Empty(_camera.Dot1xCaWrites);
    }

    [Fact]
    public async Task A_radius_server_ca_that_cannot_be_used_refuses_only_with_8021x_on()
    {
        const string Refusal = "The policy IEEE 802.1X names no CA of the RADIUS server that is in the CA list. Choose one in the policy. Nothing was changed.";
        _ca.RadiusCaAnswer = _ => throw new InvalidOperationException(Refusal);

        // IEEE 802.1X off: nothing of the RADIUS server CA is written, so it is not needed.
        var ctx = Context();
        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None));
        Assert.Equal(["oadm_dot1x", "oadm_https"], _camera.Profiles.Keys.Order(StringComparer.Ordinal));

        // On (the user replaces the network access certificate): refused before the first write.
        var camera = new EstCamera { Server = _est, Device = _device };
        camera.Dot1xOn();
        var on = new FakeTaskContext(camera) { Network = new FakeDeviceNetwork { LocalAddress = IPAddress.Parse("10.0.0.17") } };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new EstSetupTask(() => _est).ExecuteAsync(on, _device, EstJson.Serialize(new SetupPayload(ReplaceDot1x: true)), CancellationToken.None));
        Assert.Equal(Refusal, error.Message);
        Assert.Empty(camera.Profiles);
        Assert.Empty(camera.CaCertificates);
    }

    private FakeTaskContext Context() => new(_camera) { Network = new FakeDeviceNetwork { LocalAddress = IPAddress.Parse("10.0.0.17") } };

    /// <summary>The query's context: the camera, read only.</summary>
    private sealed class FakeQueryContext(IVapixClient vapix) : ITaskQueryContext
    {
        public IVapixClient Vapix => vapix;

        public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    private sealed class NoDevices : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([]);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<IDeviceInfo?>(null);
    }

    /// <summary>A certificate the camera's EST client stored: the use that holds it (null: none) and its fingerprint.</summary>
    internal sealed record StoredCertificate(string? Use, string Fingerprint, string Pem = "")
    {
        public StoredCertificate(string? use, string fingerprint)
            : this(use, fingerprint, "")
        {
        }
    }

    /// <summary>A camera with the cert and cert-est APIs: the device ID certificates of 10.0.0.48, profiles that enroll at once.</summary>
    internal sealed class EstCamera : FakeCamera
    {
        private static readonly string[] AllServices = ["MQTT", "NETAUTH", "WEBSERVER", "RTSPS"];

        public EstCamera()
        {
            Respond = Answer;
            Parameters["Network.Interface.I0.dot1x.Enabled"] = "no"; // the task reads it fresh; unknown would count as on
        }

        public bool HasEstApi { get; set; } = true;

        /// <summary>The camera's clock against the server's (negative: behind).</summary>
        public TimeSpan ClockOffset { get; set; }

        /// <summary>A new profile reports ENROLLED (else <see cref="ProfileStatus"/> for ever).</summary>
        public bool Enroll { get; set; } = true;

        public string ProfileStatus { get; set; } = "CONNECTED";

        public Action? OnProfileAdded { get; set; }

        /// <summary>
        /// Enrolling like a real camera: the EST server issues a certificate for the profile (the task waits for a new one).
        /// </summary>
        public EstService? Server { get; set; }

        public IDeviceInfo? Device { get; set; }

        public Dictionary<string, JsonObject> Profiles { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> CaCertificates { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Certificates the camera's EST client stored, by alias (= the profile id), with the use that holds it: the
        /// camera keeps a certificate bound to its use after its profile is removed, refuses to delete it while a use
        /// holds it and never stores a new one over it ("Certificate is in use", 10.0.0.48).
        /// </summary>
        public Dictionary<string, StoredCertificate> Certificates { get; } = new(StringComparer.Ordinal);

        /// <summary>The camera reports ENROLLED but stores nothing new.</summary>
        public bool KeepsOlderCertificate { get; set; }

        public List<string> RequestLog { get; } = [];

        public new int Writes { get; private set; }

        /// <summary>The answer of <c>getNetworkInfo</c> (network settings API, listed in <see cref="FakeCamera.Apis"/>); null: no answer.</summary>
        public JsonObject? NetworkInfo { get; set; }

        /// <summary>The CA lists <c>setWired8021XConfiguration</c> wrote, in order.</summary>
        public List<string[]> Dot1xCaWrites { get; } = [];

        /// <summary>A camera with IEEE 802.1X on with certificates (client certificate oadm_dot1x) checking the RADIUS server with <paramref name="certsCa"/>.</summary>
        public void Dot1xOn(params string[] certsCa)
        {
            Parameters["Network.Interface.I0.dot1x.Enabled"] = "yes";
            Apis = [.. Apis, new DeviceApi("network-settings", "1.0")];
            NetworkInfo = new JsonObject
            {
                ["devices"] = new JsonArray(new JsonObject
                {
                    ["name"] = "eth0",
                    ["wired"] = new JsonObject
                    {
                        ["8021X"] = new JsonObject
                        {
                            ["enabled"] = true,
                            ["mode"] = "WPA-Enterprise-EAPTLS",
                            ["configurations"] = new JsonArray(new JsonObject
                            {
                                ["mode"] = "WPA-Enterprise-EAPTLS",
                                ["params"] = new JsonObject
                                {
                                    ["identity"] = "10.0.0.48",
                                    ["eapolVersion"] = "EAPoLv3",
                                    ["certClient"] = "oadm_dot1x",
                                    ["certsCA"] = new JsonArray([.. certsCa.Select(c => (JsonNode)c)]),
                                },
                            }),
                        },
                    },
                }),
            };
        }

        private HttpResponseMessage NetworkSettings(string body)
        {
            var request = JsonNode.Parse(body)!;
            if (NetworkInfo is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request["method"]!.GetValue<string>() == "setWired8021XConfiguration")
            {
                var cas = request["params"]!["certsCA"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                Dot1xCaWrites.Add(cas);
                NetworkInfo["devices"]![0]!["wired"]!["8021X"]!["configurations"]![0]!["params"]!["certsCA"] = new JsonArray([.. cas.Select(c => (JsonNode)c)]);
                return Json("""{"apiVersion":"1.0","method":"setWired8021XConfiguration","data":{}}""");
            }

            return Json(new JsonObject { ["apiVersion"] = "1.0", ["data"] = NetworkInfo.DeepClone() }.ToJsonString());
        }

        private HttpResponseMessage? Answer(HttpRequestMessage request, string? body)
        {
            var path = request.RequestUri!.OriginalString;
            RequestLog.Add($"{request.Method} {path}");
            if (path == CameraClock.TimePath)
            {
                // the clock check reads (a POST that changes nothing)
                var now = (Server?.Time ?? TimeProvider.System).GetUtcNow() + ClockOffset;
                return Data(new JsonObject { ["dateTime"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) });
            }

            if (request.Method != HttpMethod.Get)
            {
                Writes++;
            }

            return (request.Method.Method, path) switch
            {
                ("GET", "config/discover") => Json(HasEstApi
                    ? """{"apis":{"cert":{"v1":{"version":"1.0","state":"released"}},"cert-est":{"v1":{"version":"1.0.1","state":"released"}}}}"""
                    : """{"apis":{"cert":{"v1":{"version":"1.0","state":"released"}}}}"""),
                ("GET", "config/rest/cert-est/v1") => Data(new JsonObject
                {
                    ["profiles"] = ProfilesJson(),
                    ["supportedServices"] = new JsonArray([.. AllServices.Except(Profiles.Values.SelectMany(p => p["services"]!.AsArray().Select(n => n!.GetValue<string>()))).Select(s => (JsonNode)s)]),
                }),
                ("GET", "config/rest/cert-est/v1/profiles") => Data(ProfilesJson()),
                ("POST", "config/rest/cert-est/v1/profiles") => AddProfile(body!),
                ("DELETE", _) when path.StartsWith("config/rest/cert-est/v1/profiles/", StringComparison.Ordinal) =>
                    Remove(Uri.UnescapeDataString(path["config/rest/cert-est/v1/profiles/".Length..])),
                ("GET", "config/rest/cert/v1/certificates") => Data(new JsonArray([
                    Certificate("Axis device ID ECC-P256 (802.1AR)", "device-id-ecc-p256-12.11.pem"),
                    Certificate("Axis device ID RSA-2048 (802.1AR)", "device-id-rsa-2048-12.11.pem"),
                    .. Certificates.Select(c => (JsonNode)new JsonObject { ["alias"] = c.Key, ["certificate"] = c.Value.Pem, ["keystore"] = "SE0" })])),
                ("GET", _) when path.StartsWith("config/rest/cert/v1/certificates/", StringComparison.Ordinal) =>
                    Certificates.TryGetValue(Uri.UnescapeDataString(path["config/rest/cert/v1/certificates/".Length..]), out var held)
                        ? Data(new JsonObject { ["alias"] = Uri.UnescapeDataString(path["config/rest/cert/v1/certificates/".Length..]), ["certificate"] = held.Pem, ["keystore"] = "SE0" })
                        : new HttpResponseMessage(HttpStatusCode.NotFound),
                ("DELETE", _) when path.StartsWith("config/rest/cert/v1/certificates/", StringComparison.Ordinal) =>
                    DeleteCertificate(Uri.UnescapeDataString(path["config/rest/cert/v1/certificates/".Length..])),
                ("GET", "config/rest/cert/v1/ca_certificates") => Data(new JsonArray([.. CaCertificates.Select(c => (JsonNode)new JsonObject { ["alias"] = c.Key, ["certificate"] = c.Value })])),
                ("POST", "config/rest/cert/v1/ca_certificates") => AddCa(body!),
                ("POST", "axis-cgi/network_settings.cgi") => NetworkSettings(body!),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private JsonArray ProfilesJson() => new([.. Profiles.Values.Select(p => (JsonNode)p.DeepClone())]);

        private HttpResponseMessage AddProfile(string body)
        {
            if (Profiles.Count >= 2)
            {
                return Json("""{"status":"error","error":{"code":7,"message":"Cannot create more items: No more profiles can be added."}}""");
            }

            var profile = JsonNode.Parse(body)!["data"]!.AsObject().DeepClone().AsObject();
            profile["status"] = Enroll ? "ENROLLED" : ProfileStatus;
            var id = profile["id"]!.GetValue<string>();
            Profiles[id] = profile;
            if (Enroll && Server is { } server && Device is { } device && EstTaskIds.IsOadm(id))
            {
                var pem = NewCertificatePem(out var fingerprint);
                server.Store.AddCertificate(new IssuedCertificate
                {
                    SerialNumber = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), DeviceId = device.Id,
                    Device = device.Address, Profile = EstTaskIds.ProfileNameOf(id), IssuedUtc = server.Time.GetUtcNow().UtcDateTime,
                    NotAfterUtc = server.Time.GetUtcNow().UtcDateTime.AddDays(365),
                    Fingerprint = KeepsOlderCertificate ? fingerprint : null, // with a fingerprint the task pins HTTPS, which this context cannot
                });

                if (Certificates.ContainsKey(id) || KeepsOlderCertificate)
                {
                    // never over an older certificate of that name, also not after a change in place; 10.0.0.48
                    // reported CONNECTED after an add and ENROLLED (with the old one) after a change
                    profile["status"] = KeepsOlderCertificate ? "ENROLLED" : "CONNECTED";
                }
                else
                {
                    // the new certificate takes the use: the certificate that held it before is free
                    var use = profile["services"]!.AsArray().Select(n => n!.GetValue<string>()).First();
                    foreach (var key in Certificates.Where(c => c.Value.Use == use).Select(c => c.Key).ToList())
                    {
                        Certificates[key] = Certificates[key] with { Use = null };
                    }

                    Certificates[id] = new(use, fingerprint, pem);
                }
            }

            OnProfileAdded?.Invoke();
            return Json("""{"status":"success"}""");
        }

        private HttpResponseMessage Remove(string id)
        {
            Profiles.Remove(id);
            return Json("""{"status":"success"}""");
        }

        private HttpResponseMessage DeleteCertificate(string alias)
        {
            if (Certificates.TryGetValue(alias, out var held) && held.Use is not null)
            {
                return Json("""{"status":"error","error":{"code":5,"message":"Validation error: Certificate is in use"}}""");
            }

            Certificates.Remove(alias);
            return Json("""{"status":"success"}""");
        }

        /// <summary>A certificate for the camera's store (any key; only its fingerprint matters).</summary>
        private static string NewCertificatePem(out string fingerprint)
        {
            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=10.0.0.48", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
            fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.RawData));
            return certificate.ExportCertificatePem();
        }

        private HttpResponseMessage AddCa(string body)
        {
            var data = JsonNode.Parse(body)!["data"]!;
            CaCertificates[data["alias"]!.GetValue<string>()] = data["certificate"]!.GetValue<string>();
            return Json("""{"status":"success"}""");
        }

        private static JsonObject Certificate(string alias, string file) => new()
        {
            ["alias"] = alias,
            ["certificate"] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)),
            ["keystore"] = "SE0",
        };

        private static HttpResponseMessage Data(JsonNode data) => Json(new JsonObject { ["status"] = "success", ["data"] = data }.ToJsonString());

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
