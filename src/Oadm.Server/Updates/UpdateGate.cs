namespace Oadm.Server.Updates;

/// <summary>
/// The state of this server's update for every call: from Install now until the result <see cref="UpdatingTo"/> names
/// the version (AuthService.Status tells the update helpers), and once the update's helper started (docs/spec/self-update.md
/// "Server" step 2) the server is closed: it takes no call but <c>AuthService.Status</c> and UpdateService until it stops
/// (UNAVAILABLE "Updating the server to 4.9.0").
/// </summary>
public sealed class UpdateGate
{
    private readonly Lock _sync = new();
    private string? _closedFor;
    private string? _updatingTo;
    private int _addOnWork;

    /// <summary>The version the server updates to while closed; null while open.</summary>
    public string? ClosedFor => Volatile.Read(ref _closedFor);

    /// <summary>The version an update of this server runs to (tasks and event rule actions held); null without one.</summary>
    public string? UpdatingTo
    {
        get
        {
            lock (_sync)
            {
                return _updatingTo;
            }
        }
    }

    /// <summary>An optional plugin is being installed or removed: Install now is refused (docs/spec/add-ons.md "Server").</summary>
    public bool AddOnWorkRuns
    {
        get
        {
            lock (_sync)
            {
                return _addOnWork > 0;
            }
        }
    }

    public void Begin(string version)
    {
        lock (_sync)
        {
            _updatingTo = version;
        }
    }

    /// <summary>Starts an update unless an optional plugin is being installed or removed; false: nothing changed.</summary>
    public bool TryBegin(string version)
    {
        lock (_sync)
        {
            if (_addOnWork > 0)
            {
                return false;
            }

            _updatingTo = version;
            return true;
        }
    }

    /// <summary>Starts the install or remove of an optional plugin unless an update runs; false: nothing changed.</summary>
    public bool TryBeginAddOnWork()
    {
        lock (_sync)
        {
            if (_updatingTo is not null)
            {
                return false;
            }

            _addOnWork++;
            return true;
        }
    }

    /// <summary>The install or remove of <see cref="TryBeginAddOnWork"/> ended.</summary>
    public void EndAddOnWork()
    {
        lock (_sync)
        {
            _addOnWork--;
        }
    }

    public void Close(string version) => Volatile.Write(ref _closedFor, version);

    public void Open() => Volatile.Write(ref _closedFor, null);

    /// <summary>The update ended: open, and no update runs.</summary>
    public void End()
    {
        Open();
        lock (_sync)
        {
            _updatingTo = null;
        }
    }

    /// <summary>The calls that stay open while closed.</summary>
    public static bool StaysOpen(string method) =>
        method == "/oadm.v1.AuthService/Status"
        || method.StartsWith("/oadm.v1.UpdateService/", StringComparison.Ordinal);
}
