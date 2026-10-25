using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Oadm.Plugins.DeviceIds;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Plugins.Radius.Server;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Sites;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests.Support;

/// <summary>Reversible "encryption" that marks its output, to prove what is stored encrypted.</summary>
public sealed class FakeSecrets : ISecretProtector
{
    public string Protect(string plaintext, string purpose) => "enc:" + purpose + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(StringReverse(plaintext)));

    public string Unprotect(string protectedValue, string purpose)
    {
        var prefix = "enc:" + purpose + ":";
        if (!protectedValue.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Wrong purpose.");
        }

        return StringReverse(Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[prefix.Length..])));
    }

    private static string StringReverse(string text) => new([.. text.Reverse()]);
}

/// <summary>The server's network cards of the tests.</summary>
public sealed class FixedInterfaces(params ServerNetworkInterface[] cards) : IServerNetworkInterfaces
{
    public List<ServerNetworkInterface> Cards { get; } = [.. cards];

    public IReadOnlyList<ServerNetworkInterface> List() => Cards;

    public static ServerNetworkInterface Card(string id, string address) =>
        new(id, id, id, [IPAddress.Parse(address)], IsUp: true, IsLoopback: false);
}

/// <summary>The event rules of the tests: what the plugin fired.</summary>
public sealed class RecordingPublisher : IEventPublisher
{
    public ConcurrentQueue<(string Type, Guid? Device, IReadOnlyDictionary<string, string> Data)> Fired { get; } = new();

    public void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null)
    {
    }

    public void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null) =>
        Fired.Enqueue((typeId, deviceId, data ?? new Dictionary<string, string>()));

    public void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices)
    {
    }

    public bool IsWatched(string typeId) => true;
}

/// <summary>
/// The OADM CA as PKI offers it, for the plugin tests: its own CA (a unique name: Windows caches chains by name), signs the
/// server certificate, issues camera certificates with <c>axis-&lt;serial&gt;.local</c>, keeps a revocation list.
/// </summary>
public sealed class TestAuthority : IDeviceCertificateAuthority, Oadm.Plugins.Pki.Ca.ICertificateDelivery, Oadm.Plugins.Pki.Ca.IServerCertificateSource, IDisposable
{
    private readonly Lock _serverGate = new();
    private Oadm.Plugins.Pki.ServerCertificateMaterial? _server;

    /// <summary>
    /// The certificate the servers show (in PKI: the one their policy picks from the Certificates list, decision 2026-10-25):
    /// one of this CA, made on the first read (counted in <see cref="SignCount"/>); none while the CA is not ready.
    /// </summary>
    public Oadm.Plugins.Pki.ServerCertificateLoad ServerCertificate(string kind)
    {
        if (!IsReady)
        {
            return new(null, null);
        }

        lock (_serverGate)
        {
            _server ??= MakeServerCertificate(); // the server's start and the first connection may read it at once
            return new(_server with { Pkcs8 = (byte[])_server.Pkcs8.Clone() }, null); // the reader clears its copy of the key
        }
    }

    public IReadOnlyList<byte[]> ServerIssuerChain(string kind) => IsReady ? Chain : [];

    public Task EnsureServerCertificateAsync(string kind, IReadOnlyList<string> names, Func<CancellationToken, Task<Oadm.Plugins.Pki.ServerCertificateImport?>>? existing, CancellationToken ct) =>
        Task.CompletedTask;

