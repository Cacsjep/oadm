using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.LicensePlates.Cameras;
using Oadm.Plugins.LicensePlates.Events;
using Oadm.Plugins.LicensePlates.Log;
using Oadm.Plugins.LicensePlates.Matching;
using Oadm.Plugins.LicensePlates.Plates;
using Oadm.Plugins.LicensePlates.Reads;
using Oadm.Plugins.LicensePlates.Tasks;
using Oadm.Plugins.LicensePlates.Tools;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LicensePlates;

/// <summary>
/// Core plugin "License Plate Center" (docs/spec/license-plate-center.md, the plugin README): one plate list with plate tags
/// for every camera that runs AXIS License Plate Verifier. The server listens to every camera of the plugin over its own
/// event connection (<see cref="CameraRuntime"/>), turns ALPV's events into one read per car (<see cref="ReadAggregator"/>),
/// decides it from the one list (<see cref="PlateMatcher"/>; the cameras' own lists stay untouched), fires the events of the
/// rules (<see cref="PlateEvents"/>) and keeps the log (<see cref="PlateLog"/>). Its task part sets up ALPV
/// (<see cref="SetupTask"/>, <see cref="ImageSettingsTask"/>). Off by default; free. Page backend: <see cref="LicensePlatesMethods"/>.
/// </summary>
public sealed partial class LicensePlatesPlugin : ICorePlugin, IDynamicEventSource, ICorePluginDownloads, IAsyncDisposable
{
    public const string SettingsKey = "settings";

    private readonly CameraOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, byte> _scopes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _names = new();
    private readonly ConcurrentDictionary<Guid, byte> _readCameras = new();
    private readonly ConcurrentDictionary<Guid, byte> _loggedCameras = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private ICorePluginContext? _ctx;
    private PlateStore? _store;
    private PlateMatcher? _matcher;
    private CameraRuntime? _runtime;
    private ReadAggregator? _aggregator;
    private PlateLog? _log;
    private ProblemReporter? _problems;
    private ITimer? _tick;
    private ITimer? _endedSweep;
    private ITimer? _maintain;
    private CancellationTokenSource? _running;
    private volatile LicensePlateSettings _settings = new();
    private long _localIds;
    private int _sweeping;

    public LicensePlatesPlugin()
        : this(new CameraOptions(), TimeProvider.System)
    {
    }

