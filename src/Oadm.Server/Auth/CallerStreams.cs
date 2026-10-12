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
    private readonly ILogger<CallerStreams> _logger;
    private long _next;

    public CallerStreams(RoleStore roles, UserStore users, ILogger<CallerStreams> logger)
    {
        _roles = roles;
        _users = users;
        _logger = logger;
        _roles.Changed += OnRolesChanged;
        _users.UserRoleChanged += OnUserRoleChanged;
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
        _streams[id] = new OpenStream(caller.UserId, caller.UserName, caller.RoleId, rule, method, abort);
        return new Registration(this, id);
    }

    public void Dispose()
    {
        _roles.Changed -= OnRolesChanged;
        _users.UserRoleChanged -= OnUserRoleChanged;
    }

    private async void OnRolesChanged(object? sender, IReadOnlyCollection<Guid> roleIds)
    {
        try
        {
            foreach (var (id, stream) in _streams.Where(s => roleIds.Contains(s.Value.RoleId)).ToList())
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
        foreach (var (id, stream) in _streams.Where(s => s.Value.UserId == userId).ToList())
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Role changed: ended {Method} of {UserName}")]
    private partial void LogEnded(string userName, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking open streams after a role change failed")]
    private partial void LogCheckFailed(Exception ex);

    private sealed record OpenStream(Guid UserId, string UserName, Guid RoleId, AccessRule Rule, string Method, Action Abort);

    private sealed class Registration(CallerStreams owner, long id) : IDisposable
    {
        public void Dispose() => owner._streams.TryRemove(id, out _);
    }
}
