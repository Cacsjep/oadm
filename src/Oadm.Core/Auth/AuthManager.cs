using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Persistence;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Auth;

/// <summary>A successful login: the token (handed to the client once) and the user.</summary>
public sealed record LoginResult(string Token, UserRecord User);

/// <summary>
/// Logins, the first administrator and the setup code. Wrong passwords, unknown and disabled users get the same
/// answer and take the same time; failures are throttled per user name (<see cref="LoginThrottle"/>) and written to
/// the audit log. A name or password longer than a user can have (<see cref="UserStore.MaxUserNameLength"/>,
/// <see cref="UserStore.MaxPasswordLength"/>) is a wrong login like any other: never looked up, never hashed, and only
/// the first characters of the name, without control characters, reach the throttle, the audit log and the server log.
/// Failed logins with unknown user names are one audit entry per client address and minute, with their count.
/// </summary>
public sealed partial class AuthManager(
    UserStore users,
    AuthTokenStore tokens,
    PasswordHasher hasher,
    LoginThrottle throttle,
    AuditLog audit,
    OadmPaths paths,
    ILogger<AuthManager>? logger = null,
    TimeProvider? time = null) : IDisposable
{
    public const string SetupCodeFileName = "setup-code.txt";
    public const string WrongLoginMessage = "The user name or password is wrong.";

    /// <summary>Throttle key of setup code attempts (not a valid user name: it contains a control character).</summary>
    private const string SetupCodeThrottleKey = "\u0001setup-code";

    private const string SetupAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>A setup code longer than this is wrong (the real one has 14 characters).</summary>
    public const int MaxSetupCodeLength = 64;

    /// <summary>Audit detail of a failed login with a user name nobody has.</summary>
    public const string UnknownUserDetail = "Unknown user";

    /// <summary>Client addresses whose running audit entry of unknown user names is remembered.</summary>
    public const int MaxUnknownLoginAddresses = 1_000;

    /// <summary>Failed logins with unknown user names from one client address within this time are one audit entry.</summary>
    public static readonly TimeSpan UnknownLoginsEntry = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _unknownGate = new(1, 1);
    private readonly Dictionary<string, UnknownLogins> _unknown = new(StringComparer.Ordinal);

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly Lock _setupGate = new();
    private string? _setupCode;

    /// <summary>Path of the setup code file in the data folder (exists only while no user exists).</summary>
    public string SetupCodePath => Path.Combine(paths.DataDirectory, SetupCodeFileName);

    /// <summary>
    /// Startup: while no user exists, creates a new one-time setup code, writes it to <see cref="SetupCodePath"/>
    /// (owner-only on Unix) and logs it. Once users exist, deletes a leftover file.
    /// </summary>
    public async Task PrepareFirstAdminAsync(CancellationToken ct)
    {
        if (await users.HasUsersAsync(ct).ConfigureAwait(false))
        {
            DeleteSetupCode();
            return;
        }

        var bytes = RandomNumberGenerator.GetBytes(12);
        var chars = bytes.Select(b => SetupAlphabet[b % SetupAlphabet.Length]).ToArray();
        var code = string.Join('-', new string(chars, 0, 4), new string(chars, 4, 4), new string(chars, 8, 4));
        lock (_setupGate)
        {
            _setupCode = code;
        }

        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            await File.WriteAllTextAsync(SetupCodePath, code + Environment.NewLine, ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(SetupCodePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSetupCodeFileFailed(ex, SetupCodePath);
        }

        LogNoAdministrator(code, SetupCodePath);
    }

    /// <summary>Logs in; throws <see cref="AuthException"/> (NotAuthenticated, Locked).</summary>
    public async Task<LoginResult> LoginAsync(string userName, string password, bool remember, string clientAddress, CancellationToken ct)
    {
        // The limits first: a text longer than a user name or a password can be is never looked up, hashed or kept.
        var name = (userName ?? string.Empty).Trim();
        var possible = name.Length is > 0 and <= UserStore.MaxUserNameLength && !name.Any(char.IsControl);
        name = ShownName(name); // the throttle key and what the audit and the server log hold
        var passwordFits = (password?.Length ?? 0) <= UserStore.MaxPasswordLength;
        password = passwordFits ? password ?? string.Empty : string.Empty;
        if (throttle.LockedUntil(name) is { } until)
        {
            throw Locked(name, until);
        }

        var user = possible ? await users.FindByNameAsync(name, ct).ConfigureAwait(false) : null;
        var ok = false;
        if (user is null || !passwordFits)
        {
            hasher.VerifyDummy(password); // a password no user can have takes the same time whether the name exists or not
        }
        else
        {
            ok = PasswordHasher.Verify(password, user.PasswordHash) && !user.Disabled;
        }

        if (!ok || user is null)
        {
            var locked = throttle.RecordFailure(name);
            if (user is null)
            {
                await AuditUnknownAsync(name, clientAddress, ct).ConfigureAwait(false);
            }
            else
            {
                await audit.WriteAsync(name, clientAddress, AuditActions.LoginFailed, name, user.Disabled ? "User disabled" : "Wrong password", ct).ConfigureAwait(false);
            }

            LogLoginFailed(name, clientAddress);
            if (locked)
            {
                await audit.WriteAsync(name, clientAddress, AuditActions.LoginLocked, name,
                    $"{LoginThrottle.MaxFailures} failed logins, locked for {LoginThrottle.LockDuration.TotalMinutes:0} minutes", ct).ConfigureAwait(false);
                LogLocked(name, clientAddress);
            }

            throw new AuthException(AuthError.NotAuthenticated, WrongLoginMessage);
        }

        throttle.RecordSuccess(name);
        return await StartSessionAsync(user, remember, clientAddress, AuditActions.LoginOk, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the first administrator and logs it in. Allowed only while no user exists, and only from a loopback
    /// client or with the setup code.
    /// </summary>
    public async Task<LoginResult> CreateFirstAdminAsync(string userName, string password, string? setupCode, bool isLoopback, bool remember, string clientAddress, CancellationToken ct)
    {
        if (await users.HasUsersAsync(ct).ConfigureAwait(false))
        {
            throw new AuthException(AuthError.Precondition, "The first administrator exists already. Log in instead.");
        }

        if (!isLoopback)
        {
            if (throttle.LockedUntil(SetupCodeThrottleKey) is { } until)
            {
                throw Locked("the setup code", until);
            }

            if (!SetupCodeMatches(setupCode))
            {
                throttle.RecordFailure(SetupCodeThrottleKey);
                await audit.WriteAsync(ShownName(userName), clientAddress, AuditActions.LoginFailed, "setup code", "Wrong setup code", ct).ConfigureAwait(false);
                throw new AuthException(AuthError.PermissionDenied,
                    string.IsNullOrWhiteSpace(setupCode)
                        ? "Enter the setup code. The server shows it at startup and keeps it in " + SetupCodePath + "."
                        : "The setup code is wrong.");
            }
        }

        var user = await users.CreateAsync(userName ?? string.Empty, password ?? string.Empty, UserRole.Admin, onlyIfNoUsers: true, ct).ConfigureAwait(false);
        DeleteSetupCode();
        LogFirstAdmin(user.UserName, clientAddress);
        await audit.WriteAsync(user.UserName, clientAddress, AuditActions.FirstAdmin, user.UserName, null, ct).ConfigureAwait(false);
        return await StartSessionAsync(user, remember, clientAddress, AuditActions.LoginOk, ct).ConfigureAwait(false);
    }

    private async Task<LoginResult> StartSessionAsync(UserEntity user, bool remember, string clientAddress, string action, CancellationToken ct)
    {
        var token = await tokens.CreateAsync(user, remember, clientAddress, ct).ConfigureAwait(false);
        await users.TouchLoginAsync(user.Id, ct).ConfigureAwait(false);
        await audit.WriteAsync(user.UserName, clientAddress, action, user.UserName, remember ? "Remember me" : null, ct).ConfigureAwait(false);
        LogLoggedIn(user.UserName, clientAddress);
        return new LoginResult(token, UserRecord.From(user) with { LastLoginUtc = DateTime.UtcNow });
    }

    public void Dispose() => _unknownGate.Dispose();

    /// <summary>The name as the throttle, the audit and the server log take it: its first characters, without control characters.</summary>
    private static string ShownName(string? userName)
    {
        var name = (userName ?? string.Empty).Trim();
        return AuditLog.Clean(name.Length > UserStore.MaxUserNameLength ? name[..UserStore.MaxUserNameLength] : name);
    }

    /// <summary>
    /// The audit entry of a failed login with an unknown user name: the first of an address is written as it is, the
    /// following ones of the next minute only raise its count, so made-up names never push the history out of the log.
    /// </summary>
    private async Task AuditUnknownAsync(string name, string clientAddress, CancellationToken ct)
    {
        await _unknownGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_unknown.TryGetValue(clientAddress, out var entry) && _time.GetElapsedTime(entry.Start) < UnknownLoginsEntry)
            {
                entry.Count++;
                await audit.SetDetailAsync(entry.Id, string.Create(CultureInfo.InvariantCulture, $"{entry.Count} failed logins with unknown user names"), ct).ConfigureAwait(false);
                return;
            }

            if (_unknown.Count >= MaxUnknownLoginAddresses)
            {
                foreach (var ended in _unknown.Where(p => _time.GetElapsedTime(p.Value.Start) >= UnknownLoginsEntry).Select(p => p.Key).ToList())
                {
                    _unknown.Remove(ended);
                }

                if (_unknown.Count >= MaxUnknownLoginAddresses)
                {
                    _unknown.Clear(); // an attack from many addresses: their next failures start new entries
                }
            }

            _unknown.Remove(clientAddress);
            if (await audit.AddAsync(name, clientAddress, AuditActions.LoginFailed, name, UnknownUserDetail, ct).ConfigureAwait(false) is { } id)
            {
                _unknown[clientAddress] = new UnknownLogins(id, _time.GetTimestamp());
            }
        }
        finally
        {
            _unknownGate.Release();
        }
    }

    private bool SetupCodeMatches(string? entered)
    {
        string? code;
        lock (_setupGate)
        {
            code = _setupCode;
        }

        if (code is null || string.IsNullOrWhiteSpace(entered) || entered.Length > MaxSetupCodeLength)
        {
            return false;
        }

        static byte[] Canonical(string text) => Encoding.ASCII.GetBytes(new string(text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()));
        return CryptographicOperations.FixedTimeEquals(Canonical(code), Canonical(entered));
    }

    private void DeleteSetupCode()
    {
        lock (_setupGate)
        {
            _setupCode = null;
        }

        try
        {
            if (File.Exists(SetupCodePath))
            {
                File.Delete(SetupCodePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSetupCodeFileFailed(ex, SetupCodePath);
        }
    }

    private static AuthException Locked(string name, DateTimeOffset until)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalMinutes));
        return new AuthException(AuthError.Locked, string.Create(CultureInfo.InvariantCulture,
            $"Too many failed logins for {name}. Try again in {minutes} {(minutes == 1 ? "minute" : "minutes")}."));
    }

    /// <summary>The running audit entry of an address's failed logins with unknown user names.</summary>
    private sealed class UnknownLogins(long id, long start)
    {
        public long Id { get; } = id;

        public long Start { get; } = start;

        public int Count { get; set; } = 1;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No administrator yet. Create the first administrator in the OADM client on this computer, or from another computer with the setup code {Code} (also in {Path})")]
    private partial void LogNoAdministrator(string code, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write or delete the setup code file {Path}")]
    private partial void LogSetupCodeFileFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserName} logged in from {Client}")]
    private partial void LogLoggedIn(string userName, string client);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed login for {UserName} from {Client}")]
    private partial void LogLoginFailed(string userName, string client);

    [LoggerMessage(Level = LogLevel.Warning, Message = "User name {UserName} locked after too many failed logins (last from {Client})")]
    private partial void LogLocked(string userName, string client);

    [LoggerMessage(Level = LogLevel.Information, Message = "First administrator {UserName} created from {Client}")]
    private partial void LogFirstAdmin(string userName, string client);
}
