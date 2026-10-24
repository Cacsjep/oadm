using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>
/// The page's server in the tests and for the screenshots: answers every plugin method of the contract from demo data
/// (switches, VLAN rules, sessions in every state, log rows with a warning and refusals, revoked and issued certificates)
/// and records what the page sent.
/// </summary>
internal sealed class FakeRadiusBackend
{
    private readonly object _gate = new();

    public FakeRadiusBackend()
    {
        var now = DateTime.UtcNow;
        State = new RadiusState
        {
            Running = true,
            StatusText = "Running on port 1812",
            StatusKind = ServiceStatus.Ok,
            HasAllDevices = true,
            Settings = new RadiusSettings
            {
                ListenOn = "eth0",
                ListenOnName = "Ethernet",
                CipherSuites = [.. Suites.Where(s => !s.Legacy).Select(s => s.Id)],
                VlansEnabled = true,
                DefaultVlan = 99,
            },
            CipherSuitesListed = Suites,
            ServerCertificate = new ServerCertificateInfo(now.AddDays(364), ["10.0.0.17", "oadm-server"]),
            Interfaces =
            [
                InterfaceOptions.All,
                new InterfaceOption("eth0", "Ethernet - 10.0.0.17/24 (Intel I219)", "Ethernet", ["10.0.0.17"]),
            ],
        };
        Switches =
        [
            new SwitchRow { Id = Guid.NewGuid(), Name = "Core switch", SiteName = "This server", Address = "10.0.0.2", State = SwitchStates.Answered, LastRequestUtc = now.AddSeconds(-4), RadiusServerAddress = "10.0.0.17" },
            new SwitchRow { Id = Guid.NewGuid(), Name = "Floor 2", SiteName = "This server", Address = "10.0.2.2", State = SwitchStates.WrongSecret, LastRequestUtc = now.AddMinutes(-3), DroppedCount = 12, RadiusServerAddress = "10.0.0.17" },
            new SwitchRow { Id = Guid.NewGuid(), Name = "Parking", SiteName = "This server", Address = "switch-parking.example.com", State = SwitchStates.NoRequests, RadiusServerAddress = "10.0.0.17" },
            new SwitchRow { Id = Guid.NewGuid(), Name = "Gate house", Address = "192.168.1.2", SiteId = BuildingB, SiteName = "Building B", State = SwitchStates.SiteOffline, LastRequestUtc = now.AddHours(-2), RadiusServerAddress = "192.168.1.5" },
        ];
        Vlans = new VlanSettings
        {
            Enabled = true,
            Rules =
            [
                new VlanRule(VlanRuleKinds.Site, BuildingB.ToString(), "Building B", 30),
                new VlanRule(VlanRuleKinds.Tag, "PTZ", "PTZ", 20),
            ],
            DefaultVlan = 99,
        };
        Sessions =
        [
            Session(now.AddSeconds(-2), "AC:CC:8E:12:34:01", "waitingForIdentity", "Waiting for identity", guess: true),
            Session(now.AddSeconds(-3), "AC:CC:8E:12:34:02", "sendingServerCertificate", "Sending server certificate: fragment 2 of 4", guess: true, fragment: new FragmentInfo(2, 4)),
            Session(now.AddSeconds(-4), "AC:CC:8E:12:34:03", "receivingClientCertificate", "Receiving client certificate: fragment 3 of 5", guess: true, fragment: new FragmentInfo(3, 5)),
            Session(now.AddSeconds(-5), "AC:CC:8E:12:34:04", "checkingClientCertificate", "Checking client certificate", guess: true),
            Session(now.AddSeconds(-6), "AC:CC:8E:12:34:05", SessionStates.Authenticated, "Authenticated with VLAN 20", vlan: 20),
            Session(now.AddSeconds(-7), "AC:CC:8E:65:43:21", SessionStates.Failed, "Failed: The device does not trust the server certificate (TLS alert 48, unknown CA)"),
        ];
        Log =
        [
            LogEntry(now.AddSeconds(-6), "AC:CC:8E:12:34:05", LogResults.Authenticated, null, null, 20, "10.0.0.45"),
            LogEntry(now.AddSeconds(-30), "AC:CC:8E:65:43:21", LogResults.Authenticated, null, "MAC address AC:CC:8E:65:43:21 does not match the certificate of ACCC8E123406", 20, "10.0.0.46"),
            LogEntry(now.AddMinutes(-2), "AC:CC:8E:12:34:07", LogResults.Failed, "Certificate of a device OADM does not manage (ACCC8E123407)", null, null, null),
            LogEntry(now.AddMinutes(-5), "AC:CC:8E:12:34:08", LogResults.Failed, "Client certificate expired on 2026-09-30", null, null, "10.0.0.48"),
            LogEntry(now.AddMinutes(-9), "AC:CC:8E:12:34:09", LogResults.Failed, "Certificate revoked on 2026-10-20 (Key compromised)", null, null, "10.0.0.49"),
        ];
        Revoked =
        [
            new RevokedRow { SerialNumber = "7DFD0549A1", RevokedUtc = now.AddDays(-4), Reason = "Key compromised", UserName = "admin", DeviceId = Guid.NewGuid(), DeviceName = "10.0.0.49", NotAfterUtc = now.AddDays(300) },
            new RevokedRow { SerialNumber = "1A2B3C4D5E", RevokedUtc = now.AddDays(-12), Reason = "Device replaced", UserName = "installer", DeviceId = Guid.NewGuid(), NotAfterUtc = now.AddDays(200) },
        ];
        Issued =
        [
            .. Enumerable.Range(0, 12).Select(i => new IssuedCertificateInfo
            {
                SerialNumber = (0x5A0000 + i).ToString("X", CultureInfo.InvariantCulture) + "01",
                DeviceId = Guid.NewGuid(),
                DeviceSerial = "ACCC8E1234" + i.ToString("X2", CultureInfo.InvariantCulture),
                Device = "10.0.0." + (40 + i).ToString(CultureInfo.InvariantCulture),
                DeviceManaged = true,
                Purpose = DeviceCertificatePurposes.Dot1x,
                Source = i % 3 == 0 ? IssuedCertificateSources.Est : IssuedCertificateSources.Task,
                NotBeforeUtc = now.AddDays(-30 - i),
                NotAfterUtc = now.AddDays(i == 2 ? 12 : 335 - i),
                IssuedUtc = now.AddDays(-30 - i),
                State = i == 2 ? IssuedCertificateState.ExpiresSoon : i == 5 ? IssuedCertificateState.Revoked : IssuedCertificateState.Valid,
                Revocation = i == 5 ? new CertificateRevocation("5A000501", now.AddDays(-1), RevocationReason.KeyCompromised, "admin") : null,
            }),
        ];
    }

