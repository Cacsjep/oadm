using System.Net.Http;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Acap;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.AppManagement.Reading;

/// <summary>Timing and limits of the reads (tests shorten them or pass a fake clock).</summary>
public sealed class AppManagementOptions
{
    public int Parallelism { get; init; } = AppManagementInfo.Parallelism;

    public TimeSpan DeviceTimeout { get; init; } = AppManagementInfo.DeviceTimeout;

    public TimeSpan FollowInterval { get; init; } = AppManagementInfo.FollowInterval;

    public TimeSpan EventInterval { get; init; } = AppManagementInfo.EventInterval;

    public int MaxSessions { get; init; } = AppManagementInfo.MaxSessions;

    /// <summary>Rows per published event before it is split further by size.</summary>
    public int EventBatchDevices { get; init; } = 200;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The page's server part (docs/spec/app-management.md): per login session the installed applications of the chosen
/// devices, read only on demand (a choice, Refresh, after a task of the page ended on a device), kept in memory only. A
/// read is a job of the caller (<see cref="IPluginJobs"/>) that reads through the caller's view, at most
/// <see cref="AppManagementOptions.Parallelism"/> devices at once over every session, each within
/// <see cref="AppManagementOptions.DeviceTimeout"/>. The rows go to the session's pages in batches. The page never writes to a
/// device: Start, Stop and Remove are tasks of the Applications plugin (<c>oadm.acap</c>) through the caller's task runner,
/// with the dialog's payload (<see cref="AcapPayload"/>, linked source).
/// </summary>
internal sealed partial class AppReadService : IAsyncDisposable
{
    private const int MaxItemsPerRun = 1_000;
    private static readonly Version MinApplicationApi = Version.Parse(AcapPlugin.ApplicationApiMinVersion);

    private readonly ICorePluginContext _ctx;
    private readonly AppManagementOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private long _clock;

    public AppReadService(ICorePluginContext ctx, AppManagementOptions options)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _slots = new SemaphoreSlim(Math.Max(1, options.Parallelism));
    }

    /// <summary>The sessions with rows (tests).</summary>
    internal int SessionCount
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Reads the chosen devices of the caller; the session's rows become these devices.</summary>
    public async Task<ReadState> ReadAsync(ICallContext caller, IPluginJobs jobs, ReadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(request);
        var devices = await SelectAsync(caller, request, ct).ConfigureAwait(false);
        var session = SessionOf(caller);
        var job = new ReadJob(devices.Count);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (session.Gate)
        {
            session.Job?.Owned?.Cancel();
            var chosen = devices.Select(d => d.Id).ToHashSet();
            foreach (var gone in session.Rows.Keys.Where(id => !chosen.Contains(id)).ToList())
            {
                session.Rows.Remove(gone);
                session.Results.Remove(gone);
                session.PendingRows.Remove(gone);
                session.PendingRemoved.Add(gone);
            }

            session.Chosen = chosen;
            session.Job = job;

            // A read the user starts replaces the messages of ended tasks.
            foreach (var id in chosen)
            {
                session.Results.Remove(id);
            }
            session.StateDirty = true;
        }

        job.Owned = jobs.Start(caller, "read", async (owned, token) =>
        {
            await ready.Task.ConfigureAwait(false);
            await RunReadAsync(session, job, devices, owned.Owner, token).ConfigureAwait(false);
        });
        ready.SetResult();
        LogReadStarted(_ctx.Logger, devices.Count);
        return State(session, job);
    }

    /// <summary>The progress of the caller's session (no device access).</summary>
    public ReadState GetState(ICallContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var session = Existing(caller);
        return session is null ? new ReadState() : State(session, session.Job);
    }

