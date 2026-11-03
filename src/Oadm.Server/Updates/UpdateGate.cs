namespace Oadm.Server.Updates;

/// <summary>
/// Closed once the update's helper started (docs/spec/self-update.md "Server" step 2): the server takes no call but
/// <c>AuthService.Status</c> and UpdateService until it stops (UNAVAILABLE "Updating the server to 4.9.0").
/// </summary>
public sealed class UpdateGate
{
    private string? _closedFor;

    /// <summary>The version the server updates to while closed; null while open.</summary>
    public string? ClosedFor => Volatile.Read(ref _closedFor);

    public void Close(string version) => Volatile.Write(ref _closedFor, version);

    public void Open() => Volatile.Write(ref _closedFor, null);

    /// <summary>The calls that stay open while closed.</summary>
    public static bool StaysOpen(string method) =>
        method == "/oadm.v1.AuthService/Status"
        || method.StartsWith("/oadm.v1.UpdateService/", StringComparison.Ordinal);
}