    public static readonly Guid BuildingB = Guid.Parse("6b1f0a0e-8a83-4a4f-9c84-6c3c3f7e2b10");

    public static IReadOnlyList<CipherSuiteOption> Suites { get; } =
    [
        new(0x1301, "TLS_AES_128_GCM_SHA256", false, true),
        new(0x1302, "TLS_AES_256_GCM_SHA384", false, true),
        new(0x1303, "TLS_CHACHA20_POLY1305_SHA256", false, true),
        new(0xC02B, "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256", false, false),
        new(0xC02C, "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384", false, false),
        new(0xCCA9, "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256", false, false),
        new(0xC02F, "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256", false, false),
        new(0xC030, "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384", false, false),
        new(0xCCA8, "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256", false, false),
        new(0xC009, "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA", true, false),
        new(0xC00A, "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA", true, false),
        new(0xC013, "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA", true, false),
        new(0xC014, "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA", true, false),
    ];

    public RadiusState State { get; set; }

    public List<SwitchRow> Switches { get; }

    public VlanSettings Vlans { get; set; }

    public List<SessionRow> Sessions { get; }

    public List<SiteRef> Sites { get; } = [new SiteRef(BuildingB, "Building B")];

    public List<LogRow> Log { get; set; }

    public List<RevokedRow> Revoked { get; }

    public List<IssuedCertificateInfo> Issued { get; }

    /// <summary>Every call: method and payload.</summary>
    public List<(string Method, string? Payload)> Calls { get; } = [];

    /// <summary>Answers a method with this exception instead (the server's refusal).</summary>
    public Dictionary<string, Exception> Failures { get; } = [];

    /// <summary>The events the page receives.</summary>
    public Channel<PluginEvent> Events { get; } = Channel.CreateUnbounded<PluginEvent>();

    /// <summary>A camera's client certificate (the log's and the picker's View certificate).</summary>
    public static byte[] CameraCertificate { get; } = MakeCertificate();

    public int CountOf(string method)
    {
        lock (_gate)
        {
            return Calls.Count(c => c.Method == method);
        }
    }

    public T LastPayload<T>(string method)
    {
        lock (_gate)
        {
            return RadiusJson.Deserialize<T>(Calls.Last(c => c.Method == method).Payload);
        }
    }

    /// <summary>Methods whose answer waits until the test completes the source (a read still running).</summary>
    public Dictionary<string, TaskCompletionSource> Holds { get; } = [];

