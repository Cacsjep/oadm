using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Plugins.DeviceConfig;
using Oadm.Sdk.Certificates;
using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Tests.Client;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Headless.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Oadm.Theme.OadmColors.Current = Headless.IsLight ? Oadm.Theme.Appearance.Light : Oadm.Theme.Appearance.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.Provisioning.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Headless helpers: one session per test process, the real host page around the view, PNGs (OADM_SCREENSHOT_DIR).</summary>
internal static class Headless
{
    private static readonly Lazy<HeadlessUnitTestSession> SessionInstance = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry)));

    public static HeadlessUnitTestSession Session => SessionInstance.Value;

    public static bool IsLight => Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE") == "Light";

    public static string Appearance => IsLight ? "light" : "dark";

    public static Window Host(Control view, int width = 1500, int height = 900)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(ProvisioningInfo.PluginId, ProvisioningInfo.DisplayName, view, true),
        };
        var window = new Window { Width = width, Height = height, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
    }

    public static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    public static void Capture(TopLevel window, string name)
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
    }
}

/// <summary>The page context with canned answers per method, the confirmation's answer and a channel of pushed events.</summary>
internal sealed class FakePageContext : ICorePluginClientContext
{
    private readonly Channel<PluginEvent> _events = Channel.CreateUnbounded<PluginEvent>();

    public Dictionary<string, Func<string?, string?>> Answers { get; } = new(StringComparer.Ordinal);

    public List<(string Method, string? Payload)> Calls { get; } = [];

    public List<(string Title, string Message, string Confirm)> Confirmations { get; } = [];

    public List<string> Opened { get; } = [];

    public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

    /// <summary>The managed devices.</summary>
    public List<IDeviceInfo> DeviceList { get; } = [];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    /// <summary>The files the page uploaded.</summary>
    public List<UploadedFile> Uploads { get; } = [];

    public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        var upload = new UploadedFile("u" + (Uploads.Count + 1), Path.GetFileName(localPath), 1234, "abc");
        Uploads.Add(upload);
        return Task.FromResult(upload);
    }

    public bool ConfirmAnswer { get; set; } = true;

    /// <summary>The signed-in user is an administrator (the eye of the credential choice).</summary>
    public bool IsAdministrator { get; set; } = true;

    /// <summary>The stored passwords the host reveals, by credential list entry.</summary>
    public Dictionary<Guid, string> StoredPasswords { get; } = [];

    /// <summary>The entries the page asked the host to reveal, in order.</summary>
    public List<Guid> Reveals { get; } = [];

    /// <summary>Holds the next reveals until it completes.</summary>
    public Task? RevealGate { get; set; }

    public async Task<string> RevealCredentialAsync(Guid credentialId, CancellationToken ct)
    {
        Reveals.Add(credentialId);
        if (RevealGate is { } gate)
        {
            await gate;
        }

        return StoredPasswords.TryGetValue(credentialId, out var password)
            ? password
            : throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.NotFound, "Credential not found."));
    }

    /// <summary>Holds every call until it completes (a loading test).</summary>
    public Task? Gate { get; set; }

    /// <summary>Holds the calls of one method until its task completes.</summary>
    public Dictionary<string, Task> MethodGates { get; } = new(StringComparer.Ordinal);

    public bool Has(string permission) => !Denied.Contains(permission);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message, confirmText));
        return Task.FromResult(ConfirmAnswer);
    }

    public Task OpenAsync(string hostPage)
    {
        Opened.Add(hostPage);
        return Task.CompletedTask;
    }

    public int CallsOf(string method) => Calls.Count(c => c.Method == method);

    public string? LastPayloadOf(string method) => Calls.LastOrDefault(c => c.Method == method).Payload;

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((method, payloadJson));
        if (Gate is { } gate)
        {
            await gate;
        }

        if (MethodGates.TryGetValue(method, out var held))
        {
            await held;
        }

        return Answers.TryGetValue(method, out var answer) ? answer(payloadJson) : throw new InvalidOperationException("No answer for " + method);
    }

    public void Push(string topic, object payload) =>
        _events.Writer.TryWrite(new PluginEvent(topic, JsonSerializer.Serialize(payload, payload.GetType(), ProvisioningJson.Options)));

    public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (await _events.Reader.WaitToReadAsync(ct))
        {
            while (_events.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }
}

/// <summary>The sample server: a profile, eight cameras in mixed states, the DHCP server, the real profile check.</summary>
internal static class Samples
{
    public static readonly Guid ProfileId = Guid.Parse("6f1d2a3b-1111-4c2d-9e8f-0a1b2c3d4e5f");

