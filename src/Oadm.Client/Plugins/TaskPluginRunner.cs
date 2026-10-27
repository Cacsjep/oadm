using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;
using Oadm.Sdk.Devices;

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
                var result = await dialogs.ShowTaskPluginDialogAsync(dialog, devices).ConfigureAwait(true);
                payload = result?.PayloadJson;
                if (result is not null)
                {
                    // Only devices of the selection; the dialog left out those where the task would change nothing.
                    var chosen = result.DeviceIds.ToHashSet();
                    devices = [.. devices.Where(d => chosen.Contains(((IDeviceInfo)d).Id))];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A broken plugin dialog never takes the client down (production hardening 4).
                LogDialogFailed(logger, ex, plugin.Id);
                await dialogs.ShowMessageAsync(plugin.DisplayName, $"The {plugin.DisplayName} dialog failed: {ex.Message}").ConfigureAwait(true);
                return null;
            }

            if (payload is null || devices.Count == 0)
            {
                return null;
            }
        }

        try
        {
            var ids = devices.Select(d => d.Id).ToList();
            IReadOnlyList<string> taskIds = Work is { } work
                ? await work.RunAsync(StartingText(ids.Count), () => api.RunTaskAsync(plugin.Id, ids, payload, OwnerName, ct)).ConfigureAwait(true)
                : await api.RunTaskAsync(plugin.Id, ids, payload, OwnerName, ct).ConfigureAwait(true);
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

    /// <summary>The progress row of the page that starts the tasks (the Devices page); null: none.</summary>
    public Oadm.Client.Infrastructure.WorkIndicator? Work { get; set; }

    /// <summary>"Starting tasks on 30,000 devices".</summary>
    public static string StartingText(int devices) =>
        devices == 1 ? "Starting the task" : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Starting tasks on {devices:N0} devices");

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {Name} started for {Count} device(s)")]
    private static partial void LogStarted(ILogger logger, string name, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Task {Name} could not be started")]
    private static partial void LogRunFailed(ILogger logger, Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "The dialog of task plugin {PluginId} failed")]
    private static partial void LogDialogFailed(ILogger logger, Exception ex, string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No client dialog installed for task plugin {PluginId}")]
    private static partial void LogDialogMissing(ILogger logger, string pluginId);
}