    private Oadm.Plugins.Pki.ServerCertificateMaterial MakeServerCertificate()
    {
        SignCount++;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=802.1X and EST server", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse("10.0.0.17"));
        request.CertificateExtensions.Add(san.Build());
        using var signed = request.Create(_ca.SubjectName, X509SignatureGenerator.CreateForECDsa(_key), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10), Serial());
        return new Oadm.Plugins.Pki.ServerCertificateMaterial("test-server", signed.RawData, Chain, key.ExportPkcs8PrivateKey(), signed.NotAfter.ToUniversalTime(), ["10.0.0.17"]);
    }

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The serials the 802.1X server reported as signed in (PKI confirms a pending IEEE 802.1X certificate).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> SignedIn { get; } = new();

    public Task DeliveredAsync(string serialNumber, CancellationToken ct) => Task.CompletedTask;

    public Task NotDeliveredAsync(string serialNumber, CancellationToken ct) => Task.CompletedTask;

    public Task SignedInAsync(string serialNumber, CancellationToken ct)
    {
        SignedIn.Enqueue(serialNumber);
        return Task.CompletedTask;
    }
    private readonly X509Certificate2 _ca;
    private readonly TestAuthority? _parent;
    private readonly Dictionary<string, CertificateRevocation> _revoked = new(StringComparer.Ordinal);
    private int _previousReads;

    public TestAuthority(string name = "OADM test CA")
    {
        _ca = CreateRoot(name, _key);
    }

    /// <summary>A CA under <paramref name="parent"/> (an OADM CA imported as an intermediate of an enterprise root, or a sibling).</summary>
    public TestAuthority(string name, TestAuthority parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        _parent = parent;
        var request = new CertificateRequest($"CN={name} {Guid.NewGuid():N}", _key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var signed = request.Create(parent._ca.SubjectName, X509SignatureGenerator.CreateForECDsa(parent._key), DateTimeOffset.UtcNow.AddDays(-9), DateTimeOffset.UtcNow.AddYears(9), Serial());
        _ca = X509CertificateLoader.LoadCertificate(signed.RawData);
    }

    public bool IsReady { get; set; } = true;

    /// <summary>The CA certificate first, then the certificates above it (PKI's <see cref="IDeviceCertificateAuthority.Chain"/>).</summary>
    public IReadOnlyList<byte[]> Chain => IsReady ? (_parent is null ? [_ca.RawData] : [_ca.RawData, .. _parent.Chain]) : [];

    public byte[] CaCertificate => _ca.RawData;

    public List<IReadOnlyList<byte[]>> Previous { get; } = [];

    public IReadOnlyList<IReadOnlyList<byte[]>> PreviousChains
    {
        get
        {
            Interlocked.Increment(ref _previousReads);
            Interlocked.Exchange(ref _onPreviousRead, null)?.Invoke();
            return Previous;
        }
    }

    private Action? _onPreviousRead;

    /// <summary>Runs once, on the next read of <see cref="PreviousChains"/> (a test saves in the middle of a rebuild with it).</summary>
    public void OnNextPreviousRead(Action action) => Volatile.Write(ref _onPreviousRead, action);

    /// <summary>How often <see cref="PreviousChains"/> was read (the trusted set is built once per CA change, never per handshake).</summary>
    public int PreviousReads => Volatile.Read(ref _previousReads);

    public int SignCount { get; private set; }

    public event EventHandler? Changed;

    public event EventHandler? RevocationChanged;

    /// <summary>Runs before every signature (a test holds a maintenance round in the middle with it).</summary>
    public Func<Task>? BeforeSign { get; set; }

    public async Task<IssuedDeviceCertificate> SignAsync(DeviceCertificateRequest request, CancellationToken ct)
    {
        if (BeforeSign is { } before)
        {
            await before();
        }

        SignCount++;
        var csr = CertificateRequest.LoadSigningRequest(request.Csr.ToArray(), HashAlgorithmName.SHA256);
        var signing = new CertificateRequest(new X500DistinguishedName("CN=" + request.CommonName), csr.PublicKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var ip in request.IpAddresses)
        {
            san.AddIpAddress(IPAddress.Parse(ip));
        }

        foreach (var dns in request.DnsNames)
        {
            san.AddDnsName(dns);
        }

        signing.CertificateExtensions.Add(san.Build());
        signing.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        signing.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(request.Use == DeviceCertificateUse.Client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")], false));
        var notAfter = DateTimeOffset.UtcNow + (request.Validity ?? TimeSpan.FromDays(365));
        using var signed = signing.Create(_ca.SubjectName, X509SignatureGenerator.CreateForECDsa(_key), DateTimeOffset.UtcNow.AddMinutes(-5), notAfter, Serial());
        return new IssuedDeviceCertificate(signed.RawData, Chain, signed.SerialNumber, signed.NotAfter.ToUniversalTime());
    }

    /// <summary>A camera certificate of this CA naming <paramref name="serial"/> (client authentication unless <paramref name="usage"/> says otherwise).</summary>
    public TestCertificate Camera(string serial, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, string usage = "1.3.6.1.5.5.7.3.2", params string[] moreNames) =>
        Camera(new X500DistinguishedName("CN=10.0.0.48"), serial, notBefore, notAfter, usage, moreNames);

    /// <summary>A camera certificate of this CA with the subject <paramref name="subject"/>.</summary>
    public TestCertificate Camera(X500DistinguishedName subject, string serial, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, string usage = "1.3.6.1.5.5.7.3.2", params string[] moreNames)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("axis-" + serial.ToLowerInvariant() + ".local");
        foreach (var name in moreNames)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.Create(_ca.SubjectName, X509SignatureGenerator.CreateForECDsa(_key),
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(1), Serial());
        return new TestCertificate([certificate.RawData, _ca.RawData], key.ExportPkcs8PrivateKey(), ServerKeyKind.Ecdsa);
    }

    public static string SerialOf(TestCertificate certificate)
    {
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.Leaf);
        return CertificateSerials.Normalize(leaf.SerialNumber);
    }

    public void Revoke(string serial, Guid? deviceId = null)
    {
        var normalized = CertificateSerials.Normalize(serial);
        _revoked[normalized] = new CertificateRevocation(normalized, new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc), RevocationReason.KeyCompromised, "admin")
        {
            DeviceId = deviceId,
            NotAfterUtc = DateTime.UtcNow.AddYears(1),
        };
        RevocationChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsRevoked(string serialNumber) => _revoked.ContainsKey(CertificateSerials.Normalize(serialNumber));

    public CertificateRevocation? FindRevocation(string serialNumber) => _revoked.GetValueOrDefault(CertificateSerials.Normalize(serialNumber));

    public string? PurposeOf(string serialNumber) =>
        Issued.Where(i => CertificateSerials.Normalize(i.Info.SerialNumber) == CertificateSerials.Normalize(serialNumber)).Select(i => i.Info.Purpose).FirstOrDefault();

    public IReadOnlyList<CertificateRevocation> Revocations => [.. _revoked.Values];

    /// <summary>PKI's list of issued certificates: the rows and their DER.</summary>
    public List<(IssuedCertificateInfo Info, byte[] Der)> Issued { get; } = [];

    public List<IssuedCertificateQuery> IssuedQueries { get; } = [];

    /// <summary>Lists <paramref name="certificate"/> as issued for <paramref name="deviceId"/>.</summary>
    public void List(TestCertificate certificate, Guid deviceId) =>
        Issued.Add((new IssuedCertificateInfo { SerialNumber = SerialOf(certificate), DeviceId = deviceId, Purpose = DeviceCertificatePurposes.Dot1x }, certificate.Leaf));

    public Task<IssuedCertificatePage> ListIssuedAsync(IssuedCertificateQuery query, CancellationToken ct)
    {
        IssuedQueries.Add(query);
        var rows = Issued.Select(i => IsRevoked(i.Info.SerialNumber) ? i.Info with { State = IssuedCertificateState.Revoked } : i.Info)
            .Where(i => query.DeviceIds is null || query.DeviceIds.Contains(i.DeviceId))
            .Where(i => query.Purpose is null || i.Purpose == query.Purpose)
            .Where(i => query.Search is null || i.SerialNumber.Contains(CertificateSerials.Normalize(query.Search), StringComparison.Ordinal))
            .ToList();
        return Task.FromResult(new IssuedCertificatePage([.. rows.Skip(query.Offset).Take(query.Limit)], rows.Count));
    }

    public Task<byte[]?> GetIssuedCertificateAsync(string serialNumber, CancellationToken ct) =>
        Task.FromResult<byte[]?>(Issued.FirstOrDefault(i => i.Info.SerialNumber == CertificateSerials.Normalize(serialNumber)).Der);

    public List<(IReadOnlyCollection<string> Serials, RevocationReason Reason, string User)> RevokeCalls { get; } = [];

    public Task<RevokeResult> RevokeAsync(IReadOnlyCollection<string> serialNumbers, RevocationReason reason, string userName, CancellationToken ct)
    {
        RevokeCalls.Add((serialNumbers, reason, userName));
        foreach (var serial in serialNumbers)
        {
            Revoke(serial);
        }

        return Task.FromResult(new RevokeResult(serialNumbers.Count, 0, 0));
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>PKI's list of CAs; null = an older PKI without it (the 802.1X server derives it from the chains).</summary>
    public List<CertificateAuthorityInfo>? AuthorityList { get; set; }

    public IReadOnlyList<CertificateAuthorityInfo> Authorities => AuthorityList ?? [];

    /// <summary>The calls of <see cref="SetUsedBy"/>.</summary>
    public ConcurrentQueue<(string Service, string Name, IReadOnlyCollection<string> Ids)> UsedBy { get; } = new();

    public void SetUsedBy(string serviceId, string serviceName, IReadOnlyCollection<string> authorityIds) => UsedBy.Enqueue((serviceId, serviceName, [.. authorityIds]));

    /// <summary>This CA as an entry of PKI's list.</summary>
    public CertificateAuthorityInfo Info(string id, CertificateAuthorityKind kind, string name = "CA") =>
        new(id, name, kind, Chain, _ca.NotAfter.ToUniversalTime(), kind == CertificateAuthorityKind.Oadm);

    public void Dispose()
    {
        _ca.Dispose();
        _key.Dispose();
    }

    internal static X509Certificate2 CreateRoot(string name, ECDsa key)
    {
        var request = new CertificateRequest($"CN={name} {Guid.NewGuid():N}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddYears(10));
    }

    internal static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x01);
        return serial;
    }
}

