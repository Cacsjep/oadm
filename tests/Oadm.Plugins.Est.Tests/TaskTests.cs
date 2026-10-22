using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

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

    public async Task InitializeAsync()
    {
        _est = new EstService(new EstHost(new FakeSettings(), new NoDevices(), new FakePluginNetwork(), () => _ca, () => true));
        await _est.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _est.DisposeAsync();
        _ca.Dispose();
    }

    [Fact]
    public async Task Set_up_writes_the_https_profile_with_the_device_id_and_a_second_run_writes_nothing()
    {
        await _est.SaveAsync(new SaveRequest(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
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
    public async Task Both_profiles_are_set_up_https_last_and_only_a_profile_with_the_same_service_is_replaced()
    {
        _camera.Profiles["other"] = new JsonObject { ["id"] = "other", ["services"] = new JsonArray("NETAUTH"), ["status"] = "ENROLLED" };
        _camera.Profiles["mqtt"] = new JsonObject { ["id"] = "mqtt", ["services"] = new JsonArray("MQTT"), ["status"] = "ENROLLED" };
        var task = new EstSetupTask(() => _est);
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, _device, "{}", CancellationToken.None));

        Assert.Equal(["mqtt", "oadm_dot1x", "oadm_https"], _camera.Profiles.Keys.Order(StringComparer.Ordinal));
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
        var device = _device with { Dot1xEnabled = true };
        var ctx = Context();

        await StepRun.RunAsync(ctx.Steps, () => new EstSetupTask(() => _est).ExecuteAsync(ctx, device, "{}", CancellationToken.None));

        Assert.Equal(["oadm_https"], _camera.Profiles.Keys);
        Assert.Contains(EstTaskIds.Dot1xSkipped, ctx.Warnings);
    }

    [Fact]
    public async Task With_ieee_8021x_on_the_8021x_profile_is_written_when_the_user_chose_it()
    {
        var device = _device with { Dot1xEnabled = true };
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
        var device = _device with { Dot1xEnabled = true };
        var task = new EstSetupTask(() => _est);
        var first = Context();
        await StepRun.RunAsync(first.Steps, () => task.ExecuteAsync(first, device, EstJson.Serialize(new SetupPayload(ReplaceDot1x: true)), CancellationToken.None));
        Assert.Equal(["oadm_dot1x", "oadm_https"], _camera.Profiles.Keys.Order(StringComparer.Ordinal));

        var again = Context();
        await StepRun.RunAsync(again.Steps, () => task.ExecuteAsync(again, device, "{}", CancellationToken.None));

        Assert.Equal(["oadm_dot1x", "oadm_https"], _camera.Profiles.Keys.Order(StringComparer.Ordinal)); // no lock-out
        Assert.Contains(EstTaskIds.Dot1xSkipped, again.Warnings);
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
        await _est.SaveAsync(new SaveRequest(new EstConfig { Profiles = [new EstProfile("web", ProfileUse.Https)] }), CancellationToken.None);
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
        await using var est = new EstService(new EstHost(new FakeSettings(), new NoDevices(), new FakePluginNetwork(sites), () => _ca, () => true) { Sites = sites });
        await est.StartAsync(CancellationToken.None);
        await est.SaveAsync(new SaveRequest(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        var device = _device with { Address = "192.168.5.20", SiteId = site.Id };
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
        await _est.SaveAsync(new SaveRequest(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
        _camera.Enroll = false;
        _camera.OnProfileAdded = () => _est.Store.AddEnrollment(new Enrollment
        {
            TimeUtc = DateTime.UtcNow, DeviceId = _device.Id, Device = _device.Address, Profile = "https", Reason = EstRefusals.NotManaged,
        });
        var ctx = Context();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new EstSetupTask(() => _est).ExecuteAsync(ctx, _device, "{}", CancellationToken.None));

        Assert.Equal("The EST server refused the camera: " + EstRefusals.NotManaged, error.Message);
    }

    [Theory]
    [InlineData("CONNECTED", false)]
    [InlineData("UNCONNECTED", true)]
    public async Task A_camera_without_a_certificate_after_two_minutes_says_whether_it_reached_the_server(string status, bool unreachable)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var est = new EstService(new EstHost(new FakeSettings(), new NoDevices(), new FakePluginNetwork(), () => _ca, () => true) { Time = time });
        await est.StartAsync(CancellationToken.None);
        await est.SaveAsync(new SaveRequest(new EstConfig { Profiles = [new EstProfile("https", ProfileUse.Https)] }), CancellationToken.None);
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
        Assert.Contains("Info: " + EstTaskIds.NothingToRemove, again.LogLines);
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

        _camera.HasEstApi = false;
        Assert.False(EstJson.Deserialize<CameraProfileList>(await new EstSetupTask(() => _est).QueryAsync(query, _device, EstTaskIds.ListProfilesMethod, null, CancellationToken.None)).Supported);
        await Assert.ThrowsAsync<ArgumentException>(() => new EstSetupTask(() => _est).QueryAsync(query, _device, "write", null, CancellationToken.None));
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

    /// <summary>A camera with the cert and cert-est APIs: the device ID certificates of 10.0.0.48, profiles that enroll at once.</summary>
    internal sealed class EstCamera : FakeCamera
    {
        private static readonly string[] AllServices = ["MQTT", "NETAUTH", "WEBSERVER", "RTSPS"];

        public EstCamera()
        {
            Respond = Answer;
        }

        public bool HasEstApi { get; set; } = true;

        /// <summary>A new profile reports ENROLLED (else <see cref="ProfileStatus"/> for ever).</summary>
        public bool Enroll { get; set; } = true;

        public string ProfileStatus { get; set; } = "CONNECTED";

        public Action? OnProfileAdded { get; set; }

        public Dictionary<string, JsonObject> Profiles { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> CaCertificates { get; } = new(StringComparer.Ordinal);

        public List<string> RequestLog { get; } = [];

        public new int Writes { get; private set; }

        private HttpResponseMessage? Answer(HttpRequestMessage request, string? body)
        {
            var path = request.RequestUri!.OriginalString;
            RequestLog.Add($"{request.Method} {path}");
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
                ("GET", "config/rest/cert/v1/certificates") => Data(new JsonArray(
                    Certificate("Axis device ID ECC-P256 (802.1AR)", "device-id-ecc-p256-12.11.pem"),
                    Certificate("Axis device ID RSA-2048 (802.1AR)", "device-id-rsa-2048-12.11.pem"))),
                ("GET", "config/rest/cert/v1/ca_certificates") => Data(new JsonArray([.. CaCertificates.Select(c => (JsonNode)new JsonObject { ["alias"] = c.Key, ["certificate"] = c.Value })])),
                ("POST", "config/rest/cert/v1/ca_certificates") => AddCa(body!),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private JsonArray ProfilesJson() => new([.. Profiles.Values.Select(p => (JsonNode)p.DeepClone())]);

        private HttpResponseMessage AddProfile(string body)
        {
            var profile = JsonNode.Parse(body)!["data"]!.AsObject().DeepClone().AsObject();
            profile["status"] = Enroll ? "ENROLLED" : ProfileStatus;
            Profiles[profile["id"]!.GetValue<string>()] = profile;
            OnProfileAdded?.Invoke();
            return Json("""{"status":"success"}""");
        }

        private HttpResponseMessage Remove(string id)
        {
            Profiles.Remove(id);
            return Json("""{"status":"success"}""");
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