    /// <summary>Holds the answers of <paramref name="method"/> until the returned source is completed.</summary>
    public TaskCompletionSource Hold(string method)
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Holds[method] = hold;
        return hold;
    }

    public async Task<string?> InvokeAsync(string method, string? payload, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add((method, payload));
        }

        if (Holds.TryGetValue(method, out var hold))
        {
            await hold.Task.WaitAsync(ct);
        }

        if (Failures.TryGetValue(method, out var failure))
        {
            throw failure;
        }

        return Answer(method, payload);
    }

    private string? Answer(string method, string? payload)
    {

        object? answer = method switch
        {
            RadiusMethods.GetState => State,
            RadiusMethods.SaveSettings => State = State with { Settings = RadiusJson.Deserialize<RadiusSettings>(payload) },
            RadiusMethods.ListSwitches => new SwitchList(Switches),
            RadiusMethods.SaveSwitch => SaveSwitch(RadiusJson.Deserialize<SaveSwitchRequest>(payload)),
            RadiusMethods.GenerateSecret => new GeneratedSecret("k7Qw9zR2mX4pL8vN3bT6yH1cF5jD0sAe"),
            RadiusMethods.DeleteSwitch => DeleteSwitch(RadiusJson.Deserialize<SwitchRef>(payload)),
            RadiusMethods.GetVlans => Vlans,
            RadiusMethods.SaveVlans => Vlans = RadiusJson.Deserialize<VlanSettings>(payload),
            RadiusMethods.ListSessions => new SessionList(Sessions),
            RadiusMethods.ListLog => ListLog(RadiusJson.Deserialize<LogQuery>(payload)),
            RadiusMethods.DeleteLog => DeleteLog(),
            RadiusMethods.GetLogCertificate => new LogCertificate(Convert.ToBase64String(CameraCertificate)),
            RadiusMethods.ListRevoked => ListRevoked(payload is null ? new RevokedQuery(null, 0, RevokedViewModel.PageSize) : RadiusJson.Deserialize<RevokedQuery>(payload)),
            RadiusMethods.Revoke => Revoke(RadiusJson.Deserialize<RevokeRequest>(payload)),
            RadiusPageMethods.ListSites => new SiteList(Sites),
            RadiusPageMethods.ListCertificates => ListCertificates(RadiusJson.Deserialize<CertificateListQuery>(payload)),
            RadiusPageMethods.GetIssuedCertificate => new LogCertificate(Convert.ToBase64String(CameraCertificate)),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
        return answer is null ? null : RadiusJson.Serialize(answer);
    }

    /// <summary>The server part with pages: newest first, the search over device, serial, reason and user.</summary>
    private RevokedPage ListRevoked(RevokedQuery query)
    {
        var matching = Revoked
            .Where(r => string.IsNullOrEmpty(query.Search) || new RevokedRowViewModel(r).Matches(query.Search))
            .OrderByDescending(r => r.RevokedUtc)
            .ToList();
        return new RevokedPage([.. matching.Skip(query.Offset).Take(Math.Clamp(query.Limit, 1, 500))]) { Total = matching.Count };
    }

    public void Publish(string topic, object payload) => Events.Writer.TryWrite(new PluginEvent(topic, RadiusJson.Serialize(payload)));

    public static SessionRow Session(DateTime started, string mac, string state, string text, bool guess = false, FragmentInfo? fragment = null, int? vlan = null) => new()
    {
        Id = Guid.NewGuid(),
        StartedUtc = started,
        SwitchName = "Core switch",
        Port = "GigabitEthernet1/0/" + mac[^1],
        PortType = "Ethernet",
        Mac = mac,
        Identity = "axis-" + mac.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant(),
        DeviceId = Guid.NewGuid(),
        DeviceName = "10.0.0.4" + mac[^1],
        DeviceGuess = guess,
        IpAddress = "10.0.0.4" + mac[^1],
        State = state,
        StateText = text,
        Fragment = fragment,
        DurationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds,
        Vlan = vlan,
        TlsVersion = "TLS 1.3",
    };

    public static LogRow LogEntry(DateTime time, string mac, string result, string? reason, string? warning, int? vlan, string? ip) => new()
    {
        Id = Interlocked.Increment(ref _nextLogId),
        TimeUtc = time,
        SwitchName = "Core switch",
        Port = "GigabitEthernet1/0/" + mac[^1],
        Mac = mac,
        Identity = "axis-" + mac.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant(),
        DeviceName = ip ?? string.Empty,
        IpAddress = ip ?? string.Empty,
        Result = result,
        Reason = reason,
        Warning = warning,
        Vlan = vlan,
        CertificateSerial = "5A000001",
        HasCertificate = true,
    };

    private static long _nextLogId;

    private SwitchRow SaveSwitch(SaveSwitchRequest request)
    {
        var row = new SwitchRow
        {
            Id = request.Id ?? Guid.NewGuid(),
            Name = request.Name ?? string.Empty,
            Address = request.Address ?? string.Empty,
            SiteId = request.SiteId,
            SiteName = request.SiteId is null ? "This server" : "Building B",
            RadiusServerAddress = "10.0.0.17",
        };
        var index = Switches.FindIndex(s => s.Id == row.Id);
        if (index >= 0)
        {
            Switches[index] = row;
        }
        else
        {
            Switches.Add(row);
        }

        return row;
    }

    private object? DeleteSwitch(SwitchRef request)
    {
        Switches.RemoveAll(s => s.Id == request.Id);
        return null;
    }

    private LogPage ListLog(LogQuery query)
    {
        var rows = Log.Where(r => query.Search is null
            || r.Mac.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || (r.Reason ?? string.Empty).Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || (r.DeviceName ?? string.Empty).Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToList();
        return new LogPage(rows.Count, [.. rows.Skip(query.Offset).Take(query.Limit)]);
    }

    private object? DeleteLog()
    {
        Log = [];
        return null;
    }

    private RevokeReply Revoke(RevokeRequest request)
    {
        var revoked = 0;
        foreach (var serial in request.Serials)
        {
            if (Revoked.Any(r => r.SerialNumber == serial))
            {
                continue;
            }

            Revoked.Add(new RevokedRow { SerialNumber = serial, RevokedUtc = DateTime.UtcNow, Reason = request.Reason, UserName = "admin", DeviceName = "10.0.0.40", NotAfterUtc = DateTime.UtcNow.AddDays(100) });
            revoked++;
        }

        return new RevokeReply(revoked, request.Serials.Count - revoked, 0);
    }

    private CertificateListPage ListCertificates(CertificateListQuery query)
    {
        var rows = Issued.Where(r => (query.Purpose is null || r.Purpose == query.Purpose) && (query.State is null || r.State == query.State)
            && (query.Search is null || r.Device.Contains(query.Search, StringComparison.OrdinalIgnoreCase))).ToList();
        return new CertificateListPage { Total = rows.Count, Offset = query.Offset, Rows = [.. rows.Skip(query.Offset).Take(query.Limit)] };
    }

    private static byte[] MakeCertificate()
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=OADM Root CA acs, O=OADM", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=axis-accc8e123405", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("axis-accc8e123405.local");
        request.CertificateExtensions.Add(san.Build());
        using var leaf = request.Create(ca, now.AddMinutes(-5), now.AddDays(365), [0x5A, 0x00, 0x00, 0x01]);
        return leaf.RawData;
    }
}