    /// <summary>
    /// One page of the session's rows, in a stable order; rows of devices that are no longer the caller's (removed, or out of
    /// the caller's device group) are dropped first. The first page (<see cref="RowsRequest.Skip"/> 0) lists the caller's
    /// devices once and keeps the sorted ids of the visible rows; the later pages take their slice of these ids, with each
    /// row as it is now, and ask the caller's view for that slice's devices only (a device the caller no longer sees is
    /// dropped, never sent). The whole paged load is O(n log n), not one list and sort per page.
    /// </summary>
    public async Task<RowsPage> GetRowsAsync(ICallContext caller, RowsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var session = Existing(caller);
        if (session is null)
        {
            return new RowsPage();
        }

        var skip = Math.Max(0, request.Skip);
        IReadOnlyList<Guid>? order;
        lock (session.Gate)
        {
            order = skip == 0 ? null : session.PageOrder;
        }

        if (order is null)
        {
            order = await PageOrderAsync(caller, session, ct).ConfigureAwait(false);
        }

        var slice = order.Skip(skip).Take(AppManagementInfo.RowsPerPage).ToList();
        var next = skip + slice.Count;
        var seen = new HashSet<Guid>(slice.Count);
        if (skip > 0)
        {
            // The first page checked every row; a later page checks its own devices again (the view may have changed).
            foreach (var id in slice)
            {
                if (await caller.Devices.FindAsync(id, ct).ConfigureAwait(false) is not null)
                {
                    seen.Add(id);
                }
            }
        }

        var page = new List<DeviceApps>(slice.Count);
        lock (session.Gate)
        {
            foreach (var id in slice)
            {
                if (skip > 0 && !seen.Contains(id))
                {
                    if (session.Rows.Remove(id))
                    {
                        session.PendingRows.Remove(id);
                        session.PendingRemoved.Add(id);
                        session.StateDirty = true;
                    }
                }
                else if (session.Rows.TryGetValue(id, out var row))
                {
                    page.Add(row);
                }
            }
        }

        return new RowsPage { Devices = page, Next = next < order.Count ? next : null, State = State(session, session.Job) };
    }

    /// <summary>Drops the rows of devices the caller no longer sees, then keeps the sorted ids of the others for the pages.</summary>
    private static async Task<IReadOnlyList<Guid>> PageOrderAsync(ICallContext caller, Session session, CancellationToken ct)
    {
        var visible = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
        lock (session.Gate)
        {
            foreach (var gone in session.Rows.Keys.Where(id => !visible.Contains(id)).ToList())
            {
                session.Rows.Remove(gone);
                session.PendingRows.Remove(gone);
                session.PendingRemoved.Add(gone);
                session.StateDirty = true;
            }

            var ids = session.Rows.Keys.ToArray();
            Array.Sort(ids);
            session.PageOrder = ids;
            return ids;
        }
    }

    /// <summary>
    /// Starts the Applications plugin's Install, Install license, Start, Stop, Remove or Change settings task for each
    /// application on its devices (Install: one application, the uploaded packages of the page's dialog), as the caller (its
    /// permissions and device group), after the same permission check as the task service; nothing starts when the role
    /// lacks it, a payload is not valid or any device is not the caller's. The page follows the tasks and reads each device
    /// again when its tasks ended.
    /// </summary>
    public async Task<RunReply> RunAsync(ICallContext caller, IPluginJobs jobs, RunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(request);
        var (key, name) = request.Action switch
        {
            AcapAction.Start or AcapAction.Stop => (AcapPermissions.StartStop, AppManagementInfo.StartStopName),
            AcapAction.Remove => (AcapPermissions.Remove, AppManagementInfo.RemoveName),
            AcapAction.Settings => (AcapPermissions.Settings, AppManagementInfo.SettingsName),
            AcapAction.Install => (AcapPermissions.Install, AppManagementInfo.InstallName),
            AcapAction.InstallLicense => (AcapPermissions.License, AppManagementInfo.LicenseName),
            _ => throw new ArgumentException("The task payload has no valid action. Nothing was changed.", nameof(request)),
        };

        // The task service checks the run's permissions before any task starts; a plugin's runner does not, so the page does.
        if (!caller.Has(PermissionKeys.Task(AcapPlugin.Id, key)))
        {
            throw new DeviceAccessRefusedException(AppManagementInfo.NotAllowed(name));
        }

        var install = request.Action == AcapAction.Install
            ? request.Install ?? throw new ArgumentException("Install needs an uploaded package. Nothing was changed.", nameof(request))
            : null;
        if (install is { AllowDowngrade: true } && !caller.Has(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Downgrade)))
        {
            throw new DeviceAccessRefusedException(AppManagementInfo.NotAllowed(AppManagementInfo.DowngradeName));
        }

        if (request.Items.Count is 0 or > MaxItemsPerRun || (install is not null && request.Items.Count != 1))
        {
            throw new ArgumentException(install is null ? "Choose 1 to 1,000 applications. Nothing was changed." : "Install one application at a time. Nothing was changed.", nameof(request));
        }

