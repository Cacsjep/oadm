using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Plugins.Provisioning;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Plugins.Schedules.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Schedules.Tests.Client;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Headless.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Oadm.Theme.OadmColors.Current = Headless.IsLight ? Oadm.Theme.Appearance.Light : Oadm.Theme.Appearance.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.Schedules.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
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

    /// <summary>The view in the client's host page (title, subtitle) like the rail shows it.</summary>
    public static Window Host(Control view, int width = 1500, int height = 900)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(SchedulesInfo.PluginId, SchedulesInfo.DisplayName, view, true),
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

/// <summary>A device of the client's device list.</summary>
internal sealed record FakeDevice(Guid Id, string Address, string? Model, string? HostName, IReadOnlyList<string> Tags) : IDeviceInfo
{
    public string Serial => "ACCC8E" + Id.ToString("N")[..6].ToUpperInvariant();

    public string? FirmwareVersion => "12.6.94";

    public DeviceStatus Status => DeviceStatus.Ok;

    public DeviceCategory Category => DeviceCategory.Camera;

    public bool HasVideo => true;

    public IReadOnlyList<DeviceApi> Apis => [];
}

/// <summary>The page context with canned answers per method, the confirmation's answer and a channel of pushed events.</summary>
internal sealed class FakePageContext : ICorePluginClientContext
{
    private readonly Channel<PluginEvent> _events = Channel.CreateUnbounded<PluginEvent>();

    public Dictionary<string, Func<string?, string?>> Answers { get; } = new(StringComparer.Ordinal);

    public List<(string Method, string? Payload)> Calls { get; } = [];

    public List<(string Title, string Message, string Confirm)> Confirmations { get; } = [];

    public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

    public List<IDeviceInfo> DeviceList { get; } = [];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public bool ConfirmAnswer { get; set; } = true;

    /// <summary>Holds the calls of one method until its task completes.</summary>
    public Dictionary<string, Task> MethodGates { get; } = new(StringComparer.Ordinal);

    public bool Has(string permission) => !Denied.Contains(permission);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message, confirmText));
        return Task.FromResult(ConfirmAnswer);
    }

    public int CallsOf(string method) => Calls.Count(c => c.Method == method);

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add((method, payloadJson));
        if (MethodGates.TryGetValue(method, out var held))
        {
            await held;
        }

        return Answers.TryGetValue(method, out var answer) ? answer(payloadJson) : throw new InvalidOperationException("No answer for " + method);
    }

    public void Push(string topic, object payload) =>
        _events.Writer.TryWrite(new PluginEvent(topic, JsonSerializer.Serialize(payload, payload.GetType(), SchedulesJson.Options)));

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

/// <summary>The sample server: three schedules with runs, the action kinds, PKI's policies, two VAPIX commands, a simple check.</summary>
internal static class Samples
{
    public static JsonSerializerOptions Json => SchedulesJson.Options;

    /// <summary>The server's "now" of the samples (server time, UTC+1).</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 23, 14, 0, 0, TimeSpan.FromHours(1));

    public static readonly Guid WeeklyId = Guid.Parse("5c5c5c5c-0001-4c2d-9e8f-0a1b2c3d4e5f");
    public static readonly Guid NightlyId = Guid.Parse("5c5c5c5c-0002-4c2d-9e8f-0a1b2c3d4e5f");
    public static readonly Guid MonthlyId = Guid.Parse("5c5c5c5c-0003-4c2d-9e8f-0a1b2c3d4e5f");
    public static readonly Guid FailedRunId = Guid.Parse("6d6d6d6d-0002-4c2d-9e8f-0a1b2c3d4e5f");

    public static readonly IReadOnlyList<IssuingPolicyInfo> Policies =
    [
        new(Guid.Parse("3c3c3c3c-0001-4c2d-9e8f-0a1b2c3d4e5f"), "Cameras", IssuingPolicyUse.Https),
        new(Guid.Parse("3c3c3c3c-0003-4c2d-9e8f-0a1b2c3d4e5f"), "Switch ports", IssuingPolicyUse.Dot1x),
    ];

    public static IReadOnlyList<ActionKindInfo> Kinds =>
    [
        Kind(NodeKind.UpgradeOnline, "Upgrade AXIS OS online", "Upgrade AXIS OS from axis.com", new UpgradeOnlineSettings(), 10),
        Kind(NodeKind.UpgradeFiles, "Upgrade AXIS OS from files", "Upgrade AXIS OS from a file", new UpgradeFilesSettings(), 10),
        Kind(NodeKind.Configuration, "Configuration", "Apply the configuration", new ConfigurationSettings(), 1),
        Kind(NodeKind.Restart, "Restart", "Restart the device", new { }, 3),
        Kind(NodeKind.Certificate, "Certificate", "Install the certificate", new CertificateSettings(), 1),
        Kind(NodeKind.Dot1x, "802.1X", "Turn on 802.1X", new Dot1xSettings(), 1),
        Kind(NodeKind.VapixCommands, "VAPIX commands", "Run the VAPIX commands", new VapixCommandsSettings(), 1),
    ];

