using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Tasks;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>"Install CA certificates": reading the files (shared by the dialog and the task).</summary>
public sealed class CaCertificateFilesTests
{
    private static DateTime Now => DateTime.UtcNow;

    [Fact]
    public void A_pem_bundle_lists_every_certificate_and_marks_the_non_ca()
    {
        using var root = TestCa.Root("Acme Root CA");
        using var issuing = TestCa.Intermediate(root);
        using var leaf = TestCa.Leaf(issuing);

        var contents = CaCertificateFiles.Read(TestCa.Pem(root, issuing, leaf), Now);

        Assert.Null(contents.Error);
        Assert.Equal(["Acme Root CA", "Acme Issuing CA", "10.0.0.48"], contents.Certificates.Select(c => c.Name));
        Assert.True(contents.Certificates[0].IsUsable);
        Assert.True(contents.Certificates[1].IsUsable);
        Assert.Equal("Acme Root CA", contents.Certificates[1].IssuedBy);
        Assert.Equal(CaCertificates.Fingerprint(root), contents.Certificates[0].Fingerprint);
        Assert.Equal("Not a CA certificate", contents.Certificates[2].ProblemLabel);
        Assert.Equal("This certificate is not a CA certificate (it cannot issue certificates).", contents.Certificates[2].Problem);
    }

    [Fact]
    public void A_der_file_is_one_certificate()
    {
        using var root = TestCa.Root("Customer CA");
        var contents = CaCertificateFiles.Read(root.RawData, Now);
        var certificate = Assert.Single(contents.Certificates);
        Assert.Equal("Customer CA", certificate.Name);
        Assert.True(certificate.IsUsable);
        Assert.Equal(CaCertificates.ToPem(root), certificate.Pem);
        Assert.Equal(root.NotAfter.ToUniversalTime(), certificate.NotAfterUtc);
    }

    [Fact]
    public void Expired_not_yet_valid_and_non_ca_certificates_have_a_problem()
    {
        using var expired = TestCa.Root("Old CA", years: 1, notBefore: DateTimeOffset.UtcNow.AddYears(-3));
        using var future = TestCa.Root("Future CA", notBefore: DateTimeOffset.UtcNow.AddDays(10));
        using var notCa = TestCa.Root("Plain", ca: false);
        using var noSigning = TestCa.Root("No signing", keyCertSign: false);

        var list = CaCertificateFiles.Read(TestCa.Pem(expired, future, notCa, noSigning), Now).Certificates;

        Assert.Equal(["Expired", "Not valid yet", "Not a CA certificate", "Not a CA certificate"], list.Select(c => c.ProblemLabel));
        Assert.StartsWith("The certificate expired on ", list[0].Problem, StringComparison.Ordinal);
        Assert.Equal("This CA certificate is not allowed to sign certificates.", list[3].Problem);
        Assert.All(list, c => Assert.False(c.IsUsable));
    }

    [Fact]
    public void Garbage_empty_large_and_key_only_files_are_errors()
    {
        Assert.Equal(CaCertificateFiles.NotACertificate, CaCertificateFiles.Read(TestCa.Ascii("hello, not a certificate"), Now).Error);
        Assert.Equal(CaCertificateFiles.NotACertificate, CaCertificateFiles.Read([0x30, 0x82, 0x01, 0x00, 0xFF], Now).Error);
        Assert.Equal("The file is empty.", CaCertificateFiles.Read([], Now).Error);
        Assert.Equal("The file is larger than 1 MB.", CaCertificateFiles.Read(new byte[CaCertificateFiles.MaxBytes + 1], Now).Error);
        using var key = System.Security.Cryptography.RSA.Create(2048);
        Assert.Equal(CaCertificateFiles.NoCertificate, CaCertificateFiles.Read(TestCa.Ascii(key.ExportPkcs8PrivateKeyPem()), Now).Error);
        using var root = TestCa.Root();
        Assert.Equal(CaCertificateFiles.NotACertificate, CaCertificateFiles.Read(TestCa.Pfx("pw", root), Now).Error); // PKCS#12 is not a CA file
    }

    [Fact]
    public void Short_fingerprint_shows_the_first_eight_bytes()
    {
        Assert.Equal("01:23:45:67:89:AB:CD:EF…", CaCertificateFiles.ShortFingerprint("0123456789ABCDEF0011"));
        Assert.Equal("01:23", CaCertificateFiles.ShortFingerprint("0123"));
    }
}

/// <summary>"Install CA certificates" against the fake 10.0.0.48.</summary>
public sealed class InstallCaTaskTests
{
    private static string Payload(params X509Certificate2[] certificates) =>
        PkiJson.Serialize(new InstallCaPayload { Certificates = [.. certificates.Select(c => new CaCertificatePayload(CaCertificates.CommonName(c), CaCertificates.ToPem(c)))] });