        // The values belong to one application's settings: never applied to another one.
        if (request.Action == AcapAction.Settings && request.Items.Count != 1)
        {
            throw new ArgumentException("Change the settings of one application at a time. Nothing was changed.", nameof(request));
        }

        var runs = new List<(string Payload, IReadOnlyList<Guid> DeviceIds)>(request.Items.Count);
        var all = new HashSet<Guid>();
        foreach (var item in request.Items)
        {
            IReadOnlyList<Guid> ids = [.. (item.DeviceIds ?? []).Distinct()];
            if (ids.Count is 0 or > DeviceLimits.MaxDevices)
            {
                throw new ArgumentException(DeviceLimits.ChooseRangeText("devices") + " Nothing was changed.", nameof(request));
            }

            // The dialog's payload, checked by the Applications plugin's own parser (application name, action).
            var payload = new AcapPayload
            {
                Action = request.Action,
                Application = item.Application?.Trim(),
                AppDisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? null : item.DisplayName.Trim(),
                Values = request.Action == AcapAction.Settings ? request.Values : null,
                FileId = install is { Files.Count: > 0 } ? install.Files[0].FileId : null,
                Sha256 = install is { Files.Count: > 0 } ? install.Files[0].Sha256 : null,
                Files = install is { Files.Count: > 1 } ? install.Files : null,
                Version = install?.Version,
                AllowDowngrade = install?.AllowDowngrade == true,
                StartAfterInstall = install?.StartAfterInstall == true,
                Kind = install?.Kind,
                LicenseKeys = request.Action == AcapAction.InstallLicense ? item.LicenseKeys ?? [] : [],
                LicenseApplicationId = request.Action == AcapAction.InstallLicense ? item.LicenseApplicationId : null,
            }.ToJson();
            _ = AcapPayload.Parse(payload);
            runs.Add((payload, ids));
            all.UnionWith(ids);
        }

        if (all.Count > DeviceLimits.MaxDevices)
        {
            throw new ArgumentException(DeviceLimits.ChooseRangeText("devices") + " Nothing was changed.", nameof(request));
        }

        // Every device must be the caller's before the first task starts (never a run in part).
        _ = await CallDevices.RequireAsync(caller, all, ct).ConfigureAwait(false);

        var owner = string.IsNullOrWhiteSpace(request.Owner) ? AppManagementInfo.DisplayName : request.Owner.Trim();
        var started = new List<(Guid Task, Guid Device, string? Application)>();
        for (var r = 0; r < runs.Count; r++)
        {
            var (payload, ids) = runs[r];
            var taskIds = await caller.Tasks.RunAsync(AcapPlugin.Id, ids, payload, owner, ct).ConfigureAwait(false);
            for (var i = 0; i < taskIds.Count && i < ids.Count; i++)
            {
                // Install concerns the whole device (a new application has no row yet): its end shows in every row.
                started.Add((taskIds[i], ids[i], install is null ? request.Items[r].Application.Trim() : null));
            }
        }

