using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Plugins.Acap;
using Oadm.Plugins.AppManagement.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.AppManagement.Tests;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        // OADM_TEST_APPEARANCE=Light renders the screenshots in the light appearance; the colors are loaded once per
        // process, so each appearance is its own test run.
        RequestedThemeVariant = Headless.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Oadm.Theme.OadmColors.Current = Headless.IsLight ? Oadm.Theme.Appearance.Light : Oadm.Theme.Appearance.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.AppManagement.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
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

    public static Window Host(Control view, int width = 1500, int height = 860)
    {
        var page = new Oadm.Client.Shell.CorePluginPageView
        {
            DataContext = new Oadm.Client.Shell.CorePluginPageViewModel(AppManagementInfo.PluginId, AppManagementInfo.DisplayName, view, new ApplicationsPage().HasOwnCards),
        };
        var window = new Window { Width = width, Height = height, Content = new Border { Padding = new Thickness(16), Child = page } };
        Oadm.Client.App.ApplyCrispText(window);
        return window;
    }

    public static bool IsLight => Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE") == "Light";

    public static string Appearance => IsLight ? "light" : "dark";

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

/// <summary>A device of the page's context (the client mirror).</summary>
internal sealed record PageDevice(string Address, string? Model = "P3265-V") : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Serial { get; init; } = "B8A44F000001";

    public string? HostName { get; init; }

    public string? FirmwareVersion { get; init; } = "12.11.77";

    public DeviceStatus Status { get; init; } = DeviceStatus.Ok;

    public DeviceCategory Category { get; init; } = DeviceCategory.Camera;

    public bool HasVideo => true;

    public IReadOnlyList<DeviceApi> Apis => [];

    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// The page context: canned answers per method (JSON in, JSON out), the role's permissions, the confirmation's answer, the
/// Applications dialog runs (<see cref="RunTaskAsync"/>) and a channel of pushed events.
/// </summary>
internal sealed class FakePageContext : ICorePluginClientContext
{
    private readonly Channel<PluginEvent> _events = Channel.CreateUnbounded<PluginEvent>();

    public List<IDeviceInfo> DeviceList { get; } = [];

    public Dictionary<string, Func<string?, string?>> Answers { get; } = new(StringComparer.Ordinal);

    public List<(string Method, string? Payload)> Calls { get; } = [];

    public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

    public List<(string Title, string Message, string Confirm)> Confirmations { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public Dictionary<string, string> LocalSettings { get; } = [];

    /// <summary>Every RunTaskAsync call: the task plugin and the devices.</summary>
    public List<(string PluginId, IReadOnlyList<Guid> DeviceIds)> TaskRuns { get; } = [];

    /// <summary>The task ids the dialog "started" (null: closed without a run).</summary>
    public IReadOnlyList<string>? StartedTasks { get; set; } = [Guid.NewGuid().ToString()];

    public IReadOnlyList<IDeviceInfo> Devices => DeviceList;

    public event EventHandler? PermissionsChanged;

    public void RaisePermissionsChanged() => PermissionsChanged?.Invoke(this, EventArgs.Empty);

    public bool Has(string permission) => !Denied.Contains(permission);

    public string? GetLocalSetting(string key) => LocalSettings.GetValueOrDefault(key);

    public void SetLocalSetting(string key, string? value)
    {
        if (value is null)
        {
            LocalSettings.Remove(key);
        }
        else
        {
            LocalSettings[key] = value;
        }
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add((title, message, confirmText));
        return Task.FromResult(ConfirmAnswer);
    }

    public Task<IReadOnlyList<string>?> RunTaskAsync(string pluginId, IReadOnlyList<Guid> deviceIds, CancellationToken ct) =>
        RunTaskAsync(pluginId, deviceIds, null, ct);

    /// <summary>The start view of each RunTaskAsync call (null: the dialog's usual start).</summary>
    public List<string?> StartViews { get; } = [];

    public Task<IReadOnlyList<string>?> RunTaskAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? startView, CancellationToken ct)
    {
        TaskRuns.Add((pluginId, deviceIds));
        StartViews.Add(startView);
        return Task.FromResult(StartedTasks);
    }

    /// <summary>Every UploadAsync call (the local path), in order; each gets the id "file-1", "file-2", ...</summary>
    public List<string> Uploads { get; } = [];

    /// <summary>Set: the upload fails with it.</summary>
    public Exception? UploadFailure { get; set; }

    public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (UploadFailure is { } failure)
        {
            return Task.FromException<UploadedFile>(failure);
        }

