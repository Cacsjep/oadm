using System.Text.Json;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LiveView;

/// <summary>
/// Core plugin "Live View": saved views of several cameras, shared by the users of the same devices. The server part keeps
/// the views in the plugin settings and tells every open page about a change; the video itself goes through the host's live
/// view (gRPC LiveViewService) like the Devices page's side panel. Each view belongs to the device scope of the user who
/// made it (<see cref="SavedView.Owner"/>, docs/spec/device-scope.md, Shared objects): users of a device group see and
/// change only their group's views, with only their group's cameras; users with all devices every view.
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

    /// <summary>In-process calls without a user (the host itself, tests): every view, owned by all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => InvokeAsync(System, method, payloadJson, ct);

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        switch (method)
        {
            case "list":
                return Serialize(await ListAsync(caller, ct).ConfigureAwait(false));
            case "save":
                return JsonSerializer.Serialize(await SaveAsync(caller, Read<SaveViewRequest>(payloadJson), ct).ConfigureAwait(false), LiveViewPluginInfo.Json);
            case "delete":
                await DeleteAsync(caller, Read<DeleteViewRequest>(payloadJson).Id, ct).ConfigureAwait(false);
                return null;
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    /// <summary>Every view (tests: a user with all devices).</summary>
    internal Task<ViewList> ListAsync(CancellationToken ct) => ListAsync(System, ct);

    /// <summary>
    /// The caller's views sorted by name: users with all devices every view, users of a group their group's views with only
    /// the cameras the group has now (a camera that left the group or was removed is left out).
    /// </summary>
    internal async Task<ViewList> ListAsync(ICallContext caller, CancellationToken ct)
    {
        List<SavedView> views;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            views = await LoadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (caller.HasAllDevices)
        {
            return new ViewList([.. Sorted(views)]);
        }

        var mine = views.Where(v => OwnerScope.Sees(caller, v.Owner)).ToList();
        if (mine.Count == 0)
        {
            return new ViewList([]);
        }

        var devices = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
        return new ViewList([.. Sorted(mine.Select(v => Within(v, devices)))]);
    }

    /// <summary>Creates or changes a view (tests: a user with all devices).</summary>
    internal Task<SavedView> SaveAsync(SaveViewRequest request, CancellationToken ct) => SaveAsync(System, request, ct);

    /// <summary>
    /// Creates or changes a view after checking its name, layout, version and the limits. A new view belongs to the caller's
    /// devices; a change keeps the owner (a view of another group answers like a deleted one). Every camera must be in the
    /// owner's devices (else NOT_FOUND, nothing stored); names are unique among the owner's views.
    /// </summary>
    internal async Task<SavedView> SaveAsync(ICallContext caller, SaveViewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
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
            var index = isNew ? -1 : views.FindIndex(v => v.Id == request.Id && OwnerScope.Sees(caller, v.Owner));
            if (!isNew && index < 0)
            {
                throw new ArgumentException(ViewRules.Gone);
            }

            var owner = OwnerScope.ForSave(Context, caller, index >= 0 ? views[index].Owner : null, isNew);
            await OwnerScope.RequireDevicesAsync(Context, caller, owner, tiles.Select(t => t.DeviceId), ct).ConfigureAwait(false);

            if (index >= 0 && request.Version != 0 && request.Version != views[index].Version)
            {
                throw new ArgumentException(ViewRules.ChangedMeanwhile(views[index].Name));
            }

            if (views.Where((v, i) => i != index && OwnerScope.Of(v.Owner) == owner).Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(ViewRules.NameTaken(name));
            }

            if (isNew && views.Count >= LiveViewPluginInfo.MaxViews)
            {
                throw new ArgumentException(ViewRules.TooManyViews);
            }

            var saved = new SavedView(isNew ? Guid.NewGuid().ToString("N") : request.Id!, name, index >= 0 ? views[index].Version + 1 : 1, [.. tiles])
            {
                Owner = owner == ICallContext.AllDevicesScopeKey ? null : owner,
            };
            var next = new List<SavedView>(views);
            if (index >= 0)
            {
                next[index] = saved;
            }
            else
            {
                next.Add(saved);
            }

            await StoreAsync(next, owner, ct).ConfigureAwait(false);
            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes a view (tests: a user with all devices).</summary>
    internal Task DeleteAsync(string id, CancellationToken ct) => DeleteAsync(System, id, ct);

    /// <summary>Deletes a view of the caller's devices; an unknown id (or a view of another group) is nothing to do.</summary>
    internal async Task DeleteAsync(ICallContext caller, string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var views = await LoadAsync(ct).ConfigureAwait(false);
            if (views.Find(v => v.Id == id && OwnerScope.Sees(caller, v.Owner)) is { } gone)
            {
                views.Remove(gone);
                await StoreAsync([.. views], OwnerScope.Of(gone.Owner), ct).ConfigureAwait(false);
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

    /// <summary>
    /// Stores the views, then tells the open pages: users with all devices get every view, the users of the changed view's
    /// group get their group's views with only the group's cameras.
    /// </summary>
    private async Task StoreAsync(List<SavedView> views, string changedOwner, CancellationToken ct)
    {
        var ctx = Context;
        // Once the write started it finishes: a cancelled caller never leaves the cache and the stored list apart.
        await ctx.Settings.SetAsync(ViewsKey, JsonSerializer.Serialize(views, LiveViewPluginInfo.Json), CancellationToken.None).ConfigureAwait(false);
        _views = views;
        if (ctx.Events is not { } events)
        {
            return;
        }

        events.Publish(LiveViewPluginInfo.ViewsTopic, Serialize(new ViewList([.. Sorted(views)])), PluginEventAudience.AllDevices);
        if (changedOwner != ICallContext.AllDevicesScopeKey && ctx.DeviceScopes is { } scopes && scopes.Exists(changedOwner))
        {
            var devices = (await scopes.Devices(changedOwner).ListAsync(CancellationToken.None).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
            var group = views.Where(v => OwnerScope.Of(v.Owner) == changedOwner).Select(v => Within(v, devices));
            events.Publish(LiveViewPluginInfo.ViewsTopic, Serialize(new ViewList([.. Sorted(group)])), PluginEventAudience.DeviceScope(changedOwner));
        }
    }

    /// <summary>A view with only the cameras of <paramref name="devices"/>.</summary>
    private static SavedView Within(SavedView view, HashSet<Guid> devices) =>
        view.Tiles.All(t => devices.Contains(t.DeviceId)) ? view : view with { Tiles = [.. view.Tiles.Where(t => devices.Contains(t.DeviceId))] };

    private ICorePluginContext Context => _ctx ?? throw new InvalidOperationException("The Live View plugin is not running.");

    /// <summary>The call of in-process work without a user (all devices).</summary>
    private ContextCall System => new(Context);

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
