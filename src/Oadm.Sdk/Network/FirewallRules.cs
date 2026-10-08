using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Oadm.Sdk.Network;

public enum FirewallProtocol
{
    Tcp = 0,
    Udp = 1,
}

/// <summary>
/// An inbound allow rule for the server executable on one local port (profiles Domain and Private).
/// <paramref name="Name"/> identifies the rule ("OADM Server (NTP, UDP 123)"): opening replaces a rule of that name,
/// closing removes it.
/// </summary>
public sealed record FirewallRule(string Name, FirewallProtocol Protocol, int Port)
{
    /// <summary>"OADM Server (NTP, UDP 123)".</summary>
    public static FirewallRule ForService(string service, FirewallProtocol protocol, int port) =>
        new(string.Create(CultureInfo.InvariantCulture, $"OADM Server ({service}, {(protocol == FirewallProtocol.Udp ? "UDP" : "TCP")} {port})"), protocol, port);
}

/// <summary>
/// The host firewall as far as plugins may change it. Offered by the server only where it manages the firewall
/// (Windows, running as the installed service, see <c>ICorePluginContext.Firewall</c>); null elsewhere, so a plugin
/// never touches the firewall in tests or development runs. Linux and macOS are not changed.
/// </summary>
public interface IFirewallRules
{
    /// <summary>Adds (or replaces) the inbound allow rule. Throws when the firewall refused it.</summary>
    Task OpenAsync(FirewallRule rule, CancellationToken ct);

    /// <summary>Removes the rule; a missing rule is not an error.</summary>
    Task CloseAsync(FirewallRule rule, CancellationToken ct);
}

/// <summary>
/// Keeps one firewall rule in line with a service's enabled state (NTP server, DHCP server): <see cref="SyncAsync"/>
/// opens the rule when the service is enabled and closes it when disabled or stopped. The first call always applies
/// the state (a rule left by a crashed server is removed), later calls only on a change. Best effort: firewall errors
/// are logged and never fail the service. Without a host firewall (<c>null</c>) it does nothing. Not thread-safe: callers
/// serialize the calls (the services call it under their own gate).
/// </summary>
public sealed partial class FirewallRuleKeeper
{
    private readonly IFirewallRules? _firewall;
    private readonly ILogger _logger;
    private bool? _open;

    public FirewallRuleKeeper(FirewallRule rule, IFirewallRules? firewall, ILogger? logger = null)
    {
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        _firewall = firewall;
        _logger = logger ?? NullLogger.Instance;
    }

    public FirewallRule Rule { get; }

    /// <summary>True once the rule was opened (and not closed since).</summary>
    public bool IsOpen => _open == true;

    public async Task SyncAsync(bool enabled, CancellationToken ct)
    {
        if (_firewall is null)
        {
            return;
        }

        if (_open == enabled)
        {
            return;
        }

        try
        {
            if (enabled)
            {
                await _firewall.OpenAsync(Rule, ct).ConfigureAwait(false);
                LogOpened(Rule.Name);
            }
            else
            {
                await _firewall.CloseAsync(Rule, ct).ConfigureAwait(false);
                if (_open == true)
                {
                    LogClosed(Rule.Name);
                }
            }

            _open = enabled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Best effort: the service keeps running, the user can open the port by hand.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(Rule.Name, ex.Message);
            _open = null; // try again on the next call
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Firewall rule \"{Rule}\" added")]
    private partial void LogOpened(string rule);

    [LoggerMessage(Level = LogLevel.Information, Message = "Firewall rule \"{Rule}\" removed")]
    private partial void LogClosed(string rule);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Firewall rule \"{Rule}\" could not be changed: {Message}")]
    private partial void LogFailed(string rule, string message);
}
