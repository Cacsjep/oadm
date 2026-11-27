namespace Oadm.Sdk.Plugins;

/// <summary>
/// The server's audit log for what a core plugin does by itself (SDK 0.17.0, <see cref="ICorePluginContext.Audit"/>): a
/// timer or a sweep that changes the plugin's data without a user's call, e.g. License Plate Center deleting the plates whose
/// time ended. The host writes a "Plugin action" entry like an audited page call (<see cref="ICorePlugin.IsAudited"/>), the
/// plugin's name as its target and <c>system</c> as its user (the user of the call when one runs). Calls a user makes are
/// audited by the host already: do not write them again.
/// </summary>
public interface IPluginAudit
{
    /// <summary>
    /// Writes one entry. One entry per run, not per item ("Deleted 3 plates whose time ended"); never a secret or personal
    /// data. Never throws for a failed write (the host logs it).
    /// </summary>
    /// <param name="detail">What the plugin did, in plain words; cut at 500 characters.</param>
    /// <param name="ct">Cancels the write.</param>
    Task WriteAsync(string detail, CancellationToken ct);
}
