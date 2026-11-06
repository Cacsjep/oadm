using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Tasks;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Install certificates checks the file before the first write (docs/spec/pki.md "Delete and install safety"): not
/// expired, not valid only later (the device's clock where the task reads it, else the server's, 5 minutes allowed), for
/// IEEE 802.1X a certificate that may log in as a client; and a private key goes over HTTP only with the user's answer
/// in the payload.
/// </summary>
public sealed class InstallFileCheckTests
{
    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string AnyUse = "2.5.29.37.0";

    /// <summary>A certificate with its key for 10.0.0.48, valid from <paramref name="start"/>, with the given extended key usages.</summary>
    private static byte[] Pfx(DateTimeOffset? start = null, params string[] usages)
    {
        using var ca = TestCa.Root("Customer CA", notBefore: DateTimeOffset.UtcNow.AddDays(-30));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=10.0.0.48", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse("10.0.0.48"));
        request.CertificateExtensions.Add(san.Build());
        if (usages.Length > 0)
        {
            var oids = new OidCollection();
            foreach (var usage in usages)
            {
                oids.Add(new Oid(usage));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
        }

        var from = start ?? DateTimeOffset.UtcNow.AddDays(-1);
        using var signed = request.Create(ca, from, DateTimeOffset.UtcNow.AddDays(300), [0x01, 0x02, 0x04]);
        using var leaf = signed.CopyWithPrivateKey(key);
        return TestCa.Pfx("secret", leaf);
    }

    private static string Payload(TaskHarness h, string purpose, bool sendKeysOverHttp = false) => PkiJson.Serialize(new InstallPayload
    {
        Purpose = purpose,
        Password = "secret",
        Files = [new InstallFile(h.Device.Id, "f1", "cam48.pfx")],
        SendKeysOverHttp = sendKeysOverHttp,
    });

    [Fact]
    public void The_file_check_knows_when_the_certificate_starts_and_what_it_is_for()
    {
        var now = DateTime.UtcNow;

        var later = CertificateFiles.Read(Pfx(DateTimeOffset.UtcNow.AddHours(1)), "secret", now);
        Assert.StartsWith("The certificate is not valid before ", later.Error, StringComparison.Ordinal);
        Assert.EndsWith(" UTC.", later.Error, StringComparison.Ordinal);

        // Within the 5 minutes OADM's own certificates start early: a clock that is a little behind is fine.
        Assert.Null(CertificateFiles.Read(Pfx(DateTimeOffset.UtcNow.AddMinutes(3)), "secret", now).Error);
        Assert.Null(CertificateFiles.NotValidYetProblem(now.AddMinutes(4), now));
        Assert.NotNull(CertificateFiles.NotValidYetProblem(now.AddMinutes(6), now));

        // Without the extension a certificate may be used for anything.
        Assert.True(CertificateFiles.Read(Pfx(), "secret", now).LeafAllowsClientAuth);
        Assert.True(CertificateFiles.Read(Pfx(null, ClientAuth), "secret", now).LeafAllowsClientAuth);
        Assert.True(CertificateFiles.Read(Pfx(null, ServerAuth, ClientAuth), "secret", now).LeafAllowsClientAuth);
        Assert.True(CertificateFiles.Read(Pfx(null, AnyUse), "secret", now).LeafAllowsClientAuth);
        var serverOnly = CertificateFiles.Read(Pfx(null, ServerAuth), "secret", now);
        Assert.False(serverOnly.LeafAllowsClientAuth);
        Assert.Null(serverOnly.Error); // fine for HTTPS
        Assert.Equal(CertificateFiles.NotForClientAuth, CertificateFiles.Dot1xProblem(serverOnly));
        Assert.Null(CertificateFiles.Dot1xProblem(CertificateFiles.Read(Pfx(), "secret", now)));
    }

    [Theory]
    [InlineData(InstallPurpose.Https)]
    [InlineData(InstallPurpose.Dot1x)]
    public async Task A_certificate_that_is_not_valid_yet_is_refused_before_any_write(string purpose)
    {
        await using var h = await TaskHarness.StartAsync();

        var ex = await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(h, purpose), Pfx(DateTimeOffset.UtcNow.AddDays(2)));

        Assert.StartsWith("cam48.pfx: The certificate is not valid before ", ex!.Message, StringComparison.Ordinal);
        Assert.EndsWith(" UTC. Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Read certificate file: Failed", h.Lines[1]);
        Assert.Empty(h.Camera.Writes);
    }