        LogRun(_ctx.Logger, request.Action, runs.Count, all.Count); // names and counts only: setting values are secret
        var groups = started.GroupBy(s => s.Device)
            .Select(g => new FollowGroup([.. g.Select(s => s.Task)], [g.Key], g.ToDictionary(s => s.Task, s => s.Application)))
            .ToList();
        Follow(caller, jobs, groups, AppActivities.Of(request.Action));
        return new RunReply { TaskIds = [.. started.Select(s => s.Task)] };
    }

    /// <summary>The tasks the Applications dialog started for the page (Install, Install licenses): the devices are read again once all ended.</summary>
    public async Task FollowAsync(ICallContext caller, IPluginJobs jobs, FollowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(request);
        var taskIds = (request.TaskIds ?? []).Distinct().ToHashSet();
        var deviceIds = (request.DeviceIds ?? []).Distinct().ToList();
        if (taskIds.Count is 0 or > DeviceLimits.MaxDevices || deviceIds.Count is 0 or > DeviceLimits.MaxDevices)
        {
            throw new ArgumentException(DeviceLimits.ChooseRangeText("devices"), nameof(request));
        }

        var known = await CallDevices.KnownAsync(caller, deviceIds, ct).ConfigureAwait(false);
        var application = string.IsNullOrWhiteSpace(request.Application) ? null : ValidApplication(request.Application);
        Follow(caller, jobs, [new FollowGroup(taskIds, [.. deviceIds.Where(known.Contains)], taskIds.ToDictionary(t => t, _ => application))], AppActivities.Installing);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        List<Task> running = [];
        lock (_sync)
        {
            foreach (var session in _sessions.Values)
            {
                running.AddRange(session.CancelJobs());
            }

            _sessions.Clear();
        }

        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Stopping never fails: the jobs log their own errors.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _stopping.Dispose();
        _slots.Dispose();
    }

    /// <summary>
    /// One device's row: not contacted when it is not Ok or has no Application API 1.x, else list.cgi parsed by the
    /// Applications plugin, then AllowUnsigned (config.cgi) and the embedded development version (param.cgi) at the same time,
    /// best effort: three read requests in one slot and one timeout. Null when <paramref name="ct"/> was cancelled (a newer read or the plugin stopping); Gone
    /// when the device is no longer the caller's.
    /// </summary>
    internal async Task<(DeviceApps? Row, bool Gone)> ReadDeviceAsync(IVapixClientFactory vapix, IDeviceInfo device, CancellationToken ct)
    {
        var row = new DeviceApps
        {
            DeviceId = device.Id,
            Address = device.Address,
            Model = device.Model,
            Mac = device.Serial,
            Firmware = device.FirmwareVersion,
            Soc = device.Soc,
            Architecture = device.Architecture,
        };
        if (Refusal(device) is { } refusal)
        {
            return (row with { Error = refusal, NotChecked = device.Status != DeviceStatus.Ok }, false);
        }

        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (null, false);
        }

        try
        {
            using var deadline = new CancellationTokenSource(_options.DeviceTimeout, _options.Time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var client = await vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false);
            var api = new ApplicationApiClient(client);
            var apps = await api.ListAsync(timeout.Token).ConfigureAwait(false);

            // The install dialog's checks need them like the Applications plugin's (best effort: null when not offered), both
            // at the same time within the device's slot and timeout.
            var allowUnsigned = api.TryGetAllowUnsignedAsync(timeout.Token);
            var embeddedDevelopment = api.TryGetEmbeddedDevelopmentVersionAsync(timeout.Token);
            await Task.WhenAll(allowUnsigned, embeddedDevelopment).ConfigureAwait(false);
            return (row with
            {
                Applications = apps,
                AllowUnsigned = await allowUnsigned.ConfigureAwait(false),
                EmbeddedDevelopmentVersion = await embeddedDevelopment.ConfigureAwait(false),
                ReadUtc = _options.Time.GetUtcNow(),
            }, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, false);
        }
        catch (Exception ex) when (ex is DevicesNotAvailableException or DeviceAccessRefusedException or KeyNotFoundException)
        {
            return (null, true);
        }
