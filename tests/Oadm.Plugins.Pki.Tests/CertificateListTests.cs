using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The Certificates card (docs/spec/pki.md "Certificates"): every certificate in one paged list (CAs, server certificates,
/// certificates created, signed or imported here, device certificates), Create, Sign request, Import, Export and Delete with
/// their refusals, the keys encrypted with the master key, and who may do what.
/// </summary>
public sealed class CertificateListTests
{
    private const string FilePassword = "correct horse";

    internal static async Task<CertificateActionReply> CreateAsync(PkiHarness pki, string use = DeviceCertificatePurposes.Https, string name = "nvr.example.com", string addresses = "nvr.example.com, 10.0.0.20", Guid? policy = null)
    {
        var kind = CertificateRules.PolicyKindOf(use) ?? PolicyKinds.HttpsCertificate;
        return await pki.InvokeAsync<CertificateActionReply>(CertificateMethods.Create,
            new CreateCertificateRequest(use, policy ?? pki.Policies.FirstOf(kind)!.Id, name, addresses));
    }

    internal static async Task<byte[]> DownloadAsync(PkiHarness pki, string id, string format, string? password = null)
    {
        await using var download = await pki.Plugin.OpenDownloadAsync(CertificateMethods.Download, PkiJson.Serialize(new DownloadCertificateRequest(id, format, password)), CancellationToken.None);
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static Task<CertificatesReply> PageAsync(PkiHarness pki, CertificatesRequest? request = null) =>
        pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, request ?? new CertificatesRequest());

    private static byte[] Csr(AsymmetricAlgorithm key, string subject = "CN=nvr.example.com", HashAlgorithmName? hash = null, params string[] dns)
    {
        var request = key is RSA rsa
            ? new CertificateRequest(subject, rsa, hash ?? HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : new CertificateRequest(subject, (ECDsa)key, hash ?? HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dns)
        {
            if (IPAddress.TryParse(name, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(name);
            }
        }

        if (dns.Length > 0)
        {
            request.CertificateExtensions.Add(san.Build());
        }

        return request.CreateSigningRequest();
    }

    [Fact]
    public async Task Create_checks_every_field_then_keeps_the_key_encrypted_and_exports_it_as_pkcs12()
    {
        await using var pki = await PkiHarness.StartAsync();
        var dot1x = pki.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!.Id;

        // Refusals below their fields; nothing is created.
        var refused = await pki.InvokeAsync<CertificateActionReply>(CertificateMethods.Create, new CreateCertificateRequest("telnet", Guid.Empty, " ", "bad name!"));
        Assert.Equal([CertificateFields.Addresses, CertificateFields.Name, CertificateFields.Policy, CertificateFields.Use], refused.Errors!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(CertificateRules.NeedsAddress, (await CreateAsync(pki, addresses: " ")).Errors![CertificateFields.Addresses]);
        Assert.Equal(CertificateRules.ChoosePolicy, (await CreateAsync(pki, policy: dot1x)).Errors![CertificateFields.Policy]); // an IEEE 802.1X policy for HTTPS
        Assert.Equal(CertificateRules.PolicyGone, (await CreateAsync(pki, policy: Guid.NewGuid())).Errors![CertificateFields.Policy]);
        Assert.Equal("\"cam_1\" is no host name or IP address.", (await CreateAsync(pki, addresses: "cam_1")).Errors![CertificateFields.Addresses]);
        Assert.Empty(pki.Service.Local.List);

        var created = await CreateAsync(pki);
        Assert.True(created.Ok, created.Error);
        var row = (await PageAsync(pki)).Rows.Single(r => r.Id == created.Id);
        Assert.Equal((ListedCertificateKind.Created, "nvr.example.com", DeviceCertificatePurposes.Https, "OADM Root CA SERVER01", true, IssuedCertificateState.Valid),
            (row.Kind, row.Name, row.Purpose, row.IssuedBy, row.HasKey, row.State));

        // The key is kept encrypted with the master key, never in plain text.
        var stored = Assert.Single(await pki.Records.ListAsync(LocalCertificates.Collection, CancellationToken.None)).Value;
        Assert.DoesNotContain("PRIVATE KEY", stored, StringComparison.Ordinal);
        var record = PkiJson.Deserialize<StoredLocalCertificate>(stored);
        Assert.NotNull(record.KeyProtected);
        Assert.NotNull(pki.Secrets!.Unprotect(record.KeyProtected!, "pki:certificate:" + record.Id));

        // PKCS#12 round trip: the certificate with its key and the OADM CA.
        var pfx = await DownloadAsync(pki, created.Id!, CertificateFormats.Pkcs12, FilePassword);
        var collection = X509CertificateLoader.LoadPkcs12Collection(pfx, FilePassword, OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        try
        {
            var leaf = Assert.Single(collection, c => c.HasPrivateKey);
            Assert.Equal("nvr.example.com", leaf.GetNameInfo(X509NameType.SimpleName, false));
            Assert.Equal(["nvr.example.com", "10.0.0.20"], CertificateFiles.NamesOf(leaf));
            Assert.Contains(collection, c => c.Thumbprint == pki.Service.Ca!.Certificate.Thumbprint);
            using var key = leaf.GetECDsaPrivateKey(); // the policy "HTTPS" of a new server: ECC
            Assert.NotNull(key);
            Assert.Contains(leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == DeviceCertificateIssuer.ServerAuthOid);
        }
        finally
        {
            foreach (var certificate in collection)
            {
                certificate.Dispose();
            }
        }

        // A short password is refused; PEM and DER need none.
        var shortPassword = await Assert.ThrowsAsync<InvalidOperationException>(() => DownloadAsync(pki, created.Id!, CertificateFormats.Pkcs12, "short"));
        Assert.Equal("Use at least 8 characters.", shortPassword.Message);
        var der = await DownloadAsync(pki, created.Id!, CertificateFormats.Der);
        using var fromDer = X509CertificateLoader.LoadCertificate(der);
        Assert.Contains("-----BEGIN CERTIFICATE-----", System.Text.Encoding.ASCII.GetString(await DownloadAsync(pki, created.Id!, CertificateFormats.Pem)), StringComparison.Ordinal);

        // An IEEE 802.1X certificate is a client certificate; its addresses are optional.
        var client = await CreateAsync(pki, DeviceCertificatePurposes.Dot1x, "door-controller", addresses: "");
        Assert.True(client.Ok, client.Error);
    }

    [Fact]
    public async Task Sign_request_checks_the_request_and_issues_with_its_names_without_a_key()
    {
        await using var pki = await PkiHarness.StartAsync();
        var https = pki.Policies.FirstOf(PolicyKinds.HttpsCertificate)!; // ECC on a new server
        Task<CertificateActionReply> SignAsync(byte[] csr, Guid? policy = null) =>
            pki.InvokeAsync<CertificateActionReply>(CertificateMethods.Sign, new SignCertificateRequest(policy ?? https.Id, Convert.ToBase64String(csr), "request.csr"));
        string? FileError(CertificateActionReply reply) => reply.Errors?.GetValueOrDefault(CertificateFields.File);

        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var good = Csr(ec, "CN=nvr.example.com", null, "nvr.example.com", "10.0.0.20");
        var signed = await SignAsync(System.Text.Encoding.ASCII.GetBytes(PemEncoding.WriteString("CERTIFICATE REQUEST", good)));
        Assert.True(signed.Ok, FileError(signed) ?? signed.Error);
        var row = (await PageAsync(pki)).Rows.Single(r => r.Id == signed.Id);
        Assert.Equal((ListedCertificateKind.SignedRequest, "nvr.example.com", false), (row.Kind, row.Name, row.HasKey));
        using (var certificate = X509CertificateLoader.LoadCertificate(await DownloadAsync(pki, signed.Id!, CertificateFormats.Der)))
        {
            Assert.Equal(["nvr.example.com", "10.0.0.20"], CertificateFiles.NamesOf(certificate));
            using var issuedKey = certificate.GetECDsaPublicKey();
            Assert.Equal(ec.ExportSubjectPublicKeyInfo(), issuedKey!.ExportSubjectPublicKeyInfo()); // the request's key: it never left the other system
        }

        // OADM holds no key of a signed request: no PKCS#12.
        Assert.Equal(CertificateRules.NoKeyHere, (await Assert.ThrowsAsync<InvalidOperationException>(() => DownloadAsync(pki, signed.Id!, CertificateFormats.Pkcs12, FilePassword))).Message);

        // A wrong signature.
        var tampered = (byte[])good.Clone();
        tampered[^3] ^= 0x55;
        Assert.Equal("The request cannot be read or its signature is wrong.", FileError(await SignAsync(tampered)));

        // Unsupported or weak keys, a key of another kind than the policy's, a weak digest.
        using var rsa = RSA.Create(2048);
        Assert.Equal("The request has an RSA key. The policy HTTPS uses ECC.", FileError(await SignAsync(Csr(rsa, "CN=x", null, "x.example.com"))));
        using var weak = RSA.Create(1024);
        Assert.StartsWith("The request has a weak key (RSA 1024)", FileError(await SignAsync(Csr(weak, "CN=x", null, "x.example.com"))), StringComparison.Ordinal);
        if (!OperatingSystem.IsMacOS()) // macOS has no P-224 at all: it cannot make the key, nor read such a request
        {
            using var p224 = ECDsa.Create(ECCurve.CreateFromValue("1.3.132.0.33"));
            Assert.Equal("The request has an ECC key on a curve OADM does not support. Use P-256 or larger.", FileError(await SignAsync(Csr(p224, "CN=x", null, "x.example.com"))));
        }

        // Names: an HTTPS certificate needs one; a name that is no host name is refused; no file, garbage.
        Assert.Equal("The request names no host name or IP address. An HTTPS certificate needs one.", FileError(await SignAsync(Csr(ec, "CN=nvr"))));
        Assert.Equal("The request asks for \"bad_name\", which is no host name.", FileError(await SignAsync(Csr(ec, "CN=x", null, "bad_name"))));
        Assert.Equal("The file is no certificate request (PKCS#10).", FileError(await SignAsync(TestCa.Ascii("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n"))));
        Assert.Equal("Choose a file.", FileError(await pki.InvokeAsync<CertificateActionReply>(CertificateMethods.Sign, new SignCertificateRequest(https.Id, null, null))));
        Assert.Single(pki.Service.Local.List); // only the good one
    }

    [Fact]
    public async Task Create_and_sign_request_never_name_a_camera_and_what_they_make_is_never_a_devices_certificate()
    {
        // EST and the 802.1X server take a camera's serial from an axis-<serial> name of an OADM CA certificate: Create and
        // Sign request refuse such names (any domain, any case), below their fields.
        await using var pki = await PkiHarness.StartAsync();
        var https = pki.Policies.FirstOf(PolicyKinds.HttpsCertificate)!;
        Assert.Equal(CertificateRules.CameraName, (await CreateAsync(pki, name: "AXIS-ACCC8E000001.local")).Errors![CertificateFields.Name]);
        Assert.Equal(CertificateRules.CameraName, (await CreateAsync(pki, DeviceCertificatePurposes.Dot1x, "axis-accc8e000001", addresses: "")).Errors![CertificateFields.Name]);
        Assert.Equal("\"axis-ACCC8E000001.example.com\" belongs to an Axis camera.",
            (await CreateAsync(pki, addresses: "nvr.example.com axis-ACCC8E000001.example.com")).Errors![CertificateFields.Addresses]);
        Assert.Null(CertificateRules.Name("axis-lobby.example.com")); // only the camera pattern

        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Task<CertificateActionReply> SignAsync(byte[] csr) =>
            pki.InvokeAsync<CertificateActionReply>(CertificateMethods.Sign, new SignCertificateRequest(https.Id, Convert.ToBase64String(csr), "request.csr"));
        Assert.Equal("The request asks for \"axis-accc8e000001.local\", which belongs to an Axis camera.",
            (await SignAsync(Csr(ec, "CN=nvr.example.com", null, "nvr.example.com", "axis-accc8e000001.local"))).Errors![CertificateFields.File]);
        Assert.Equal("The name of the request: " + CertificateRules.CameraName,
            (await SignAsync(Csr(ec, "CN=Axis-ACCC8E000001.local", null, "nvr.example.com"))).Errors![CertificateFields.File]);
        Assert.Empty(pki.Service.Local.List);

        // Whatever a row of the list names (here one forced into the store with a camera's name and even listed as a device
        // certificate), PKI says it is no device's certificate: EST and the 802.1X server refuse it.
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        var created = await CreateAsync(pki, DeviceCertificatePurposes.Dot1x, "door-controller", addresses: "");
        var createdSerial = pki.Service.Local.Find(created.Id![LocalCertificates.RowPrefix.Length..])!.Serial;
        Assert.Equal(PkiCertificateAuthority.NotDevicePurpose, authority.PurposeOf(createdSerial.ToLowerInvariant()));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csrPem = new CertificateRequest("CN=x", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        using var crafted = DeviceCertificateIssuer.Sign(pki.Service.Ca!, csrPem, new CertificateNames("axis-accc8e000001.local", [], ["axis-accc8e000001.local"]),
            CertificatePurpose.Dot1x, pki.Service.Time.GetUtcNow(), TimeSpan.FromDays(30));
        var forced = await pki.Service.Local.AddAsync(new StoredLocalCertificate
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = ListedCertificateKind.Created,
            Name = "axis-accc8e000001.local",
            Purpose = DeviceCertificatePurposes.Dot1x,
            Der = Convert.ToBase64String(crafted.RawData),
            AddedUtc = pki.Service.Time.GetUtcNow().UtcDateTime,
        }, null, CancellationToken.None);
        Assert.Null(forced.Error);
        await pki.Service.Issued.AddAsync(new IssuedCertificate
        {
            SerialNumber = crafted.SerialNumber,
            DeviceId = Guid.NewGuid(),
            DeviceSerial = "ACCC8E000001",
            Purpose = DeviceCertificatePurposes.Dot1x,
            NotBeforeUtc = crafted.NotBefore.ToUniversalTime(),
            NotAfterUtc = crafted.NotAfter.ToUniversalTime(),
            IssuedUtc = pki.Service.Time.GetUtcNow().UtcDateTime,
        }, crafted.RawData, CancellationToken.None);
        Assert.Equal(PkiCertificateAuthority.NotDevicePurpose, authority.PurposeOf(crafted.SerialNumber));

        // A device certificate stays one.
        var device = await RevocationTests.SignAsync(authority, Guid.NewGuid(), use: DeviceCertificateUse.Client);
        Assert.Equal(DeviceCertificatePurposes.Dot1x, authority.PurposeOf(device.SerialNumber));
    }

    [Fact]
    public async Task Import_takes_a_certificate_or_a_pkcs12_file_with_its_key_and_refuses_what_does_not_belong()
    {
        await using var pki = await PkiHarness.StartAsync();
        Task<CertificateActionReply> ImportAsync(byte[] file, string? password = null) =>
            pki.InvokeAsync<CertificateActionReply>(CertificateMethods.Import, new ImportCertificateRequest(Convert.ToBase64String(file), "file", password));
        using var root = TestCa.Root("Contoso Root");
        using var leaf = TestCa.Leaf(root, "10.0.0.30");
        using var rsa = RSA.Create(2048);
        using var other = new CertificateRequest("CN=10.0.0.31", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300), [0x22, 0x33]);

        // A PEM certificate: no key, issued by a CA PKI does not list.
        var pem = await ImportAsync(TestCa.Pem(leaf));
        Assert.True(pem.Ok, pem.Error);
        var row = (await PageAsync(pki)).Rows.Single(r => r.Id == pem.Id);
        Assert.Equal((ListedCertificateKind.Imported, "10.0.0.30", false, "Contoso Root", ListedCertificateUses.Other), (row.Kind, row.Name, row.HasKey, row.IssuedBy, row.Purpose));
        Assert.Equal(1, (await PageAsync(pki, new CertificatesRequest { IssuedBy = PkiService.OtherIssuerId })).Total);
        Assert.Equal(CertificateRules.AlreadyListed, (await ImportAsync(TestCa.Pem(leaf))).Errors![CertificateFields.File]);

        // A PKCS#12 file with its password: the key is kept (encrypted) and exported again.
        using var withKey = other.CopyWithPrivateKey(rsa);
        using var publicRoot = X509CertificateLoader.LoadCertificate(root.RawData);
        var pfx = TestCa.Pfx(FilePassword, withKey, publicRoot); // with its chain
        // Only a PKCS#12 file asks for another password (below Password); a file that is none cannot be read (below File).
        var wrong = await ImportAsync(pfx, "wrong password");
        Assert.Equal(new KeyValuePair<string, string>(CertificateFields.Password, CertificateFiles.WrongPassword), Assert.Single(wrong.Errors!));
        foreach (var unreadable in new[] { TestCa.Ascii("nonsense"), [0x30, 0x03, 0x02, 0x01, 0x05], pfx[..(pfx.Length / 2)] })
        {
            var refused = await ImportAsync(unreadable, FilePassword);
            Assert.Equal(new KeyValuePair<string, string>(CertificateFields.File, CertificateRules.Unreadable), Assert.Single(refused.Errors!));
        }

        Assert.True(PkiService.LooksLikePkcs12(pfx));
        var imported = await ImportAsync(pfx, FilePassword);
        Assert.True(imported.Ok, imported.Error ?? string.Join(", ", imported.Errors?.Values ?? []));
        Assert.True((await PageAsync(pki)).Rows.Single(r => r.Id == imported.Id).HasKey);
        var again = await DownloadAsync(pki, imported.Id!, CertificateFormats.Pkcs12, "another password");
        using (var back = X509CertificateLoader.LoadPkcs12(again, "another password", OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet))
        {
            Assert.Equal(other.Thumbprint, back.Thumbprint);
            using var key = back.GetRSAPrivateKey();
            Assert.Equal(rsa.ExportSubjectPublicKeyInfo(), key!.ExportSubjectPublicKeyInfo());
        }

        // What does not belong here.
        Assert.Equal("This is a CA certificate. Use Import > CA certificate in Certificate authorities.", (await ImportAsync(TestCa.Pem(root))).Errors![CertificateFields.File]);
        Assert.Equal("The file contains a private key. Import a .pfx or .p12 file to keep a key.",
            (await ImportAsync(TestCa.Ascii(PemEncoding.WriteString("CERTIFICATE", leaf.RawData) + "\n" + rsa.ExportPkcs8PrivateKeyPem()))).Errors![CertificateFields.File]);
        Assert.Equal("The file holds 2 certificates. Import one at a time.", (await ImportAsync(TestCa.Pem(leaf, other))).Errors![CertificateFields.File]);
        using var expiredRoot = TestCa.Root("Old Root", years: 1, notBefore: DateTimeOffset.UtcNow.AddYears(-2));
        using var expired = TestCa.Leaf(expiredRoot);
        Assert.Equal("This certificate has expired.", (await ImportAsync(TestCa.Pem(expired))).Errors![CertificateFields.File]);
        Assert.Equal(2, pki.Service.Local.List.Count);
    }

    [Fact]
    public async Task Delete_takes_every_row_but_a_ca_all_or_nothing_and_a_deleted_valid_certificate_is_no_longer_accepted()
    {
        // Decision 2026-10-26: Delete is the only action, it never asks for a revoke first. A still valid certificate OADM
        // issued goes on PKI's internal list of certificates no longer accepted (the 802.1X and EST servers refuse it).
        await using var pki = await PkiHarness.StartAsync();
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        var device = Guid.NewGuid();
        var valid = (await RevocationTests.SignAsync(authority, device)).SerialNumber;
        var other = (await RevocationTests.SignAsync(authority, device, use: DeviceCertificateUse.Client)).SerialNumber;
        var created = (await CreateAsync(pki)).Id!;
        var createdSerial = pki.Service.Local.Find(created[LocalCertificates.RowPrefix.Length..])!.Serial;
        var ca = (await PageAsync(pki)).Rows.First(r => r.Kind == ListedCertificateKind.Authority).Id!;
        Task<DeleteCertificatesReply> DeleteAsync(params string[] ids) => pki.InvokeAsync<DeleteCertificatesReply>(CertificateMethods.Delete, new DeleteCertificatesRequest(ids));

        // A CA among the rows: refused with the reason, nothing deleted (CAs are deleted in their own list).
        Assert.Equal("Remove CAs in Certificate authorities. Nothing was deleted.", (await DeleteAsync(valid, created, ca)).Error);
        Assert.Equal(PkiCertificateTexts.ChooseOne, (await DeleteAsync()).Error);
        Assert.NotNull(pki.Service.Local.Find(created[LocalCertificates.RowPrefix.Length..]));
        Assert.NotNull(pki.Service.Issued.Find(valid));
        Assert.False(authority.IsRevoked(valid));

        // An imported one goes at once (OADM did not issue it: nothing to refuse).
        using var root = TestCa.Root("Contoso Root");
        using var leaf = TestCa.Leaf(root, "10.0.0.30");
        var imported = await pki.InvokeAsync<CertificateActionReply>(CertificateMethods.Import, new ImportCertificateRequest(Convert.ToBase64String(TestCa.Pem(leaf)), "leaf.crt", null));
        Assert.Equal(1, (await DeleteAsync(imported.Id!)).Deleted);

        // A still valid device certificate and a created one: deleted together, both no longer accepted ("Certificate deleted in OADM").
        var deleted = await DeleteAsync(valid, created);
        Assert.Equal((2, (string?)null), (deleted.Deleted, deleted.Error));
        Assert.True(authority.IsRevoked(valid));
        Assert.True(authority.IsRevoked(createdSerial));
        Assert.Equal(NotAcceptedTexts.Deleted, NotAcceptedTexts.Of(authority.FindRevocation(valid)));
        Assert.Null(pki.Service.Issued.Find(valid));
        Assert.Empty(pki.Service.Local.List);
        Assert.Empty(await pki.Records.ListAsync(LocalCertificates.Collection, CancellationToken.None));
        var rows = (await PageAsync(pki)).Rows;
        Assert.DoesNotContain(rows, r => r.SerialNumber == CertificateSerials.Normalize(valid)); // the list of no longer accepted ones is never shown
        Assert.NotNull(pki.Service.Issued.Find(other)); // the other certificate of the device stays
        Assert.False(authority.IsRevoked(other));
    }

    [Fact]
    public async Task Export_gives_pem_and_der_of_every_row_and_never_a_ca_or_device_key()
    {
        await using var pki = await PkiHarness.StartAsync();
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        var issued = await RevocationTests.SignAsync(authority, Guid.NewGuid());
        var ca = (await PageAsync(pki)).Rows.First(r => r.Kind == ListedCertificateKind.Authority).Id!;

        Assert.Equal(issued.Certificate, await DownloadAsync(pki, issued.SerialNumber, CertificateFormats.Der));
        Assert.Equal(pki.Service.Ca!.Certificate.RawData, await DownloadAsync(pki, ca, CertificateFormats.Der));
        Assert.Equal(CertificateRules.NoCaKey, (await Assert.ThrowsAsync<InvalidOperationException>(() => DownloadAsync(pki, ca, CertificateFormats.Pkcs12, FilePassword))).Message);
        Assert.Equal(CertificateRules.NoKeyHere, (await Assert.ThrowsAsync<InvalidOperationException>(() => DownloadAsync(pki, issued.SerialNumber, CertificateFormats.Pkcs12, FilePassword))).Message);
        Assert.Equal(CertificateRules.NotListed, (await Assert.ThrowsAsync<InvalidOperationException>(() => DownloadAsync(pki, "local:nothing", CertificateFormats.Pem))).Message);
        Assert.Equal(CertificateRules.ChooseFormat, (await Assert.ThrowsAsync<InvalidOperationException>(() => DownloadAsync(pki, ca, "zip"))).Message);

        // The audit entry names the certificate and the format, never the password.
        await using var download = await pki.Plugin.OpenDownloadAsync(CertificateMethods.Download, PkiJson.Serialize(new DownloadCertificateRequest(ca, CertificateFormats.Pem)), CancellationToken.None);
        Assert.Equal("Export certificate OADM Root CA SERVER01 (PEM)", download.AuditDetail);
        Assert.Equal("OADM Root CA SERVER01.crt", download.FileName);
    }

    [Fact]
    public async Task The_servers_certificates_are_listed_as_used_by_their_server_and_cannot_be_deleted()
    {
        await using var pki = await PkiHarness.StartAsync();
        var authority = new PkiCertificateAuthority(pki.Service);
        await authority.SignAsync(new DeviceCertificateRequest(RevocationTests.Csr(), "est.example.com", ["10.0.0.5"], ["est.example.com"], DeviceCertificateUse.Server)
        {
            Source = IssuedCertificateSources.Est,
        }, CancellationToken.None);

        var row = Assert.Single((await PageAsync(pki, new CertificatesRequest { Purpose = ListedCertificateUses.Server })).Rows);
        Assert.Equal((ListedCertificateKind.Server, "est.example.com", "EST server", "OADM Root CA SERVER01"), (row.Kind, row.Name, row.UsedBy, row.IssuedBy));
        Assert.Equal("Used by the EST server. Nothing was deleted.",
            (await pki.InvokeAsync<DeleteCertificatesReply>(CertificateMethods.Delete, new DeleteCertificatesRequest([row.Id!]))).Error);
        Assert.Empty(pki.Service.Issued.Snapshot()); // no device certificate
    }

    [Fact]
    public async Task The_search_looks_in_every_column_and_the_filters_combine()
    {
        var devices = new[] { new PkiFakeDevice(Guid.NewGuid(), "10.0.0.48", "B8A44F631339") };
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)); // never across a month end
        await using var pki = await PkiHarness.StartAsync(devices: devices, time: time);
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        await RevocationTests.SignAsync(authority, devices[0].Id);
        await CreateAsync(pki, name: "nvr.example.com");
        async Task<int> CountAsync(string search, string? purpose = null) => (await PageAsync(pki, new CertificatesRequest { Search = search, Purpose = purpose })).Total;

        Assert.Equal(1, await CountAsync("nvr.example"));
        Assert.Equal(1, await CountAsync("10.0.0.48")); // the device (name and used by)
        Assert.Equal(3, await CountAsync("HTTPS")); // the two certificates and the OADM CA (Used by: "Issuing: HTTPS, ...")
        Assert.Equal(1, await CountAsync("HTTPS", ListedCertificateUses.Ca)); // the OADM CA by its Used by, nothing of another use
        Assert.Equal(0, await CountAsync("Axis device ID", DeviceCertificatePurposes.Https));
        Assert.True(await CountAsync("Axis device ID") >= 2); // every Axis CA certificate is issued by the Axis device ID
        Assert.True(await CountAsync("OADM Root CA SERVER01") >= 3);
        Assert.Equal(await CountAsync("Valid"), await CountAsync("valid")); // case-insensitive
        Assert.Equal(2, await CountAsync("2027-10", DeviceCertificatePurposes.Https));

        var mine = (await PageAsync(pki, new CertificatesRequest { State = IssuedCertificateState.Valid, Purpose = DeviceCertificatePurposes.Https, IssuedBy = CertificateAuthorityInfo.OadmId })).Rows;
        Assert.Equal(2, mine.Count);
        Assert.Equal("10.0.0.48", mine.Single(r => r.Kind == ListedCertificateKind.Device).UsedBy); // installed on its device
    }

    [Fact]
    public async Task Pages_run_across_the_rows_before_the_device_certificates()
    {
        await using var pki = await PkiHarness.StartAsync();
        var now = DateTime.UtcNow;
        await pki.SetIssuedAsync(Enumerable.Range(0, 700).Select(i => new IssuedCertificate
        {
            SerialNumber = "A" + i.ToString("X5", System.Globalization.CultureInfo.InvariantCulture),
            DeviceId = Guid.NewGuid(),
            Purpose = CertificatePurpose.Https,
            IssuedUtc = now.AddMinutes(-i),
            NotAfterUtc = now.AddDays(300),
            CaId = pki.Service.Ca!.Id,
        }));
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await CreateAsync(pki, name: $"nvr{i}.example.com", addresses: $"nvr{i}.example.com")).Ok);
        }

        var first = await PageAsync(pki);
        var before = first.Total - 700;
        Assert.True(before >= 5); // the OADM CA, the Axis device ID CAs, the three created ones
        Assert.Equal(["nvr2.example.com", "nvr1.example.com", "nvr0.example.com"], first.Rows.Where(r => r.Kind == ListedCertificateKind.Created).Select(r => r.Name)); // newest first
        Assert.Equal("A00000", first.Rows[before].SerialNumber);
        var second = await PageAsync(pki, new CertificatesRequest { Offset = 500 });
        Assert.Equal(700 + before - 500, second.Rows.Count);
        Assert.Equal(first.Rows.Concat(second.Rows).Select(r => r.Id ?? r.SerialNumber).Distinct().Count(), first.Total); // no row twice, none missing
        Assert.All(second.Rows, r => Assert.Equal(ListedCertificateKind.Device, r.Kind));
        Assert.All(second.Rows, r => Assert.Equal("OADM Root CA SERVER01", r.IssuedBy));
    }

    [Fact]
    public async Task Every_action_needs_manage_and_all_devices_and_is_audited_a_group_user_gets_none()
    {
        var plugin = new PkiPlugin();
        foreach (var method in CertificateMethods.All)
        {
            Assert.Equal(UserRole.Admin, ((ICorePlugin)plugin).RequiredRole(method));
            Assert.True(((ICorePlugin)plugin).IsAudited(method), method);
            Assert.True(((ICorePlugin)plugin).RequiresAllDevices(method), method);
            Assert.Null(((ICorePlugin)plugin).RequiredPermission(method)); // the plugin's "manage"
        }

        var ctx = new FakeCoreContext() { Secrets = TestSecrets.Create() };
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.48", Serial = "B8A44F631339" });
        await using var started = new PkiPlugin(PkiHarness.Options(new FakeTrustStore()));
        await started.StartAsync(ctx, CancellationToken.None);
        await started.Service!.Background;
        var group = FakeCallContext.WithGroup(ctx, [inside.Id], "tech2", ctx.FakeScopes.Add("Building B", [inside.Id]));
        var policy = started.Service.Policies.FirstOf(PolicyKinds.HttpsCertificate)!.Id;
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ctx.InvokeAsync(started, group, CertificateMethods.Create,
            PkiJson.Serialize(new CreateCertificateRequest(DeviceCertificatePurposes.Https, policy, "x.example.com", "x.example.com"))));
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => started.OpenDownloadAsync(group, CertificateMethods.Download,
            PkiJson.Serialize(new DownloadCertificateRequest("ca:x", CertificateFormats.Pem)), CancellationToken.None));
        Assert.Empty(started.Service.Local.List);

        // The admin's call names the admin.
        var admin = ctx.CallAsAllDevices();
        var created = PkiJson.Deserialize<CertificateActionReply>(await ctx.InvokeAsync(started, admin, CertificateMethods.Create,
            PkiJson.Serialize(new CreateCertificateRequest(DeviceCertificatePurposes.Https, policy, "x.example.com", "x.example.com"))));
        Assert.True(created.Ok);
        Assert.Equal("admin", Assert.Single(started.Service.Local.List).Stored.AddedBy);

        // A group user sees only the certificates of the group's devices: no CA, created or server certificate, and no View of them.
        var seen = PkiJson.Deserialize<CertificatesReply>(await ctx.InvokeAsync(started, group, PkiMethods.GetCertificates, "{}"));
        Assert.Equal(0, seen.Total);
        var view = PkiJson.Deserialize<CertificateDetailsReply>(await ctx.InvokeAsync(started, group, PkiMethods.GetCertificate, PkiJson.Serialize(new CertificateDetailsRequest(created.Id!))));
        Assert.Equal(PkiCertificateTexts.NotListed, view.Error);
        var adminView = PkiJson.Deserialize<CertificateDetailsReply>(await ctx.InvokeAsync(started, admin, PkiMethods.GetCertificate, PkiJson.Serialize(new CertificateDetailsRequest(created.Id!))));
        Assert.NotNull(adminView.Certificate);
    }

    [Fact]
    public async Task A_created_key_survives_a_restart_and_another_master_key_cannot_read_it()
    {
        var settings = new Oadm.Core.Plugins.InMemoryPluginSettingsProvider();
        var records = new MemoryPluginRecords();
        var secrets = TestSecrets.Create();
        string id;
        await using (var pki = await PkiHarness.StartAsync(settings, secrets, records: records))
        {
            id = (await CreateAsync(pki)).Id!;
        }

        await using (var again = await PkiHarness.StartAsync(settings, secrets, records: records))
        {
            Assert.NotEmpty(await DownloadAsync(again, id, CertificateFormats.Pkcs12, FilePassword));
            Assert.True((await PageAsync(again)).Rows.Single(r => r.Id == id).HasKey);
        }

        // Another master key (a copy of the records without it): the certificate stays listed, its key cannot be exported.
        await using var foreign = await PkiHarness.StartAsync(settings, TestSecrets.Create(), records: records);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DownloadAsync(foreign, id, CertificateFormats.Pkcs12, FilePassword));
        Assert.Equal("The key of this certificate cannot be read (another master key).", refused.Message);
    }

    [Fact]
    public async Task The_card_creates_and_saves_the_pkcs12_file_signs_imports_exports_and_deletes()
    {
        await using var pki = await PkiHarness.StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var settingsFile = Path.Combine(Path.GetTempPath(), "oadm-pki-certificates-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using var page = new PkiViewModel(ctx, new FakeClientTrust(), new PkiClientSettingsStore(settingsFile));
            await page.Policies.LoadAsync(CancellationToken.None);
            var tab = page.Certificates;
            var dialogs = new FakeCertificateDialogs();
            tab.Dialogs = dialogs;
            await tab.LoadAsync();
            Assert.True(tab.CreateCommand.CanExecute(null));

            // Create: the errors below their fields while typing, then the PKCS#12 file is saved.
            dialogs.Drive = async d =>
            {
                var create = (CreateCertificateViewModel)d;
                Assert.Equal(["HTTPS", "IEEE 802.1X"], CreateCertificateViewModel.UseChoices.Select(c => c.Label));
                Assert.StartsWith("HTTPS (365 days, ECC", create.Policy!.Label, StringComparison.Ordinal);
                Assert.False(create.HasErrors); // nothing shown on an untouched form
                create.Name = "nvr.example.com";
                create.Addresses = "nvr.example.com bad_name";
                Assert.Equal("\"bad_name\" is no host name or IP address.", create.ErrorOf(nameof(CreateCertificateViewModel.Addresses)));
                create.Addresses = "nvr.example.com";
                create.Password = "short";
                Assert.Equal("Use at least 8 characters.", create.ErrorOf(nameof(CreateCertificateViewModel.Password)));
                create.Password = FilePassword;
                create.ConfirmPassword = FilePassword;
                Assert.True(create.CreateCommand.CanExecute(null));
                await create.CreateCommand.ExecuteAsync(null);
                Assert.True(create.Completed);
            };
            await tab.CreateCommand.ExecuteAsync(null);
            var saved = Assert.Single(dialogs.Files.Saved);
            Assert.Equal(("nvr.example.com.p12", PkiFileKind.Pkcs12), (saved.FileName, saved.Kind));
            using (var p12 = X509CertificateLoader.LoadPkcs12(saved.Data, FilePassword, OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet))
            {
                Assert.True(p12.HasPrivateKey);
            }

            await Eventually.TrueAsync(() => tab.Rows.Any(r => r.Name == "nvr.example.com"), "the new row");
            var row = tab.Rows.Single(r => r.Name == "nvr.example.com");
            Assert.True(row.HasKey && row.CanDelete); // the OADM CA's and still valid: Delete revokes it first (decision 2026-10-25)

            // Export with key: the password dialog, then the file; PEM at once.
            dialogs.Drive = async d =>
            {
                var export = (ExportKeyViewModel)d;
                Assert.Equal("Export nvr.example.com with its key", export.Heading);
                export.Password = "another one";
                export.ConfirmPassword = "another one";
                await export.ExportCommand.ExecuteAsync(null);
            };
            await tab.ExportKeyCommand.ExecuteAsync(row);
            Assert.Equal(2, dialogs.Files.Saved.Count);
            await tab.ExportPemCommand.ExecuteAsync(row);
            Assert.Equal(("nvr.example.com.crt", PkiFileKind.CertificatePem), (dialogs.Files.Saved[^1].FileName, dialogs.Files.Saved[^1].Kind));

            // Sign request: a server error below the file, then the signed certificate is saved.
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            dialogs.Drive = async d =>
            {
                var sign = (SignRequestViewModel)d;
                sign.SetFile(new PickedFile("bad.csr", TestCa.Ascii("nonsense")));
                await sign.SignCommand.ExecuteAsync(null);
                Assert.Equal("The request cannot be read or its signature is wrong.", sign.ErrorOf(nameof(SignRequestViewModel.File)));
                sign.SetFile(new PickedFile("good.csr", Csr(ec, "CN=door.example.com", null, "door.example.com")));
                await sign.SignCommand.ExecuteAsync(null);
                Assert.True(sign.Completed);
            };
            await tab.SignRequestCommand.ExecuteAsync(null);
            Assert.Equal("door.example.com.crt", dialogs.Files.Saved[^1].FileName);

            // Import: a PEM certificate.
            using var root = TestCa.Root("Contoso Root");
            using var leaf = TestCa.Leaf(root, "10.0.0.30");
            dialogs.Drive = async d =>
            {
                var import = (ImportCertificateViewModel)d;
                import.SetFile(new PickedFile("leaf.crt", TestCa.Pem(leaf)));
                Assert.False(import.NeedsPassword);
                await import.ImportCommand.ExecuteAsync(null);
                Assert.True(import.Completed);
            };
            await tab.ImportCommand.ExecuteAsync(null);
            Assert.Contains(tab.Rows, r => r.Name == "10.0.0.30" && r.Source.Kind == ListedCertificateKind.Imported);

            // Delete: confirmed, the row is gone; Cancel keeps it.
            var imported = tab.Rows.Single(r => r.Name == "10.0.0.30");
            dialogs.DeleteAnswer = false;
            await tab.DeleteRowCommand.ExecuteAsync(imported);
            Assert.Contains(tab.Rows, r => r.Name == "10.0.0.30");
            dialogs.DeleteAnswer = true;
            await tab.DeleteRowCommand.ExecuteAsync(imported);
            Assert.Equal(("Delete the certificate 10.0.0.30?", Oadm.Sdk.Client.Controls.IssuedCertificateTexts.DeleteMessageOne), dialogs.DeleteAsked[^1]);
            Assert.DoesNotContain(tab.Rows, r => r.Name == "10.0.0.30");
            Assert.Empty(ctx.Messages);

            // The CAs are no rows of this list; in their own list a CA row cannot be deleted, the row says so before the server does.
            Assert.DoesNotContain(tab.Rows, r => r.Source.Kind == ListedCertificateKind.Authority);
            await page.Authorities.LoadAsync();
            var caRow = page.Authorities.Rows.First(r => r.Source.Kind == ListedCertificateKind.Authority);
            Assert.False(page.Authorities.DeleteRowCommand.CanExecute(caRow));
            Assert.False(page.Authorities.ExportKeyCommand.CanExecute(caRow));
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }
}
