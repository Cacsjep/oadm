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

    public async Task<string?> QueryAsync(string pluginId, Guid deviceId, string method, string? payloadJson, CancellationToken ct)
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
            var device = await _devices.FindAsync(deviceId, token).ConfigureAwait(false)
                ?? throw new TaskQueryException(TaskQueryError.NotFound, $"Device {deviceId} not found.");
            if (device.Status == DeviceStatus.CertificateChanged)
            {
                throw new TaskQueryException(TaskQueryError.FailedPrecondition,
                    "The device certificate changed. Accept the new certificate first.");
            }

            var vapix = await _vapix.CreateAsync(deviceId, token).ConfigureAwait(false);
            var logger = _loggerFactory.CreateLogger("Oadm.Plugins." + registration.Id);
            return await query.QueryAsync(new QueryContext(vapix, logger), device, method, payloadJson, token).ConfigureAwait(false);
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

    private sealed class QueryContext(IVapixClient vapix, ILogger logger) : ITaskQueryContext
    {
        public IVapixClient Vapix { get; } = vapix;

        public ILogger Logger { get; } = logger;
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
