using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The certificates of recipients (docs/spec/recipient-sessions.md "Security", docs/spec/pki.md "Certificates"): the two
/// selects of the recipient dialog, the certificate of a session, and what a recipient keeps from Delete.
/// </summary>
public sealed class RecipientCertificateTests
{
    private const string FilePassword = "correct horse";

    private sealed class Uses(params (string Id, string Name)[] uses) : IRecipientCertificateUse
    {
        public Dictionary<string, IReadOnlyList<string>> Now { get; } = uses.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(u => u.Name)]);

        public int Reads { get; private set; }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> UsesAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(Now);
        }
    }

    private static IRecipientCertificates Offer(PkiHarness pki) => Assert.IsAssignableFrom<IRecipientCertificates>(pki.Core.FakeServices.Find<IRecipientCertificates>());

    private static async Task<string> CreateAsync(PkiHarness pki, string name = "nvr.example.com") =>
        (await CertificateListTests.CreateAsync(pki, name: name, addresses: name)).Id!;

    private static async Task<string> ImportAsync(PkiHarness pki, byte[] file, string? password = null)
    {
        var reply = await pki.InvokeAsync<CertificateActionReply>(CertificateMethods.Import, new ImportCertificateRequest(Convert.ToBase64String(file), "file", password));
        return reply.Id ?? throw new InvalidOperationException(string.Join(' ', reply.Errors?.Values ?? []));
    }

    private static string CaRow(PkiHarness pki) =>
        "ca:" + Convert.ToHexString(SHA256.HashData(pki.Plugin.Service!.Authorities().Single(a => a.Id == CertificateAuthorityInfo.OadmId).Certificates[0]));

    private static async Task<string> ImportedCaRowAsync(PkiHarness pki, X509Certificate2 root)
    {
        var state = await pki.InvokeAsync<PkiReply>(PkiMethods.ImportAuthority, new ImportAuthorityRequest(Convert.ToBase64String(TestCa.Pem(root)), "root.pem"));
        Assert.True(state.Ok, state.Error);
        return "ca:" + Convert.ToHexString(SHA256.HashData(root.RawData));
    }

    private static Task<DeleteCertificatesReply> DeleteAsync(PkiHarness pki, params string[] ids) =>
        pki.InvokeAsync<DeleteCertificatesReply>(CertificateMethods.Delete, new DeleteCertificatesRequest(ids));

    private static Task<CertificatesReply> PageAsync(PkiHarness pki) => pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, new CertificatesRequest());

    private static byte[] Pfx(string subject = "CN=client.example.com")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        return certificate.Export(X509ContentType.Pfx, FilePassword);
    }

    [Fact]
    public async Task Client_certificates_are_the_rows_with_a_key_OADM_holds_without_any_CA()
    {
        await using var pki = await PkiHarness.StartAsync();
        var created = await CreateAsync(pki);
        var imported = await ImportAsync(pki, Pfx(), FilePassword);
        using var noKey = TestCa.Root("Key elsewhere", ca: false, keyCertSign: false);
        var withoutKey = await ImportAsync(pki, TestCa.Pem(noKey));

        var choices = await Offer(pki).ClientCertificatesAsync(CancellationToken.None);

        Assert.Equal(new[] { created, imported }.Order(StringComparer.Ordinal), choices.Select(c => c.Value).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(withoutKey, choices.Select(c => c.Value));
        Assert.DoesNotContain(choices, c => c.Value.StartsWith("ca:", StringComparison.Ordinal));
        var issuer = pki.Plugin.Service!.Local.List.Single(l => l.RowId == created);
        Assert.Equal(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"nvr.example.com ({issuer.IssuerName}), until {issuer.NotAfterUtc:yyyy-MM-dd}"),
            choices.Single(c => c.Value == created).Label);
    }

    [Fact]
    public async Task An_ended_certificate_and_one_no_longer_accepted_are_no_client_certificates()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var pki = await PkiHarness.StartAsync(time: time);
        var accepted = await CreateAsync(pki, "kept.example.com");
        var deleted = await CreateAsync(pki, "gone.example.com");
        await pki.Service.Issued.RevokeAsync([pki.Service.Local.List.Single(l => l.RowId == deleted).Serial], RevocationReason.Other, "admin", CancellationToken.None);
        Assert.Equal(accepted, Assert.Single(await Offer(pki).ClientCertificatesAsync(CancellationToken.None)).Value);
        Assert.Null(await Offer(pki).LoadAsync(deleted, CancellationToken.None));

        time.SetUtcNow(pki.Service.Local.List.Single(l => l.RowId == accepted).NotAfterUtc.AddDays(1));
        Assert.Empty(await Offer(pki).ClientCertificatesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Trusted_certificates_are_the_CAs_and_the_imported_certificates()
    {
        await using var pki = await PkiHarness.StartAsync();
        var created = await CreateAsync(pki);
        using var server = TestCa.Root("opc.example.com", ca: false, keyCertSign: false);
        var imported = await ImportAsync(pki, TestCa.Pem(server));

        var choices = await Offer(pki).TrustedCertificatesAsync(CancellationToken.None);

        Assert.Contains(choices, c => c.Value == CaRow(pki));
        Assert.Contains(choices, c => c.Value == imported);
        Assert.DoesNotContain(choices, c => c.Value == created);
        Assert.DoesNotContain("(", choices.Single(c => c.Value == CaRow(pki)).Label, StringComparison.Ordinal); // a root CA names itself once
    }

    [Fact]
    public async Task A_client_certificate_loads_with_its_key_then_its_issuers()
    {
        await using var pki = await PkiHarness.StartAsync();
        var id = await CreateAsync(pki);

        var chain = await Offer(pki).LoadAsync(id, CancellationToken.None);

        Assert.NotNull(chain);
        try
        {
            Assert.True(chain.Count >= 2);
            Assert.True(chain[0].HasPrivateKey);
            Assert.All(chain.Skip(1), issuer => Assert.False(issuer.HasPrivateKey));
            Assert.Equal(chain[0].Issuer, chain[1].Subject);
            Assert.Equal(pki.Service.Local.List.Single(l => l.RowId == id).Der, chain[0].RawData);
        }
        finally
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_loaded_key_signs_in_a_TLS_handshake_with_client_authentication()
    {
        await using var pki = await PkiHarness.StartAsync();
        var id = await ImportAsync(pki, Pfx(), FilePassword);
        var chain = await Offer(pki).LoadAsync(id, CancellationToken.None);
        using var client = Assert.Single(chain!);
        using var serverKey = RSA.Create(2048);
        using var generated = new CertificateRequest("CN=broker", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var serverCertificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var accepted = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var tls = new SslStream(socket.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, presented, _, _) => presented is not null && presented.GetRawCertData().SequenceEqual(client.RawData),
                }, timeout.Token);
                _ = await tls.ReadAsync(new byte[1], timeout.Token);
                return tls.RemoteCertificate?.Subject;
            }, timeout.Token);

            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            await using var tls = new SslStream(socket.GetStream());
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "broker",
                ClientCertificates = [client],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => client,
                RemoteCertificateValidationCallback = (_, presented, _, _) => presented is not null && presented.GetRawCertData().SequenceEqual(serverCertificate.RawData),
            }, timeout.Token);
            await tls.WriteAsync(new byte[] { 1 }, timeout.Token);

            Assert.Equal("CN=client.example.com", await accepted);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_CA_loads_without_a_key_and_a_certificate_that_is_gone_is_null()
    {
        await using var pki = await PkiHarness.StartAsync();
        var id = await CreateAsync(pki);

        var ca = await Offer(pki).LoadAsync(CaRow(pki), CancellationToken.None);
        using var root = Assert.Single(ca!);
        Assert.False(root.HasPrivateKey);

        Assert.Equal(1, (await DeleteAsync(pki, id)).Deleted);
        Assert.Null(await Offer(pki).LoadAsync(id, CancellationToken.None));
        Assert.Null(await Offer(pki).LoadAsync("ca:00", CancellationToken.None));
        Assert.Null(await Offer(pki).LoadAsync("something else", CancellationToken.None));
    }

    [Fact]
    public async Task A_certificate_a_recipient_uses_cannot_be_deleted_and_shows_the_recipient()
    {
        await using var pki = await PkiHarness.StartAsync();
        var id = await CreateAsync(pki);
        var other = await CreateAsync(pki, "other.example.com");
        var uses = new Uses((id, "Line 2"), (id, "Plant broker"));
        pki.Core.FakeServices.Offer<IRecipientCertificateUse>(uses);

        var row = (await PageAsync(pki)).Rows.Single(r => r.Id == id);
        Assert.Equal("Recipient: Line 2, Plant broker", row.UsedBy);
        Assert.Equal(["Recipient"], row.UsedByBadges);

        var refused = await DeleteAsync(pki, other, id); // all or nothing
        Assert.Equal(CertificateRules.UsedByRecipient("Line 2") + CertificateRules.NothingDeleted, refused.Error);
        Assert.Equal("Used by the recipient Plant broker. Pick another certificate first.", CertificateRules.UsedByRecipient("Plant broker"));
        Assert.Equal(0, refused.Deleted);
        Assert.Contains(pki.Service.Local.List, l => l.RowId == other);

        uses.Now.Clear();
        Assert.Equal(2, (await DeleteAsync(pki, other, id)).Deleted);
    }

    [Fact]
    public async Task A_CA_a_recipient_uses_cannot_be_deleted_and_shows_the_recipient()
    {
        await using var pki = await PkiHarness.StartAsync();
        using var root = TestCa.Root("Plant Root CA");
        var row = await ImportedCaRowAsync(pki, root);
        var uses = new Uses((row, "Plant broker"));
        pki.Core.FakeServices.Offer<IRecipientCertificateUse>(uses);

        Assert.Contains("Recipient: Plant broker", (await PageAsync(pki)).Rows.Single(r => r.Id == row).UsedBy);
        Assert.Contains("Recipient: Plant broker", (await pki.StateAsync()).Authorities.Single(a => a.Name.Contains("Plant Root CA", StringComparison.Ordinal)).UsedBy);

        var refused = await pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(row));
        Assert.False(refused.Ok);
        Assert.Equal(CertificateRules.UsedByRecipient("Plant broker"), refused.Error);

        uses.Now.Clear();
        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.DeleteAuthority, new DeleteAuthorityRequest(row))).Ok);
    }

    [Fact]
    public async Task Without_the_host_nothing_uses_a_certificate()
    {
        await using var pki = await PkiHarness.StartAsync();
        var id = await CreateAsync(pki);

        Assert.Null((await PageAsync(pki)).Rows.Single(r => r.Id == id).UsedBy);
        Assert.Equal(1, (await DeleteAsync(pki, id)).Deleted);
    }

    [Fact]
    public async Task A_page_of_the_list_reads_the_uses_once()
    {
        await using var pki = await PkiHarness.StartAsync();
        for (var i = 0; i < 5; i++)
        {
            _ = await CreateAsync(pki, $"nvr{i}.example.com");
        }

        var uses = new Uses();
        pki.Core.FakeServices.Offer<IRecipientCertificateUse>(uses);
        _ = await PageAsync(pki);
        Assert.Equal(1, uses.Reads);
    }

    [Fact]
    public async Task A_policy_still_comes_first_and_keeps_its_badge()
    {
        await using var pki = await PkiHarness.StartAsync();
        var id = await CreateAsync(pki);
        var policy = pki.Policies.FirstOf(PolicyKinds.EstServer)!;
        var local = pki.Service.Local.List.Single(l => l.RowId == id);
        var saved = await pki.Service.SavePolicyAsync(policy with { CertificateId = local.Stored.Id }, "admin", CancellationToken.None);
        Assert.Null(saved.Error);
        pki.Core.FakeServices.Offer<IRecipientCertificateUse>(new Uses((id, "Plant broker")));

        var row = (await PageAsync(pki)).Rows.Single(r => r.Id == id);
        Assert.Equal(PolicyKinds.Name(PolicyKinds.EstServer) + "; Recipient: Plant broker", row.UsedBy);
        Assert.Equal([PolicyKinds.Name(PolicyKinds.EstServer), "Recipient"], row.UsedByBadges);
        Assert.Equal(CertificateRules.UsedByPolicy(policy.Name) + CertificateRules.NothingDeleted, (await DeleteAsync(pki, id)).Error);
    }

    [Fact]
    public async Task A_user_limited_to_a_device_group_reads_no_recipient_names()
    {
        await using var pki = await PkiHarness.StartAsync();
        var row = CaRow(pki);
        var uses = new Uses((row, "Plant broker"));
        pki.Core.FakeServices.Offer<IRecipientCertificateUse>(uses);

        var state = await pki.Service.GetStateAsync(Oadm.Plugins.Shared.ViewedSet.From([]), CancellationToken.None);

        Assert.DoesNotContain(state.Authorities, a => a.UsedBy.Any(u => u.Contains("Recipient", StringComparison.Ordinal)));
        Assert.Equal(0, uses.Reads);
    }
}
