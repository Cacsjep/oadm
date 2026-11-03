namespace Oadm.Server.Updates;

/// <summary>
/// The state of this server's update for every call: from Install now until the result <see cref="UpdatingTo"/> names
/// the version (AuthService.Status tells the update helpers), and once the update's helper started (docs/spec/self-update.md
/// "Server" step 2) the server is closed: it takes no call but <c>AuthService.Status</c> and UpdateService until it stops
/// (UNAVAILABLE "Updating the server to 4.9.0").
/// </summary>
public sealed class UpdateGate
{
    private string? _closedFor;
    private string? _updatingTo;

    /// <summary>The version the server updates to while closed; null while open.</summary>
    public string? ClosedFor => Volatile.Read(ref _closedFor);

    /// <summary>The version an update of this server runs to (tasks and event rule actions held); null without one.</summary>
    public string? UpdatingTo => Volatile.Read(ref _updatingTo);

    public void Begin(string version) => Volatile.Write(ref _updatingTo, version);

    public void Close(string version) => Volatile.Write(ref _closedFor, version);

    public void Open() => Volatile.Write(ref _closedFor, null);

    /// <summary>The update ended: open, and no update runs.</summary>
    public void End()
    {
        Open();
        Volatile.Write(ref _updatingTo, null);
    }

    /// <summary>The calls that stay open while closed.</summary>
    public static bool StaysOpen(string method) =>
        method == "/oadm.v1.AuthService/Status"
        || method.StartsWith("/oadm.v1.UpdateService/", StringComparison.Ordinal);
}
