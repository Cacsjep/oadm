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

    /// <summary>
    /// The address answers through several sites with different devices OADM does not manage: which one is meant is not
    /// known, so it waits for Try now (or for the line kept again with a Site column).
    /// </summary>
    Held,

    /// <summary>The entry was removed or kept again while it was tried: nothing is added or written back.</summary>
    Changed,
}

/// <summary>The outcome of one try with its plain-language text.</summary>
public sealed record WaitingOutcome(WaitingOutcomeKind Kind, string Message);

/// <summary>
/// Tries the waiting list (<see cref="WaitingDeviceStore"/>) again: a round every Devices.WaitingRetryMinutes after the end of
/// the last one (first one a minute after the start; a changed setting applies within a minute), or at once for
/// <see cref="TryNow"/>. Each device goes the add page's way for an imported line
/// (<see cref="IDiscoveryBackend.StartAddressProbeAsync"/> with the normal first contact, not the import's short one, so a
/// device behind a slow link is found; the expected MAC address and the line's login in the session, the Axis check before
/// any password, then the credential list; at most <see cref="Parallelism"/> devices at once and the authenticator's own
/// login limit) and is stored by <see cref="DeviceAdder"/> like an add from the page: with the login that worked, or as
/// Password not set for a factory-default device, and only while its entry is still the one the round read (a removed
/// entry is never added). Added devices get the line's tags, a first full refresh and the audit entry "Device added
/// automatically". Entries whose device OADM manages by now are removed before each round. Entries of a site that is not
/// online are left alone (not tried, nothing written: they keep their last result) until it is back; entries of a site
/// that no longer exists are removed.
/// <para>Each entry is tried as the user who kept it is now (docs/spec/device-scope.md, Decisions "New devices"): a user with
/// a device group only through the sites of the group (the server's own network only with "This server"), only with the
/// entry's own login (never the credential list), and only a device that lands inside the group; the new device remembers
/// the group. An entry whose user cannot add there any more (another group, no devices, removed) is not tried, nothing is
/// sent to the address (<see cref="AddDeviceTexts.KeeperCannotAdd"/>, held until Try now). An entry kept before device groups
/// existed (no user id) is tried as before.</para>
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
    Core.DeviceGroups.DeviceViewFactory views,
    TimeProvider time,
    ILogger<WaitingDeviceRunner> logger,
    Core.Sites.ISiteStates? sites = null,
    AccessEpochs? epochs = null) : BackgroundService
{
    /// <summary>Waiting devices tried at the same time (the logins are limited by the authenticator as well).</summary>
    public const int Parallelism = 32;

    /// <summary>Outcomes (results and added entries) written in one transaction.</summary>
    private const int ResultBatch = 256;

    /// <summary>The first round after a start.</summary>
    public static readonly TimeSpan FirstRoundDelay = TimeSpan.FromMinutes(1);

    /// <summary>How often a wait checks the retry setting again (a change applies within this time).</summary>
    private static readonly TimeSpan SettingCheck = TimeSpan.FromMinutes(1);

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

        Oadm.Core.Auth.CallerContext.Raise(StateChanged, this, EventArgs.Empty);
        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastRound = time.GetUtcNow();
        var first = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitForRoundAsync(lastRound, first, stoppingToken).ConfigureAwait(false);
                first = false;

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

                lastRound = time.GetUtcNow();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Waits until the next round is due (<paramref name="lastRound"/> + the setting, read again every minute so a change
    /// applies at once) or Try now wakes it.
    /// </summary>
    private async Task WaitForRoundAsync(DateTimeOffset lastRound, bool first, CancellationToken ct)
    {
        while (true)
        {
            var due = lastRound + (first ? FirstRoundDelay : await RetryIntervalAsync(ct).ConfigureAwait(false));
            lock (_gate)
            {
                _nextTry = due;
            }

            Oadm.Core.Auth.CallerContext.Raise(StateChanged, this, EventArgs.Empty);
            var left = due - time.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                return;
            }

            if (await WaitAsync(left < SettingCheck ? left : SettingCheck, ct).ConfigureAwait(false))
            {
                return; // Try now
            }
        }
    }

    /// <summary>
    /// One round: tries <paramref name="ids"/> (null: every entry whose login did not fail, or every entry with
    /// <paramref name="includeLoginFailed"/>), after removing the entries OADM manages by now. Returns how many were added.
    /// </summary>
    public async Task<int> RunRoundAsync(IReadOnlyCollection<Guid>? ids, bool includeLoginFailed, CancellationToken ct)
    {
        _trying = true;
        Oadm.Core.Auth.CallerContext.Raise(StateChanged, this, EventArgs.Empty);
        try
        {
            var entries = await store.ToTryAsync(ids, includeLoginFailed, ct).ConfigureAwait(false);
            if (entries.Count > 0)
            {
                // Devices added another way meanwhile leave the list without a try (read only while something waits;
                // every entry, also failed logins, by address as a device stores it or by the expected MAC address).
                // Per route (docs/spec/sites.md, "Overlapping networks"): an entry of a site compares with that site's devices.
                var managed = await devices.ListAddressesAsync(ct).ConfigureAwait(false);
                var gone = await store.RemoveManagedAsync(managed, ct).ConfigureAwait(false);
                if (gone.Count > 0)
                {
                    var removed = gone.ToHashSet();
                    entries = [.. entries.Where(e => !removed.Contains(e.Id))];
                }

                entries = await LeaveOfflineSitesAsync(entries, ct).ConfigureAwait(false);
            }

            foreach (var entry in entries)
            {
                _checking.TryAdd(entry.Id, 0);
            }

            _checkingAll = false;
            Oadm.Core.Auth.CallerContext.Raise(StateChanged, this, EventArgs.Empty);
            if (entries.Count == 0)
            {
                return 0;
            }

            var results = new ConcurrentQueue<WaitingResult>();
            var done = new ConcurrentQueue<Guid>();
            var added = 0;
            using var flush = new SemaphoreSlim(1);

            // Results and added entries go back in batches; a failed write is logged and the round goes on (the next
            // round tries those entries again).
            async Task FlushAsync(bool force, CancellationToken token)
            {
                if (!force && results.Count + done.Count < ResultBatch)
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
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    LogFlushFailed(ex);
                }
                finally
                {
                    flush.Release();
                }
            }

            // The reach of each user who kept entries, read once per round and again when the user's access or group changed
            // since that read (a round can run for many minutes; the add itself checks again). A user whose reach cannot be
            // read is skipped this round; the other users' entries are tried.
            var reaches = new ConcurrentDictionary<Guid, Lazy<KeeperReach>>();
            await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (entry, token) =>
            {
                var keeper = await CurrentReachAsync(reaches, entry, token).ConfigureAwait(false);
                if (keeper is null)
                {
                    _checking.TryRemove(entry.Id, out _);
                    return;
                }

                var outcome = await TryOneAsync(entry, keeper, token).ConfigureAwait(false);
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
                    case WaitingOutcomeKind.Changed:
                        break; // removed or kept again meanwhile: nothing to write
                    default:
                        results.Enqueue(new WaitingResult(entry.Id, entry.Kept, outcome.Message,
                            outcome.Kind == WaitingOutcomeKind.LoginFailed, outcome.Kind == WaitingOutcomeKind.Held));
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
            Oadm.Core.Auth.CallerContext.Raise(StateChanged, this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The entries a round tries: those of a site that is not online stay as they are (no try, no "Checking", no result
    /// written: an offline site is no news about the device); those of a site that no longer exists are removed.
    /// </summary>
    private async Task<IReadOnlyList<WaitingTry>> LeaveOfflineSitesAsync(IReadOnlyList<WaitingTry> entries, CancellationToken ct)
    {
        if (sites is null || entries.All(e => e.SiteId is null))
        {
            return entries;
        }

        var keep = new List<WaitingTry>(entries.Count);
        var orphans = new List<Guid>();
        foreach (var entry in entries)
        {
            if (entry.SiteId is not { } site || sites.IsOnline(site))
            {
                keep.Add(entry);
            }
            else if (sites.NameOf(site) is null && !sites.IsStarting(site))
            {
                orphans.Add(entry.Id); // the site was removed (or a removed site's tombstone): nothing will reach it
            }
        }

        if (orphans.Count > 0)
        {
            await store.RemoveAsync(orphans, all: false, ct).ConfigureAwait(false);
            LogSiteGone(orphans.Count);
        }

        return keep;
    }

    /// <summary>One try of one waiting device, as the user who kept it is now. Never throws.</summary>
    public async Task<WaitingOutcome> TryOneAsync(WaitingTry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AddReach reach;
        var epoch = epochs?.Capture() ?? 0;
        try
        {
            reach = await AddReach.ForUserAsync(views, entry.QueuedByUserId, entry.AddedBy, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogTryFailed(entry.Address, ex.Message);
            return new WaitingOutcome(WaitingOutcomeKind.StillWaiting, $"No Axis device answered at {entry.Address}.");
        }

        return await TryOneAsync(entry, new KeeperReach(reach, epoch), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The reach of the entry's keeper from this round's cache, read again when the keeper's access or group changed after
    /// the cached read (<see cref="AccessEpochs"/>); null when it cannot be read.
    /// </summary>
    private async Task<KeeperReach?> CurrentReachAsync(ConcurrentDictionary<Guid, Lazy<KeeperReach>> reaches, WaitingTry entry, CancellationToken ct)
    {
        var key = entry.QueuedByUserId ?? Guid.Empty;
        while (true)
        {
            var lazy = reaches.GetOrAdd(key, _ => new Lazy<KeeperReach>(() =>
            {
                var epoch = epochs?.Capture() ?? 0; // before the read: a change during it counts as after
                return new KeeperReach(ReachOrNullAsync(entry, ct), epoch);
            }));
            var keeper = lazy.Value;
            if (await keeper.ReachAsync.ConfigureAwait(false) is not { } reach)
            {
                return null;
            }

            if (!IsStale(keeper, reach))
            {
                return keeper;
            }

            reaches.TryRemove(new KeyValuePair<Guid, Lazy<KeeperReach>>(key, lazy)); // read again
        }
    }

    /// <summary>True when the keeper's access, devices or group changed after <paramref name="reach"/> was read.</summary>
    private bool IsStale(KeeperReach keeper, AddReach reach) =>
        epochs is not null && epochs.ChangedSince(keeper.Epoch, reach.UserId ?? Guid.Empty, null, reach.View.GroupId?.ToString());

    /// <summary>A try ended because the keeper's access changed during it: nothing is written, the next round tries again.</summary>
    private static readonly WaitingOutcome AccessChanged = new(WaitingOutcomeKind.Changed, "The access of the user who kept it changed meanwhile.");

    /// <summary>A keeper's reach (null when it could not be read) and the access epoch captured before the read.</summary>
    private sealed record KeeperReach(Task<AddReach?> ReachAsync, long Epoch)
    {
        public KeeperReach(AddReach reach, long epoch)
            : this(Task.FromResult<AddReach?>(reach), epoch)
        {
        }
    }

    /// <summary>
    /// One try of one waiting device with the reach of the user who kept it. Never throws (one entry's failure must not end
    /// the round for the others). A change of the keeper's access during the try ends it before the next step that sends
    /// anything (the probe, the credential list, a login): nothing is written, the next round tries with the new reach.
    /// </summary>
    private async Task<WaitingOutcome> TryOneAsync(WaitingTry entry, KeeperReach keeper, CancellationToken ct)
    {
        var reach = (await keeper.ReachAsync.ConfigureAwait(false))!;
        if (IsStale(keeper, reach))
        {
            return AccessChanged;
        }

        if (!reach.CanAdd || (entry.SiteId is { } entrySite && !reach.Routes.Allows(entrySite)))
        {
            // The user cannot add devices there any more: nothing is sent to the address, it waits for Try now.
            return new WaitingOutcome(WaitingOutcomeKind.Held, AddDeviceTexts.KeeperCannotAdd);
        }

        string session;
        try
        {
            // The normal first contact: the import's short one is for the page's speed; a retry has time. Through the entry's
            // site only, else the server's own network and every online site that reaches the address (the user's routes).
            session = (await discovery.StartAddressProbeAsync(entry.Address, importLine: false, ct, entry.SiteId, entry.ExpectedSerial, reach.Routes).ConfigureAwait(false)).Id;
        }
        catch (ArgumentException ex)
        {
            return new WaitingOutcome(WaitingOutcomeKind.StillWaiting, ex.Message); // a host name that does not resolve (yet)
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogTryFailed(entry.Address, ex.Message);
            return new WaitingOutcome(WaitingOutcomeKind.StillWaiting, $"No Axis device answered at {entry.Address}.");
        }

        try
        {
            if (IsStale(keeper, reach))
            {
                return AccessChanged;
            }

            if (reach.UsesCredentialList)
            {
                authenticator.AllowCredentialList(session); // a user with a device group: only the entry's own login
            }

            if (entry.ExpectedSerial is { } serial)
            {
                authenticator.SetExpectedSerial(session, serial);
            }

            if (entry.Login is { } login)
            {
                authenticator.AddSessionCredential(session, login.UserName, login.Password);
            }

            // Every device the address answers with, per MAC address (several routes may answer with different devices).
            var answers = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
            string? problem = null;
            await foreach (var e in discovery.WatchAsync(session, ct).ConfigureAwait(false))
            {
                if (e.Device is { } device)
                {
                    answers[device.Serial] = device;
                }
                else if (e.Finished)
                {
                    problem = e.Problem;
                }
            }

            if (answers.Count == 0)
            {
                return new WaitingOutcome(WaitingOutcomeKind.StillWaiting, problem ?? $"No Axis device answered at {entry.Address}.");
            }

            var notManaged = new List<DiscoveredDevice>();
            foreach (var device in answers.Values)
            {
                if (!await adder.IsManagedAsync(device.Serial, ct).ConfigureAwait(false))
                {
                    notManaged.Add(device);
                }
            }

            if (notManaged.Count == 0)
            {
                return new WaitingOutcome(WaitingOutcomeKind.AlreadyManaged, $"{string.Join(", ", answers.Keys)} is managed already.");
            }

            if (notManaged.Count > 1)
            {
                // Different devices OADM does not manage at this address through several sites: which one is meant is not
                // known. Tried again only with Try now (never every round), or kept again with a Site column.
                return new WaitingOutcome(WaitingOutcomeKind.Held, AnswersThrough(notManaged));
            }

            // Exactly one device it does not manage yet (the others answering are managed): that one is meant.

            var found = notManaged[0];
            if (!reach.Takes(found.Serial, found.Route))
            {
                // It would not be in the group of the user who kept it: no login, no password sent.
                return new WaitingOutcome(WaitingOutcomeKind.Held, AddDeviceTexts.KeeperCannotAdd);
            }

            if (IsStale(keeper, reach))
            {
                return AccessChanged; // before the first login
            }

            authenticator.Ensure(session, found, alreadyManaged: false);

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
        // Removed ("OADM stops trying to add them") or kept again with another login while it was tried: not added.
        if (!await store.IsCurrentAsync(entry.Id, entry.Kept, ct).ConfigureAwait(false))
        {
            return new WaitingOutcome(WaitingOutcomeKind.Changed, "Removed or kept again meanwhile.");
        }

        // The user who kept it, as that user is now: the device must still land inside their devices.
        var reach = await AddReach.ForUserAsync(views, entry.QueuedByUserId, entry.AddedBy, ct).ConfigureAwait(false);
        if (!reach.Takes(found.Serial, found.Route))
        {
            return new WaitingOutcome(WaitingOutcomeKind.Held, AddDeviceTexts.KeeperCannotAdd);
        }

        var server = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
        (Guid Id, SdkDeviceStatus Status)? added;
        try
        {
            added = await adder.AddAsync(found, useHostName: false, initialPassword: null, credentials, server.ServerName, reach.AddedBy, ct).ConfigureAwait(false);
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
                // The tag store guards the tags of the queuing user's devices: a user with a group never adds a tag a group uses.
                var viewed = await reach.View.GetAsync([device.Id], ct).ConfigureAwait(false);
                await tags.SetDeviceTagsAsync(viewed, [.. entry.Tags], [], ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TagNameException or TagLimitException or TagNeedsAllDevicesException or Oadm.Sdk.Devices.DevicesNotAvailableException)
            {
                LogTagsFailed(found.Serial, ex.Message);
            }
        }

        var message = device.Status switch
        {
            SdkDeviceStatus.PasswordNotSet => "Added; the device has no password yet (factory default).",
            SdkDeviceStatus.Ok or SdkDeviceStatus.Unknown => "Added and logged in as " + (credentials?.UserName ?? "-") + ".",
            SdkDeviceStatus.SiteOffline => "Added; " + Sdk.Sites.SiteTexts.SiteOffline(null) + ".",
            _ => "Added; the login no longer works on it.",
        };
        LogAdded(found.Serial, found.ConnectAddress, entry.Source);
        await audit.WriteAsync("system", string.Empty, AuditActions.DeviceAddedAutomatically, $"{found.ConnectAddress} ({found.Serial})",
            $"{entry.Source} (waiting list, kept by {entry.AddedBy}): {message}", ct).ConfigureAwait(false);
        await MembershipAudit.AddedAsync(views, audit, "system", string.Empty, [device.Id], "waiting list, kept by " + entry.AddedBy, ct).ConfigureAwait(false);
        return new WaitingOutcome(WaitingOutcomeKind.Added, message);
    }

    /// <summary>"Answers through Building A and Building B." (the server's own network is "this server").</summary>
    internal string AnswersThrough(IEnumerable<DiscoveredDevice> found)
    {
        var names = found.Select(d => d.Route.SiteId is { } site ? sites?.NameOf(site) ?? site.ToString() : Contracts.Sites.SiteDiscoveryTexts.ThisServer)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return Contracts.Sites.SiteDiscoveryTexts.AnswersThrough(names);
    }

    /// <summary>Devices.WaitingRetryMinutes; the default when the setting cannot be read (a failure here never ends the loop,
    /// an exception escaping a hosted service would stop the server).</summary>
    private async Task<TimeSpan> RetryIntervalAsync(CancellationToken ct)
    {
        try
        {
            var current = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
            return TimeSpan.FromMinutes(Math.Clamp(current.WaitingRetryMinutes, ServerSettings.MinWaitingRetryMinutes, ServerSettings.MaxWaitingRetryMinutes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogRoundFailed(ex);
            return TimeSpan.FromMinutes(ServerSettings.DefaultWaitingRetryMinutes);
        }
    }

    /// <summary>Waits <paramref name="delay"/> or until Try now; true when Try now woke it.</summary>
    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wake = _wake.WaitAsync(linked.Token);
        var timer = Task.Delay(delay, time, linked.Token);
        var first = await Task.WhenAny(wake, timer).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var woken = first == wake && wake.IsCompletedSuccessfully;
        if (woken)
        {
            while (_wake.CurrentCount > 0)
            {
                await _wake.WaitAsync(ct).ConfigureAwait(false); // several Try now in a row: one round
            }
        }

        return woken;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting list: {Count} entries of removed sites removed")]
    private partial void LogSiteGone(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting list: {Tried} devices tried, {Added} added")]
    private partial void LogRound(int tried, int added);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting device {Serial} at {Address} added ({Source})")]
    private partial void LogAdded(string serial, string address, string source);

    /// <summary>The reach of the entry's keeper now, or null when it cannot be read (logged; the entry waits for the next round).</summary>
    private async Task<AddReach?> ReachOrNullAsync(WaitingTry entry, CancellationToken ct)
    {
        try
        {
            return await (ReachOf ?? AddReach.ForUserAsync)(views, entry.QueuedByUserId, entry.AddedBy, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogReachFailed(ex, entry.AddedBy);
            return null;
        }
    }

    /// <summary>Tests: reads a keeper's reach instead of <see cref="AddReach.ForUserAsync"/>.</summary>
    internal Func<Core.DeviceGroups.DeviceViewFactory, Guid?, string, CancellationToken, Task<AddReach>>? ReachOf { get; set; }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waiting list: the devices of {UserName} could not be read; their entries wait for the next round")]
    private partial void LogReachFailed(Exception ex, string userName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waiting list round failed")]
    private partial void LogRoundFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing waiting list results failed")]
    private partial void LogFlushFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Trying waiting device {Address} failed: {Reason}")]
    private partial void LogTryFailed(string address, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tags of the added waiting device {Serial} were not set: {Reason}")]
    private partial void LogTagsFailed(string serial, string reason);
}
