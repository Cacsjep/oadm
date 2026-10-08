using System.Globalization;

using Oadm.Sdk.Network;

namespace Oadm.Server.Hosting;

/// <summary>
/// Windows Defender Firewall rules through <c>netsh advfirewall firewall</c> (the service runs as SYSTEM). A rule is an
/// inbound allow rule for the server executable on one local port, profiles Domain and Private (never Public). Opening
/// deletes a rule of the same name first, so restarts never pile up duplicates. Only used when the server runs as the
/// installed Windows service (<see cref="ServiceHosting"/>); tests and development runs never touch the firewall.
/// </summary>
public sealed class NetshFirewallRules(ICommandRunner runner, string programPath) : IFirewallRules
{
    public const string Profiles = "domain,private";

    public string ProgramPath { get; } = string.IsNullOrWhiteSpace(programPath) ? throw new ArgumentException("Program path missing.", nameof(programPath)) : programPath;

    public async Task OpenAsync(FirewallRule rule, CancellationToken ct)
    {
        Validate(rule);
        await runner.RunAsync("netsh", DeleteArguments(rule), ct).ConfigureAwait(false); // "No rules match" is fine
        var result = await runner.RunAsync("netsh", AddArguments(rule), ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"netsh exit code {result.ExitCode}: {result.Output}");
        }
    }

    public async Task CloseAsync(FirewallRule rule, CancellationToken ct)
    {
        Validate(rule);

        // Exit code 1 with "No rules match the specified criteria." when the rule does not exist: not an error.
        await runner.RunAsync("netsh", DeleteArguments(rule), ct).ConfigureAwait(false);
    }

    public IReadOnlyList<string> AddArguments(FirewallRule rule) =>
    [
        "advfirewall", "firewall", "add", "rule",
        "name=" + rule.Name,
        "dir=in",
        "action=allow",
        "protocol=" + (rule.Protocol == FirewallProtocol.Udp ? "UDP" : "TCP"),
        "localport=" + rule.Port.ToString(CultureInfo.InvariantCulture),
        "program=" + ProgramPath,
        "profile=" + Profiles,
        "enable=yes",
    ];

    public static IReadOnlyList<string> DeleteArguments(FirewallRule rule) =>
        ["advfirewall", "firewall", "delete", "rule", "name=" + rule.Name];

    private static void Validate(FirewallRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Contains('"', StringComparison.Ordinal) || rule.Name.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid firewall rule name '{rule.Name}'.", nameof(rule));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(rule.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rule.Port, 65535);
    }
}
