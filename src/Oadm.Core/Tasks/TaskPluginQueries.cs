using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Plugins;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tasks;

/// <summary>
/// Runs read-only <see cref="ITaskPluginQuery"/> calls for task plugin dialogs (gRPC <c>TaskService.Query</c>):
/// resolves the plugin and the device, hands the plugin a pre-authenticated VAPIX client and enforces
/// a timeout. Every failure becomes a <see cref="TaskQueryException"/> with a message for the user.
/// </summary>
public sealed partial class TaskPluginQueries
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly PluginRegistry _plugins;
    private readonly IDeviceRepository _devices;
    private readonly IVapixClientFactory _vapix;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public TaskPluginQueries(PluginRegistry plugins, IDeviceRepository devices, IVapixClientFactory vapix, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(vapix);
        _plugins = plugins;
        _devices = devices;
        _vapix = vapix;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<TaskPluginQueries>();
    }

    /// <summary>Upper bound for one query, including the VAPIX calls. Default 30 s.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>The routes of the devices for <c>ITaskQueryContext.Network</c>; null = the server's own network only.</summary>
    public Sites.DeviceNetworks? Networks { get; init; }

    /// <summary>
    /// The query of a user's task dialog: <paramref name="device"/> (exactly one) comes from the caller's view, the plugin gets
    /// the caller's call (<see cref="ITaskPluginQuery.QueryAsync(ITaskQueryContext, ICallContext, IDeviceInfo, string, string?, CancellationToken)"/>)
    /// and, for a user with a device group, <see cref="ITaskQueryContext.Devices"/> holds only the devices of the view.
    /// </summary>
    public Task<string?> QueryAsync(string pluginId, DeviceGroups.ViewedDevices device, string method, string? payloadJson, ICallContext caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(caller);
        if (device.Count != 1)
        {
            throw new ArgumentException("A query reads exactly one device.", nameof(device));
        }

        var found = device[0];
        return QueryCoreAsync(pluginId, found.Id, _ => Task.FromResult<IDeviceInfo?>(found), method, payloadJson, caller, ct);
    }

    /// <summary>A query without a user (tests, system work): the device by id from the full repository.</summary>
    internal Task<string?> QueryAsync(string pluginId, Guid deviceId, string method, string? payloadJson, CancellationToken ct) =>
        QueryCoreAsync(pluginId, deviceId, token => _devices.FindAsync(deviceId, token), method, payloadJson, null, ct);

    private async Task<string?> QueryCoreAsync(string pluginId, Guid deviceId, Func<CancellationToken, Task<IDeviceInfo?>> find, string method, string? payloadJson, ICallContext? caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new TaskQueryException(TaskQueryError.InvalidArgument, "A query method is required.");
        }

        if (string.IsNullOrWhiteSpace(pluginId) || !_plugins.TryGetTaskPlugin(pluginId, out var registration))
        {
            throw new TaskQueryException(TaskQueryError.NotFound, $"Unknown task plugin '{pluginId}'.");
        }

        if (registration.Plugin is not ITaskPluginQuery query)
        {
            throw new TaskQueryException(TaskQueryError.NotSupported, $"Task plugin '{pluginId}' does not support queries.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var token = timeout.Token;
        try
        {
            var device = await find(token).ConfigureAwait(false)
                ?? throw new TaskQueryException(TaskQueryError.NotFound, DeviceMessages.Removed);
            if (device.Status == DeviceStatus.CertificateChanged)
            {
                throw new TaskQueryException(TaskQueryError.FailedPrecondition, DeviceMessages.CertificateChanged);
            }

            if (device.Status == DeviceStatus.SiteOffline)
            {
                throw new TaskQueryException(TaskQueryError.FailedPrecondition, Sdk.Sites.SiteTexts.SiteOffline(device.SiteName));
            }

            if (registration.SiteRefusal(device) is { } site)
            {
                throw new TaskQueryException(TaskQueryError.FailedPrecondition, site);
            }

            var vapix = await _vapix.CreateAsync(deviceId, token).ConfigureAwait(false);
            var logger = _loggerFactory.CreateLogger("Oadm.Plugins." + registration.Id);
            var network = (Networks ?? Sites.DeviceNetworks.Local).For(device);
            if (caller is null)
            {
                return await query.QueryAsync(new QueryContext(vapix, logger, _devices, network), device, method, payloadJson, token).ConfigureAwait(false);
            }

            // A user with a device group: the dialog's reads of other devices stay inside the view.
            var devices = caller.HasAllDevices ? _devices : caller.Devices;
            return await query.QueryAsync(new QueryContext(vapix, logger, devices, network), caller, device, method, payloadJson, token).ConfigureAwait(false);
        }
        catch (TaskQueryException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TaskQueryException(TaskQueryError.Timeout, $"The device did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DeviceNotCompatibleException ex)
        {
            throw new TaskQueryException(TaskQueryError.FailedPrecondition, ex.Message, ex);
        }
        catch (VapixAuthenticationException ex)
        {
            throw new TaskQueryException(TaskQueryError.FailedPrecondition, "The device rejected the stored credentials.", ex);
        }
        catch (Exception ex) when (ex is VapixException or HttpRequestException)
        {
            throw new TaskQueryException(TaskQueryError.Unavailable, ex.Message, ex);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException)
        {
            throw new TaskQueryException(TaskQueryError.InvalidArgument, ex.Message, ex);
        }
#pragma warning disable CA1031 // Plugin code is untrusted; its failures go back to the dialog as a message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogQueryFailed(ex, registration.Id, method, deviceId);
            throw new TaskQueryException(TaskQueryError.Failed, ex.Message, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query {Method} of task plugin {PluginId} failed for device {DeviceId}")]
    private partial void LogQueryFailed(Exception ex, string pluginId, string method, Guid deviceId);

    private sealed class QueryContext(IVapixClient vapix, ILogger logger, IDeviceRepository devices, Sdk.Sites.IDeviceNetwork network) : ITaskQueryContext
    {
        public IVapixClient Vapix { get; } = vapix;

        public ILogger Logger { get; } = logger;

        public IDeviceRepository? Devices { get; } = devices;

        public Sdk.Sites.IDeviceNetwork? Network { get; } = network;
    }
}

/// <summary>Why a plugin query failed; the gRPC layer maps it to a status code.</summary>
public enum TaskQueryError
{
    Failed,
    NotFound,
    NotSupported,
    InvalidArgument,
    FailedPrecondition,
    Unavailable,
    Timeout,
}

/// <summary>A plugin query failed. <see cref="Exception.Message"/> is meant for the user.</summary>
public sealed class TaskQueryException : Exception
{
    public TaskQueryException()
    {
    }

    public TaskQueryException(string message)
        : base(message)
    {
    }

    public TaskQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TaskQueryException(TaskQueryError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public TaskQueryError Error { get; }
}
