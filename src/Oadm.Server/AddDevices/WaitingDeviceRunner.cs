using System.Collections.Concurrent;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Settings;
using Oadm.Server.Discovery;
using Oadm.Sdk.Devices;

using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.AddDevices;

/// <summary>What one try of a waiting device found.</summary>
public enum WaitingOutcomeKind
{
    /// <summary>Added; the entry is removed.</summary>
    Added,

    /// <summary>OADM manages the device already (added another way); the entry is removed.</summary>
    AlreadyManaged,

    /// <summary>No answer, not an Axis device, another device at the address, the device limit: tried again next round.</summary>
    StillWaiting,

    /// <summary>The device answered but rejected every login: waits for Try now (no repeated wrong passwords).</summary>
    LoginFailed,
}

/// <summary>The outcome of one try with its plain-language text.</summary>
public sealed record WaitingOutcome(WaitingOutcomeKind Kind, string Message);

/// <summary>
/// Tries the waiting list (<see cref="WaitingDeviceStore"/>) again: a round every Devices.WaitingRetryMinutes (first one a
/// minute after the start), or at once for <see cref="TryNow"/>. Each device goes the add page's way for an imported line
/// (<see cref="IDiscoveryBackend.StartAddressProbeAsync"/> with the short first contact, the expected MAC address and the
/// line's login in the session, the Axis check before any password, then the credential list; at most
/// <see cref="Parallelism"/> devices at once and the authenticator's own login limit) and is stored by
/// <see cref="DeviceAdder"/> like an add from the page: with the login that worked, or as Password not set for a
/// factory-default device. Added devices get the line's tags, a first full refresh and the audit entry "Device added
/// automatically". Entries whose device OADM manages by now are removed before each round.
/// </summary>
public sealed partial class WaitingDeviceRunner(
    WaitingDeviceStore store,
    IDiscoveryBackend discovery,
    DiscoveryAuthenticator authenticator,
    DeviceRepository devices,
    DeviceAdder adder,
    DeviceTagStore tags,
    ServerSettingsStore settings,
    DevicePollingService polling,
    AuditLog audit,
    TimeProvider time,
    ILogger<WaitingDeviceRunner> logger) : BackgroundService
{
    /// <summary>Waiting devices tried at the same time (the logins are limited by the authenticator as well).</summary>
    public const int Parallelism = 32;

    /// <summary>Results written in one transaction.</summary>
    private const int ResultBatch = 256;

    /// <summary>The first round after a start.</summary>
    public static readonly TimeSpan FirstRoundDelay = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _requested = [];
    private bool _requestedAll;

    // Entries asked for (Try now) or in the running round that were not tried yet: their rows say "Checking".
    private readonly ConcurrentDictionary<Guid, byte> _checking = new();
    private volatile bool _checkingAll;
    private volatile bool _trying;
    private DateTimeOffset? _nextTry;

    /// <summary>Raised when a round starts or ends (the list itself raises <see cref="WaitingDeviceStore.Changed"/>).</summary>
    public event EventHandler? StateChanged;

    /// <summary>A round runs now.</summary>
    public bool IsTrying => _trying;

    /// <summary>The entry was asked for or is in the running round and was not tried yet.</summary>
    public bool IsChecking(Guid id) => _checkingAll || _checking.ContainsKey(id);

    /// <summary>How many entries of the running round (or asked for) were not tried yet; -1 = every entry (Try now for all).</summary>
    public int Remaining => _checkingAll ? -1 : _checking.Count;

    /// <summary>When the next round starts; null while one runs.</summary>
    public DateTimeOffset? NextTry
    {
        get
        {
            lock (_gate)
            {
                return _trying ? null : _nextTry;
            }
        }
    }

    /// <summary>Tries these entries (null: every entry, also those whose login failed) right after the running round.</summary>
    public void TryNow(IReadOnlyCollection<Guid>? ids)
    {
        lock (_gate)
        {
            if (ids is null)
            {
                _requestedAll = true;
                _checkingAll = true;
            }
            else
            {
                _requested.UnionWith(ids);
                foreach (var id in ids)
                {
                    _checking.TryAdd(id, 0);
                }
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = FirstRoundDelay;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _nextTry = time.GetUtcNow() + delay;
                }

                StateChanged?.Invoke(this, EventArgs.Empty);
                await WaitAsync(delay, stoppingToken).ConfigureAwait(false);

                // Try now for some entries runs only those; Try now for all also the failed logins; else the scheduled round.
                IReadOnlyCollection<Guid>? ids;
                bool includeLoginFailed;
                lock (_gate)
                {
                    includeLoginFailed = _requestedAll;
                    ids = _requestedAll || _requested.Count == 0 ? null : [.. _requested];
                    _requested.Clear();
                    _requestedAll = false;
                }

                try
                {
                    await RunRoundAsync(ids, includeLoginFailed, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LogRoundFailed(ex);
                }

                var current = await settings.GetServerSettingsAsync(stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromMinutes(Math.Clamp(current.WaitingRetryMinutes, ServerSettings.MinWaitingRetryMinutes, ServerSettings.MaxWaitingRetryMinutes));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One round: removes the entries OADM manages by now, then tries <paramref name="ids"/> (null: every entry whose login
    /// did not fail, or every entry with <paramref name="includeLoginFailed"/>). Returns how many devices were added.
    /// </summary>
    public async Task<int> RunRoundAsync(IReadOnlyCollection<Guid>? ids, bool includeLoginFailed, CancellationToken ct)
    {
        _trying = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var managed = await devices.ListDevicesAsync(ct).ConfigureAwait(false);
            await store.RemoveManagedAsync(
                managed.Select(d => WaitingDeviceStore.Normalize(d.Address)).ToHashSet(StringComparer.Ordinal),
                managed.Select(d => d.Serial).ToHashSet(StringComparer.Ordinal), ct).ConfigureAwait(false);

            var entries = await store.ToTryAsync(ids, includeLoginFailed, ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                _checking.TryAdd(entry.Id, 0);
            }

            _checkingAll = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (entries.Count == 0)
            {
                return 0;
            }

            var results = new ConcurrentQueue<WaitingResult>();
            var done = new ConcurrentQueue<Guid>();
            var added = 0;
            var flush = new SemaphoreSlim(1);
            async Task FlushAsync(bool force, CancellationToken token)
            {
                if (!force && results.Count < ResultBatch)
                {
                    return;
                }

                await flush.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var batch = Drain(results);
                    var gone = Drain(done);
                    await store.RecordAsync(batch, token).ConfigureAwait(false);
                    if (gone.Count > 0)
                    {
                        await store.RemoveAsync(gone, all: false, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    flush.Release();
                }
            }

            await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (entry, token) =>
            {
                var outcome = await TryOneAsync(entry, token).ConfigureAwait(false);
                _checking.TryRemove(entry.Id, out _);
                switch (outcome.Kind)
                {
                    case WaitingOutcomeKind.Added:
                        Interlocked.Increment(ref added);
                        done.Enqueue(entry.Id);
                        break;
                    case WaitingOutcomeKind.AlreadyManaged:
                        done.Enqueue(entry.Id);
                        break;
                    default:
                        results.Enqueue(new WaitingResult(entry.Id, outcome.Message, outcome.Kind == WaitingOutcomeKind.LoginFailed));
                        break;
                }

                await FlushAsync(force: false, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await FlushAsync(force: true, ct).ConfigureAwait(false);
            LogRound(entries.Count, added);
            return added;
        }
        finally
        {
            // Try now asked during this round waits for the next one: those entries stay "Checking".
            lock (_gate)
            {
                foreach (var id in _checking.Keys.Where(id => !_requested.Contains(id)).ToList())
                {
                    _checking.TryRemove(id, out _);
                }

                _checkingAll = _requestedAll;
            }

            _trying = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>One try of one waiting device. Never throws for device errors.</summary>
    public async Task<WaitingOutcome> TryOneAsync(WaitingTry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string session;
        try
        {
            session = (await discovery.StartAddressProbeAsync(entry.Address, importLine: true, ct).ConfigureAwait(false)).Id;
        }
        catch (ArgumentException ex)
        {
            return new WaitingOutcome(WaitingOutcomeKind.StillWaiting, ex.Message); // a host name that does not resolve (yet)
        }

        try
        {
            if (entry.ExpectedSerial is { } serial)
            {
                authenticator.SetExpectedSerial(session, serial);
            }

            if (entry.Login is { } login)
            {
                authenticator.AddSessionCredential(session, login.UserName, login.Password);
            }

            DiscoveredDevice? found = null;
            await foreach (var e in discovery.WatchAsync(session, ct).ConfigureAwait(false))
            {
                if (e.Device is not { } device)
                {
                    continue;
                }

                found = device;
                if (await devices.FindBySerialAsync(device.Serial, ct).ConfigureAwait(false) is not null)
                {
                    return new WaitingOutcome(WaitingOutcomeKind.AlreadyManaged, $"{device.Serial} is managed already.");
                }

                authenticator.Ensure(session, device, alreadyManaged: false);
            }

            if (found is null)
            {
                return new WaitingOutcome(WaitingOutcomeKind.StillWaiting, $"No Axis device answered at {entry.Address}.");
            }

            await authenticator.WhenIdleAsync(session, ct).ConfigureAwait(false);
            var auth = authenticator.Get(session, found.Serial);
            switch (auth.State)
            {
                case DeviceAuthState.Authenticated:
                    var credentials = authenticator.MatchedCredentials(session, found.Serial);
                    return await AddAsync(entry, found, credentials, ct).ConfigureAwait(false);
                case DeviceAuthState.PasswordNotSet:
                    return await AddAsync(entry, found, null, ct).ConfigureAwait(false);
                case DeviceAuthState.AlreadyAdded:
                    return new WaitingOutcome(WaitingOutcomeKind.AlreadyManaged, $"{found.Serial} is managed already.");
                case DeviceAuthState.LoginFailed:
                    return new WaitingOutcome(WaitingOutcomeKind.LoginFailed, auth.Detail ?? "The user name or password is wrong.");
                default:
                    return new WaitingOutcome(WaitingOutcomeKind.StillWaiting, auth.Detail ?? $"No Axis device answered at {entry.Address}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogTryFailed(entry.Address, ex.Message);
            return new WaitingOutcome(WaitingOutcomeKind.StillWaiting, $"No Axis device answered at {entry.Address}.");
        }
        finally
        {
            await discovery.StopAsync(session).ConfigureAwait(false);
            authenticator.Forget(session);
        }
    }

    private async Task<WaitingOutcome> AddAsync(WaitingTry entry, DiscoveredDevice found, Core.Security.DeviceCredentials? credentials, CancellationToken ct)
    {
        var server = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
        (Guid Id, SdkDeviceStatus Status)? added;
        try
        {
            added = await adder.AddAsync(found, useHostName: false, initialPassword: null, credentials, server.ServerName, ct).ConfigureAwait(false);
        }
        catch (DeviceLimitException)
        {
            return new WaitingOutcome(WaitingOutcomeKind.StillWaiting, DeviceLimits.TooManyDevicesText);
        }

        if (added is not { } device)
        {
            return new WaitingOutcome(WaitingOutcomeKind.AlreadyManaged, $"{found.Serial} is managed already.");
        }

        polling.QueueRefresh([device.Id]);
        if (entry.Tags.Count > 0)
        {
            try
            {
                await tags.SetDeviceTagsAsync([device.Id], [.. entry.Tags], [], ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TagNameException or TagLimitException)
            {
                LogTagsFailed(found.Serial, ex.Message);
            }
        }

        var message = device.Status switch
        {
            SdkDeviceStatus.PasswordNotSet => "Added; the device has no password yet (factory default).",
            SdkDeviceStatus.Ok or SdkDeviceStatus.Unknown => "Added and logged in as " + (credentials?.UserName ?? "-") + ".",
            _ => "Added; the login no longer works on it.",
        };
        LogAdded(found.Serial, found.ConnectAddress, entry.Source);
        await audit.WriteAsync("system", string.Empty, AuditActions.DeviceAddedAutomatically, $"{found.ConnectAddress} ({found.Serial})",
            $"{entry.Source} (waiting list, kept by {entry.AddedBy}): {message}", ct).ConfigureAwait(false);
        return new WaitingOutcome(WaitingOutcomeKind.Added, message);
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wake = _wake.WaitAsync(linked.Token);
        var timer = Task.Delay(delay, time, linked.Token);
        await Task.WhenAny(wake, timer).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        while (_wake.CurrentCount > 0)
        {
            await _wake.WaitAsync(ct).ConfigureAwait(false); // several Try now in a row: one round
        }
    }

    private static List<T> Drain<T>(ConcurrentQueue<T> queue)
    {
        var list = new List<T>();
        while (queue.TryDequeue(out var item))
        {
            list.Add(item);
        }

        return list;
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting list: {Tried} devices tried, {Added} added")]
    private partial void LogRound(int tried, int added);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting device {Serial} at {Address} added ({Source})")]
    private partial void LogAdded(string serial, string address, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waiting list round failed")]
    private partial void LogRoundFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Trying waiting device {Address} failed: {Reason}")]
    private partial void LogTryFailed(string address, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tags of the added waiting device {Serial} were not set: {Reason}")]
    private partial void LogTagsFailed(string serial, string reason);
}
