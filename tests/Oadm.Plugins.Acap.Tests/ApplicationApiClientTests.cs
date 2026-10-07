namespace Oadm.Plugins.Acap.Tests;

public sealed class ApplicationApiClientTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Parses_the_recorded_list_of_a_P3265V_on_AXIS_OS_12_11()
    {
        var apps = ApplicationApiClient.ParseList(Fixture("list-p3265v-12.11.xml"));

        Assert.Equal(8, apps.Count);
        var aoa = apps.Single(a => a.Name == "objectanalytics");
        Assert.Equal("AXIS Object Analytics", aoa.NiceName);
        Assert.Equal("Axis Communications", aoa.Vendor);
        Assert.Equal("1.26.205", aoa.Version);
        Assert.True(aoa.IsRunning);
        Assert.True(aoa.Bundled);
        Assert.Equal("Signed", aoa.SignatureStatus);
        Assert.Equal("412806", aoa.ApplicationId);
        Assert.Equal(new OsVersionRange("12", "12"), Assert.Single(aoa.CompatibleOsVersions));

        var gencam = apps.Single(a => a.Name == "ax_gencam");
        Assert.Equal("Commend Österreich GmbH", gencam.Vendor);
        Assert.Equal("Valid", gencam.License);
        Assert.Equal("2026-11-07", gencam.LicenseExpirationDate);
        Assert.Equal("Stopped", gencam.Status);
        Assert.False(gencam.Bundled);

        var simulator = apps.Single(a => a.Name == "lpv_simulator");
        Assert.Null(simulator.ApplicationId);
        Assert.Empty(simulator.CompatibleOsVersions);

        Assert.Equal(apps.Select(a => a.DisplayName).Order(StringComparer.OrdinalIgnoreCase), apps.Select(a => a.DisplayName));
    }

    [Fact]
    public void Error_reply_from_list_throws()
    {
        var ex = Assert.Throws<AcapDeviceException>(() => ApplicationApiClient.ParseList("<reply result=\"error\"><error type=\"1\" message=\"boom\" /></reply>"));
        Assert.Equal(1, ex.Code);
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
        Assert.Throws<AcapDeviceException>(() => ApplicationApiClient.ParseList("<html>"));
    }

    [Fact]
    public void Parses_config_get_reply_recorded_on_the_device()
    {
        Assert.True(ApplicationApiClient.ParseConfigBool("<reply result=\"ok\">\n\t<param name=\"AllowUnsigned\" value=\"true\"/>\n</reply>", "AllowUnsigned"));
        Assert.False(ApplicationApiClient.ParseConfigBool("<reply result=\"ok\"><param name=\"AllowUnsigned\" value=\"false\"/></reply>", "AllowUnsigned"));
        Assert.Null(ApplicationApiClient.ParseConfigBool("<reply result=\"error\"><error type=\"1\" message=\"x\"/></reply>", "AllowUnsigned"));
        Assert.Null(ApplicationApiClient.ParseConfigBool("not xml", "AllowUnsigned"));
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("OK\r\n")]
    public void Ok_reply_passes(string reply) => ApplicationApiClient.EnsureOk(reply, ApplicationApiClient.UploadErrors);

    [Theory]
    [InlineData("Error: 2", 2, "signature")]
    [InlineData("Error: 5\n", 5, "not compatible")]
    [InlineData("Error: 27", 27, "vendor")]
    [InlineData("Error: 99", 99, "Error: 99")]
    public void Upload_errors_map_to_clear_messages(string reply, int code, string text)
    {
        var ex = Assert.Throws<AcapDeviceException>(() => ApplicationApiClient.EnsureOk(reply, ApplicationApiClient.UploadErrors));
        Assert.Equal(code, ex.Code);
        Assert.Contains(text, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexpected_reply_throws() =>
        Assert.Throws<AcapDeviceException>(() => ApplicationApiClient.EnsureOk("<html>login</html>", ApplicationApiClient.ControlErrors));

    [Theory]
    [InlineData(AcapControlAction.Start, "hello", "/axis-cgi/applications/control.cgi?action=start&package=hello")]
    [InlineData(AcapControlAction.Stop, "ax_msf", "/axis-cgi/applications/control.cgi?action=stop&package=ax_msf")]
    [InlineData(AcapControlAction.Remove, "a.b-c", "/axis-cgi/applications/control.cgi?action=remove&package=a.b-c")]
    public void Builds_control_requests(AcapControlAction action, string package, string expected) =>
        Assert.Equal(expected, ApplicationApiClient.BuildControlUri(action, package));

    [Fact]
    public async Task Control_posts_and_rejects_unsafe_names_before_sending()
    {
        var device = new FakeAcapDevice();
        device.Add("hello", "1.0.0");
        var client = new ApplicationApiClient(device);

        await client.ControlAsync(AcapControlAction.Start, "hello", CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ControlAsync(AcapControlAction.Remove, "x&action=remove", CancellationToken.None));

        var request = Assert.Single(device.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(["start hello"], device.Writes);
    }

    [Fact]
    public async Task Upload_sends_multipart_file_field_and_reports_progress()
    {
        var device = new FakeAcapDevice();
        var eap = EapBuilder.FromManifest(EapBuilder.Manifest(appName: "hello"));
        var reported = new List<long>();

        await new ApplicationApiClient(device).UploadAsync(new MemoryStream(eap), "hello_1_2_0_aarch64.eap", new SyncProgress(reported.Add), CancellationToken.None);

        Assert.Equal(eap.Length, device.UploadedBytes);
        Assert.Equal("hello_1_2_0_aarch64.eap", device.UploadedFileName);
        Assert.Equal(eap.Length, reported[^1]);
        Assert.True(device.Apps.ContainsKey("hello"));
        Assert.Equal(HttpMethod.Post, device.Requests.Single().Method);
    }

    [Fact]
    public async Task Upload_error_is_reported()
    {
        var device = new FakeAcapDevice { UploadReply = "Error: 2" };
        var ex = await Assert.ThrowsAsync<AcapDeviceException>(() =>
            new ApplicationApiClient(device).UploadAsync(new MemoryStream(EapBuilder.FromManifest(EapBuilder.Manifest())), "x.eap", null, CancellationToken.None));
        Assert.Equal(2, ex.Code);
    }

    [Fact]
    public async Task Device_facts_combine_basicdeviceinfo_param_and_config()
    {
        var device = new FakeAcapDevice { AllowUnsigned = false };
        var facts = await new ApplicationApiClient(device).GetDeviceFactsAsync(CancellationToken.None);

        Assert.Equal("aarch64", facts.Architecture);
        Assert.Equal("12.11.77", facts.FirmwareVersion);
        Assert.Equal("2.18", facts.EmbeddedDevelopmentVersion);
        Assert.False(facts.AllowUnsigned);
        Assert.Empty(device.Writes);
    }

    [Fact]
    public async Task Missing_config_cgi_means_unknown_allow_unsigned()
    {
        var device = new FakeAcapDevice { AllowUnsigned = null };
        Assert.Null(await new ApplicationApiClient(device).TryGetAllowUnsignedAsync(CancellationToken.None));
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