    public static readonly Guid RootCredential = Guid.Parse("0c0c0c0c-2222-4c2d-9e8f-0a1b2c3d4e5f");

    public static JsonSerializerOptions Json => ProvisioningJson.Options;

    /// <summary>The node kinds with the VAPIX Commander library of the repository.</summary>
    public static readonly NodeRegistry Registry =
        NodeRegistry.Create(VapixCommander.CommandLibrary.Load(Oadm.Tests.Shared.TestRepository.Path("plugins", "Oadm.Plugins.VapixCommander", "Library")));

    /// <summary>The commands a profile can run (as the plugin lists them).</summary>
    public static IReadOnlyList<ProfileCommand> Commands =>
        [.. ((VapixCommandsNode)Registry.Find(NodeKind.VapixCommands)!).Library.Commands
            .Where(c => !c.Dangerous && !c.Fields.Any(f => f.IsSecret || f.IsFile))
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ProfileCommand(c.Id, c.Name, c.Category, c.Description, c.Fields))];

    public static IReadOnlyList<NodeKindInfo> Kinds =>
        [.. Registry.All.Select(n => new NodeKindInfo(n.Kind, n.Name, n.IsFixed, n.AlwaysStops, NodeSettings.Write(n.DefaultSettings)))];

    /// <summary>PKI's policies: two for HTTPS, one for 802.1X.</summary>
    public static readonly IReadOnlyList<IssuingPolicyInfo> Policies =
    [
        new(Guid.Parse("3c3c3c3c-0001-4c2d-9e8f-0a1b2c3d4e5f"), "Cameras", IssuingPolicyUse.Https),
        new(Guid.Parse("3c3c3c3c-0002-4c2d-9e8f-0a1b2c3d4e5f"), "Lobby, 1 year", IssuingPolicyUse.Https),
        new(Guid.Parse("3c3c3c3c-0003-4c2d-9e8f-0a1b2c3d4e5f"), "Switch ports", IssuingPolicyUse.Dot1x),
    ];

    public static readonly IReadOnlyList<DeviceCredentialName> Credentials =
        [new(RootCredential, "root", new DateTimeOffset(2026, 10, 2, 9, 15, 0, TimeSpan.Zero)), new(Guid.Parse("0d0d0d0d-3333-4c2d-9e8f-0a1b2c3d4e5f"), "operator")];

    public static readonly Guid SecondRoot = Guid.Parse("0e0e0e0e-4444-4c2d-9e8f-0a1b2c3d4e5f");

    public static readonly Guid ThirdRoot = Guid.Parse("0f0f0f0f-5555-4c2d-9e8f-0a1b2c3d4e5f");

    /// <summary>Three root entries, told apart only by when they were added (the eye's tests and screenshot).</summary>
    public static readonly IReadOnlyList<DeviceCredentialName> ThreeRoots =
    [
        .. Credentials,
        new(SecondRoot, "root", new DateTimeOffset(2026, 11, 3, 16, 40, 0, TimeSpan.Zero)),
        new(ThirdRoot, "root", new DateTimeOffset(2026, 11, 18, 8, 5, 0, TimeSpan.Zero)),
    ];

    public static DhcpServerSummary Dhcp(bool running = true) =>
        new(running, running ? "Running" : "Stopped", "Ethernet", "10.0.0.17", "255.255.255.0", "10.0.0.100", "10.0.0.199");

    public static Profile Profile => new()
    {
        Id = ProfileId,
        Name = "Lobby cameras",
        Nodes =
        [
            new FlowNode { Id = "n1", Kind = NodeKind.SetPassword, ContinueOnError = false, Settings = NodeSettings.Write(new SetPasswordSettings(RootCredential)) },
            new FlowNode
            {
                Id = "n4", Kind = NodeKind.Network, ContinueOnError = false,
                Settings = NodeSettings.Write(new NetworkNodeSettings(IpMode.Pool, "192.168.15.10", "192.168.15.50", "255.255.255.0", "192.168.15.1", ["192.168.15.1"])),
            },
            new FlowNode { Id = "n5", Kind = NodeKind.DateTime, Settings = NodeSettings.Write(new DateTimeSettings("Europe/Vienna", ["10.0.0.17"])) },
        ],
    };

    public static readonly Guid FullProfileId = Guid.Parse("8b8b8b8b-5555-4c2d-9e8f-0a1b2c3d4e5f");

    /// <summary>A realistic profile with ten nodes (the editor's full screenshot).</summary>
    public static Profile FullProfile => new()
    {
        Id = FullProfileId,
        Name = "Parking garage",
        Nodes =
        [
            new FlowNode { Id = "n1", Kind = NodeKind.SetPassword, ContinueOnError = false, Settings = NodeSettings.Write(new SetPasswordSettings(RootCredential)) },
            new FlowNode { Id = "n3", Kind = NodeKind.UpgradeOnline, Settings = NodeSettings.Write(new UpgradeOnlineSettings(OsTarget.LatestLts, "12.6.95")) },
            new FlowNode { Id = "n4", Kind = NodeKind.Network, Settings = NodeSettings.Write(new NetworkNodeSettings(DnsServers: ["10.0.0.1"], SearchDomain: "site.local")) },
            new FlowNode { Id = "n5", Kind = NodeKind.DateTime, Settings = NodeSettings.Write(new DateTimeSettings("Europe/Vienna", [], OadmNtp: true)) },
            new FlowNode
            {
                Id = "n6", Kind = NodeKind.Configuration,
                Settings = JsonSerializer.SerializeToElement(new { fileId = "1f1f1f1f-6666-4c2d-9e8f-0a1b2c3d4e5f", fileName = "parking-p3265.cfg", parameters = true, actionRules = true, streamProfiles = false }),
            },
            new FlowNode
            {
                Id = "n7", Kind = NodeKind.Users,
                Settings = NodeSettings.Write(new UsersSettings([new UserAccount("operator1", CameraRole.Operator, false, Guid.Parse("2e2e2e2e-7777-4c2d-9e8f-0a1b2c3d4e5f"))])),
            },
            new FlowNode { Id = "n8", Kind = NodeKind.Certificate, Settings = NodeSettings.Write(new CertificateSettings(CertificateMode.Policy, Policies[0].Id)) },
            new FlowNode { Id = "n9", Kind = NodeKind.SdCard, Settings = NodeSettings.Write(new SdCardSettings(true, false, null, 30)) },
            new FlowNode { Id = "n10", Kind = NodeKind.Autofocus, Settings = NodeSettings.Write(new AutofocusSettings()) },
            new FlowNode
            {
                Id = "n11", Kind = NodeKind.Network, ContinueOnError = false,
                Settings = NodeSettings.Write(new NetworkNodeSettings(IpMode.Pool, "192.168.20.10", "192.168.20.80", "255.255.255.0", "192.168.20.1", ["192.168.20.1"])),
            },
        ],
    };

    public static StatusReply Status(bool on = true) => new()
    {
        On = on,
        OnBy = on ? "anna" : null,
        OnAt = on ? new DateTimeOffset(2026, 11, 18, 14, 2, 0, TimeSpan.Zero) : null,
        ActiveProfileId = ProfileId,
        ActiveProfileName = "Lobby cameras",
        ActiveProfileValid = true,
        Dhcp = Dhcp(),
        Warnings = ["The pool overlaps the DHCP server's range 10.0.0.100 to 10.0.0.199."],
    };

    public static ProfilesReply Profiles(bool valid = true) =>
        new([new ProfileSummary(ProfileId, "Lobby cameras", 5, true, valid), new ProfileSummary(Guid.Parse("7a7a7a7a-4444-4c2d-9e8f-0a1b2c3d4e5f"), "Parking", 3, false, true)], ProfileId);

    /// <summary>
    /// The configuration file of the previews: the file ADM wrote from an AXIS P3265-V (AXIS OS 12.11.77; 1 action rule with
    /// its own schedule, 3 stream profiles) with more image parameters, an address, a host name, a connection setting and an
    /// empty community string, which Configure devices never writes on every camera.
    /// </summary>
    public static string ConfigFile
    {
        get
        {
            var file = AdmConfigFile.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "adm-p3265v-12.11.77.cfg")));
            NameValue[] more =
            [
                new("Image.I0.Appearance.Compression", "30"), new("Image.I0.Appearance.Resolution", "1920x1080"), new("Image.I0.Appearance.Mirror", "no"),
                new("Image.I0.Name", "View Area 1"), new("Image.I0.Stream.FPS", "25"), new("Image.I0.Text.TextEnabled", "yes"),
                new("Image.I0.Text.String", "Parking garage"), new("Image.I0.Text.DateEnabled", "yes"), new("Image.I0.Text.ClockEnabled", "yes"),
                new("Image.I0.RateControl.Mode", "vbr"), new("Image.I0.RateControl.TargetBitrate", "4000"), new("Image.I0.MPEG.ZStrength", "20"),
                new("ImageSource.I0.Sensor.WDR", "on"), new("ImageSource.I0.DayNight.IrCutFilter", "auto"), new("ImageSource.I0.Sensor.Brightness", "50"),
                new("ImageSource.I0.Sensor.Sharpness", "50"), new("Audio.A0.Enabled", "no"), new("SNMP.Enabled", "no"), new("SNMP.V2.Community", ""),
                new("Network.HostName", "lobby-1"), new("Network.IPAddress", "10.0.0.48"), new("Network.DNSServer1", "10.0.0.1"), new("HTTPS.Enabled", "yes"),
            ];
            return System.Text.Encoding.UTF8.GetString(AdmConfigFile.Write(file with { Parameters = [.. file.Parameters, .. more] }));
        }
    }

    /// <summary>A context that answers like the server.</summary>
    public static FakePageContext Context(StatusReply? status = null, ProfilesReply? profiles = null)
    {
        var ctx = new FakePageContext();
        var stored = new Dictionary<Guid, Profile> { [ProfileId] = Profile, [FullProfileId] = FullProfile };
        ctx.Answers[ProvisioningMethods.GetStatus] = _ => Serialize(status ?? Status());
        ctx.Answers[ProvisioningMethods.ListProfiles] = _ => Serialize(profiles ?? Profiles());
        ctx.Answers[ProvisioningMethods.NodeKinds] = _ => Serialize(Kinds);
        ctx.Answers[ProvisioningMethods.Credentials] = _ => Serialize(Credentials);
        ctx.Answers[ProvisioningMethods.Policies] = _ => Serialize(Policies);
        ctx.Answers[ProvisioningMethods.VapixCommands] = _ => Serialize(Commands);
        ctx.Answers[ProvisioningMethods.StoreSecret] = _ => Serialize(new SecretReply(Guid.NewGuid()));
        ctx.Answers[ProvisioningMethods.ReadConfigFile] = _ => Serialize(new ConfigFileReply("parking-p3265.cfg", ConfigFile));
        ctx.Answers[ProvisioningMethods.StoreFile] = payload =>
        {
            var upload = ctx.Uploads.Single(u => u.Id == JsonSerializer.Deserialize<FileRequest>(payload!, Json)!.UploadId);
            return Serialize(new StoredFile(Guid.NewGuid(), upload.Name, upload.Size, upload.Sha256, DateTimeOffset.UnixEpoch));
        };
        ctx.Answers[ProvisioningMethods.GetProfile] = payload =>
        {
            var id = JsonSerializer.Deserialize<IdRequest>(payload!, Json)!.Id;
            return Serialize(new ProfileReply(stored[id], Check(stored[id])));
        };
        ctx.Answers[ProvisioningMethods.CheckProfile] = payload => Serialize(Check(JsonSerializer.Deserialize<ProfileRequest>(payload!, Json)!.Profile));
        ctx.Answers[ProvisioningMethods.SaveProfile] = payload =>
        {
            var request = JsonSerializer.Deserialize<SaveProfileRequest>(payload!, Json)!;
            var profile = request.Profile;
            if (request.Password is not null)
            {
                var id = Guid.NewGuid();
                profile = profile with
                {
                    Nodes = [.. profile.Nodes.Select(n => n.Kind == NodeKind.SetPassword ? n with { Settings = NodeSettings.Write(new SetPasswordSettings(id)) } : n)],
                };
            }

            stored[profile.Id] = profile;
            return Serialize(new ProfileReply(profile, Check(profile)));
        };
        ctx.Answers[ProvisioningMethods.TurnOn] = _ => Serialize(Status(on: true));
        ctx.Answers[ProvisioningMethods.TurnOff] = _ => Serialize(Status(on: false));
        ctx.Answers[ProvisioningMethods.ActivateProfile] = _ => Serialize(Status(on: false));
        ctx.Answers[ProvisioningMethods.DeleteProfile] = _ => Serialize(new ProfilesReply([], null));

        return ctx;
    }

    /// <summary>The server's own check, with the sample credential list and DHCP server.</summary>
    public static ProfileCheckResult Check(Profile profile) =>
        ProfileCheck.Check(profile, Registry, new ProfileCheckContext(Credentials.ToDictionary(c => c.Id, c => c.UserName), Dhcp()) { Policies = Policies });

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
