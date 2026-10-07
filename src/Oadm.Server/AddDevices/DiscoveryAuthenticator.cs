using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Oadm.Core.Discovery;
using Oadm.Core.Security;
using Oadm.Core.Vapix;

using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.AddDevices;

/// <summary>Outcome of the automatic login of a discovered device.</summary>
public enum DeviceAuthState
{
    /// <summary>Not probed yet, or a login is running.</summary>
    Pending,

    /// <summary>A known credential (or one the technician entered) worked.</summary>
    Authenticated,

    /// <summary>Factory default: no admin password yet.</summary>
    PasswordNotSet,

    /// <summary>No known credential worked.</summary>
    LoginFailed,

    /// <summary>The device did not answer.</summary>
    Unreachable,

    /// <summary>The serial is managed already.</summary>
    AlreadyAdded,
}

/// <summary>What the add page shows for a discovered device. Never contains a password.</summary>
/// <param name="State">Login outcome.</param>
/// <param name="UserName">User of the credential that worked.</param>
/// <param name="CredentialId">"list:&lt;id&gt;", "device:&lt;id&gt;" or "entered".</param>
/// <param name="Detail">User facing reason for LoginFailed and Unreachable.</param>
/// <param name="PassphrasePolicy">Factory default only: systemready passphrasepolicy.</param>
public sealed record DeviceAuthResult(
    DeviceAuthState State,
    string? UserName = null,
    string? CredentialId = null,
    string? Detail = null,
    string? PassphrasePolicy = null)
{
    public static DeviceAuthResult Pending { get; } = new(DeviceAuthState.Pending);
}

/// <summary>
/// Logs in to discovered devices automatically, server side, with the technician's known
/// credentials: first the encrypted credential list (Settings page, in the order added), then the
/// distinct credentials stored for managed devices (most used first), at most
/// <see cref="MaxAttemptsPerDevice"/> per device and one attempt at a time per device so a
/// brute-force protection on the device is never triggered by OADM. One attempt is
/// <c>basicdeviceinfo getAllProperties</c> with the credential (plus <c>param.cgi</c> network
/// parameters on devices with anonymous access, where the first call proves nothing); a different
/// serial at the address counts as unreachable. Results are kept per discovery session and serial
/// (sessions expire <see cref="SessionLifetime"/> after their last use) together with the
/// credential that worked, which <see cref="AddDevicesGrpcService.Commit"/> stores for the new
/// device. Passwords stay in server memory and the database; results carry only user names.
/// </summary>
public sealed partial class DiscoveryAuthenticator : IDisposable
{
    /// <summary>Most known credentials tried on one device.</summary>
    public const int MaxAttemptsPerDevice = 10;

    /// <summary>Devices logged in to at the same time.</summary>
    public const int Parallelism = 8;

    /// <summary>Credential id of credentials the technician typed (RetryAuth) and did not save.</summary>
    public const string EnteredCredentialId = "entered";

    private readonly CredentialStore _deviceCredentials;
    private readonly CredentialListStore _credentialList;
    private readonly IVapixConnector _connector;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscoveryAuthenticator> _logger;
    private readonly ConcurrentDictionary<string, AuthSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _parallel = new(Parallelism);
    private readonly CancellationTokenSource _stopping = new();

    public DiscoveryAuthenticator(
        CredentialStore deviceCredentials,
        CredentialListStore credentialList,
        IVapixConnector connector,
        TimeProvider time,
        ILogger<DiscoveryAuthenticator> logger)
    {
        _deviceCredentials = deviceCredentials;
        _credentialList = credentialList;
        _connector = connector;
        _time = time;
        _logger = logger;
        _credentialList.Changed += OnCredentialListChanged;
    }

    /// <summary>Timeout of one login request.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Auth results of a session are forgotten this long after the session was last used.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>The current result for a serial of a session (Pending when unknown).</summary>
    public DeviceAuthResult Get(string sessionId, string serial) =>
        _sessions.TryGetValue(sessionId, out var session) ? session.Get(serial)?.Result ?? DeviceAuthResult.Pending : DeviceAuthResult.Pending;

    /// <summary>The credentials that worked for a serial of a session, server side only.</summary>
    internal DeviceCredentials? MatchedCredentials(string sessionId, string serial) =>
        _sessions.TryGetValue(sessionId, out var session) ? session.Get(serial)?.Credentials : null;

