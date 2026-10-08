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
/// the audit log.
/// </summary>
public sealed partial class AuthManager(
    UserStore users,
    AuthTokenStore tokens,
    PasswordHasher hasher,
    LoginThrottle throttle,
    AuditLog audit,
    OadmPaths paths,
    ILogger<AuthManager>? logger = null)
{
    public const string SetupCodeFileName = "setup-code.txt";
    public const string WrongLoginMessage = "The user name or password is wrong.";

    /// <summary>Throttle key of setup code attempts (not a valid user name: it contains a control character).</summary>
    private const string SetupCodeThrottleKey = "\u0001setup-code";

    private const string SetupAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

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
        var name = (userName ?? string.Empty).Trim();
        password ??= string.Empty;
        if (throttle.LockedUntil(name) is { } until)
        {
            throw Locked(name, until);
        }

        var user = name.Length is > 0 and <= UserStore.MaxUserNameLength ? await users.FindByNameAsync(name, ct).ConfigureAwait(false) : null;
        var ok = false;
        if (user is null)
        {
            hasher.VerifyDummy(password);
        }
        else
        {
            ok = PasswordHasher.Verify(password, user.PasswordHash) && !user.Disabled;
        }

        if (!ok || user is null)
        {
            var locked = throttle.RecordFailure(name);
            await audit.WriteAsync(name, clientAddress, AuditActions.LoginFailed, name,
                user is null ? "Unknown user" : user.Disabled ? "User disabled" : "Wrong password", ct).ConfigureAwait(false);
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
                await audit.WriteAsync(userName ?? string.Empty, clientAddress, AuditActions.LoginFailed, "setup code", "Wrong setup code", ct).ConfigureAwait(false);
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

    private bool SetupCodeMatches(string? entered)
    {
        string? code;
        lock (_setupGate)
        {
            code = _setupCode;
        }

        if (code is null || string.IsNullOrWhiteSpace(entered))
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
