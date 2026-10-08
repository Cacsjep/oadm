using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins;

/// <summary>Runs a task plugin for a selection: opens the plugin dialog first when required, then calls TaskService.Run.</summary>
public sealed partial class TaskPluginRunner(
    IOadmApi api,
    IClientPluginRegistry registry,
    IDialogService dialogs,
    ILogger<TaskPluginRunner> logger)
{
    public static string OwnerName => $"{Environment.UserName}@{Environment.MachineName}";

    /// <returns>The task ids (one per device), or null when nothing was started (cancelled dialog, missing dialog, error).</returns>
    public async Task<IReadOnlyList<string>?> RunAsync(TaskPluginInfo plugin, IReadOnlyList<DeviceRowViewModel> devices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
        {
            return null;
        }

        string? payload = null;
        if (plugin.RequiresDialog)
        {
            var dialog = registry.FindDialog(plugin.Id);
            if (dialog is null)
            {
                LogDialogMissing(logger, plugin.Id);
                await dialogs.ShowMessageAsync(plugin.DisplayName,
                    $"The task \"{plugin.DisplayName}\" needs a client plugin ({plugin.Id}) that is not installed on this computer.").ConfigureAwait(true);
                return null;
            }

            try
            {
                payload = await dialogs.ShowTaskPluginDialogAsync(dialog, devices).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A broken plugin dialog never takes the client down (production hardening 4).
                LogDialogFailed(logger, ex, plugin.Id);
                await dialogs.ShowMessageAsync(plugin.DisplayName, $"The {plugin.DisplayName} dialog failed: {ex.Message}").ConfigureAwait(true);
                return null;
            }

            if (payload is null)
            {
                return null;
            }
        }

        try
        {
            IReadOnlyList<string> taskIds = await api.RunTaskAsync(plugin.Id, devices.Select(d => d.Id).ToList(), payload, OwnerName, ct).ConfigureAwait(true);
            LogStarted(logger, plugin.DisplayName, devices.Count);
            return taskIds;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRunFailed(logger, ex, plugin.DisplayName);
            await dialogs.ShowMessageAsync(plugin.DisplayName, "The task could not be started: " + ex.Message).ConfigureAwait(true);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {Name} started for {Count} device(s)")]
    private static partial void LogStarted(ILogger logger, string name, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Task {Name} could not be started")]
    private static partial void LogRunFailed(ILogger logger, Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "The dialog of task plugin {PluginId} failed")]
    private static partial void LogDialogFailed(ILogger logger, Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No client dialog installed for task plugin {PluginId}")]
    private static partial void LogDialogMissing(ILogger logger, string pluginId);
}