    /// <summary>
    /// Called for every discovery event: starts the automatic login once per device state
    /// (address, scheme, probe status). Managed serials become AlreadyAdded without a login.
    /// </summary>
    public void Ensure(string sessionId, DiscoveredDevice device, bool alreadyManaged)
    {
        ArgumentNullException.ThrowIfNull(device);
        var session = Session(sessionId);
        var key = string.Join('|', device.ConnectAddress, device.Scheme, device.Status, alreadyManaged);
        int version;
        lock (session.Gate)
        {
            var entry = session.Get(device.Serial);
            if (entry is not null && (entry.Key == key || entry.Result.State == DeviceAuthState.Authenticated && !alreadyManaged))
            {
                return; // same state, or a login already worked (keep it, also when the address follows a new observation)
            }

            version = session.NextVersion();
            var initial = alreadyManaged
                ? new DeviceAuthResult(DeviceAuthState.AlreadyAdded)
                : device.Status switch
                {
                    DiscoveredDeviceStatus.PasswordNotSet => new DeviceAuthResult(DeviceAuthState.PasswordNotSet),
                    DiscoveredDeviceStatus.Unreachable => new DeviceAuthResult(DeviceAuthState.Unreachable, Detail: "The device did not answer on HTTPS or HTTP."),
                    _ => DeviceAuthResult.Pending,
                };
            // Rejected credentials stay counted for the serial (lockout protection survives a new address).
            session.Set(device.Serial, new AuthEntry(key, version, initial, null, device, entry?.Attempts ?? 0, entry?.Tried ?? []));
        }

        session.Notify(device.Serial);
        if (alreadyManaged)
        {
            return;
        }

        switch (device.Status)
        {
            case DiscoveredDeviceStatus.PasswordNotSet:
                session.Track(Task.Run(() => ReadPolicyAsync(session, device, version, _stopping.Token)));
                break;
            case DiscoveredDeviceStatus.CredentialsRequired or DiscoveredDeviceStatus.AnonymousAccess:
                session.Track(Task.Run(() => TryKnownCredentialsAsync(session, device, version, _stopping.Token)));
                break;
        }
    }

    /// <summary>
    /// Logs in with credentials the technician typed, right away. On success the credential is kept
    /// for the add (and stored in the credential list when <paramref name="saveToList"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is factory default (nothing to log in to).</exception>
    public Task<DeviceAuthResult> RetryAsync(string sessionId, DiscoveredDevice device, string userName, string password, bool saveToList, CancellationToken ct) =>
        RetryAsync(sessionId, device, userName, password, saveToList, [], ct);

