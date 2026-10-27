using System.Globalization;
using System.Text;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// The canary test of a core plugin (SDK 0.4.0, docs/spec/device-scope.md, Tests): a user limited to a device group that
/// holds <c>inside</c> calls every method of the plugin with the device outside the group in each id field. Each case must
/// answer exactly like for an unknown id (<see cref="DevicesNotAvailableException"/> with the same text: the host answers
/// NOT_FOUND "1 device is no longer available."), hand out no VAPIX client for the outside device, send it no request, open no measuring stream of it,
/// start no task on it and change no setting or record; the same call with the inside device must not be refused. Methods
/// must never use the context's full accesses in a group user's call (<c>ctx.Devices</c>, <c>ctx.Vapix</c>, ...).
/// </summary>
/// <remarks>
/// Name every method of the plugin once: <see cref="Method"/> (payload with device ids), <see cref="Download"/>,
/// <see cref="Job"/> (payload with the id of a job another user started), <see cref="Query"/> (a task plugin query),
/// <see cref="WithoutDevices"/> (with the reason), <see cref="AllDevicesOnly"/> (declared through
/// <see cref="ICorePlugin.RequiresAllDevices"/>). In payload templates <c>{device}</c> is the id under test (outside,
/// unknown, inside), <c>{inside}</c> always the inside device, <c>{job}</c> the job id. <see cref="RunAsync"/> runs every
/// case and throws <see cref="PluginCanaryException"/> listing every failure.
/// </remarks>
public sealed class PluginCanary
{
    private readonly FakeCoreContext _ctx;
    private readonly ICorePlugin _plugin;
    private readonly List<Case> _cases = [];

    /// <param name="ctx">The plugin's context; <paramref name="inside"/> and <paramref name="outside"/> are in its <see cref="FakeCoreContext.DeviceList"/>.</param>
    /// <param name="plugin">The plugin, started with <paramref name="ctx"/>.</param>
    /// <param name="inside">The device of the group user's group.</param>
    /// <param name="outside">A managed device outside the group.</param>
    public PluginCanary(FakeCoreContext ctx, ICorePlugin plugin, IDeviceInfo inside, IDeviceInfo outside)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(inside);
        ArgumentNullException.ThrowIfNull(outside);
        if (!ctx.DeviceList.Exists(d => d.Id == inside.Id) || !ctx.DeviceList.Exists(d => d.Id == outside.Id))
        {
            throw new ArgumentException("Add both devices to the context first.", nameof(ctx));
        }