    /// <param name="options">Timings of the camera connections (tests shorten them).</param>
    /// <param name="time">The clock (tests pass a fake one).</param>
    public LicensePlatesPlugin(CameraOptions options, TimeProvider time)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        TaskPlugins = [new SetupTask(AfterTask), new ImageSettingsTask(AfterTask)];
    }

    public event EventHandler? EventTypesChanged;

    public string Id => LicensePlatesInfo.PluginId;

    public string DisplayName => LicensePlatesInfo.DisplayName;

    public string? IconKey => LicensePlatesInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Integrations;

    /// <summary>The tasks "Set up License Plate Verifier" and "Use recommended image settings" (page tasks).</summary>
    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    /// <summary>The tools are named <c>oadm_license_plates_*</c>.</summary>
    public string? ToolArea => "license_plates";

    /// <summary>The tools (docs/spec/mcp.md "Plugins"): plates and tags (list, change with preview), cameras with status, the log (pages, never pictures), stats.</summary>
    public IReadOnlyList<PluginTool> Tools => LicensePlateTools.Set.Tools;

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, "View License Plate Center", "See the page, the plates and the cameras with their status.") { ViewerDefault = true },
        new(LicensePlatePermissions.Log, "View plate log", "See the reads of the cameras and their statistics.") { ViewerDefault = false },
        new(LicensePlatePermissions.Export, "Export plate log", "Save the reads as a CSV or PDF file.") { OperatorDefault = false },
        new(LicensePlatePermissions.Plates, "Manage plates", "Add, change, import and delete plates and plate tags."),
        new(LicensePlatePermissions.Cameras, "Set up cameras", "Add and remove cameras, choose their allowed tags and set up License Plate Verifier on them."),
    ];

    /// <summary>The events of the cameras that take part, Camera problem and Any camera problem; none while the plugin is stopped.</summary>
    public IReadOnlyList<EventTypeInfo> EventTypes => _store is { } store ? PlateEvents.Types([.. store.Cameras.Select(c => c.DeviceId)]) : [];

    internal PlateStore? Store => _store;

    internal PlateMatcher? Matcher => _matcher;

    internal CameraRuntime? Runtime => _runtime;

    internal PlateLog? Log => _log;

    internal ReadAggregator? Aggregator => _aggregator;

    internal LicensePlateSettings CurrentSettings => _settings;

    private ILogger Logger => _ctx?.Logger ?? NullLogger.Instance;

    /// <summary>The camera's name in the plugin: its own name, else the device's name in OADM (host name, else address).</summary>
    internal string CameraName(Guid camera) =>
        _store?.Camera(camera)?.Name is { Length: > 0 } own ? own : _names.GetValueOrDefault(camera) ?? camera.ToString("D");

    internal (ICorePluginContext Context, PlateStore Store, PlateMatcher Matcher, CameraRuntime Runtime, PlateLog Log) Running =>
        _ctx is { } ctx && _store is { } store && _matcher is { } matcher && _runtime is { } runtime && _log is { } log
            ? (ctx, store, matcher, runtime, log)
            : throw new InvalidOperationException(LicensePlateTexts.NotRunning);

    /// <summary>Opens SQLite in memory and builds a PDF page: what the log and the export need at run time.</summary>
    public string? SelfCheck(string pluginDirectory)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json_array(1);";
        cmd.ExecuteScalar();
        var clock = Oadm.Server.Logging.Export.LogExportClock.Of(null, 0);
        var pdf = PlateLogExport.Pdf([], new PlateExportHead([], "self-check", "OADM", _time.GetUtcNow(), 0), clock, _ => null, CancellationToken.None);
        return pdf.Length > 0 ? null : "The PDF report could not be made.";
    }

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var records = ctx.Records ?? throw new InvalidOperationException("This OADM server cannot keep plates. Update OADM.");
        var store = new PlateStore(records);
        await store.LoadAsync(ct).ConfigureAwait(false);
        var settings = PluginJson.TryParse<LicensePlateSettings>(await ctx.Settings.GetAsync(SettingsKey, ct).ConfigureAwait(false), LicensePlatesJson.Options) ?? new LicensePlateSettings();
        _settings = settings;
        var matcher = new PlateMatcher(store) { Deviation = settings.Deviation };
        store.PlatesChanged += matcher.Changed;
        var log = new PlateLog(_time, ctx.Logger ?? NullLogger.Instance);
        log.Changed += OnLogChanged;
        await log.ApplyAsync(settings, ct).ConfigureAwait(false);
        await LoadLoggedCamerasAsync(log).ConfigureAwait(false);

        _ctx = ctx;
        _store = store;
        _matcher = matcher;
        _log = log;
        _running = new CancellationTokenSource();
        _problems = ctx.EventPublisher is { } publisher ? new ProblemReporter(publisher) : null;
        _aggregator = new ReadAggregator(_time, Match, () => TimeSpan.FromSeconds(Math.Clamp(_settings.WaitSeconds, 1, 10)), OnDecided, OnChanged, NextReadId);
        var runtime = new CameraRuntime(ctx, _options, _time, OnEvent);
        runtime.Changed += OnCamerasChanged;
        runtime.Reconnected += OnReconnected;
        _runtime = runtime;
        EventTypesChanged?.Invoke(this, EventArgs.Empty);
        await runtime.SyncAsync([.. store.Cameras.Select(c => c.DeviceId)]).ConfigureAwait(false);
        ReportProblems();
        foreach (var camera in store.Cameras)
        {
            // The names of the events and the log from the start, not only after the first call of the page.
            if (await ctx.Devices.FindAsync(camera.DeviceId, ct).ConfigureAwait(false) is { } device)
            {
                _names[camera.DeviceId] = NameOf(device);
            }
        }


        _tick = _time.CreateTimer(_ => OnTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _endedSweep = _time.CreateTimer(_ => _ = SweepEndedAsync(), null, TimeSpan.Zero, LicensePlateLimits.EndedSweep);
        _maintain = _time.CreateTimer(_ => _ = MaintainAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        LogStarted(Logger, store.PlateCount, store.Cameras.Count);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var timer in new[] { _tick, _endedSweep, _maintain })
        {
            if (timer is not null)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }
        }

        _tick = _endedSweep = _maintain = null;
        if (_running is { } running)
        {
            _running = null;
            await running.CancelAsync().ConfigureAwait(false);
            running.Dispose();
        }

        if (_runtime is { } runtime)
        {
            _runtime = null;
            await runtime.DisposeAsync().ConfigureAwait(false);
        }

        _aggregator?.Dispose();
        _aggregator = null;
        if (_log is { } log)
        {
            _log = null;
            await log.DisposeAsync().ConfigureAwait(false);
        }

        _store?.Dispose();
        _store = null;
        _matcher = null;
        _problems = null;
        _ctx = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _settingsGate.Dispose();
    }

    // ---- reads ----

    private long NextReadId() => _log?.Store?.NextId() ?? Interlocked.Increment(ref _localIds);

    private MatchResult Match(Guid camera, string text)
    {
        if (_store?.Camera(camera) is not { } entry || _matcher is not { } matcher)
        {
            return MatchResult.Unknown;
        }

        return matcher.Match(text, entry.AllowedTags.ToHashSet(), LocalNow());
    }

    /// <summary>The server's wall clock (Allowed from and Allowed to are times of the server's zone).</summary>
    internal DateTime LocalNow() => TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).DateTime;

    private void OnEvent(Guid camera, AlpvEvent e) => _aggregator?.Handle(camera, e);

    private void OnDecided(DecidedRead read)
    {
        if (_store is not { } store || store.Camera(read.Camera) is null)
        {
            return; // the camera left meanwhile
        }

        var plate = read.Match.Plate;
        var row = new PlateRead
        {
            Id = read.ReadId,
            Time = read.Time,
            CameraId = read.Camera,
            Camera = CameraName(read.Camera),
            Plate = plate?.Text ?? read.Event.Text,
            ReadAs = plate is not null && !string.Equals(plate.Text, read.Event.Text, StringComparison.Ordinal) ? read.Event.Text : null,
            Result = read.Match.Result,
            Reason = read.Match.Reason,
            ReasonTime = read.Match.ReasonTime,
            Name = plate?.Name ?? string.Empty,
            Tags = plate is null ? [] : store.NamesOf(plate.Tags),
            Direction = read.Direction,
            Area = read.Event.Area,
            Country = read.Event.Country,
            Confidence = read.Event.Percent,
            View = read.Event.View,
            VehicleType = read.Event.VehicleType,
            VehicleColor = read.Event.VehicleColor,
            VehicleMake = read.Event.VehicleMake,
            VehicleModel = read.Event.VehicleModel,
            VehicleConfidence = read.Event.VehicleConfidence is { } vehicle ? (int)Math.Round(vehicle * 100, MidpointRounding.AwayFromZero) : null,
            State = read.Event.State,
            HasPicture = read.Image is not null && _settings.KeepPictures,
        };

        if (_ctx?.EventPublisher is { } publisher)
        {
            PlateEvents.Fire(publisher, row);
        }

        _runtime?.MarkRead(read.Camera, read.Time);
        if (row.Result != ReadResult.Unknown || _settings.KeepUnknown)
        {
            _log?.Store?.Insert(new LogInsert(row, Picture(read.Image)));
            _loggedCameras.TryAdd(read.Camera, 0);
        }

        _readCameras.TryAdd(read.Camera, 0);
    }

    private void OnChanged(ReadChange change)
    {
        _log?.Store?.Update(new LogUpdate(change.ReadId, change.Direction, Picture(change.Image)));
        _readCameras.TryAdd(change.Camera, 0);
    }

    /// <summary>ALPV's base64 plate crop as bytes; null when it is not base64 or the log keeps no pictures.</summary>
    private byte[]? Picture(string? base64)
    {
        if (base64 is null || !_settings.KeepPictures)
        {
            return null;
        }

        var text = base64.Contains(',', StringComparison.Ordinal) ? base64[(base64.IndexOf(',', StringComparison.Ordinal) + 1)..] : base64;
        var buffer = new byte[text.Length * 3 / 4 + 3];
        return Convert.TryFromBase64String(text.Trim(), buffer, out var written) ? buffer[..written] : null;
    }

    private void OnReconnected(Guid camera, DateTimeOffset from, DateTimeOffset until)
    {
        if (_log?.Store is not { } store)
        {
            return;
        }

        store.Insert(new LogInsert(new PlateRead
        {
            Id = store.NextId(),
            Time = from,
            Until = until,
            Gap = true,
            CameraId = camera,
            Camera = CameraName(camera),
        }, null));
        _loggedCameras.TryAdd(camera, 0);
        _readCameras.TryAdd(camera, 0);
    }

    // ---- timers ----

    private void OnTick()
    {
        try
        {
            _runtime?.Tick();
            ReportProblems();
            PublishReads();
        }
#pragma warning disable CA1031 // The next second tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTickFailed(Logger, ex);
        }
    }

    /// <summary>The cameras with new reads since the last second, to the users who see them.</summary>
    private void PublishReads()
    {
        if (_ctx?.Events is not { } events || _readCameras.IsEmpty)
        {
            return;
        }

        var ids = _readCameras.Keys.ToList();
        foreach (var id in ids)
        {
            _readCameras.TryRemove(id, out _);
        }

        events.Publish(LicensePlatesMethods.ReadsTopic, PluginJson.Serialize(ids, LicensePlatesJson.Options), PluginEventAudience.DeviceRows(ids));
    }

    private void ReportProblems()
    {
        if (_problems is not { } problems || _store is not { } store || _runtime is not { } runtime)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var map = new Dictionary<Guid, (string Name, string Problem)>();
        foreach (var camera in store.Cameras)
        {
            if (runtime.StateOf(camera.DeviceId).ProblemText(now) is { } text)
            {
                map[camera.DeviceId] = (CameraName(camera.DeviceId), text);
            }
        }

        problems.Report(map, _log is { HasFolder: true } log ? log.Problem : null);
    }

    /// <summary>The audit entry of one sweep: the count, never a plate's text (plates are personal data).</summary>
    internal static string EndedAuditDetail(int count) =>
        count == 1 ? "Deleted 1 plate whose time ended" : "Deleted " + count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " plates whose time ended";

    /// <summary>Plates with Delete when it ends are deleted within a minute after their Allowed to; one audit entry per sweep.</summary>
    internal async Task SweepEndedAsync()
    {
        if (_store is not { } store || Interlocked.Exchange(ref _sweeping, 1) == 1)
        {
            return;
        }

        try
        {
            var ended = store.EndedBy(LocalNow());
            if (ended.Count > 0)
            {
                await store.WritePlatesAsync([], [.. ended.Select(p => p.Text)], _running?.Token ?? CancellationToken.None).ConfigureAwait(false);
                foreach (var plate in ended)
                {
                    LogPlateEnded(Logger, plate.Text);
                }

                PublishPlates();
                if (_ctx?.Audit is { } audit)
                {
                    await audit.WriteAsync(EndedAuditDetail(ended.Count), _running?.Token ?? CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
#pragma warning disable CA1031 // Tried again in 30 s.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogTickFailed(Logger, ex);
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    internal async Task MaintainAsync()
    {
        if (_log is not { } log)
        {
            return;
        }

        try
        {
            await log.MaintainAsync(_running?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Tried again in a minute.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogTickFailed(Logger, ex);
        }
    }

    private void OnLogChanged()
    {
        ReportProblems();
        PublishStatus();
    }

    private async Task LoadLoggedCamerasAsync(PlateLog log)
    {
        _loggedCameras.Clear();
        if (log.Store is { } store)
        {
            try
            {
                foreach (var id in await store.CamerasAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    _loggedCameras.TryAdd(id, 0);
                }
            }
#pragma warning disable CA1031 // The log's cameras are its readers' scope only; a problem shows on the page.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogTickFailed(Logger, ex);
            }
        }
    }

    // ---- page events ----

    private void OnCamerasChanged(IEnumerable<Guid> ids)
    {
        List<Guid> list = [.. ids];
        if (_ctx?.Events is { } events && list.Count > 0)
        {
            events.Publish(LicensePlatesMethods.CamerasTopic, PluginJson.Serialize(list, LicensePlatesJson.Options), PluginEventAudience.DeviceRows(list));
        }
    }

    /// <summary>Server-wide news (plates, tags, the log's status) to users with all devices and to every group that opened the page.</summary>
    private void PublishWide(string topic)
    {
        if (_ctx?.Events is not { } events)
        {
            return;
        }

        events.Publish(topic, "{}", PluginEventAudience.AllDevices);
        foreach (var scope in _scopes.Keys)
        {
            if (scope != ICallContext.AllDevicesScopeKey)
            {
                events.Publish(topic, "{}", PluginEventAudience.DeviceScope(scope));
            }
        }
    }

    private void PublishPlates() => PublishWide(LicensePlatesMethods.PlatesTopic);

    private void PublishStatus() => PublishWide(LicensePlatesMethods.StatusTopic);

    /// <summary>A task of the plugin ended on a camera: ALPV of it is read again.</summary>
    private void AfterTask(Guid camera) => _runtime?.Recheck([camera]);

    [LoggerMessage(Level = LogLevel.Information, Message = "License Plate Center: {Plates} plates, {Cameras} cameras")]
    private static partial void LogStarted(ILogger logger, int plates, int cameras);

    [LoggerMessage(Level = LogLevel.Information, Message = "License Plate Center: deleted plate {Plate}, its Allowed to passed (Delete when it ends)")]
    private static partial void LogPlateEnded(ILogger logger, string plate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "License Plate Center: background work failed")]
    private static partial void LogTickFailed(ILogger logger, Exception ex);
}