/// <summary>An Axis device ID hierarchy like Axis' (root, intermediate, device certificates with the serial in the subject).</summary>
public sealed class TestDeviceIds : IDisposable
{
    private readonly ECDsa _rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _intermediate;

    public TestDeviceIds()
    {
        _root = TestAuthority.CreateRoot("Axis device ID root test", _rootKey);
        var request = new CertificateRequest($"CN=Axis device ID intermediate {Guid.NewGuid():N}", _intermediateKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var signed = request.Create(_root.SubjectName, X509SignatureGenerator.CreateForECDsa(_rootKey), DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddYears(8), TestAuthority.Serial());
        _intermediate = X509CertificateLoader.LoadCertificate(signed.RawData);
    }

    public AxisDeviceId Verifier => new([_root, _intermediate]);

    /// <summary>PKI's entry "Axis device ID" with these roots and intermediates.</summary>
    public CertificateAuthorityInfo Info => new(CertificateAuthorityInfo.AxisDeviceIdId, "Axis device ID", CertificateAuthorityKind.AxisDeviceId,
        [_root.RawData, _intermediate.RawData], _root.NotAfter.ToUniversalTime(), false);

    /// <summary>The factory certificate of <paramref name="serial"/>; the camera sends it with the intermediate.</summary>
    public TestCertificate Device(string serial)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedNameBuilder();
        name.AddOrganizationName("Axis Communications AB");
        name.Add("2.5.4.5", serial);
        var request = new CertificateRequest(name.Build(), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        using var certificate = request.Create(_intermediate.SubjectName, X509SignatureGenerator.CreateForECDsa(_intermediateKey), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), TestAuthority.Serial());
        return new TestCertificate([certificate.RawData, _intermediate.RawData], key.ExportPkcs8PrivateKey(), ServerKeyKind.Ecdsa);
    }

