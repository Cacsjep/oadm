using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr;

/// <summary>
/// Core plugin "Text recognition" (docs/spec/ocr.md): reads text in the cameras' pictures and hands it to the event rules.
/// An add-on (docs/spec/add-ons.md), off by default (plugin.json). Read only for devices. Page backend methods:
/// <see cref="OcrMethods"/>. The readers, recognition, events and the action Read text follow in the next build steps.
/// </summary>
public sealed class OcrPlugin : ICorePlugin, IAsyncDisposable
{
    private const string NotRunning = "The Text recognition plugin is not running.";
    private ICorePluginContext? _ctx;

    public string Id => OcrInfo.PluginId;

    public string DisplayName => OcrInfo.DisplayName;

    public string? IconKey => OcrInfo.IconKey;

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

    /// <summary>Nothing changes the server's configuration beyond the plugin's own readers: no method is for administrators only.</summary>
    public UserRole RequiredRole(string method) => UserRole.Operator;

    public bool IsAudited(string method) => method is OcrMethods.SaveReader or OcrMethods.DeleteReader;

    /// <summary>The page reads only (Viewer on); changing readers and reading now are a right of their own.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, "Show the page", "See the text readers and what they read.") { ViewerDefault = true },
        new(OcrInfo.ChangePermission, OcrInfo.ChangePermissionName, "Create, change and delete text readers and read now."),
    ];

    /// <summary>New, Delete, Save and Read now: "change"; the rest: "show".</summary>
    public string? RequiredPermission(string method) =>
        method is OcrMethods.SaveReader or OcrMethods.DeleteReader or OcrMethods.ReadNow ? OcrInfo.ChangePermission : PermissionKeys.Show;

    /// <summary>A user's call. Not built yet: every method answers that the plugin is not running.</summary>
    public Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        _ = _ctx ?? throw new InvalidOperationException(NotRunning);
        return method switch
        {
            OcrMethods.ListReaders => Task.FromResult<string?>(OcrJson.Serialize(new ReaderList())),
            OcrMethods.GetReader or OcrMethods.SaveReader or OcrMethods.DeleteReader or OcrMethods.ReadNow or OcrMethods.GetReading =>
                throw new NotSupportedException($"{method} is not built yet."),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    /// <summary>In-process calls without a user (the host itself, tests): as a user with all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(new ContextCall(_ctx ?? throw new InvalidOperationException(NotRunning)), method, payloadJson, ct);

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
