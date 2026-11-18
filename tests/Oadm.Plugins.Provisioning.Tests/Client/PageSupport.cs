using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

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
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(ProvisioningInfo.PluginId, ProvisioningInfo.DisplayName, view, new ProvisioningPage().HasOwnCards),
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

    public bool ConfirmAnswer { get; set; } = true;

    /// <summary>Holds every call until it completes (a loading test).</summary>
    public Task? Gate { get; set; }

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

    public static IReadOnlyList<NodeKindInfo> Kinds =>
        [.. NodeRegistry.Default.All.Select(n => new NodeKindInfo(n.Kind, n.Name, n.IsFixed, n.AlwaysStops, NodeSettings.Write(n.DefaultSettings)))];

    public static readonly IReadOnlyList<DeviceCredentialName> Credentials =
        [new(RootCredential, "root"), new(Guid.Parse("0d0d0d0d-3333-4c2d-9e8f-0a1b2c3d4e5f"), "operator")];

    public static DhcpServerSummary Dhcp(bool running = true) =>
        new(running, running ? "Running" : "Stopped", "Ethernet", "10.0.0.17", "255.255.255.0", "10.0.0.100", "10.0.0.199");

    public static Profile Profile => new()
    {
        Id = ProfileId,
        Name = "Lobby cameras",
        Nodes =
        [
            new FlowNode { Id = "n1", Kind = NodeKind.SetPassword, ContinueOnError = false, Settings = NodeSettings.Write(new SetPasswordSettings(RootCredential)) },
            new FlowNode { Id = "n2", Kind = NodeKind.AddToOadm, Settings = NodeSettings.Write(new AddToOadmSettings(["Lobby"])) },
            new FlowNode { Id = "n3", Kind = NodeKind.WaitForApproval, Settings = NodeSettings.Write(new WaitForApprovalSettings()) },
            new FlowNode
            {
                Id = "n4", Kind = NodeKind.SetIp, ContinueOnError = false,
                Settings = NodeSettings.Write(new SetIpSettings("192.168.15.10", "192.168.15.50", "255.255.255.0", "192.168.15.1", ["192.168.15.1"])),
            },
            new FlowNode { Id = "n5", Kind = NodeKind.DateTime, Settings = NodeSettings.Write(new DateTimeSettings("Europe/Vienna", ["10.0.0.17"])) },
        ],
    };

    public static StatusReply Status(bool started = true, bool dhcpRunning = true, CameraCounts? counts = null) => new()
    {
        Started = started,
        StartedAt = started ? new DateTimeOffset(2026, 11, 18, 14, 2, 0, TimeSpan.Zero) : null,
        StartedBy = started ? "anna" : null,
        ActiveProfileId = ProfileId,
        ActiveProfileName = "Lobby cameras",
        SessionProfileName = started ? "Lobby cameras" : null,
        SessionProfile = started ? Profile : null,
        Dhcp = Dhcp(dhcpRunning),
        Counts = counts ?? (started ? Counts(Cameras()) : new CameraCounts(0, 0, 0, 0, 0, 0, 0, 0)),
        Warnings = started ? ["The pool overlaps the DHCP server's range 10.0.0.100 to 10.0.0.199."] : [],
    };

    public static ProfilesReply Profiles(bool valid = true) =>
        new([new ProfileSummary(ProfileId, "Lobby cameras", 5, true, valid), new ProfileSummary(Guid.Parse("7a7a7a7a-4444-4c2d-9e8f-0a1b2c3d4e5f"), "Parking", 3, false, true)], ProfileId);

    private static NodeResult Ok(string m) => NodeResult.Success(m);

    /// <summary>Eight cameras: done, with warnings, failed at Set IP, running, waiting for approval, queued, not new, starting.</summary>
    public static List<CameraRow> Cameras() =>
    [
        Row(1, "AXIS P3265-LVE", "192.168.15.10", CameraState.Done, 4,
            [Ok("Password set."), Ok("Added to OADM with the tag Lobby."), Ok("Continued by anna."), Ok("192.168.15.10 assigned."), Ok("Time zone Europe/Vienna, NTP 10.0.0.17.")]),
        Row(2, "AXIS M3086-V", "192.168.15.11", CameraState.DoneWithWarnings, 4,
            [Ok("Password set."), NodeResult.Warning("Added to OADM. OADM has not read the camera yet; the next nodes may fail."), Ok("Continued by anna."), Ok("192.168.15.11 assigned."), Ok("Time zone Europe/Vienna, NTP 10.0.0.17.")]),
        Row(3, "AXIS Q6135-LE", "10.0.0.131", CameraState.Failed, 3,
            [Ok("Password set."), NodeResult.Warning("Added to OADM. The tag Lobby could not be set."), Ok("Continued by anna."), NodeResult.Error("The camera did not take 192.168.15.12: it did not answer within 2 minutes. OADM keeps it at 10.0.0.131."), null]),
        Row(4, "AXIS P1468-LE", "10.0.0.132", CameraState.Running, 3, [Ok("Password set."), Ok("Added to OADM with the tag Lobby."), Ok("Continued by anna."), null, null]),
        Row(5, "AXIS M4317-PLVE", "10.0.0.133", CameraState.Running, 2, [Ok("Password set."), Ok("Added to OADM with the tag Lobby."), null, null, null], waiting: true),
        Row(6, "AXIS P3268-LV", "10.0.0.134", CameraState.Queued, -1, []),
        Row(7, "AXIS Q1656-LE", "10.0.0.135", CameraState.NotNew, -1, [], message: "Not new, left as it is."),
        Row(8, null, "10.0.0.136", CameraState.Running, 0, [null, null, null, null, null]),
    ];

    public static CameraRow Row(int n, string? model, string address, CameraState state, int node, IReadOnlyList<NodeResult?> results, bool waiting = false, string? message = null, long version = 1)
    {
        var mac = $"AC:CC:8E:4A:0{n / 10}:{n % 10:00}";
        var names = new[] { "Set password", "Add to OADM", "Wait for approval", "Set IP", "Date and time" };
        return new CameraRow
        {
            Mac = mac,
            Serial = mac.Replace(":", string.Empty, StringComparison.Ordinal),
            Address = address,
            Model = model,
            State = state,
            NodeIndex = node,
            NodeName = node >= 0 ? names[node] : null,
            WaitingForApproval = waiting,
            Results = results,
            Message = message,
            Version = version,
        };
    }

    public static CameraCounts Counts(IReadOnlyCollection<CameraRow> rows) => new(
        rows.Count,
        rows.Count(r => r.State == CameraState.Queued),
        rows.Count(r => r.State == CameraState.Running && !r.WaitingForApproval),
        rows.Count(r => r.WaitingForApproval),
        rows.Count(r => r.State == CameraState.Done),
        rows.Count(r => r.State == CameraState.DoneWithWarnings),
        rows.Count(r => r.State == CameraState.Failed),
        rows.Count(r => r.State == CameraState.NotNew));

    /// <summary>A context that answers like the server with <paramref name="cameras"/>, paged by <c>listCameras</c>.</summary>
    public static FakePageContext Context(StatusReply? status = null, List<CameraRow>? cameras = null, ProfilesReply? profiles = null)
    {
        var ctx = new FakePageContext();
        var rows = cameras ?? Cameras();
        var sortedRows = rows.OrderBy(r => r.Mac, StringComparer.Ordinal).ToList();
        var counts = Counts(rows);
        var stored = new Dictionary<Guid, Profile> { [ProfileId] = Profile };
        ctx.Answers[ProvisioningMethods.GetStatus] = _ => Serialize(status ?? Status());
        ctx.Answers[ProvisioningMethods.ListProfiles] = _ => Serialize(profiles ?? Profiles());
        ctx.Answers[ProvisioningMethods.NodeKinds] = _ => Serialize(Kinds);
        ctx.Answers[ProvisioningMethods.Credentials] = _ => Serialize(Credentials);
        ctx.Answers[ProvisioningMethods.ListCameras] = payload =>
        {
            var request = JsonSerializer.Deserialize<CamerasRequest>(payload!, Json)!;
            var sorted = sortedRows;
            var from = request.After is null ? 0 : sorted.FindIndex(r => string.CompareOrdinal(r.Mac, request.After) > 0);
            from = from < 0 ? sorted.Count : from;
            var page = sorted.Skip(from).Take(request.Limit).ToList();
            var more = from + page.Count < sorted.Count;
            return Serialize(new CamerasPage(page, sorted.Count, more ? page[^1].Mac : null, counts, 1));
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
        ctx.Answers[ProvisioningMethods.Start] = _ => Serialize(Status(started: true));
        ctx.Answers[ProvisioningMethods.Stop] = _ => Serialize(Status(started: false));
        ctx.Answers[ProvisioningMethods.ActivateProfile] = _ => Serialize(Status(started: false));
        ctx.Answers[ProvisioningMethods.DeleteProfile] = _ => Serialize(new ProfilesReply([], null));
        foreach (var method in new[] { ProvisioningMethods.RunFailedAgain, ProvisioningMethods.Continue, ProvisioningMethods.RemoveCameras })
        {
            ctx.Answers[method] = payload => Serialize(new CountReply(JsonSerializer.Deserialize<MacsRequest>(payload!, Json)!.Macs.Count));
        }

        return ctx;
    }

    /// <summary>The server's own check, with the sample credential list and DHCP server.</summary>
    public static ProfileCheckResult Check(Profile profile) =>
        ProfileCheck.Check(profile, NodeRegistry.Default, new ProfileCheckContext(Credentials.ToDictionary(c => c.Id, c => c.UserName), Dhcp()));

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}