    public static IReadOnlyList<ProfileCommand> Commands =>
    [
        new("light-on", "Turn on the IR light", "Light", null, []),
        new("defog", "Turn on defog", "Image", null, []),
    ];

    /// <summary>"Site A" tagged cameras and some more: 12 devices.</summary>
    public static IEnumerable<IDeviceInfo> Devices() =>
        Enumerable.Range(1, 12).Select(i => (IDeviceInfo)new FakeDevice(
            Guid.Parse($"7e7e7e7e-{i:D4}-4c2d-9e8f-0a1b2c3d4e5f"),
            $"10.0.0.{40 + i}",
            i % 3 == 0 ? "AXIS Q6135-LE" : "AXIS P3265-LVE",
            $"cam-{i:D2}",
            i <= 8 ? ["Site A"] : []));

    public static Schedule Weekly => new()
    {
        Id = WeeklyId,
        Name = "Upgrade Site A",
        On = true,
        Stages = true,
        Devices = new ScheduleDevices { Kind = ScheduleDevicesKind.Chosen, Tags = ["Site A"], DeviceIds = [Guid.Parse("7e7e7e7e-0010-4c2d-9e8f-0a1b2c3d4e5f")] },
        When = new ScheduleWhen { Recurrence = Recurrence.Weekly, Days = [DayOfWeek.Monday], Time = new TimeOnly(23, 0), WindowEnd = new TimeOnly(5, 0) },
        Actions =
        [
            new FlowNode { Id = "n1", Kind = NodeKind.UpgradeOnline, Settings = NodeSettings.Write(new UpgradeOnlineSettings(OsTarget.LatestLts)) },
            new FlowNode { Id = "n2", Kind = NodeKind.Restart, Settings = JsonSerializer.SerializeToElement(new { }) },
        ],
        Owner = "anna",
    };

    public static ScheduleRun Run(Guid id, Guid schedule, int daysAgo, RunResult result, int done, int failed = 0, int warnings = 0, string? reason = null) => new()
    {
        Id = id,
        ScheduleId = schedule,
        Started = Now.AddDays(-daysAgo).Date.AddHours(23) is var day ? new DateTimeOffset(day, Now.Offset) : Now,
        Ended = result is RunResult.Running or RunResult.Missed or RunResult.Skipped ? null : new DateTimeOffset(Now.AddDays(-daysAgo).Date.AddHours(26).AddMinutes(12), Now.Offset),
        Result = result,
        Reason = reason,
        Devices = done + failed + warnings + 12,
        Done = done,
        Failed = failed,
        Warnings = warnings,
        NotReachable = result is RunResult.Missed or RunResult.Skipped ? new Dictionary<string, int>() : new Dictionary<string, int> { ["Offline"] = 12 },
    };

    public static IReadOnlyList<ScheduleSummary> Summaries =>
    [
        new()
        {
            Id = WeeklyId, Name = "Upgrade Site A", On = true, Stages = true, DevicesKind = ScheduleDevicesKind.Chosen, DeviceCount = 1, Tags = ["Site A"],
            When = Weekly.When, Actions = [NodeKind.UpgradeOnline, NodeKind.Restart], Owner = "anna", NextRun = new DateTimeOffset(2026, 11, 30, 23, 0, 0, Now.Offset),
            LastRun = Run(FailedRunId, WeeklyId, 0, RunResult.Failed, 212, failed: 3),
        },
        new()
        {
            Id = NightlyId, Name = "Restart lobby cameras", On = true, DevicesKind = ScheduleDevicesKind.All,
            When = new ScheduleWhen { Recurrence = Recurrence.Daily, Time = new TimeOnly(3, 30) }, Actions = [NodeKind.Restart], Owner = "ben",
            NextRun = new DateTimeOffset(2026, 11, 24, 3, 30, 0, Now.Offset), LastRun = Run(Guid.NewGuid(), NightlyId, 1, RunResult.Done, 230),
        },
        new()
        {
            Id = MonthlyId, Name = "Certificates", On = false, DevicesKind = ScheduleDevicesKind.Chosen, DeviceCount = 40, Tags = ["Parking", "Lobby"],
            When = new ScheduleWhen { Recurrence = Recurrence.Monthly, DayOfMonth = 1, Time = new TimeOnly(2, 0) }, Actions = [NodeKind.Certificate, NodeKind.Dot1x, NodeKind.UpgradeOnline],
            Owner = "anna", Warning = "Sign in to MyAxis.",
        },
    ];

