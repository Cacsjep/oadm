using System.Text.Json;

using Oadm.Plugins.EdgeRecordingExport.Recordings;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.EdgeRecordingExport;

/// <summary>
/// Core plugin "Edge Recording Export": finds the recordings on the SD card or network share of chosen cameras in a time
/// range and passes each one's export (MP4, else Matroska) on to the client, which saves them in a folder per camera.
/// Off by default (plugin.json). The page drives the work camera by camera and file by file; the server keeps no state
/// and stores no file. Read-only for the cameras; every camera comes from the user's call (device groups, SDK 0.4.0).
/// </summary>
public sealed class EdgeRecordingExportPlugin : ICorePlugin, ICorePluginDownloads
{
    private readonly EdgeRecordingOptions _options;
    private EdgeRecordingService? _service;

    public EdgeRecordingExportPlugin()
        : this(new EdgeRecordingOptions())
    {
    }

    public EdgeRecordingExportPlugin(EdgeRecordingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => EdgeRecordingInfo.PluginId;

    public string DisplayName => EdgeRecordingInfo.DisplayName;

    public string? IconKey => EdgeRecordingInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Maintenance;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, "Show the page", "See the page and search the recordings of the cameras.") { ViewerDefault = true },
        new(EdgeRecordingInfo.ExportKey, "Export recordings", "Download recordings from the cameras to this computer."),
    ];

    public string? RequiredPermission(string method) => method switch
    {
        EdgeRecordingMethods.Search => PermissionKeys.Show,
        EdgeRecordingMethods.Export => EdgeRecordingInfo.ExportKey,
        _ => null,
    };

    /// <summary>Every downloaded recording is in the audit log (camera and time range).</summary>
    public bool IsAudited(string method) => method == EdgeRecordingMethods.Export;

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _service = new EdgeRecordingService(ctx, _options);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _service = null;
        return Task.CompletedTask;
    }

    /// <summary>Only through the user's call (<see cref="InvokeAsync(ICallContext, string, string?, CancellationToken)"/>).</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("Call the Edge Recording Export plugin with the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The Edge Recording Export plugin is not running.");
        return method switch
        {
            EdgeRecordingMethods.Search => JsonSerializer.Serialize(
                await service.SearchAsync(caller, Read<SearchRequest>(payloadJson), ct).ConfigureAwait(false), EdgeRecordingInfo.Json),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    /// <summary>Only through the user's call (<see cref="OpenDownloadAsync(ICallContext, string, string?, CancellationToken)"/>).</summary>
    public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("Call the Edge Recording Export plugin with the user's call.");

    public Task<PluginDownload> OpenDownloadAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The Edge Recording Export plugin is not running.");
        return method == EdgeRecordingMethods.Export
            ? service.OpenExportAsync(caller, Read<ExportRequest>(payloadJson), ct)
            : throw new ArgumentException($"Unknown download '{method}'.", nameof(method));
    }

    private static T Read<T>(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json ?? string.Empty, EdgeRecordingInfo.Json) ?? throw new ArgumentException("The request is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The request cannot be read: " + ex.Message, ex);
        }
    }
}
