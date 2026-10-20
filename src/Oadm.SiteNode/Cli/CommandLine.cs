using System.Globalization;

using Oadm.SiteNode.Admin;

namespace Oadm.SiteNode.Cli;

/// <summary>
/// The command line of the installed node (docs/sites.md, "App": headless machines): it talks to the running service over
/// the local channel. Reading the state needs no rights; the pairing code and every change need an administrator
/// (sudo / an elevated prompt).
/// </summary>
public static class CommandLine
{
    public const string Usage = """
        OADM Site Node

        oadm-site-node                          run the service (console)
        oadm-site-node status                   state, server, networks and ports
        oadm-site-node pairing-code             the pairing code (administrators)
        oadm-site-node server <address>         connect to the OADM server (oadm.example.com, 10.0.0.5:5081)
        oadm-site-node server --wait            wait for the server on port 5081 instead
        oadm-site-node allow-binding            binding open for another 24 hours, a new code
        oadm-site-node networks                 network cards and extra networks
        oadm-site-node networks card <name> on|off
        oadm-site-node networks all on|off
        oadm-site-node networks add|remove <network like 10.20.0.0/16>
        oadm-site-node ports                    allowed and requested ports
        oadm-site-node ports allow|remove|dismiss connect|listen tcp|udp <port>
        oadm-site-node accept-server            let the server's new identity bind with the code
        oadm-site-node reset                    forget the server, new identity and code
        oadm-site-node log [count]              the newest connections

        --data-dir <folder>   another data folder (default: the installed node's)
        --port <port>         the port of "wait for the server" (default 5081)
        --timeout <seconds>   wait this long for the service to answer (default: no wait; installers)
        """;

    /// <summary>The verb, the arguments without the data folder and port options, the data folder and port when given.</summary>
    public static (string? Verb, string[] Arguments, string? DataDirectory, int? Port) Split(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? dataDirectory = null;
        int? port = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--data-dir" && i + 1 < args.Length)
                dataDirectory = args[++i];
            else if (args[i].StartsWith("--data-dir=", StringComparison.Ordinal))
                dataDirectory = args[i]["--data-dir=".Length..];
            else if (args[i] is "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
            {
                port = p;
                i++;
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        var verb = rest.Count > 0 && !rest[0].StartsWith('-') ? rest[0] : null;
        return (verb, verb is null ? [.. rest] : [.. rest.Skip(1)], dataDirectory, port);
    }

    /// <param name="firewall">Runs one netsh call (tests); null = netsh on Windows, nothing elsewhere.</param>
    public static async Task<int> RunAsync(string verb, string[] args, string? dataDirectory, TextWriter output, TextWriter error, CancellationToken ct,
        AdminClient? client = null, Func<IReadOnlyList<string>, CancellationToken, Task<int>>? firewall = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        client ??= new AdminClient(NodeOptions.EndpointFor(dataDirectory ?? NodeOptions.DefaultDataDirectory()));
        if (verb is "help" or "--help" or "-h")
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }

        if (TakeTimeout(args) is not { } split || Request(verb, split.Arguments) is not { } request)
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        args = split.Arguments;
        var response = await SendAsync(client, request, split.Wait, ct).ConfigureAwait(false);
        if (!response.Ok)
        {
            await error.WriteLineAsync(response.Error).ConfigureAwait(false);
            return 1;
        }

        // "server --wait" opens TCP 5081 in Windows Firewall, "server <address>" closes it (the service cannot).
        if (await NodeFirewall.ApplyAsync(request, response, ct, firewall).ConfigureAwait(false) is { } firewallProblem)
            await error.WriteLineAsync(firewallProblem).ConfigureAwait(false);

        switch (verb)
        {
            case "pairing-code":
            case "accept-server":
                await output.WriteLineAsync(response.PairingCode).ConfigureAwait(false);
                break;
            case "networks" when args.Length == 0:
                await output.WriteLineAsync(Networks(response.State!)).ConfigureAwait(false);
                break;
            case "ports" when args.Length == 0:
                await output.WriteLineAsync(Ports(response.State!)).ConfigureAwait(false);
                break;
            case "log":
                foreach (var entry in response.Log)
                    await output.WriteLineAsync(string.Join("  ", entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), entry.Server, entry.Target, entry.Kind, entry.Result)).ConfigureAwait(false);
                break;
            default:
                if (response.State is { } state)
                    await output.WriteLineAsync(Status(state)).ConfigureAwait(false);
                break;
        }

        return 0;
    }