    /// <summary>
    /// Logs in with credentials the technician typed, right away. On success the credential is kept
    /// for the add (and stored in the credential list when <paramref name="saveToList"/>), and it is
    /// tried on every device of this session and of <paramref name="relatedSessionIds"/> (the other
    /// sessions of the same add page) whose login failed: at most <see cref="MaxAttemptsPerDevice"/>
    /// rejected credentials per device, one login at a time per device. Those devices show Pending
    /// before this method returns; their results reach the session watchers.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is factory default (nothing to log in to).</exception>
    public async Task<DeviceAuthResult> RetryAsync(
        string sessionId,
        DiscoveredDevice device,
        string userName,
        string password,
        bool saveToList,
        IEnumerable<string> relatedSessionIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(relatedSessionIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (device.Status == DiscoveredDeviceStatus.PasswordNotSet)
        {
            throw new InvalidOperationException("The device has no password yet (factory default); set its first password instead.");
        }

        var session = Session(sessionId);
        int version;
        var key = string.Join('|', device.ConnectAddress, device.Scheme, device.Status, false);
        AuthEntry? before;
        lock (session.Gate)
        {
            before = session.Get(device.Serial);
            version = session.NextVersion();
            session.Set(device.Serial, new AuthEntry(key, version, DeviceAuthResult.Pending, null, device, before?.Attempts ?? 0, before?.Tried ?? []));
        }

        session.Notify(device.Serial);
        var user = userName.Trim();
        var credentials = new DeviceCredentials(user, password);
        var outcome = await TryOneAsync(device, user, password, ct).ConfigureAwait(false);
        DeviceAuthResult result;
        if (outcome.Kind == AttemptKind.Ok)
        {
            var credentialId = EnteredCredentialId;
            if (saveToList)
            {
                try
                {
                    var saved = await _credentialList.AddAsync(user, password, ct).ConfigureAwait(false);
                    credentialId = "list:" + saved.Id.ToString("N");
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    LogSaveFailed(device.Serial, ex.Message);
                }
            }

            result = new DeviceAuthResult(DeviceAuthState.Authenticated, user, credentialId);
            Apply(session, device.Serial, version, result, credentials);
        }
        else
        {
            result = outcome.Kind == AttemptKind.Rejected
                ? new DeviceAuthResult(DeviceAuthState.LoginFailed, Detail: "The user name or password is wrong.")
                : new DeviceAuthResult(DeviceAuthState.Unreachable, Detail: outcome.Detail);
            if (outcome.Kind == AttemptKind.Rejected)
            {
                // Typed by the technician: not counted against the automatic attempts, but never tried again automatically.
                RecordRejected(session, device.Serial, version, credentials, countAttempt: false);
            }

            Apply(session, device.Serial, version, result, null);
        }

        LogRetry(device.Serial, result.State);
        if (result.State == DeviceAuthState.Authenticated)
        {
            // A working credential: the other devices of the add page whose login failed get it too.
            var candidate = new Candidate(result.CredentialId ?? EnteredCredentialId, credentials);
            var targets = new List<AuthSession> { session };
            foreach (var related in relatedSessionIds.Where(id => !string.IsNullOrWhiteSpace(id) && id != sessionId).Distinct(StringComparer.Ordinal))
            {
                if (_sessions.TryGetValue(related, out var other))
                {
                    targets.Add(other);
                }
            }

            foreach (var target in targets)
            {
                target.AddEntered(candidate);
                await StartFollowUpsAsync(target).ConfigureAwait(false);
            }
        }

        return result;
    }

    /// <summary>
    /// Starts a login with the credentials not tried yet (credential list, entered ones) on every device
    /// of the session whose login failed and that has attempts left. Claimed devices show Pending.
    /// </summary>
    private async Task StartFollowUpsAsync(AuthSession session)
    {
        IReadOnlyList<Candidate> candidates;
        try
        {
            candidates = await AllCandidatesAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return; // server stopping
        }

        var claimed = new List<(DiscoveredDevice Device, int Version)>();
        lock (session.Gate)
        {
            foreach (var (serial, entry) in session.Entries())
            {
                if (entry.Result.State != DeviceAuthState.LoginFailed || entry.Device is null
                    || entry.Attempts >= MaxAttemptsPerDevice || !candidates.Any(c => !entry.Tried.Contains(KeyOf(c.Credentials))))
                {
                    continue;
                }

                var version = session.NextVersion();
                session.Set(serial, entry with { Version = version, Result = DeviceAuthResult.Pending, Credentials = null });
                claimed.Add((entry.Device, version));
            }
        }

        foreach (var (device, version) in claimed)
        {
            session.Notify(device.Serial);
            session.Track(Task.Run(() => TryKnownCredentialsAsync(session, device, version, _stopping.Token)));
        }

        if (claimed.Count > 0)
        {
            LogFollowUp(claimed.Count);
        }
    }

    /// <summary>Entered credentials first (they just worked on a device of the page), then the known ones.</summary>
    private async Task<IReadOnlyList<Candidate>> AllCandidatesAsync(AuthSession session)
    {
        var known = await session.CandidatesAsync(LoadCandidatesAsync).ConfigureAwait(false);
        return [.. session.Entered(), .. known];
    }

    /// <summary>Identity of a credential for "already tried" checks; the password itself is not kept twice.</summary>
    private static string KeyOf(DeviceCredentials credentials) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.UserName + "\n" + credentials.Password)));

    /// <summary>Marks serials as added (after Commit), so open pages grey them out.</summary>
    public void MarkAdded(string sessionId, IEnumerable<string> serials)
    {
        ArgumentNullException.ThrowIfNull(serials);
        var session = Session(sessionId);
        foreach (var serial in serials)
        {
            lock (session.Gate)
            {
                var old = session.Get(serial);
                session.Set(serial, new AuthEntry((old?.Key ?? string.Empty) + "|added", session.NextVersion(), new DeviceAuthResult(DeviceAuthState.AlreadyAdded), null));
            }

            session.Notify(serial);
        }
    }

    /// <summary>Calls <paramref name="onChanged"/> with the serial whenever a result of the session changes.</summary>
    public IDisposable Subscribe(string sessionId, Action<string> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        var session = Session(sessionId);
        session.Changed += onChanged;
        return new Unsubscriber(() => session.Changed -= onChanged);
    }

    /// <summary>Completes when no login of the session is running anymore.</summary>
    public async Task WhenIdleAsync(string sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        while (session.Running() is { Length: > 0 } running)
        {
            await Task.WhenAll(running).WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _credentialList.Changed -= OnCredentialListChanged;
        _stopping.Cancel();
        _stopping.Dispose();
        _parallel.Dispose();
    }

    private AuthSession Session(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var now = _time.GetUtcNow();
        foreach (var (id, old) in _sessions)
        {
            if (now - old.LastUsed > SessionLifetime && old.Running().Length == 0)
            {
                _sessions.TryRemove(id, out _);
            }
        }

        var session = _sessions.GetOrAdd(sessionId, _ => new AuthSession());
        session.LastUsed = now;
        return session;
    }

    /// <summary>A credential was added to (or removed from) the list: reload it and try new ones on failed devices.</summary>
    private void OnCredentialListChanged(object? sender, EventArgs e)
    {
        foreach (var session in _sessions.Values)
        {
            session.ResetCandidates();
            session.Track(Task.Run(() => StartFollowUpsAsync(session)));
        }
    }

    /// <summary>
    /// Tries the credentials the device has not rejected yet, in order (entered ones, credential list,
    /// credentials of managed devices), until one works, the device does not answer or it rejected
    /// <see cref="MaxAttemptsPerDevice"/> of them. Candidates are read again before every attempt, so a
    /// credential added meanwhile is tried in the same run.
    /// </summary>
    private async Task TryKnownCredentialsAsync(AuthSession session, DiscoveredDevice device, int version, CancellationToken ct)
    {
        try
        {
            await _parallel.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var entry = session.Get(device.Serial);
                    if (entry is null || entry.Version != version)
                    {
                        return; // superseded by a retry or a new observation
                    }

                    var candidates = await AllCandidatesAsync(session).ConfigureAwait(false);
                    var candidate = entry.Attempts >= MaxAttemptsPerDevice
                        ? null
                        : candidates.FirstOrDefault(c => !entry.Tried.Contains(KeyOf(c.Credentials)));
                    if (candidate is null)
                    {
                        var detail = entry.Attempts switch
                        {
                            0 => "No known credentials. Log in once, or add credentials on the Settings page.",
                            1 => "The known credential did not work.",
                            _ => string.Create(CultureInfo.InvariantCulture, $"None of the {entry.Attempts} known credentials worked."),
                        };
                        Apply(session, device.Serial, version, new DeviceAuthResult(DeviceAuthState.LoginFailed, Detail: detail), null);
                        LogLoginFailed(device.Serial, entry.Attempts);
                        return;
                    }

                    var outcome = await TryOneAsync(device, candidate.Credentials.UserName, candidate.Credentials.Password, ct).ConfigureAwait(false);
                    if (outcome.Kind == AttemptKind.Ok)
                    {
                        Apply(session, device.Serial, version, new DeviceAuthResult(DeviceAuthState.Authenticated, candidate.Credentials.UserName, candidate.Id), candidate.Credentials);
                        LogAuthenticated(device.Serial, candidate.Credentials.UserName, candidate.Id);
                        return;
                    }

                    if (outcome.Kind == AttemptKind.Unreachable)
                    {
                        Apply(session, device.Serial, version, new DeviceAuthResult(DeviceAuthState.Unreachable, Detail: outcome.Detail), null);
                        return;
                    }

                    RecordRejected(session, device.Serial, version, candidate.Credentials, countAttempt: true);
                }
            }
            finally
            {
                _parallel.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
            // server stopping
        }
        catch (Exception ex)
        {
            LogAuthError(ex, device.Serial);
            Apply(session, device.Serial, version, new DeviceAuthResult(DeviceAuthState.LoginFailed, Detail: "The login could not be checked: " + ex.Message), null);
        }
    }

    private async Task ReadPolicyAsync(AuthSession session, DiscoveredDevice device, int version, CancellationToken ct)
    {
        try
        {
            using var client = _connector.Connect(new VapixConnectionOptions
            {
                Address = device.ConnectAddress,
                Scheme = device.Scheme ?? Uri.UriSchemeHttps,
                Timeout = AttemptTimeout,
            });
            var ready = await client.GetSystemReadyAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ready.PassphrasePolicy))
            {
                Apply(session, device.Serial, version, new DeviceAuthResult(DeviceAuthState.PasswordNotSet, PassphrasePolicy: ready.PassphrasePolicy.Trim()), null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The hint is optional; the device still enforces its own policy on add.
            LogPolicyFailed(device.Serial, ex.Message);
        }
    }

    /// <summary>Credential list first (in the order added), then the distinct credentials of managed devices.</summary>
    private async Task<IReadOnlyList<Candidate>> LoadCandidatesAsync()
    {
        var ct = _stopping.Token;
        var result = new List<Candidate>();
        var seen = new HashSet<(string, string)>();
        foreach (var entry in await _credentialList.GetAllAsync(ct).ConfigureAwait(false))
        {
            if (seen.Add((entry.UserName, entry.Password)))
            {
                result.Add(new Candidate("list:" + entry.Id.ToString("N"), new DeviceCredentials(entry.UserName, entry.Password)));
            }
        }

        foreach (var (deviceId, credentials) in await _deviceCredentials.ListDistinctAsync(ct).ConfigureAwait(false))
        {
            if (seen.Add((credentials.UserName, credentials.Password)))
            {
                result.Add(new Candidate("device:" + deviceId.ToString("N"), credentials));
            }
        }

        return result;
    }

    /// <summary>One login: basicdeviceinfo with the credential, plus network parameters on devices with anonymous access.</summary>
    private async Task<Attempt> TryOneAsync(DiscoveredDevice device, string userName, string password, CancellationToken ct)
    {
        try
        {
            using var client = _connector.Connect(new VapixConnectionOptions
            {
                Address = device.ConnectAddress,
                Scheme = device.Scheme ?? Uri.UriSchemeHttps,
                Credentials = new NetworkCredential(userName, password),
                Timeout = AttemptTimeout,
            });
            var info = await client.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            if (!string.Equals(info.SerialNumber, device.Serial, StringComparison.OrdinalIgnoreCase))
            {
                return new Attempt(AttemptKind.Unreachable, $"The address now answers as another device ({info.SerialNumber}).");
            }

            if (device.Status == DiscoveredDeviceStatus.AnonymousAccess)
            {
                await client.GetNetworkInfoAsync(ct).ConfigureAwait(false);
            }

            return new Attempt(AttemptKind.Ok, null);
        }
        catch (VapixAuthenticationException)
        {
            return new Attempt(AttemptKind.Rejected, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return DeviceStatusClassifier.FromException(ex) switch
            {
                SdkDeviceStatus.CredentialsRequired => new Attempt(AttemptKind.Rejected, null),
                SdkDeviceStatus.Unreachable => new Attempt(AttemptKind.Unreachable, "The device did not answer: " + ex.Message),
                _ => new Attempt(AttemptKind.Unreachable, "The login failed: " + ex.Message),
            };
        }
    }

    private static void Apply(AuthSession session, string serial, int version, DeviceAuthResult result, DeviceCredentials? credentials)
    {
        AuthEntry? updated = null;
        lock (session.Gate)
        {
            var entry = session.Get(serial);
            if (entry is not null && entry.Version == version)
            {
                updated = entry with { Result = result, Credentials = credentials };
                session.Set(serial, updated);
            }
        }

        if (updated is not null)
        {
            session.Notify(serial);
        }
    }

    /// <summary>Remembers a rejected credential for the serial (and counts it as an automatic attempt).</summary>
    private static void RecordRejected(AuthSession session, string serial, int version, DeviceCredentials credentials, bool countAttempt)
    {
        lock (session.Gate)
        {
            var entry = session.Get(serial);
            if (entry is not null && entry.Version == version)
            {
                session.Set(serial, entry with { Attempts = entry.Attempts + (countAttempt ? 1 : 0), TriedKeys = entry.Tried.Add(KeyOf(credentials)) });
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Discovered device {Serial}: logged in as {UserName} ({CredentialId})")]
    private partial void LogAuthenticated(string serial, string userName, string credentialId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discovered device {Serial}: none of {Tried} known credential(s) worked")]
    private partial void LogLoginFailed(string serial, int tried);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trying new credentials on {Count} discovered device(s) whose login failed")]
    private partial void LogFollowUp(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discovered device {Serial}: login with entered credentials: {State}")]
    private partial void LogRetry(string serial, DeviceAuthState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discovered device {Serial}: the credential could not be saved to the credential list: {Reason}")]
    private partial void LogSaveFailed(string serial, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discovered device {Serial}: passphrase policy not read: {Reason}")]
    private partial void LogPolicyFailed(string serial, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discovered device {Serial}: automatic login failed")]
    private partial void LogAuthError(Exception ex, string serial);

    private enum AttemptKind
    {
        Ok,
        Rejected,
        Unreachable,
    }

    private sealed record Attempt(AttemptKind Kind, string? Detail);

    private sealed record Candidate(string Id, DeviceCredentials Credentials);

    /// <param name="Device">The device as last observed (for logins started later).</param>
    /// <param name="Attempts">Credentials the device rejected in automatic logins.</param>
    /// <param name="TriedKeys">Credentials (<see cref="KeyOf"/>) the device rejected, automatic or typed.</param>
    private sealed record AuthEntry(
        string Key,
        int Version,
        DeviceAuthResult Result,
        DeviceCredentials? Credentials,
        DiscoveredDevice? Device = null,
        int Attempts = 0,
        ImmutableHashSet<string>? TriedKeys = null)
    {
        public ImmutableHashSet<string> Tried => TriedKeys ?? [];
    }

    private sealed class AuthSession
    {
        private readonly Dictionary<string, AuthEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Task> _running = [];
        private Task<IReadOnlyList<Candidate>>? _candidates;
        private readonly List<Candidate> _entered = [];
        private int _version;

        public Lock Gate { get; } = new();

        public DateTimeOffset LastUsed { get; set; }

        public event Action<string>? Changed;

        public AuthEntry? Get(string serial)
        {
            lock (Gate)
            {
                return _entries.GetValueOrDefault(serial);
            }
        }

        public void Set(string serial, AuthEntry entry) => _entries[serial] = entry;

        /// <summary>Snapshot of all entries; call under <see cref="Gate"/>.</summary>
        public List<KeyValuePair<string, AuthEntry>> Entries() => [.. _entries];

        /// <summary>Credentials typed on the add page that worked on a device (kept for follow-up logins).</summary>
        public void AddEntered(Candidate candidate)
        {
            lock (Gate)
            {
                if (!_entered.Exists(c => KeyOf(c.Credentials) == KeyOf(candidate.Credentials)))
                {
                    _entered.Add(candidate);
                }
            }
        }

        public Candidate[] Entered()
        {
            lock (Gate)
            {
                return [.. _entered];
            }
        }

        public int NextVersion() => ++_version;

        public bool IsCurrent(string serial, int version)
        {
            lock (Gate)
            {
                return _entries.TryGetValue(serial, out var entry) && entry.Version == version;
            }
        }

        public void Notify(string serial) => Changed?.Invoke(serial);

        public void Track(Task task)
        {
            lock (Gate)
            {
                _running.RemoveAll(t => t.IsCompleted);
                _running.Add(task);
            }
        }

        public Task[] Running()
        {
            lock (Gate)
            {
                _running.RemoveAll(t => t.IsCompleted);
                return [.. _running];
            }
        }

        /// <summary>Loaded once per session (and again after the credential list changed).</summary>
        public Task<IReadOnlyList<Candidate>> CandidatesAsync(Func<Task<IReadOnlyList<Candidate>>> load)
        {
            lock (Gate)
            {
                if (_candidates is null || _candidates.IsFaulted || _candidates.IsCanceled)
                {
                    _candidates = load();
                }

                return _candidates;
            }
        }

        public void ResetCandidates()
        {
            lock (Gate)
            {
                _candidates = null;
            }
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