    public static IReadOnlyList<ScheduleRun> Runs(Guid schedule) => schedule == WeeklyId
        ?
        [
            Run(FailedRunId, WeeklyId, 0, RunResult.Failed, 212, failed: 3),
            Run(Guid.Parse("6d6d6d6d-0003-4c2d-9e8f-0a1b2c3d4e5f"), WeeklyId, 7, RunResult.Stopped, 1, failed: 1, reason: "Stopped at stage 2: 1 failed"),
            Run(Guid.Parse("6d6d6d6d-0004-4c2d-9e8f-0a1b2c3d4e5f"), WeeklyId, 14, RunResult.DoneWithWarnings, 220, warnings: 4),
            Run(Guid.Parse("6d6d6d6d-0005-4c2d-9e8f-0a1b2c3d4e5f"), WeeklyId, 21, RunResult.Missed, 0),
        ]
        : [];

    public static IReadOnlyList<RunFailure> Failures(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new RunFailure(Guid.NewGuid(), $"10.0.{i / 250}.{i % 250 + 1}", "Upgrade AXIS OS from axis.com", "The download from axis.com failed."))];

    /// <summary>A check like the server's for the wizard: a name, devices, an action; the estimate and the warning when asked.</summary>
    public static ScheduleCheck Check(Schedule s, string? whenWarning = null) => new()
    {
        NameError = s.Name.Length == 0 ? "Enter a name." : s.Name == "Taken" ? "A schedule with this name exists." : null,
        DevicesError = s.Devices.Kind == ScheduleDevicesKind.Chosen && s.Devices.DeviceIds.Count == 0 && s.Devices.Tags.Count == 0 ? "Choose at least one device or tag." : null,
        ActionsError = s.Actions.Count == 0 ? "Add at least one action." : null,
        ActionErrors = s.Actions.Where(a => a.Kind == NodeKind.Configuration && NodeSettings.Read<ConfigurationSettings>(a.Settings)?.FileId is null)
            .ToDictionary(a => a.Id, _ => "Choose the configuration file."),
        WhenWarning = whenWarning,
        DeviceCount = 9,
        StageSizes = s.Stages ? [1, 3, 8, 15, 30, 93] : [],
        Estimate = s.Actions.Count == 0 ? null : TimeSpan.FromMinutes(200),
        NextRun = s.On ? new DateTimeOffset(2026, 11, 24, 23, 0, 0, Now.Offset) : null,
    };

    /// <summary>A context that answers like the server.</summary>
    public static FakePageContext Context(Func<Schedule, ScheduleCheck>? check = null)
    {
        var ctx = new FakePageContext();
        ctx.DeviceList.AddRange(Devices());
        var stored = new Dictionary<Guid, Schedule> { [WeeklyId] = Weekly };
        check ??= s => Check(s);
        ctx.Answers[SchedulesMethods.List] = _ => Serialize(new SchedulesReply(Summaries));
        ctx.Answers[SchedulesMethods.ActionKinds] = _ => Serialize(Kinds);
        ctx.Answers[SchedulesMethods.Policies] = _ => Serialize(Policies);
        ctx.Answers[SchedulesMethods.VapixCommands] = _ => Serialize(Commands);
        ctx.Answers[SchedulesMethods.Get] = payload =>
        {
            var schedule = stored[Parse<IdRequest>(payload).Id];
            return Serialize(new ScheduleReply(schedule, check(schedule)));
        };
        ctx.Answers[SchedulesMethods.Check] = payload => Serialize(check(Parse<ScheduleRequest>(payload).Schedule));
        ctx.Answers[SchedulesMethods.Save] = payload =>
        {
            var schedule = Parse<ScheduleRequest>(payload).Schedule with { Owner = "anna" };
            stored[schedule.Id] = schedule;
            return Serialize(new ScheduleReply(schedule, check(schedule)));
        };
        ctx.Answers[SchedulesMethods.Runs] = payload => Serialize(new RunsReply(Runs(Parse<IdRequest>(payload).Id)));
        ctx.Answers[SchedulesMethods.RunFailures] = _ => Serialize(new RunFailuresReply(Failures(3)));
        ctx.Answers[SchedulesMethods.RunNow] = payload => Serialize(Run(Guid.NewGuid(), Parse<IdRequest>(payload).Id, 0, RunResult.Running, 0));
        ctx.Answers[SchedulesMethods.TurnOn] = payload =>
        {
            var request = Parse<TurnOnRequest>(payload);
            return Serialize(Summaries.Single(s => s.Id == request.Id) with { On = request.On });
        };
        ctx.Answers[SchedulesMethods.Delete] = payload =>
        {
            var id = Parse<IdRequest>(payload).Id;
            return Serialize(new SchedulesReply([.. Summaries.Where(s => s.Id != id)]));
        };
        ctx.Answers[SchedulesMethods.StoreFile] = _ => Serialize(new StoredFile(Guid.NewGuid(), "site-a.cfg", 1234, "abc", DateTimeOffset.UnixEpoch));
        return ctx;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T Parse<T>(string? json) => JsonSerializer.Deserialize<T>(json!, Json)!;

    private static ActionKindInfo Kind(NodeKind kind, string name, string step, object settings, int minutes) =>
        new(kind, name, step, NodeSettings.Write(settings), TimeSpan.FromMinutes(minutes));
}