    public void Dispose()
    {
        _root.Dispose();
        _intermediate.Dispose();
        _rootKey.Dispose();
        _intermediateKey.Dispose();
    }
}

/// <summary>
/// The 802.1X server plugin started on a <see cref="FakeCoreContext"/> with OADM PRO, a test CA, encrypted secrets, a fake
/// clock, fixed network cards and recorded events; switches talk to it through the in-memory UDP listener (or a site's relay).
/// </summary>
public sealed class PluginHarness : IAsyncDisposable
{
    public const string Secret = "a-shared-secret-of-24-ch";

    private readonly ConcurrentDictionary<(InMemoryUdpRelay Relay, IPEndPoint To), Channel<byte[]>> _answers = new();
    private readonly ConcurrentDictionary<InMemoryUdpRelay, Task> _readers = new();
    private readonly CancellationTokenSource _cts = new();

    private PluginHarness(FakeCoreContext ctx, TestAuthority ca, TestDeviceIds deviceIds, FakeTimeProvider time, FixedInterfaces interfaces)
    {
        Ctx = ctx;
        Ca = ca;
        DeviceIds = deviceIds;
        Time = time;
        Interfaces = interfaces;
    }

    public FakeCoreContext Ctx { get; }

    public TestAuthority Ca { get; }

