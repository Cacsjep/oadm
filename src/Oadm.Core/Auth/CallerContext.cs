using Oadm.Sdk.Plugins;

namespace Oadm.Core.Auth;

/// <summary>The authenticated user of the current gRPC call.</summary>
/// <param name="ClientAddress">Remote IP address of the client.</param>
/// <param name="ClientMachine">Machine name the client sent (oadm-client-machine), shortened and cleaned.</param>
/// <param name="TokenHash">SHA-256 of the session token (Logout revokes it).</param>
public sealed record AuthenticatedCaller(Guid UserId, string UserName, UserRole Role, string ClientAddress, string ClientMachine, string TokenHash)
{
    /// <summary>Task owner: "&lt;user&gt;@&lt;client machine&gt;" (the user is authenticated, the machine is what the client says).</summary>
    public string Owner => string.IsNullOrEmpty(ClientMachine) ? UserName : UserName + "@" + ClientMachine;

    public bool IsAdmin => Role == UserRole.Admin;
}

/// <summary>
/// The caller of the current call, set by the server's auth interceptor for the duration of the call (flows into
/// everything the call awaits: task runs, core plugin invocations). Null outside calls (background work) and in tests
/// that call services directly.
/// </summary>
public static class CallerContext
{
    private static readonly AsyncLocal<AuthenticatedCaller?> CurrentCaller = new();

    public static AuthenticatedCaller? Current
    {
        get => CurrentCaller.Value;
        set => CurrentCaller.Value = value;
    }

    /// <summary>Sets <paramref name="caller"/> until the returned scope is disposed (tests, background calls on behalf of a user).</summary>
    public static IDisposable Use(AuthenticatedCaller? caller)
    {
        var previous = CurrentCaller.Value;
        CurrentCaller.Value = caller;
        return new Scope(previous);
    }

    private sealed class Scope(AuthenticatedCaller? previous) : IDisposable
    {
        public void Dispose() => CurrentCaller.Value = previous;
    }
}