    /// <summary>The arguments without "--timeout &lt;seconds&gt;" and the wait it asks for; null for a bad value.</summary>
    public static (string[] Arguments, TimeSpan Wait)? TakeTimeout(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var index = Array.IndexOf(args, "--timeout");
        if (index < 0)
            return (args, TimeSpan.Zero);
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 3600)
            return null;
        return ([.. args.Take(index), .. args.Skip(index + 2)], TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Sends the request; while the service does not answer (an installer starts it right before) it tries again every
    /// second until <paramref name="wait"/> is over.
    /// </summary>
    private static async Task<AdminResponse> SendAsync(AdminClient client, AdminRequest request, TimeSpan wait, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.Ok || response.Error != AdminTexts.ServiceNotRunning || System.Diagnostics.Stopwatch.GetElapsedTime(started) >= wait)
                return response;
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The request of a verb, or null for a usage error.</summary>
    public static AdminRequest? Request(string verb, string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return (verb, args.Length) switch
        {
            ("status", 0) => AdminRequest.Of(AdminCommands.State),
            ("pairing-code", 0) => AdminRequest.Of(AdminCommands.PairingCode),
            ("allow-binding", 0) => AdminRequest.Of(AdminCommands.AllowBinding),
            ("accept-server", 0) => AdminRequest.Of(AdminCommands.AcceptServer),
            ("reset", 0) => AdminRequest.Of(AdminCommands.Reset),
            ("log", 0) => AdminRequest.Of(AdminCommands.Log, ("count", "50")),
            ("log", 1) => AdminRequest.Of(AdminCommands.Log, ("count", args[0])),
            ("server", 1) when args[0] == "--wait" => AdminRequest.Of(AdminCommands.SetServer, ("mode", "wait")),
            ("server", 1) => AdminRequest.Of(AdminCommands.SetServer, ("mode", "connect"), ("address", args[0])),
            ("networks", 0) => AdminRequest.Of(AdminCommands.State),
            ("networks", 2) when args[0] == "all" && OnOff(args[1]) is { } on => AdminRequest.Of(AdminCommands.AllCards, ("on", on)),
            ("networks", 2) when args[0] == "add" => AdminRequest.Of(AdminCommands.AddSubnet, ("subnet", args[1])),
            ("networks", 2) when args[0] == "remove" => AdminRequest.Of(AdminCommands.RemoveSubnet, ("subnet", args[1])),
            ("networks", 3) when args[0] == "card" && OnOff(args[2]) is { } on => AdminRequest.Of(AdminCommands.SetCard, ("id", args[1]), ("reached", on)),
            ("ports", 0) => AdminRequest.Of(AdminCommands.State),
            ("ports", 4) when PortCommand(args[0]) is { } command && args[1] is "connect" or "listen" && args[2] is "tcp" or "udp" =>
                AdminRequest.Of(command, ("use", args[1]), ("protocol", args[2]), ("port", args[3])),
            _ => null,
        };
    }

    public static string Status(NodeView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var lines = new List<string>
        {
            $"OADM Site Node {view.Version}, id {view.NodeId}",
            view.Binding switch
            {
                NodeBinding.Bound => $"Bound to {view.Bound?.Name} as {view.Bound?.SiteName} since {view.Bound?.BoundUtc.ToLocalTime():yyyy-MM-dd HH:mm}",
                NodeBinding.Closed => "Binding closed. Run allow-binding.",
                _ => $"Not bound. Binding open for {Math.Ceiling(view.BindingHoursLeft).ToString(CultureInfo.InvariantCulture)} more hours.",
            },
            view.Mode == NodeLinkMode.Wait ? $"Waits for the server on port {view.ListenPort}" : $"Server: {(view.ServerAddress.Length > 0 ? view.ServerAddress : "not set")}",
        };
        if (view.ConnectionText.Length > 0)
            lines.Add(view.ConnectionText);
        if (view.NewServerIdentity.Length > 0)
            lines.Add(view.NewServerAccepted
                ? $"Accepted the new identity {view.NewServerIdentity}: bind it with the pairing code (pairing-code)."
                : $"{view.Bound?.Name}{(view.NewServerFrom.Length > 0 ? " at " + view.NewServerFrom : "")} presents a new identity ({view.NewServerIdentity}). Run accept-server, then bind it in OADM with the pairing code it shows.");
        if (view.LockedUntil is { } locked)
            lines.Add($"Too many wrong pairing codes: binding refused until {locked.ToLocalTime():HH:mm}.");
        lines.Add($"Connections: {view.ShortConnections} short, {view.PermanentConnections} permanent");
        foreach (var relay in view.Relays)
            lines.Add($"Relay {AdminTexts.Name(relay.Protocol)} {relay.Port} ({relay.Reason}): {relay.State}{(relay.Message.Length > 0 ? " - " + relay.Message : "")}");
        if (view.Requested.Count > 0)
            lines.Add($"{view.Requested.Count} ports wait for approval (ports)");
        return string.Join(Environment.NewLine, lines);
    }

    public static string Networks(NodeView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var lines = new List<string>();
        foreach (var card in view.Cards)
            lines.Add($"{(card.Reached ? "[x]" : "[ ]")} {card.Name} {string.Join(", ", card.Addresses)}{(card.HasDefaultRouter ? " (default router)" : "")}");
        lines.Add(view.AllCards ? "All cards: on" : "All cards: off");
        foreach (var subnet in view.ExtraSubnets)
            lines.Add("Extra network " + subnet);
        return string.Join(Environment.NewLine, lines);
    }

    public static string Ports(NodeView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var lines = new List<string>
        {
            "Connect to: " + string.Join(", ", view.Allowed.Where(p => p.Use == PortUse.Connect).Select(p => p.Name)),
            "Listen on: " + string.Join(", ", view.Allowed.Where(p => p.Use == PortUse.Listen).Select(p => p.Name)),
        };
        foreach (var request in view.Requested)
            lines.Add($"Asked for: {(request.Use == PortUse.Listen ? "listen on" : "connect to")} {request.Name}{(request.Reason.Length > 0 ? ": " + request.Reason : "")}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string? OnOff(string value) => value switch
    {
        "on" => "true",
        "off" => "false",
        _ => null,
    };

    private static string? PortCommand(string value) => value switch
    {
        "allow" => AdminCommands.AllowRequest,
        "remove" => AdminCommands.RemovePort,
        "dismiss" => AdminCommands.DismissRequest,
        _ => null,
    };
}