    [Fact]
    public async Task For_8021x_the_start_is_compared_with_the_device_clock()
    {
        // Valid in 4 minutes by the server's clock (allowed), but the device is 4.5 minutes behind: 8.5 minutes to go there.
        await using var h = await TaskHarness.StartAsync();
        h.Camera.ClockOffset = TimeSpan.FromMinutes(-4.5);

        var ex = await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(h, InstallPurpose.Dot1x), Pfx(DateTimeOffset.UtcNow.AddMinutes(4), ClientAuth));

        Assert.StartsWith("The certificate is not valid before ", ex!.Message, StringComparison.Ordinal);
        Assert.Contains(" UTC. The device's time is ", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(" UTC. Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["Check compatibility: Done", "Read certificate file: Done", "Check device clock: Failed"], h.Lines.Take(3));
        Assert.Empty(h.Camera.Writes);

        // HTTPS reads no device clock: the server's decides, and 4 minutes are within the allowance.
        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(h, InstallPurpose.Https), Pfx(DateTimeOffset.UtcNow.AddMinutes(4))));
    }

    [Fact]
    public async Task For_8021x_a_certificate_that_may_not_log_in_as_a_client_is_refused_before_any_write()
    {
        await using var h = await TaskHarness.StartAsync();
        var serverOnly = Pfx(null, ServerAuth);

        var ex = await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(h, InstallPurpose.Dot1x), serverOnly);

        Assert.Equal("cam48.pfx: The certificate is not for client authentication, which IEEE 802.1X needs. Nothing was changed.", ex!.Message);
        Assert.Equal("Read certificate file: Failed", h.Lines[1]);
        Assert.Empty(h.Camera.Writes);

        // The same file is fine for HTTPS; one without the extension, or with client authentication, for 802.1X.
        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(h, InstallPurpose.Https), serverOnly));
        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(h, InstallPurpose.Dot1x), Pfx()));
        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(h, InstallPurpose.Dot1x), Pfx(null, ServerAuth, ClientAuth)));
    }

    [Fact]
    public void The_dialog_marks_a_file_that_8021x_cannot_use()
    {
        var device = new PkiFakeDevice(Guid.NewGuid());
        var serverOnly = Pfx(null, ServerAuth);
        var vm = new InstallCertificatesViewModel(new FakeDialogContext(_ => Task.FromResult<string?>(null)), [device], _ => serverOnly) { Password = "secret" };
        vm.SetFiles(["c:/certs/cam48.pfx"]);
        Assert.Equal("Matched", vm.Files[0].Status);

        vm.SelectedPurpose = vm.Purposes.Single(p => p.Value == InstallPurpose.Dot1x);
        vm.Evaluate();

        Assert.Equal(("Cannot be used", CertificateFiles.NotForClientAuth), (vm.Files[0].Status, vm.Files[0].StatusDetail));
        Assert.True(vm.Files[0].IsError);
    }

    [Fact]
    public void The_payload_of_a_run_is_parsed_and_indexed_once_for_all_its_devices()
    {
        // 5,000 devices get the same payload string with 5,000 files: read once, each device looks up its own file.
        var devices = Enumerable.Range(0, 5000).Select(_ => Guid.NewGuid()).ToList();
        var files = devices.Select((id, i) => new InstallFile(id, "f" + i, "cam" + i + ".pfx")).ToList();
        files.Insert(1, new InstallFile(Guid.Empty, "all", "ca.p12"));
        files.Add(new InstallFile(devices[0], "again", "cam0-again.pfx"));
        var json = PkiJson.Serialize(new InstallPayload { Purpose = InstallPurpose.CaOnly, Password = "secret", Files = files });

        var payload = PkiJson.DeserializeShared<InstallPayload>(json);
        Assert.Same(payload, PkiJson.DeserializeShared<InstallPayload>(json));
        Assert.NotSame(payload, PkiJson.Deserialize<InstallPayload>(json));
        Assert.NotSame(payload, PkiJson.DeserializeShared<InstallPayload>(new string(json.AsSpan()))); // another run: its own string

        Assert.Equal(["cam4999.pfx"], payload.FilesFor(devices[4999]).Select(f => f.File.FileName));
        Assert.Equal(["cam0.pfx", "cam0-again.pfx"], payload.FilesFor(devices[0]).Select(f => f.File.FileName)); // in the payload's order
        Assert.Equal(["ca.p12"], payload.FilesFor(Guid.Empty).Select(f => f.File.FileName));
        Assert.Empty(payload.FilesFor(Guid.NewGuid()));
        Assert.Same(PkiJson.DeserializeShared<DeletePayload>(json), PkiJson.DeserializeShared<DeletePayload>(json));
        Assert.Throws<ArgumentException>(() => PkiJson.DeserializeShared<InstallPayload>(null));
    }

    [Fact]
    public async Task Ca_only_installs_the_files_of_the_device_and_the_files_for_all_in_the_payloads_order()
    {
        await using var h = await TaskHarness.StartAsync();
        using var first = TestCa.Root("First CA");
        using var second = TestCa.Root("Second CA");
        using var firstPublic = X509CertificateLoader.LoadCertificate(first.RawData);
        using var secondPublic = X509CertificateLoader.LoadCertificate(second.RawData);
        var payload = PkiJson.Serialize(new InstallPayload
        {
            Purpose = InstallPurpose.CaOnly,
            Password = "secret",
            Files = [new InstallFile(Guid.Empty, "all", "all.p12"), new InstallFile(Guid.NewGuid(), "other", "other.p12"), new InstallFile(h.Device.Id, "own", "own.p12")],
        });
        var ctx = new PkiTaskContext(h.Camera);
        ctx.MemoryFiles.Files["all"] = ("all.p12", TestCa.Pfx("secret", firstPublic));
        ctx.MemoryFiles.Files["own"] = ("own.p12", TestCa.Pfx("secret", secondPublic));
        h.Context = ctx;

        await Oadm.Sdk.Testing.StepRun.RunAsync(ctx.Steps, () => h.Pki.Plugin.TaskPlugins.Single(t => t.Id == PkiTaskIds.Install).ExecuteAsync(ctx, h.Device, payload, CancellationToken.None));

        Assert.Equal("all.p12, own.p12 · 2 certificates", h.Detail(PkiSteps.ReadFile));
    }

    // ------------------------------------------------------------ keys over HTTP

    [Theory]
    [InlineData(InstallPurpose.Https)]
    [InlineData(InstallPurpose.Dot1x)]
    public async Task A_key_goes_over_http_only_with_the_answer_in_the_payload(string purpose)
    {
        await using var h = await TaskHarness.StartAsync(new FakeCamera { BaseAddress = new Uri("http://10.0.0.48/") });

        var ex = await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(h, purpose), Pfx(null, ClientAuth));

        Assert.Equal(InstallPayload.KeysOverHttpRefused, ex!.Message);
        Assert.Equal(
            "The device is reached over HTTP: its private key would cross the network unencrypted. Start the task again and tick Send the keys anyway. Nothing was changed.",
            ex.Message);
        Assert.Equal("Check compatibility: Failed", h.Lines[0]);
        Assert.Empty(h.Camera.Requests); // not even a read

        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(h, Payload(h, purpose, sendKeysOverHttp: true), Pfx(null, ClientAuth)));
        Assert.Contains(h.Context.LogEntries, e => e.Message == "cam48.pfx was sent with its private key over HTTP: unencrypted on the network.");
    }

    [Fact]
    public async Task A_payload_of_an_older_client_has_no_answer_and_is_refused_over_http()
    {
        await using var h = await TaskHarness.StartAsync(new FakeCamera { BaseAddress = new Uri("http://10.0.0.48/") });
        var old = $$"""{"purpose":"https","password":"secret","files":[{"deviceId":"{{h.Device.Id}}","fileId":"f1","fileName":"cam48.pfx"}]}""";

        var ex = await RenewDeleteInstallTests.RunWithFileAsync(h, old, Pfx());

        Assert.Equal(InstallPayload.KeysOverHttpRefused, ex!.Message);
        Assert.Empty(h.Camera.Requests);
    }

    [Fact]
    public async Task Without_the_answer_https_devices_and_ca_certificates_run_as_before()
    {
        await using var https = await TaskHarness.StartAsync();
        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(https, Payload(https, InstallPurpose.Https), Pfx()));

        // CA certificates only: no key leaves OADM, also over HTTP.
        await using var http = await TaskHarness.StartAsync(new FakeCamera { BaseAddress = new Uri("http://10.0.0.48/") });
        using var root = TestCa.Root("Customer CA");
        using var publicRoot = X509CertificateLoader.LoadCertificate(root.RawData);
        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(http, Payload(http, InstallPurpose.CaOnly), TestCa.Pfx("secret", publicRoot)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_dialog_puts_the_answer_into_the_payload(bool overHttp)
    {
        var device = new PkiFakeDevice(Guid.NewGuid()) { ConnectsOverHttps = !overHttp };
        var pfx = Pfx();
        var vm = new InstallCertificatesViewModel(new FakeDialogContext(_ => Task.FromResult<string?>(null)), [device], _ => pfx) { Password = "secret" };
        string? payload = null;
        vm.CloseRequested += (_, p) => payload = p;
        vm.Confirm = (_, _, _) => Task.FromResult(true);
        vm.SetFiles(["c:/certs/cam48.pfx"]);
        vm.SendKeysOverHttp = true; // without a device over HTTP the box is not shown and nothing was asked

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(overHttp, PkiJson.Deserialize<InstallPayload>(payload).SendKeysOverHttp);
    }
}
