using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.SiteNode.Admin;

/// <summary>The binding state of the node.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NodeBinding>))]
public enum NodeBinding
{
    /// <summary>Not bound, binding open: Bind with the right pairing code works.</summary>
    Open,

    /// <summary>Not bound, binding closed: Allow binding first.</summary>
    Closed,

    /// <summary>Bound to one server.</summary>
    Bound,
}

/// <summary>Connect to (devices) or Listen on (relays).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PortUse>))]
public enum PortUse
{
    Connect,
    Listen,
}

[JsonConverter(typeof(JsonStringEnumConverter<PortProtocol>))]
public enum PortProtocol
{
    Tcp,
    Udp,
}

/// <summary>One allowed or requested port.</summary>
public sealed record PortEntry(PortUse Use, PortProtocol Protocol, int Port, string Reason = "")
{
    /// <summary>True for the same use, protocol and port (the reason does not count).</summary>
    public bool SameAs(PortEntry other) => other is not null && other.Use == Use && other.Protocol == Protocol && other.Port == Port;

    /// <summary>"TCP 8443".</summary>
    [JsonIgnore]
    public string Name => $"{(Protocol == PortProtocol.Tcp ? "TCP" : "UDP")} {Port}";
}

/// <summary>A network card of the node's machine.</summary>
public sealed record CardView(string Id, string Name, IReadOnlyList<string> Addresses, bool Reached, bool HasDefaultRouter);

/// <summary>The server the node is bound to.</summary>
public sealed record BoundServerView(string Name, string Fingerprint, DateTimeOffset BoundUtc, string SiteName);

/// <summary>A relay the server opened on the node.</summary>
public sealed record RelayView(PortProtocol Protocol, int Port, string Reason, string State, string Message);

/// <summary>A wrong pairing code: when and from where.</summary>
public sealed record WrongCodeView(DateTimeOffset Time, string From);

/// <summary>One line of the connection log (never contents).</summary>
public sealed record ConnectionLogEntry(DateTimeOffset Time, string Server, string Target, string Kind, string Result);

/// <summary>The node's state as the app and the command line show it. Everyone may read it; it never holds the pairing code.</summary>
public sealed class NodeView
{
    public string NodeId { get; init; } = "";
    public string Version { get; init; } = "";
    public string HostName { get; init; } = "";
    public NodeBinding Binding { get; init; }

    /// <summary>The typed server address ("oadm.example.com", "10.0.0.5:5081"); empty when none.</summary>
    public string ServerAddress { get; init; } = "";

    /// <summary>Hours of run time left for binding (only while not bound and open).</summary>
    public double BindingHoursLeft { get; init; }

    public BoundServerView? Bound { get; init; }

    /// <summary>"Connected to 10.0.0.5:5081", "Cannot reach 10.0.0.5:5081".</summary>
    public string ConnectionText { get; init; } = "";

    public bool Connected { get; init; }

    public IReadOnlyList<CardView> Cards { get; init; } = [];

    /// <summary>The node reaches every card (the default, or no card chosen).</summary>
    public bool AllCards { get; init; }
    public IReadOnlyList<string> ExtraSubnets { get; init; } = [];
    public IReadOnlyList<PortEntry> Allowed { get; init; } = [];
    public IReadOnlyList<PortEntry> Requested { get; init; } = [];
    public IReadOnlyList<RelayView> Relays { get; init; } = [];
    public int ShortConnections { get; init; }
    public int PermanentConnections { get; init; }

    /// <summary>The bound server presented another identity (fingerprint hex); Accept replaces the pinned one.</summary>
    public string NewServerIdentity { get; init; } = "";

    /// <summary>Where <see cref="NewServerIdentity"/> came from ("10.0.0.5:5081").</summary>
    public string NewServerFrom { get; init; } = "";

    /// <summary>Accepted: that server binds with the pairing code.</summary>
    public bool NewServerAccepted { get; init; }

    public IReadOnlyList<WrongCodeView> WrongCodes { get; init; } = [];

    /// <summary>Binding refused until then after 5 wrong codes.</summary>
    public DateTimeOffset? LockedUntil { get; init; }
}

/// <summary>One request on the admin channel.</summary>
public sealed class AdminRequest
{
    public string Command { get; init; } = "";

    public Dictionary<string, string> Args { get; init; } = new(StringComparer.Ordinal);

    public string Arg(string name) => Args.TryGetValue(name, out var value) ? value : "";

    public static AdminRequest Of(string command, params (string Name, string Value)[] args)
    {
        var request = new AdminRequest { Command = command };
        foreach (var (name, value) in args)
            request.Args[name] = value;
        return request;
    }
}

/// <summary>The answer: ok, or a plain error text.</summary>
public sealed class AdminResponse
{
    public bool Ok { get; init; }
    public string Error { get; init; } = "";
    public NodeView? State { get; init; }
    public string PairingCode { get; init; } = "";
    public IReadOnlyList<ConnectionLogEntry> Log { get; init; } = [];

    public static AdminResponse Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>The commands of the admin channel. Only <see cref="State"/> and <see cref="Log"/> are open to every local user.</summary>
public static class AdminCommands
{
    public const string State = "state";
    public const string Log = "log";
    public const string PairingCode = "pairing-code";
    public const string SetServer = "set-server";
    public const string AllowBinding = "allow-binding";
    public const string SetCard = "set-card";
    public const string AllCards = "all-cards";
    public const string AddSubnet = "add-subnet";
    public const string RemoveSubnet = "remove-subnet";
    public const string AddPort = "add-port";
    public const string RemovePort = "remove-port";
    public const string AllowRequest = "allow-request";
    public const string DismissRequest = "dismiss-request";
    public const string AcceptServer = "accept-server";
    public const string Reset = "reset";

    /// <summary>True for the commands every local user may send.</summary>
    public static bool IsOpen(string command) => command is State or Log;
}

/// <summary>JSON of the admin channel: one request line, one answer line.</summary>
public static class AdminJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