        Uploads.Add(localPath);
        progress?.Report(1.0);
        var id = "file-" + Uploads.Count.ToString(CultureInfo.InvariantCulture);
        return Task.FromResult(new UploadedFile(id, Path.GetFileName(localPath), 1, "SHA" + Uploads.Count.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The plugin packages that are on (<see cref="IsPluginOn"/>); others are off.</summary>
    public HashSet<string> PluginsOn { get; } = new(StringComparer.Ordinal);

    public bool? IsPluginOn(string packageId) => PluginsOn.Contains(packageId);

    /// <summary>Every OpenDevicePage call; <see cref="OpensDevicePages"/> is its answer.</summary>
    public List<(Guid DeviceId, string Path)> OpenedPages { get; } = [];

    public bool OpensDevicePages { get; set; } = true;

    public bool OpenDevicePage(Guid deviceId, string path)
    {
        OpenedPages.Add((deviceId, path));
        return OpensDevicePages;
    }

    public int CallsOf(string method)
    {
        lock (Calls)
        {
            return Calls.Count(c => c.Method == method);
        }
    }

    public string? LastPayloadOf(string method)
    {
        lock (Calls)
        {
            return Calls.LastOrDefault(c => c.Method == method).Payload;
        }
    }

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (Calls)
        {
            Calls.Add((method, payloadJson));
        }

        return Answers.TryGetValue(method, out var answer)
            ? Task.FromResult(answer(payloadJson))
            : Task.FromException<string?>(new InvalidOperationException("No answer for " + method));
    }

    /// <summary>Answers getRows with no rows and read with a finished read (the usual start of a page test).</summary>
    public FakePageContext WithEmptyServer()
    {
        Answers[AppManagementMethods.GetRows] = _ => AppManagementJson.Serialize(new RowsPage());
        Answers[AppManagementMethods.Read] = _ => AppManagementJson.Serialize(new ReadState { Reading = false });
        Answers[AppManagementMethods.GetState] = _ => AppManagementJson.Serialize(new ReadState());
        Answers[AppManagementMethods.Run] = _ => AppManagementJson.Serialize(new RunReply());
        Answers[AppManagementMethods.Follow] = _ => "{}";
        return this;
    }

    public void Push(string topic, object payload) =>
        _events.Writer.TryWrite(new PluginEvent(topic, AppManagementJson.Serialize(payload)));

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

/// <summary>Sample data: applications, devices and the list.cgi reply of a camera.</summary>
internal static class Samples
{
    public static readonly DateOnly Today = new(2026, 10, 28);

    public static InstalledApplication App(
        string name,
        string? nice = null,
        string version = "1.0",
        string status = "Running",
        string license = "None",
        string? expires = null,
        bool bundled = false,
        string vendor = "Axis Communications") => new()
    {
        Name = name,
        NiceName = nice,
        Vendor = vendor,
        Version = version,
        Status = status,
        License = license,
        LicenseExpirationDate = expires,
        Bundled = bundled,
    };

    public static DeviceApps Device(string address, params InstalledApplication[] apps) => new()
    {
        DeviceId = Guid.NewGuid(),
        Address = address,
        Model = "P3265-V",
        Mac = "B8A44F0000" + int.Parse(address.Split('.')[^1], CultureInfo.InvariantCulture).ToString("X2", CultureInfo.InvariantCulture),
        Firmware = "12.11.77",
        Soc = "Axis Artpec-8",
        Architecture = "aarch64",
        Applications = apps,
    };

    public static DeviceApps Failed(string address, string error, bool notChecked = false) => new()
    {
        DeviceId = Guid.NewGuid(),
        Address = address,
        Model = "M3106-L",
        Mac = "ACCC8E0000" + int.Parse(address.Split('.')[^1], CultureInfo.InvariantCulture).ToString("X2", CultureInfo.InvariantCulture),
        Firmware = "10.12.200",
        Error = error,
        NotChecked = notChecked,
    };

    /// <summary>A list.cgi reply ("Bundled" Yes / No as the device sends it).</summary>
    public static string ListXml(params InstalledApplication[] apps)
    {
        var xml = new StringBuilder("<reply result=\"ok\">");
        foreach (var app in apps)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<application Name=\"{app.Name}\" NiceName=\"{app.NiceName}\" Vendor=\"{app.Vendor}\" Version=\"{app.Version}\" ")
                .Append(CultureInfo.InvariantCulture, $"Status=\"{app.Status}\" License=\"{app.License}\" LicenseExpirationDate=\"{app.LicenseExpirationDate}\" ")
                .Append(CultureInfo.InvariantCulture, $"Bundled=\"{(app.Bundled ? "Yes" : "No")}\" />");
        }

        return xml.Append("</reply>").ToString();
    }

    /// <summary>A camera that answers list.cgi with <paramref name="apps"/> and refuses everything else.</summary>
    public static FakeCamera Camera(params InstalledApplication[] apps) => new()
    {
        Respond = (request, _) => request.RequestUri?.OriginalString.StartsWith(ApplicationApiClient.ListPath, StringComparison.Ordinal) == true
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ListXml(apps)) }
            : new HttpResponseMessage(HttpStatusCode.BadRequest),
    };

    /// <summary>A device with the Application API in its cached list.</summary>
    public static FakeDevice ManagedDevice(string address, DeviceStatus status = DeviceStatus.Ok, bool applicationApi = true) => new()
    {
        Address = address,
        Status = status,
        Apis = applicationApi ? [new DeviceApi(AcapPlugin.ApplicationApiId, "1.3")] : [new DeviceApi("param-cgi", "1.0")],
    };
}