    public TestDeviceIds DeviceIds { get; }

    public FakeTimeProvider Time { get; }

    public FixedInterfaces Interfaces { get; }

    public RecordingPublisher Publisher { get; } = new();

    /// <summary>PKI's settings (the policies are stored there, not in the 802.1X server's).</summary>
    public FakeSettings PkiSettings { get; } = new();

    /// <summary>The protection of the policies' tags under PKI.</summary>
    public FakeProtectedTags PkiTags { get; } = new();

    /// <summary>PKI's policies.</summary>
    public PolicyStore Policies { get; private set; } = null!;

    /// <summary>Saves a policy as "admin" (the save must pass).</summary>
    /// <summary>The id of the test CA's server certificate in the harness's Certificates list.</summary>
    public const string ServerCertificateId = "test-server";

    public async Task<PkiPolicy> SavePolicyAsync(PkiPolicy policy)
    {
        if (policy.Kind == PolicyKinds.Dot1xServer && policy.CertificateId is null)
        {
            policy = policy with { CertificateId = ServerCertificateId }; // the server policy shows a certificate of the list
        }

        var result = await Policies.SaveAsync(policy, "admin", RadiusPolicies.CipherSuites, CancellationToken.None);
        Assert.Null(result.Error);
        return result.Saved!;
    }

    /// <summary>Deletes every policy of <paramref name="kind"/>.</summary>
    public async Task DeletePoliciesAsync(string kind)
    {
        foreach (var policy in Policies.PoliciesOf(kind).ToList())
        {
            await Policies.DeleteAsync(policy.Id, "admin", CancellationToken.None);
        }
    }

    /// <summary>The CAs of the Allow CA policies, in place of every Allow CA policy there was.</summary>
    public async Task SetAllowCasAsync(params (string Id, bool OnlyManaged)[] cas)
    {
        await DeletePoliciesAsync(PolicyKinds.AllowCa);
        foreach (var (id, onlyManaged) in cas)
        {
            await SavePolicyAsync(new PkiPolicy { Name = "Allow " + id, Kind = PolicyKinds.AllowCa, CaId = id, OnlyManaged = onlyManaged });
        }
    }

    /// <summary>The 802.1X server policy changed (TLS, packet, reauthentication).</summary>
    public Task<PkiPolicy> SetServerPolicyAsync(Func<PkiPolicy, PkiPolicy> change) => SavePolicyAsync(change(Policies.ServerPolicy ?? RadiusPolicies.DefaultServerPolicy()));

    public RadiusPlugin Plugin { get; private set; } = null!;

    public RadiusService Service => Plugin.Service!;

    public FakeProLicense Pro => (FakeProLicense)Ctx.Pro!;

    public FakeCallContext Admin => field ??= Ctx.CallAsAllDevices();

    /// <summary>The listener on this server's network.</summary>
    public InMemoryUdpRelay Listener => (InMemoryUdpRelay)Service.Listener!;

