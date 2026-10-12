using System.Text.Json;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LiveView;

/// <summary>
/// Core plugin "Live View": saved views of several cameras, shared by every user of the server. The server part keeps the
/// views in the plugin settings and tells every open page about a change; the video itself goes through the host's live
/// view (gRPC LiveViewService) like the Devices page's side panel.
/// </summary>
public sealed class LiveViewPlugin : ICorePlugin, IDisposable
{
    private const string ViewsKey = "views";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ICorePluginContext? _ctx;
    private List<SavedView>? _views;

    public string Id => LiveViewPluginInfo.PluginId;

    public string DisplayName => LiveViewPluginInfo.DisplayName;

    public string? IconKey => LiveViewPluginInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Monitoring;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>"show" comes automatically (Viewer too, a monitoring page); changing views is a right of its own.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(LiveViewPluginInfo.ManageKey, "Change views", "Create, change and delete the saved views of the Live View page."),
    ];

    public string? RequiredPermission(string method) => method switch
    {
        "list" => PermissionKeys.Show,
        "save" or "delete" => LiveViewPluginInfo.ManageKey,
        _ => null,
    };

    public bool IsAudited(string method) => method is "save" or "delete";

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _views = null;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _views = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _gate.Dispose();

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        switch (method)
        {
            case "list":
                return Serialize(await ListAsync(ct).ConfigureAwait(false));
            case "save":
                return JsonSerializer.Serialize(await SaveAsync(Read<SaveViewRequest>(payloadJson), ct).ConfigureAwait(false), LiveViewPluginInfo.Json);
            case "delete":
                await DeleteAsync(Read<DeleteViewRequest>(payloadJson).Id, ct).ConfigureAwait(false);
                return null;
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    /// <summary>Every view, sorted by name.</summary>
    internal async Task<ViewList> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return new ViewList([.. Sorted(await LoadAsync(ct).ConfigureAwait(false))]);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates or changes a view after checking its name, layout, version and the limits.</summary>
    internal async Task<SavedView> SaveAsync(SaveViewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? string.Empty;
        if (ViewRules.NameProblem(name) is { } nameProblem)
        {
            throw new ArgumentException(nameProblem);
        }

        var tiles = request.Tiles ?? [];
        if (ViewRules.TilesProblem(tiles) is { } tileProblem)
        {
            throw new ArgumentException(tileProblem);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var views = await LoadAsync(ct).ConfigureAwait(false);
            var isNew = string.IsNullOrEmpty(request.Id);
            var index = isNew ? -1 : views.FindIndex(v => v.Id == request.Id);
            if (!isNew && index < 0)
            {
                throw new ArgumentException(ViewRules.Gone);
            }

            if (index >= 0 && request.Version != 0 && request.Version != views[index].Version)
            {
                throw new ArgumentException(ViewRules.ChangedMeanwhile(views[index].Name));
            }

            if (views.Where((v, i) => i != index).Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(ViewRules.NameTaken(name));
            }

            if (isNew && views.Count >= LiveViewPluginInfo.MaxViews)
            {
                throw new ArgumentException(ViewRules.TooManyViews);
            }

            var saved = new SavedView(isNew ? Guid.NewGuid().ToString("N") : request.Id!, name, index >= 0 ? views[index].Version + 1 : 1, [.. tiles]);
            var next = new List<SavedView>(views);
            if (index >= 0)
            {
                next[index] = saved;
            }
            else
            {
                next.Add(saved);
            }

            await StoreAsync(next, ct).ConfigureAwait(false);
            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes a view; an unknown id is nothing to do.</summary>
    internal async Task DeleteAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var views = await LoadAsync(ct).ConfigureAwait(false);
            if (views.RemoveAll(v => v.Id == id) > 0)
            {
                await StoreAsync([.. views], ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<SavedView>> LoadAsync(CancellationToken ct)
    {
        if (_views is not null)
        {
            return [.. _views];
        }

        var ctx = _ctx ?? throw new InvalidOperationException("The Live View plugin is not running.");
        var json = await ctx.Settings.GetAsync(ViewsKey, ct).ConfigureAwait(false);
        List<SavedView> views = [];
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                views = JsonSerializer.Deserialize<List<SavedView>>(json, LiveViewPluginInfo.Json) ?? [];
            }
            catch (JsonException ex)
            {
                ctx.Logger.LogUnreadableViews(ex);
                await ctx.Settings.SetAsync(ViewsKey + "Unreadable", json, ct).ConfigureAwait(false); // kept, never overwritten by the next save
            }
        }

        _views = views;
        return [.. views];
    }

    private async Task StoreAsync(List<SavedView> views, CancellationToken ct)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("The Live View plugin is not running.");
        // Once the write started it finishes: a cancelled caller never leaves the cache and the stored list apart.
        await ctx.Settings.SetAsync(ViewsKey, JsonSerializer.Serialize(views, LiveViewPluginInfo.Json), CancellationToken.None).ConfigureAwait(false);
        _views = views;
        ctx.Events?.Publish(LiveViewPluginInfo.ViewsTopic, Serialize(new ViewList([.. Sorted(views)])));
    }

    private static IEnumerable<SavedView> Sorted(IEnumerable<SavedView> views) =>
        views.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase);

    private static string Serialize(ViewList list) => JsonSerializer.Serialize(list, LiveViewPluginInfo.Json);

    private static T Read<T>(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json ?? "null", LiveViewPluginInfo.Json) ?? throw new ArgumentException("The request is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The request could not be read: " + ex.Message, ex);
        }
    }
}

internal static partial class LiveViewLog
{
    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "The saved views could not be read; the list starts empty")]
    public static partial void LogUnreadableViews(this Microsoft.Extensions.Logging.ILogger logger, Exception ex);
}