/// <summary>The page's view of the fake server: calls go to the backend, events come from its channel.</summary>
internal sealed class FakePageContext(FakeRadiusBackend backend) : ICorePluginClientContext
{
    public bool Confirm { get; set; } = true;

    public List<string> Confirms { get; } = [];

    public List<(string Title, string Message)> Messages { get; } = [];

    public bool AllDevices { get; set; } = true;

    public bool Manage { get; set; } = true;

    /// <summary>The devices this client knows (their tags are the VLAN rule choices).</summary>
    public IReadOnlyList<IDeviceInfo> DeviceList { get; set; } =
    [
        new Oadm.Sdk.Testing.FakeDevice { Address = "10.0.0.45", Serial = "ACCC8E123405", Tags = ["PTZ"] },
        new Oadm.Sdk.Testing.FakeDevice { Address = "10.0.0.46", Serial = "ACCC8E123406", Tags = ["Outdoor"], SiteId = FakeRadiusBackend.BuildingB, SiteName = "Building B" },
    ];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public bool HasAllDevices => AllDevices;

    public event EventHandler? PermissionsChanged;

    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public bool Has(string permission) => Manage || !permission.EndsWith(":" + PermissionKeys.Manage, StringComparison.Ordinal);

    public void RaisePermissionsChanged() => PermissionsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Answers a method itself instead of the fake server (an older server part); null = the fake server answers.</summary>
    public Func<string, string?, string?>? Answer { get; set; }

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        Answer?.Invoke(method, payloadJson) is { } answer ? Task.FromResult<string?>(answer) : backend.InvokeAsync(method, payloadJson, ct);

    public IAsyncEnumerable<PluginEvent> WatchEventsAsync(CancellationToken ct) => backend.Events.Reader.ReadAllAsync(ct);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirms.Add(message);
        return Task.FromResult(Confirm);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }
}