    public static async Task<PluginHarness> CreateAsync(Action<PluginHarness>? beforeStart = null, int workers = 4, bool start = true, TestAuthority? authority = null)
    {
        var ctx = new FakeCoreContext { Pro = FakeProLicense.ActivePro(), Secrets = new FakeSecrets() };
        var ca = authority ?? new TestAuthority();
        ctx.FakeServices.Offer<IDeviceCertificateAuthority>(ca);
        var harness = new PluginHarness(ctx, ca, new TestDeviceIds(), new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedInterfaces(FixedInterfaces.Card("eth0", "10.0.0.17")));

        // PKI's policies as PKI offers them (its own settings and tag protection): the 802.1X server reads its rules there.
        harness.Policies = new PolicyStore(harness.PkiSettings, _ => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. ctx.DeviceList]), () => ctx.FakeSites.List,
            () => CaTrust.AuthoritiesOf(ctx.FakeServices.Find<IDeviceCertificateAuthority>(), harness.DeviceIds.Verifier), harness.PkiTags);
        await harness.Policies.LoadAsync(CancellationToken.None);

        // The test CA's server certificate is the one the Certificates list offers (in PKI the server policy picks it).
        harness.Policies.ServerCertificates = () => [new PolicyCertificateChoice(ServerCertificateId, "802.1X and EST server", "OADM test CA", DateTime.UtcNow.AddYears(10), true, ["10.0.0.17"])];
        ctx.FakeServices.Offer(harness.Policies);
        beforeStart?.Invoke(harness);
        if (start)
        {
            await harness.StartAsync(workers);
        }

        return harness;
    }

    /// <summary>The server is turned on before the plugin starts (default; false: the stored choice stays as it is).</summary>
    public bool TurnedOn { get; set; } = true;

    public async Task StartAsync(int workers = 4)
    {
        Plugin = new RadiusPlugin(host => host with
        {
            Time = Time,
            Interfaces = Interfaces,
            DeviceIds = DeviceIds.Verifier,
            HostName = "oadm-test",
            Publisher = Publisher,
            Workers = workers,
        });
        if (TurnedOn)
        {
            await Ctx.Settings.SetAsync(Oadm.Plugins.Pki.Servers.ServerSwitch<RadiusService>.SettingKey, "true", CancellationToken.None);
        }

        await Plugin.StartAsync(Ctx, CancellationToken.None);
    }

    /// <summary>A managed device with this serial (and site, tags).</summary>
    public FakeDevice AddDevice(string serial, Guid? siteId = null, IReadOnlyList<string>? tags = null, string? address = null) =>
        Ctx.Add(new FakeDevice { Serial = serial, SiteId = siteId, Tags = tags ?? [], Address = address ?? "10.0.1." + (Ctx.DeviceList.Count + 10) });

    /// <summary>The device list read again (as the 10 s round does).</summary>
    public Task RefreshDevicesAsync() => Service.Index.RefreshAsync(onlyAfterMiss: false, CancellationToken.None);

    public async Task<T> CallAsync<T>(string method, object? payload = null, ICallContext? call = null)
    {
        var json = await Ctx.InvokeAsync(Plugin, call ?? Admin, method, payload is null ? null : payload as string ?? RadiusJson.Serialize(payload));
        return RadiusJson.Deserialize<T>(json);
    }

    public Task CallAsync(string method, object? payload = null, ICallContext? call = null) =>
        Ctx.InvokeAsync(Plugin, call ?? Admin, method, payload is null ? null : payload as string ?? RadiusJson.Serialize(payload));

    public Task<SwitchRow> AddSwitchAsync(string name, string address, Guid? siteId = null, string secret = Secret) =>
        CallAsync<SwitchRow>(RadiusMethods.SaveSwitch, new SaveSwitchRequest { Name = name, Address = address, SiteId = siteId, Secret = secret });

    /// <summary>The ticks of the Sessions tab and the log (500 ms each).</summary>
    public void Tick(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            Time.Advance(TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>A switch at <paramref name="address"/> sending through <paramref name="relay"/> (this server's listener by default).</summary>
    public PluginSwitch Switch(string address = "10.0.0.2", int port = 51812, string secret = Secret, InMemoryUdpRelay? relay = null) =>
        new(this, relay ?? Listener, new IPEndPoint(IPAddress.Parse(address), port), Encoding.ASCII.GetBytes(secret));

    /// <summary>The next answer to <paramref name="to"/> on <paramref name="relay"/> (30 s at most).</summary>
    internal async Task<byte[]?> AnswerAsync(InMemoryUdpRelay relay, IPEndPoint to, TimeSpan? wait = null)
    {
        _ = _readers.GetOrAdd(relay, r => Task.Run(() => ReadAsync(r)));
        var channel = _answers.GetOrAdd((relay, to), _ => Channel.CreateUnbounded<byte[]>());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(wait ?? TimeSpan.FromSeconds(30));
        try
        {
            return await channel.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task ReadAsync(InMemoryUdpRelay relay)
    {
        try
        {
            while (true)
            {
                var sent = await relay.ReadSentAsync(_cts.Token);
                _answers.GetOrAdd((relay, sent.Remote), _ => Channel.CreateUnbounded<byte[]>()).Writer.TryWrite(sent.Data.ToArray());
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (Plugin is not null)
        {
            await Plugin.StopAsync(CancellationToken.None);
        }

        Ca.Dispose();
        DeviceIds.Dispose();
        Policies?.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// A switch of the plugin tests: <see cref="TestSwitch"/>'s requests and checks, sent as datagrams through the plugin's
/// UDP listener; <see cref="RunAsync"/> runs a whole conversation with a <see cref="TestSupplicant"/>.
/// </summary>
public sealed class PluginSwitch
{
    private readonly PluginHarness _harness;
    private readonly InMemoryUdpRelay _relay;
    private readonly TestSwitch _builder;

    internal PluginSwitch(PluginHarness harness, InMemoryUdpRelay relay, IPEndPoint source, byte[] secret)
    {
        _harness = harness;
        _relay = relay;
        Source = source;
        _builder = new TestSwitch(null!) { Client = new Protocol.Sessions.RadiusClient("plugin-test", secret), Source = source };
    }

    public IPEndPoint Source { get; }

    public string? CallingStationId { get; init; } = "AC-CC-8E-12-34-56";

    /// <summary>The switch port (a port holds at most 8 sessions: many cameras need ports of their own).</summary>
    public string? NasPortId { get; init; } = "GigabitEthernet1/0/5";

    /// <summary>Sends one EAP packet; the checked answer, or null when none came within <paramref name="wait"/>.</summary>
    public async Task<(RadiusPacket? Answer, byte[] Authenticator)> SendAsync(byte[] eap, byte[]? state, TimeSpan? wait = null)
    {
        var builder = _builder;
        builder.State = state;
        var request = BuildRequest(eap, out var authenticator);
        _relay.Deliver(request, Source);
        var bytes = await _harness.AnswerAsync(_relay, Source, wait);
        if (bytes is null)
        {
            return (null, authenticator);
        }

        var result = new Protocol.Sessions.RadiusResult(Protocol.Sessions.RadiusOutcome.Answered, bytes, Protocol.Sessions.RadiusDropReason.None, null, null);
        return (builder.Check(result, authenticator, request), authenticator);
    }

    /// <summary>An Access-Request around <paramref name="eap"/> with this switch's attributes.</summary>
    public byte[] BuildRequest(byte[] eap, out byte[] authenticator)
    {
        authenticator = RandomNumberGenerator.GetBytes(16);
        var builder = new TestSwitch(null!)
        {
            Client = _builder.Client,
            Source = Source,
            CallingStationId = CallingStationId,
            NasPortId = NasPortId,
            State = _builder.State,
        };
        return builder.Build(eap, authenticator);
    }

    /// <summary>A whole conversation: identity, TLS, the result.</summary>
    public async Task<Conversation> RunAsync(TestSupplicant camera, int maxRoundTrips = 200)
    {
        byte[]? state = null;
        var eap = EapPacket.Response(1, EapType.Identity, Encoding.UTF8.GetBytes("axis-accc8e123456")).ToBytes();
        for (var round = 1; round <= maxRoundTrips; round++)
        {
            var (answer, authenticator) = await SendAsync(eap, state);
            if (answer is null)
            {
                return new Conversation(null, round, null, null, []);
            }

            var eapBack = TestSwitch.Join(answer);
            var packet = eapBack.Length > 0 ? EapPacket.Parse(eapBack).Packet : null;
            switch (answer.Code)
            {
                case RadiusCode.AccessChallenge:
                    state = answer.Find(RadiusAttributeType.State)?.Value.ToArray() ?? throw new InvalidOperationException("Challenge without State");
                    eap = camera.Respond(packet ?? throw new InvalidOperationException("Challenge without EAP"))!.ToBytes();
                    break;
                case RadiusCode.AccessAccept:
                    if (packet is not null)
                    {
                        camera.Respond(packet);
                    }

                    return new Conversation(RadiusCode.AccessAccept, round, _builder.DecryptMsk(answer, authenticator), answer, []);
                default:
                    if (packet is not null)
                    {
                        camera.Respond(packet);
                    }

                    return new Conversation(answer.Code, round, null, answer, []);
            }
        }

        throw new InvalidOperationException("The conversation did not end.");
    }

    /// <summary>The VLAN of an Access-Accept (Tunnel-Private-Group-ID), or null.</summary>
    public static int? VlanOf(RadiusPacket accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        var group = accept.Find(81)?.Value.ToArray();
        if (group is null)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(group[0] < 0x20 ? group[1..] : group);
        return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }
}