#pragma warning disable CA1031 // A device problem becomes the row's status; the read goes on with the next device.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (row with { Error = Failure(ex), ReadUtc = _options.Time.GetUtcNow() }, false);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Why a device is not contacted: not Ok ("Not checked: ..."), or no Application API 1.x; null when it may be.</summary>
    internal static string? Refusal(IDeviceInfo device) =>
        device.Status != DeviceStatus.Ok ? AppManagementInfo.NotChecked(DeviceMessages.StatusText(device))
        : !CachedApiCheck.Supports(device.Apis, AcapPlugin.ApplicationApiId, MinApplicationApi) ? AppManagementInfo.NoApplicationApi
        : null;

    /// <summary>One application's log lines on one device of the caller (systemlog.cgi, read only).</summary>
    public async Task<LogReply> GetLogAsync(ICallContext caller, LogRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var application = ValidApplication(request.Application);
        var device = (await CallDevices.RequireAsync(caller, [request.DeviceId], ct).ConfigureAwait(false))[0];
        if (Refusal(device) is { } refusal)
        {
            return new LogReply { Error = refusal };
        }

        try
        {
            var log = await WithDeviceAsync(caller.Vapix, device, (api, token) => api.ReadLogAsync(application, token), ct).ConfigureAwait(false);
            return new LogReply { Lines = log.Lines, Truncated = log.Truncated };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The device's problem is the answer.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new LogReply { Error = Failure(ex) };
        }
    }

    /// <summary>
    /// One application's settings on devices of the caller (param.cgi listdefinitions, read only), hidden ones left out. The
    /// values go only to callers who may change settings: they are often addresses or keys.
    /// </summary>
    public async Task<SettingsReply> GetSettingsAsync(ICallContext caller, SettingsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        var application = ValidApplication(request.Application);
        var ids = (request.DeviceIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException(DeviceLimits.ChooseRangeText("devices"), nameof(request));
        }

        if (ids.Count > AppManagementInfo.MaxSettingsDevices)
        {
            throw new ArgumentException(AppManagementInfo.TooManySettingsDevices, nameof(request));
        }

        var devices = await CallDevices.RequireAsync(caller, ids, ct).ConfigureAwait(false);
        var showValues = caller.Has(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Settings));
        var results = new DeviceSettings[devices.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, devices.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Parallelism), CancellationToken = ct },
            async (i, token) =>
            {
                var device = devices[i];
                var row = new DeviceSettings
                {
                    DeviceId = device.Id,
                    Address = device.Address,
                };
                if (Refusal(device) is { } refusal)
                {
                    results[i] = row with { Error = refusal };
                    return;
                }

                try
                {
                    var settings = await WithDeviceAsync(caller.Vapix, device, (api, t) => api.ReadSettingsAsync(application, t), token).ConfigureAwait(false);
                    results[i] = row with
                    {
                        Settings = [.. settings.Where(d => !d.Hidden).Select(d => showValues ? d : d with { Value = null })],
                    };
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // The device's problem is its row.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    results[i] = row with { Error = Failure(ex) };
                }
            }).ConfigureAwait(false);
        return new SettingsReply { Devices = results, ValuesShown = showValues };
    }

    private static string ValidApplication(string? application) =>
        EapReader.IsValidAppName(application?.Trim()) ? application!.Trim()
            : throw new ArgumentException("The application name is not valid.", nameof(application));

    /// <summary>One read of a device within the shared limit and the device timeout.</summary>
    private async Task<T> WithDeviceAsync<T>(IVapixClientFactory vapix, IDeviceInfo device, Func<ApplicationApiClient, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var deadline = new CancellationTokenSource(_options.DeviceTimeout, _options.Time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var client = await vapix.CreateAsync(device.Id, timeout.Token).ConfigureAwait(false);
            return await read(new ApplicationApiClient(client), timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>The usual short text of a failed read.</summary>
    private string Failure(Exception ex) => ex switch
    {
        OperationCanceledException => DeviceMessages.Timeout(_options.DeviceTimeout),
        HttpRequestException { InnerException: SocketException socket } => DeviceMessages.Unreachable(socket.Message),
        HttpRequestException http => DeviceMessages.Unreachable(http.Message),
        _ => ex.Message,
    };

    private static async Task<IReadOnlyList<IDeviceInfo>> SelectAsync(ICallContext caller, ReadRequest request, CancellationToken ct)
    {
        if (request.All)
        {
            return await caller.Devices.ListAsync(ct).ConfigureAwait(false);
        }

        var ids = (request.DeviceIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > DeviceLimits.MaxDevices)
        {
            throw new ArgumentException(DeviceLimits.ChooseRangeText("devices"), nameof(request));
        }

        // A user with all devices reads the chosen devices that still exist; a group user's ids must all be in the group.
        if (!caller.HasAllDevices)
        {
            return await CallDevices.RequireAsync(caller, ids, ct).ConfigureAwait(false);
        }

        var known = await CallDevices.KnownAsync(caller, ids, ct).ConfigureAwait(false);
        return [.. (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).Where(d => known.Contains(d.Id))];
    }

    private async Task RunReadAsync(Session session, ReadJob job, IReadOnlyList<IDeviceInfo> devices, ICallContext owner, CancellationToken token)
    {
        using var publish = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        var publisher = Task.Run(() => PublishLoopAsync(session, publish.Token), CancellationToken.None);
        try
        {
            await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Parallelism), CancellationToken = token },
                async (device, ct) =>
                {
                    await ReadIntoAsync(session, owner.Vapix, device, ct).ConfigureAwait(false);
                    job.Completed();
                    lock (session.Gate)
                    {
                        session.StateDirty = true;
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // a newer read, the session's end or the plugin stopping
        }
#pragma warning disable CA1031 // A failed read never stops the plugin; the page can read again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReadFailed(_ctx.Logger, ex);
        }
        finally
        {
            job.Finish(_options.Time.GetUtcNow());
            lock (session.Gate)
            {
                session.StateDirty = true;
            }

            await publish.CancelAsync().ConfigureAwait(false);
            try
            {
                await publisher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            Flush(session);
        }
    }

    /// <summary>Reads one device into the session's rows unless a read that started later already set them.</summary>
    private async Task ReadIntoAsync(Session session, IVapixClientFactory vapix, IDeviceInfo device, CancellationToken ct)
    {
        long read;
        lock (session.Gate)
        {
            read = ++session.Reads;
            session.ReadOf[device.Id] = read;
        }

        var (row, gone) = await ReadDeviceAsync(vapix, device, ct).ConfigureAwait(false);
        lock (session.Gate)
        {
            if (session.ReadOf.GetValueOrDefault(device.Id) != read)
            {
                return; // a later read owns the row
            }

            if (gone || !session.Chosen.Contains(device.Id))
            {
                if (session.Rows.Remove(device.Id))
                {
                    session.PendingRows.Remove(device.Id);
                    session.PendingRemoved.Add(device.Id);
                    session.StateDirty = true;
                }

                return;
            }

            if (row is null)
            {
                return;
            }

            if (session.Results.TryGetValue(device.Id, out var results))
            {
                row = row with { TaskResults = results };
            }

            session.Rows[device.Id] = row;
            session.PendingRows[device.Id] = row;
            session.PendingRemoved.Remove(device.Id);
            session.StateDirty = true;
        }
    }

    private void Follow(ICallContext caller, IPluginJobs jobs, IReadOnlyList<FollowGroup> groups, string activity)
    {
        if (groups.Count == 0)
        {
            return;
        }

        var session = SessionOf(caller);
        lock (session.Gate)
        {
            foreach (var group in groups)
            {
                // Only the rows of the started applications show the activity; a task without one (Install) the whole device.
                IReadOnlyList<string> apps = group.Applications.Values.Any(a => a is null)
                    ? []
                    : [.. group.Applications.Values.OfType<string>().Distinct(StringComparer.Ordinal)];
                foreach (var id in group.Devices)
                {
                    if (session.Rows.TryGetValue(id, out var row))
                    {
                        session.Rows[id] = row = row with { Activity = activity, ActivityApplications = apps };
                        session.PendingRows[id] = row;
                    }
                }
            }
        }

        Flush(session);
        var job = jobs.Start(caller, "follow", (owned, token) => FollowLoopAsync(session, groups, owned.Owner, token));
        lock (session.Gate)
        {
            session.Follows.Add(job);
            session.Follows.RemoveAll(j => j.Completion.IsCompleted);
        }
    }

    /// <summary>
    /// Asks every <see cref="AppManagementOptions.FollowInterval"/> whether the tasks ended; once all tasks of a group ended,
    /// keeps the messages of the failed and warning ones for their devices and reads the group's devices again.
    /// </summary>
    private async Task FollowLoopAsync(Session session, IReadOnlyList<FollowGroup> groups, ICallContext owner, CancellationToken token)
    {
        var open = groups.Select(g => new FollowGroup([.. g.Tasks], g.Devices, g.Applications)).ToList();
        try
        {
            while (open.Count > 0 && !token.IsCancellationRequested)
            {
                await Task.Delay(_options.FollowInterval, _options.Time, token).ConfigureAwait(false);
                var ended = new List<Guid>();
                foreach (var group in open)
                {
                    group.Tasks.RemoveWhere(t => owner.Tasks.IsActive(t) != true);
                    if (group.Tasks.Count == 0)
                    {
                        ended.AddRange(group.Devices);
                        await KeepResultsAsync(session, group, owner, token).ConfigureAwait(false);
                    }
                }

                open.RemoveAll(g => g.Tasks.Count == 0);
                if (ended.Count > 0)
                {
                    await ReadAgainAsync(session, ended.Distinct().ToList(), owner, token).ConfigureAwait(false);
                    Flush(session);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // The task's own result counts; the page reads again on Refresh.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFollowFailed(_ctx.Logger, ex);
        }
    }

    /// <summary>
    /// The end of a group's tasks (<see cref="Sdk.Tasks.ITaskRunner.GetOutcomeAsync"/> as the caller): the messages of the failed
    /// and warning ones per device, which the read that follows carries; a device whose tasks all went well loses its old ones.
    /// </summary>
    private static async Task KeepResultsAsync(Session session, FollowGroup group, ICallContext owner, CancellationToken token)
    {
        var devices = group.Devices.ToHashSet();
        var results = devices.ToDictionary(d => d, _ => new List<AppTaskResult>());
        foreach (var (taskId, application) in group.Applications)
        {
            if (await owner.Tasks.GetOutcomeAsync(taskId, token).ConfigureAwait(false) is not { } outcome || !results.TryGetValue(outcome.DeviceId, out var list)
                || outcome.State is not (Sdk.Tasks.TaskEndState.Failed or Sdk.Tasks.TaskEndState.DoneWithWarnings))
            {
                continue;
            }

            list.Add(new AppTaskResult
            {
                Application = application,
                Failed = outcome.State == Sdk.Tasks.TaskEndState.Failed,
                Message = outcome.Message ?? (outcome.State == Sdk.Tasks.TaskEndState.Failed ? AppManagementInfo.TaskFailed : AppManagementInfo.TaskWarnings),
            });
        }

        lock (session.Gate)
        {
            foreach (var (device, list) in results)
            {
                if (list.Count == 0)
                {
                    session.Results.Remove(device);
                }
                else
                {
                    session.Results[device] = list;
                }
            }
        }
    }

    /// <summary>Reads devices of the session again (after their tasks ended); a device the session no longer shows is skipped.</summary>
    private async Task ReadAgainAsync(Session session, IReadOnlyList<Guid> ids, ICallContext owner, CancellationToken token)
    {
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Parallelism), CancellationToken = token },
            async (id, ct) =>
            {
                lock (session.Gate)
                {
                    if (!session.Chosen.Contains(id))
                    {
                        return;
                    }
                }

                IDeviceInfo? device;
                try
                {
                    device = await owner.Devices.FindAsync(id, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is DevicesNotAvailableException or DeviceAccessRefusedException)
                {
                    device = null;
                }

                if (device is null)
                {
                    lock (session.Gate)
                    {
                        if (session.Rows.Remove(id))
                        {
                            session.PendingRows.Remove(id);
                            session.PendingRemoved.Add(id);
                        }
                    }

                    return;
                }

                await ReadIntoAsync(session, owner.Vapix, device, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task PublishLoopAsync(Session session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.EventInterval, _options.Time, ct).ConfigureAwait(false);
                Flush(session);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The rows changed since the last flush and the progress, to the session's pages only.</summary>
    internal void Flush(Session session)
    {
        if (_ctx.Events is not { } events)
        {
            return;
        }

        lock (session.Flushing)
        {
            List<DeviceApps> rows;
            List<Guid> removed;
            bool state;
            lock (session.Gate)
            {
                rows = [.. session.PendingRows.Values];
                removed = [.. session.PendingRemoved];
                state = session.StateDirty;
                session.PendingRows.Clear();
                session.PendingRemoved.Clear();
                session.StateDirty = false;
            }

            var audience = PluginEventAudience.Session(session.Id);
            if (removed.Count > 0)
            {
                events.Publish(AppManagementMethods.RowsTopic, AppManagementJson.Serialize(new RowsEvent { Removed = removed }), audience);
            }

            foreach (var chunk in rows.Chunk(Math.Max(1, _options.EventBatchDevices)))
            {
                PublishRows(events, chunk, audience);
            }

            if (state || rows.Count > 0 || removed.Count > 0)
            {
                events.Publish(AppManagementMethods.StateTopic, AppManagementJson.Serialize(State(session, session.Job)), audience);
            }
        }
    }

    /// <summary>One event per chunk; a chunk over the event size limit is split in halves.</summary>
    private static void PublishRows(IPluginEvents events, DeviceApps[] rows, PluginEventAudience audience)
    {
        var json = AppManagementJson.Serialize(new RowsEvent { Devices = rows });
        if (json.Length <= IPluginEvents.MaxPayloadLength || rows.Length <= 1)
        {
            events.Publish(AppManagementMethods.RowsTopic, json, audience);
            return;
        }

        var half = rows.Length / 2;
        PublishRows(events, rows[..half], audience);
        PublishRows(events, rows[half..], audience);
    }

    private static ReadState State(Session session, ReadJob? job)
    {
        int count;
        lock (session.Gate)
        {
            count = session.Rows.Count;
        }

        return job is null
            ? new ReadState { Devices = count }
            : new ReadState
            {
                JobId = job.Owned?.Id,
                Reading = job.IsRunning,
                Done = job.Done,
                Total = job.Total,
                Devices = count,
                FinishedUtc = job.FinishedUtc,
            };
    }

    private Session? Existing(ICallContext caller)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(caller.SessionId, out var session))
            {
                session.LastUsed = ++_clock;
                return session;
            }

            return null;
        }
    }

    /// <summary>The caller's session, made on first use; beyond <see cref="AppManagementOptions.MaxSessions"/> the least recently used one is dropped.</summary>
    private Session SessionOf(ICallContext caller)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(caller.SessionId, out var session))
            {
                while (_sessions.Count >= Math.Max(1, _options.MaxSessions))
                {
                    var oldest = _sessions.Values.MinBy(s => s.LastUsed)!;
                    _sessions.Remove(oldest.Id);
                    _ = oldest.CancelJobs();
                }

                session = new Session(caller.SessionId);
                _sessions[session.Id] = session;
            }

            session.LastUsed = ++_clock;
            return session;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applications: reading {Count} devices")]
    private static partial void LogReadStarted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applications: reading the devices failed")]
    private static partial void LogReadFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applications: {Action} of {Applications} applications on {Devices} devices started")]
    private static partial void LogRun(ILogger logger, AcapAction action, int applications, int devices);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applications: following the tasks failed")]
    private static partial void LogFollowFailed(ILogger logger, Exception ex);

    /// <summary>Tasks, the devices to read again once all of them ended, and each task's application (null: not known).</summary>
    private sealed record FollowGroup(HashSet<Guid> Tasks, IReadOnlyList<Guid> Devices, IReadOnlyDictionary<Guid, string?> Applications);

    /// <summary>The rows and jobs of one login session.</summary>
    internal sealed class Session(string id)
    {
        public string Id { get; } = id;

        public Lock Gate { get; } = new();

        public Lock Flushing { get; } = new();

        public Dictionary<Guid, DeviceApps> Rows { get; } = [];

        public Dictionary<Guid, long> ReadOf { get; } = [];

        /// <summary>The failed and warning tasks of the page per device, until the next read the user starts.</summary>
        public Dictionary<Guid, IReadOnlyList<AppTaskResult>> Results { get; } = [];

        public Dictionary<Guid, DeviceApps> PendingRows { get; } = [];

        public HashSet<Guid> PendingRemoved { get; } = [];

        public HashSet<Guid> Chosen { get; set; } = [];

        /// <summary>The sorted ids of the rows the last first page saw (<see cref="GetRowsAsync"/>); null before one.</summary>
        public IReadOnlyList<Guid>? PageOrder { get; set; }

        public List<IPluginJob> Follows { get; } = [];

        public ReadJob? Job { get; set; }

        public long Reads { get; set; }

        public bool StateDirty { get; set; }

        public long LastUsed { get; set; }

        /// <summary>Cancels the read and the follows; returns their runs.</summary>
        public List<Task> CancelJobs()
        {
            List<IPluginJob> jobs;
            lock (Gate)
            {
                jobs = [.. Follows];
                if (Job?.Owned is { } read)
                {
                    jobs.Add(read);
                }
            }

            foreach (var job in jobs)
            {
                job.Cancel();
            }

            return [.. jobs.Select(j => j.Completion)];
        }
    }

    /// <summary>One read of chosen devices: its progress.</summary>
    internal sealed class ReadJob(int total)
    {
        private int _done;
        private volatile bool _running = true;

        public IPluginJob? Owned { get; set; }

        public int Total { get; } = total;

        public int Done => Volatile.Read(ref _done);

        public bool IsRunning => _running;

        public DateTimeOffset? FinishedUtc { get; private set; }

        public void Completed() => Interlocked.Increment(ref _done);

        public void Finish(DateTimeOffset now)
        {
            FinishedUtc = now;
            _running = false;
        }
    }
}
