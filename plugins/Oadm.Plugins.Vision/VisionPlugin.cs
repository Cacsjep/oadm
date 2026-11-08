using Oadm.Plugins.Shared;
using Oadm.Plugins.Vision.Api;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision;

/// <summary>
/// Core plugin "Vision Center" (docs/spec/vision-center.md): pipelines of nodes on the user's own ONNX models, read only for
/// devices. An add-on (docs/spec/add-ons.md), off by default (plugin.json). SKELETON: the runtime, store, events, actions
/// and page methods are filled in by the build stages (Pipelines/, Models/, Events/, Api/).
/// </summary>
public sealed partial class VisionPlugin : ICorePlugin, IAsyncDisposable
{
    private ICorePluginContext? _ctx;

    public string Id => VisionInfo.PluginId;

    public string DisplayName => VisionInfo.DisplayName;

    public string? IconKey => VisionInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Vision;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _ctx = null;
        return Task.CompletedTask;
    }

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, VisionInfo.ViewPermissionName, "See the vision pipelines and what they find.") { ViewerDefault = true },
        new(VisionInfo.ChangePermission, VisionInfo.ChangePermissionName, "Create, change and delete vision pipelines."),
        new(VisionInfo.ModelsPermission, VisionInfo.ModelsPermissionName, "Upload, change and delete vision models."),
    ];

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(new ContextCall(_ctx ?? throw new InvalidOperationException("The Vision Center plugin is not running.")), method, payloadJson, ct);

    public Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("Unknown method " + method + ".");

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
