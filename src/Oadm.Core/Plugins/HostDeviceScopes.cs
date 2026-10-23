using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>
/// The device groups as core plugins see them (<see cref="ICorePluginContext.DeviceScopes"/>, docs/spec/device-scope.md,
/// Plugins): objects a plugin keeps per group (dashboards, device triggers, saved views, sweeps) are stored with
/// <see cref="ICallContext.DeviceScopeKey"/> and run only on that scope's devices. A key that is neither "all" nor a group
/// that exists covers nothing (fail closed: a deleted group's objects are off). The checks are a few hash lookups on the
/// device row (<see cref="DeviceView.Contains(IDeviceInfo)"/>), no request. <see cref="Changed"/> follows the group store,
/// raised without a caller.
/// </summary>
public sealed class HostDeviceScopes : IDeviceScopes, IDisposable
{
    private readonly DeviceGroupStore _groups;
    private readonly DeviceViewFactory _views;

    public HostDeviceScopes(DeviceGroupStore groups, DeviceViewFactory views)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(views);
        _groups = groups;
        _views = views;
        _groups.Changed += OnGroupChanged;
        _groups.Deleted += OnGroupDeleted;
    }

    public event EventHandler<string>? Changed;

    /// <summary>Loads the groups (the host does it before the core plugins start).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await _groups.LoadAsync(ct).ConfigureAwait(false);
        await _views.Catalog.EnsureLoadedAsync(ct).ConfigureAwait(false);
    }

    public bool Exists(string scopeKey) => IsAll(scopeKey) || GroupOf(scopeKey) is not null;

    public bool Contains(string scopeKey, IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (IsAll(scopeKey))
        {
            return true;
        }

        return GroupOf(scopeKey) is { } id && _views.FilterOf(id).Contains(device);
    }

    public IDeviceRepository Devices(string scopeKey) => new ScopeDevices(this, scopeKey);

    public bool InAnyGroup(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_groups.Loaded is not { } loaded)
        {
            return true; // groups not loaded yet: fail closed
        }

        foreach (var id in loaded.Keys)
        {
            if (_views.FilterOf(id).Contains(device))
            {
                return true;
            }
        }

        return false;
    }

    public string? NameOf(string scopeKey) =>
        IsAll(scopeKey) ? DeviceGroupTexts.AllDevices : GroupOf(scopeKey) is { } id ? _groups.Loaded![id].Name : null;

    public void Dispose()
    {
        _groups.Changed -= OnGroupChanged;
        _groups.Deleted -= OnGroupDeleted;
    }

    private static bool IsAll(string? scopeKey) => string.Equals(scopeKey, ICallContext.AllDevicesScopeKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>The id of an existing group with this key; null otherwise (unknown, deleted, groups not loaded yet).</summary>
    private Guid? GroupOf(string? scopeKey) =>
        Guid.TryParse(scopeKey, out var id) && _groups.Loaded is { } loaded && loaded.ContainsKey(id) ? id : null;

    private void OnGroupChanged(object? sender, Guid groupId) => CallerContext.Raise(Changed, this, groupId.ToString());

    private void OnGroupDeleted(object? sender, DeviceGroupDeleteResult deleted) => CallerContext.Raise(Changed, this, deleted.Id.ToString());

    /// <summary>The devices of a scope as of each read (a group's current rules; nothing for a deleted group).</summary>
    private sealed class ScopeDevices(HostDeviceScopes scopes, string scopeKey) : IDeviceRepository
    {
        public async Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) =>
            [.. await (await ViewAsync(ct).ConfigureAwait(false)).ListAsync(ct).ConfigureAwait(false)];

        public async Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) =>
            await (await ViewAsync(ct).ConfigureAwait(false)).FindAsync(id, ct).ConfigureAwait(false);

        private Task<DeviceView> ViewAsync(CancellationToken ct)
        {
            var scope = IsAll(scopeKey) ? DeviceScope.All
                : Guid.TryParse(scopeKey, out var id) ? DeviceScope.Group(id)
                : DeviceScope.None;
            return scopes._views.ForScopeAsync(scope, null, ct);
        }
    }
}