    private static string Alias(X509Certificate2 certificate) =>
        CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, CaCertificates.Fingerprint(certificate));

    [Fact]
    public async Task Installs_every_certificate_with_an_oadm_alias_and_verifies()
    {
        await using var h = await TaskHarness.StartAsync();
        using var root = TestCa.Root("Acme Root CA");
        using var issuing = TestCa.Intermediate(root);

        Assert.Null(await h.RunAsync(PkiTaskIds.InstallCa, Payload(root, issuing)));

        Assert.Equal(
            ["Check compatibility: Done", "Read installed CA certificates: Done", "Install CA certificate Acme Root CA: Done",
             "Install CA certificate Acme Issuing CA: Done", "Verify CA certificates: Done", "Completed: Done"],
            h.Lines);
        Assert.Contains(Alias(root), h.Camera.CaAliases);
        Assert.Contains(Alias(issuing), h.Camera.CaAliases);
        Assert.Equal(Alias(root), h.Detail("Install CA certificate Acme Root CA"));
        Assert.Equal(CaCertificates.ToPem(root), h.Camera.CaCertificatePem(Alias(root)));
        Assert.Equal(2, h.Camera.Writes.Count);
        Assert.All(h.Camera.Writes, w => Assert.Equal("POST", w.Method));
    }

    [Fact]
    public async Task A_certificate_the_device_already_has_is_skipped_under_its_own_alias()
    {
        await using var h = await TaskHarness.StartAsync();
        using var root = TestCa.Root("Acme Root CA");
        using var other = TestCa.Root("Customer CA");
        h.Camera.AddCaCertificate("Customer root (installed by hand)", CaCertificates.ToPem(root));

        Assert.Null(await h.RunAsync(PkiTaskIds.InstallCa, Payload(root, other)));

        Assert.Equal(
            ["Check compatibility: Done", "Read installed CA certificates: Done", "Install CA certificate Acme Root CA: Skipped",
             "Install CA certificate Customer CA: Done", "Verify CA certificates: Done", "Completed: Done"],
            h.Lines);
        Assert.Equal("Already installed", h.Detail("Install CA certificate Acme Root CA"));
        Assert.DoesNotContain(Alias(root), h.Camera.CaAliases);
        Assert.Single(h.Camera.Writes);

        // A second run changes nothing.
        Assert.Null(await h.RunAsync(PkiTaskIds.InstallCa, Payload(root, other)));
        Assert.Single(h.Camera.Writes);
        Assert.Equal("All CA certificates were already installed", h.Detail("Verify CA certificates"));
    }

    [Fact]
    public async Task An_older_device_or_an_invalid_certificate_changes_nothing()
    {
        await using var h = await TaskHarness.StartAsync();
        using var root = TestCa.Root("Acme Root CA");
        h.Camera.HasCertApi = false;
        var ex = await h.RunAsync(PkiTaskIds.InstallCa, Payload(root));
        Assert.Equal(CertApi.NeedsNewerFirmware, ex!.Message);
        Assert.Equal("Check compatibility: Failed", h.Lines[0]);
        Assert.Empty(h.Camera.Writes);

        // An expired or non-CA certificate in the payload fails before any request.
        h.Camera.HasCertApi = true;
        h.Camera.Requests.Clear();
        using var expired = TestCa.Root("Old CA", years: 1, notBefore: DateTimeOffset.UtcNow.AddYears(-3));
        ex = await h.RunAsync(PkiTaskIds.InstallCa, Payload(root, expired));
        Assert.StartsWith("Old CA: The certificate expired on ", ex!.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Check compatibility: Failed", h.Lines[0]);
        Assert.Empty(h.Camera.Requests);

        using var plain = TestCa.Root("Plain", ca: false);
        ex = await h.RunAsync(PkiTaskIds.InstallCa, Payload(plain));
        Assert.Contains("not a CA certificate", ex!.Message, StringComparison.Ordinal);
        ex = await h.RunAsync(PkiTaskIds.InstallCa, PkiJson.Serialize(new InstallCaPayload { Certificates = [new("Broken", "garbage")] }));
        Assert.Equal("Broken: The file is not a certificate (PEM or DER). Nothing was changed.", ex!.Message);
        ex = await h.RunAsync(PkiTaskIds.InstallCa, PkiJson.Serialize(new InstallCaPayload()));
        Assert.Equal("No CA certificate was chosen. Nothing was changed.", ex!.Message);
        Assert.Empty(h.Camera.Requests);
    }

    [Fact]
    public async Task Duplicates_are_installed_once_and_equal_names_get_a_number()
    {
        await using var h = await TaskHarness.StartAsync();
        using var first = TestCa.Root("Acme Root CA");
        using var second = TestCa.Root("Acme Root CA");

        Assert.Null(await h.RunAsync(PkiTaskIds.InstallCa, Payload(first, second, first)));

        Assert.Equal(
            ["Check compatibility: Done", "Read installed CA certificates: Done", "Install CA certificate Acme Root CA: Done",
             "Install CA certificate Acme Root CA (2): Done", "Verify CA certificates: Done", "Completed: Done"],
            h.Lines);
        Assert.Equal(2, h.Camera.Writes.Count);
    }

    [Fact]
    public async Task Task_names_say_what_is_installed()
    {
        await using var h = await TaskHarness.StartAsync();
        using var root = TestCa.Root("Acme Root CA");
        using var other = TestCa.Root("Customer CA");
        var task = h.Task<InstallCaTask>(PkiTaskIds.InstallCa);

        Assert.Equal("Install CA certificate Acme Root CA", task.GetTaskName(Payload(root)));
        Assert.Equal("Install 2 CA certificates", task.GetTaskName(Payload(root, other)));
        Assert.Equal("Install CA certificates", task.GetTaskName(null));
        Assert.True(task.RequiresDialog);
        Assert.Equal("upload", task.IconKey);
    }
}
