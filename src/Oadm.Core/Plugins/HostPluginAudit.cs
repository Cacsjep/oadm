using Oadm.Core.Auth;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>
/// <see cref="ICorePluginContext.Audit"/> of one core plugin: a "Plugin action" entry (<see cref="AuditActions.PluginCall"/>)
/// with the plugin's name as its target, like an audited page call; the user is the current caller, "system" in the
/// plugin's own timers (<see cref="AuditLog.WriteAsync(string, string, string?, CancellationToken)"/>).
/// </summary>
internal sealed class HostPluginAudit(AuditLog audit, ICorePlugin plugin, string pluginId) : IPluginAudit
{
    public Task WriteAsync(string detail, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return audit.WriteAsync(AuditActions.PluginCall, NameOf(), detail, ct);
    }

    private string NameOf()
    {
        try
        {
            return plugin.DisplayName is { Length: > 0 } name ? name : pluginId;
        }
#pragma warning disable CA1031 // A plugin's name that throws must not lose the entry.
        catch (Exception)
#pragma warning restore CA1031
        {
            return pluginId;
        }
    }
}
