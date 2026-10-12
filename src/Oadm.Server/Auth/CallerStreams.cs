using System.Collections.Concurrent;

using Oadm.Core.Auth;

namespace Oadm.Server.Auth;

/// <summary>
/// The streaming calls that are open (device and task feeds, live view, plugin events, Web View requests). A stream is
/// checked when it starts; when a role changes, the streams it no longer allows are ended (the client reconnects and is
/// refused), and when a user gets another role all of that user's streams end and are checked again. The session stream
/// (AuthService.WatchSession) stays: it tells the client about the change.
/// </summary>
public sealed partial class CallerStreams : IDisposable
{
    private readonly ConcurrentDictionary<long, OpenStream> _streams = new();
    private readonly RoleStore _roles;
    private readonly UserStore _users;
    private readonly AuthTokenStore? _tokens;
    private readonly ILogger<CallerStreams> _logger;
    private long _next;

    public CallerStreams(RoleStore roles, UserStore users, ILogger<CallerStreams> logger, AuthTokenStore? tokens = null)
    {
        _roles = roles;
        _users = users;
        _tokens = tokens;
        _logger = logger;
        _roles.Changed += OnRolesChanged;
        _users.UserRoleChanged += OnUserRoleChanged;
        if (_tokens is not null)
        {
            _tokens.SessionsEnded += OnSessionsEnded;
        }
    }

    /// <summary>The number of open streams (tests).</summary>
    public int Count => _streams.Count;

    /// <summary>Registers an open stream until the returned scope is disposed.</summary>
    /// <param name="abort">Ends the call (the HTTP request is aborted, the call's cancellation token fires).</param>
    public IDisposable Open(AuthenticatedCaller caller, AccessRule rule, string method, Action abort)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(abort);
        var id = Interlocked.Increment(ref _next);
        _streams[id] = new OpenStream(caller.UserId, caller.UserName, caller.RoleId, caller.TokenHash, rule, method, abort, IsSessionStream(method));
        return new Registration(this, id);
    }

    /// <summary>The session stream tells the client about a role change; it ends only with the session.</summary>
    private static bool IsSessionStream(string method) => method == "/oadm.v1.AuthService/WatchSession";

    public void Dispose()
    {
        _roles.Changed -= OnRolesChanged;
        _users.UserRoleChanged -= OnUserRoleChanged;
        if (_tokens is not null)
        {
            _tokens.SessionsEnded -= OnSessionsEnded;
        }
    }

    /// <summary>A logout, a new password, a disabled or deleted user: every stream of those sessions ends, the session stream too.</summary>
    private void OnSessionsEnded(object? sender, EndedSessions e)
    {
        foreach (var (id, stream) in _streams.Where(s => (e.UserId is { } user && s.Value.UserId == user)
                     || (e.TokenHash is { } hash && s.Value.TokenHash == hash)).ToList())
        {
            End(id, stream);
        }
    }

    private async void OnRolesChanged(object? sender, IReadOnlyCollection<Guid> roleIds)
    {
        try
        {
            foreach (var (id, stream) in _streams.Where(s => !s.Value.IsSession && roleIds.Contains(s.Value.RoleId)).ToList())
            {
                var permissions = await _roles.PermissionsOfAsync(stream.RoleId, CancellationToken.None).ConfigureAwait(false);
                bool allowed = stream.Rule.Access switch
                {
                    Access.Permission => permissions.Has(stream.Rule.Permission!),
                    Access.InCall => false, // the plugin or task decides: checked again on reconnect
                    _ => true,
                };
                if (!allowed)
                {
                    End(id, stream);
                }
            }
        }
#pragma warning disable CA1031 // An event handler must not throw; a stream that stays open is checked on its next start.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCheckFailed(ex);
        }
    }

    private void OnUserRoleChanged(object? sender, Guid userId)
    {
        foreach (var (id, stream) in _streams.Where(s => !s.Value.IsSession && s.Value.UserId == userId).ToList())
        {
            End(id, stream);
        }
    }

    private void End(long id, OpenStream stream)
    {
        if (_streams.TryRemove(id, out _))
        {
            LogEnded(stream.UserName, stream.Method);
            stream.Abort();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Access changed: ended {Method} of {UserName}")]
    private partial void LogEnded(string userName, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking open streams after a role change failed")]
    private partial void LogCheckFailed(Exception ex);

    private sealed record OpenStream(Guid UserId, string UserName, Guid RoleId, string TokenHash, AccessRule Rule, string Method, Action Abort, bool IsSession);

    private sealed class Registration(CallerStreams owner, long id) : IDisposable
    {
        public void Dispose() => owner._streams.TryRemove(id, out _);
    }
}