        _ctx = ctx;
        _plugin = plugin;
        Inside = inside;
        Outside = outside;
        GroupCall = ctx.CallAsGroup([inside.Id], "canary");
        Owner = ctx.CallAsAllDevices("owner");
    }

    /// <summary>The device of the group.</summary>
    public IDeviceInfo Inside { get; }

    /// <summary>The device outside the group.</summary>
    public IDeviceInfo Outside { get; }

    /// <summary>The group user's call (user "canary", only <see cref="Inside"/>).</summary>
    public FakeCallContext GroupCall { get; }

    /// <summary>Another user (all devices) who starts the jobs of <see cref="Job"/>.</summary>
    public FakeCallContext Owner { get; }

    /// <summary>The methods named so far.</summary>
    public IReadOnlyList<string> Methods => [.. _cases.Select(c => c.Method).Distinct(StringComparer.Ordinal)];

    /// <summary>A method that takes device ids in its payload (<c>{device}</c>, optionally <c>{inside}</c>).</summary>
    public PluginCanary Method(string method, string payloadTemplate) =>
        Add(new Case(CaseKind.Invoke, method, payloadTemplate));

    /// <summary>A download (<see cref="ICorePluginDownloads"/>) that takes device ids in its payload.</summary>
    public PluginCanary Download(string method, string payloadTemplate) =>
        Add(new Case(CaseKind.Download, method, payloadTemplate));

    /// <summary>
    /// A method (or download, <paramref name="download"/>) that takes the id of a job: <paramref name="startAsOwner"/> starts
    /// a job as <see cref="Owner"/> and returns its id; the group user's call with that id must be answered exactly like
    /// with an unknown id (the plugin finds jobs through <see cref="IPluginJobs.Find"/>).
    /// </summary>
    public PluginCanary Job(string method, string payloadTemplate, Func<FakeCallContext, Task<string>> startAsOwner, bool download = false)
    {
        ArgumentNullException.ThrowIfNull(startAsOwner);
        return Add(new Case(download ? CaseKind.JobDownload : CaseKind.Job, method, payloadTemplate) { StartJob = startAsOwner });
    }

    /// <summary>A query of a task plugin the core plugin contributes (or any <see cref="ITaskPluginQuery"/>), on the inside device, with device ids in its payload.</summary>
    public PluginCanary Query(ITaskPluginQuery query, string method, string payloadTemplate)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Add(new Case(CaseKind.Query, method, payloadTemplate) { TaskQuery = query });
    }

    /// <summary>A method without device ids and device data; the group user's call must not use the context's full accesses.</summary>
    public PluginCanary WithoutDevices(string method, string reason, string? payload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return Add(new Case(CaseKind.WithoutDevices, method, payload) { Reason = reason });
    }

    /// <summary>A method only users with all devices may call: the plugin declares it, the group user is refused before it runs.</summary>
    public PluginCanary AllDevicesOnly(string method, string? payload = null) =>
        Add(new Case(CaseKind.AllDevicesOnly, method, payload));

    /// <summary>Runs every case; throws <see cref="PluginCanaryException"/> with every failure.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_cases.Count == 0)
        {
            throw new PluginCanaryException(["No method named: name every method of the plugin."]);
        }

        var failures = new List<string>();
        foreach (var c in _cases)
        {
            _refusedBefore = _ctx.RefusedAccesses.Count;
            try
            {
                var failure = c.Kind switch
                {
                    CaseKind.WithoutDevices => await WithoutDevicesAsync(c, ct).ConfigureAwait(false),
                    CaseKind.AllDevicesOnly => await AllDevicesOnlyAsync(c, ct).ConfigureAwait(false),
                    CaseKind.Job or CaseKind.JobDownload => await JobAsync(c, ct).ConfigureAwait(false),
                    _ => await DevicesAsync(c, ct).ConfigureAwait(false),
                };
                if (failure is not null)
                {
                    failures.Add($"{c.Method}: {failure}");
                }
            }
#pragma warning disable CA1031 // Every failure of every case is reported together.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add($"{c.Method}: the canary failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new PluginCanaryException(failures);
        }
    }

    private int _refusedBefore;

    private PluginCanary Add(Case c)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(c.Method);
        _cases.Add(c);
        return this;
    }

    private async Task<string?> DevicesAsync(Case c, CancellationToken ct)
    {
        var before = Snapshot();
        var outside = await TryAsync(c, GroupCall, Outside.Id.ToString(), null, ct).ConfigureAwait(false);
        var after = Snapshot();
        if (outside is DeviceAccessRefusedException)
        {
            return FullAccess();
        }

        if (outside is not DevicesNotAvailableException refused)
        {
            return Expected("the device outside the group", outside);
        }

        if (before != after)
        {
            return $"the call with the device outside the group reached it or changed something ({before} -> {after})";
        }

        var unknown = await TryAsync(c, GroupCall, Guid.NewGuid().ToString(), null, ct).ConfigureAwait(false);
        if (unknown is not DevicesNotAvailableException unknownRefused)
        {
            return Expected("an unknown device", unknown);
        }

        if (unknownRefused.Message != refused.Message)
        {
            return $"the answers tell an unknown device (\"{unknownRefused.Message}\") from one outside the group (\"{refused.Message}\")";
        }

        var inside = await TryAsync(c, GroupCall, Inside.Id.ToString(), null, ct).ConfigureAwait(false);
        return inside switch
        {
            DeviceAccessRefusedException => FullAccess(),
            DevicesNotAvailableException => "the device of the group was refused",
            _ => null,
        };
    }

    private async Task<string?> JobAsync(Case c, CancellationToken ct)
    {
        var jobId = await c.StartJob!(Owner).ConfigureAwait(false);
        var foreign = await TryAsync(c, GroupCall, Inside.Id.ToString(), jobId, ct).ConfigureAwait(false);
        var unknown = await TryAsync(c, GroupCall, Inside.Id.ToString(), Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        if (foreign is DeviceAccessRefusedException || unknown is DeviceAccessRefusedException)
        {
            return FullAccess();
        }

        if (unknown is null)
        {
            return "an unknown job id was answered without an error";
        }

        if (foreign is null || foreign.GetType() != unknown.GetType() || foreign.Message != unknown.Message)
        {
            return $"another user's job is answered differently from an unknown id ({Describe(foreign)} / {Describe(unknown)})";
        }

        return null;
    }

    private async Task<string?> WithoutDevicesAsync(Case c, CancellationToken ct)
    {
        var result = await TryAsync(c, GroupCall, Inside.Id.ToString(), null, ct).ConfigureAwait(false);
        return result is DeviceAccessRefusedException ? FullAccess() : null;
    }

    private async Task<string?> AllDevicesOnlyAsync(Case c, CancellationToken ct)
    {
        if (!_plugin.RequiresAllDevices(c.Method))
        {
            return "RequiresAllDevices(method) is false: declare the method";
        }

        var before = Snapshot();
        var result = await TryAsync(c, GroupCall, Inside.Id.ToString(), null, ct).ConfigureAwait(false);
        return result is DeviceAccessRefusedException && before == Snapshot() ? null : Expected("a group user", result, "the refusal for users with all devices only");
    }

    private string FullAccess() =>
        "used the context's full access in a group user's call (ctx." + string.Join(", ctx.", _ctx.RefusedAccesses.Skip(_refusedBefore).Distinct()) + "): use the call";

    private static string Expected(string what, Exception? got, string expected = "DevicesNotAvailableException") =>
        $"with {what} expected {expected}, got {Describe(got)}";

    private static string Describe(Exception? ex) => ex is null ? "an answer" : $"{ex.GetType().Name} \"{ex.Message}\"";

    /// <summary>Runs the case; returns its exception, or null when it answered.</summary>
    private async Task<Exception?> TryAsync(Case c, FakeCallContext call, string deviceId, string? jobId, CancellationToken ct)
    {
        var payload = c.Payload?
            .Replace("{device}", deviceId, StringComparison.Ordinal)
            .Replace("{inside}", Inside.Id.ToString(), StringComparison.Ordinal)
            .Replace("{job}", jobId ?? string.Empty, StringComparison.Ordinal);
        try
        {
            switch (c.Kind)
            {
                case CaseKind.Download or CaseKind.JobDownload:
                    await using (await _ctx.OpenDownloadAsync(_plugin, call, c.Method, payload, ct).ConfigureAwait(false))
                    {
                    }

                    break;
                case CaseKind.Query:
                    await _ctx.QueryAsync(c.TaskQuery!, call, Inside, c.Method, payload, ct).ConfigureAwait(false);
                    break;
                default:
                    await _ctx.InvokeAsync(_plugin, call, c.Method, payload, ct).ConfigureAwait(false);
                    break;
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The canary compares the answers.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex;
        }
    }

    /// <summary>What a refused call must leave as it was: contact with the outside device, tasks, settings and records.</summary>
    private string Snapshot()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"clients {_ctx.ClientsCreated.GetValueOrDefault(Outside.Id)}");
        if (_ctx.Cameras.TryGetValue(Outside.Id, out var camera))
        {
            text.Append(CultureInfo.InvariantCulture, $", requests {camera.Requests.Count}");
        }

        var streams = _ctx.FakeStreamProbes;
        text.Append(CultureInfo.InvariantCulture, $", stream sources {streams.ListCallsFor(Outside.Id)}, streams {streams.OpensFor(Outside.Id)}");

        int tasks;
        lock (_ctx.TaskRuns)
        {
            tasks = _ctx.TaskRuns.Count(r => r.DeviceIds.Contains(Outside.Id));
        }

        text.Append(CultureInfo.InvariantCulture, $", tasks {tasks}, records {_ctx.PluginRecords.WrittenRecords}, settings ");
        foreach (var (key, value) in _ctx.PluginSettings.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append(key).Append('=').Append(value.GetHashCode(StringComparison.Ordinal)).Append(';');
        }

        return text.ToString();
    }

    private enum CaseKind
    {
        Invoke,
        Download,
        Job,
        JobDownload,
        Query,
        WithoutDevices,
        AllDevicesOnly,
    }

    private sealed record Case(CaseKind Kind, string Method, string? Payload)
    {
        public Func<FakeCallContext, Task<string>>? StartJob { get; init; }

        public ITaskPluginQuery? TaskQuery { get; init; }

        public string? Reason { get; init; }
    }
}

/// <summary>The failures of a <see cref="PluginCanary"/> run, one line each.</summary>
public sealed class PluginCanaryException : Exception
{
    /// <summary>Without failures.</summary>
    public PluginCanaryException()
        : this([])
    {
    }

    /// <summary>One failure.</summary>
    public PluginCanaryException(string message)
        : this([message])
    {
    }

    /// <summary>One failure and its cause.</summary>
    public PluginCanaryException(string message, Exception innerException)
        : base(message, innerException)
    {
        Failures = [message];
    }

    /// <summary>The failures of a run.</summary>
    public PluginCanaryException(IReadOnlyList<string> failures)
        : base("The canary found methods that reach devices outside the user's group:" + Environment.NewLine + string.Join(Environment.NewLine, failures ?? []))
    {
        Failures = failures ?? [];
    }

    /// <summary>One line per failed case ("method: what went wrong").</summary>
    public IReadOnlyList<string> Failures { get; }
}
