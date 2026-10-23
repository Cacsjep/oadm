using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Shared;

/// <summary>
/// The call of the old <c>InvokeAsync(method, payload, ct)</c> overload (SDK 0.4.0): the host uses it only for in-process work
/// without a user (and tests), never for a user's call (that one always comes with its <see cref="ICallContext"/>). It
/// reaches devices through the context's own members and owns objects for all devices, like a plugin before device groups.
/// </summary>
internal sealed class ContextCall(ICorePluginContext ctx) : ICallContext
{
    public string UserName => "OADM";

    public string SessionId => "oadm";

    public string DeviceScopeKey => ICallContext.AllDevicesScopeKey;

    public bool HasAllDevices => true;

    public IDeviceRepository Devices => ctx.Devices;

    public IVapixClientFactory Vapix => ctx.Vapix;

    public ITaskRunner Tasks => ctx.Tasks;

    public IDeviceEventStreams? EventStreams => ctx.EventStreams;

    public bool Has(string permission) => true;

    public bool Contains(IDeviceInfo device) => true;
}

/// <summary>
/// Objects a plugin keeps for a device scope (docs/spec/device-scope.md, Shared objects: device triggers, Live View views,
/// dashboards): each stores the <see cref="ICallContext.DeviceScopeKey"/> of the user who made it; objects stored before
/// device groups have none and belong to all devices. Users with a group see and change only their group's objects, users
/// with all devices every object; an edit keeps the owner; an object of a deleted group is off.
/// </summary>
internal static class OwnerScope
{
    /// <summary>The owner of a stored object (none = all devices, objects from before device groups).</summary>
    public static string Of(string? stored) => string.IsNullOrWhiteSpace(stored) ? ICallContext.AllDevicesScopeKey : stored;

    /// <summary>The refusal of a save by a user without devices (no group, or the group was deleted).</summary>
    public const string NoDevicesRefusal = "Your account has no devices.";

    /// <summary>
    /// Whether <paramref name="caller"/> has no devices: not all devices and no group. A group's scope key is the group's id
    /// (a GUID); any other key of a user without all devices ("none", or a key the host makes per user or session) is no
    /// group, whatever the host calls it. Such callers share no objects, jobs or slots (fail closed).
    /// </summary>
    public static bool HasNoDevices(ICallContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return !caller.HasAllDevices && !Guid.TryParse(caller.DeviceScopeKey, out _);
    }

    /// <summary>
    /// Whether <paramref name="caller"/> sees and may change an object of <paramref name="owner"/>; never for a caller
    /// without devices (<see cref="HasNoDevices"/>), so such users never see each other's objects.
    /// </summary>
    public static bool Sees(ICallContext caller, string? owner)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.HasAllDevices
            || (!HasNoDevices(caller) && string.Equals(Of(owner), caller.DeviceScopeKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the owner still exists (false for a deleted group: its objects are off). Hosts without groups: only all devices.</summary>
    public static bool Exists(ICorePluginContext ctx, string? owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var key = Of(owner);
        return ctx.DeviceScopes?.Exists(key) ?? key == ICallContext.AllDevicesScopeKey;
    }

    /// <summary>
    /// The owner a saved object gets: a new one the caller's scope; a changed one keeps its owner, except that a user with
    /// all devices who saves an object of a deleted group gives it to all devices (it runs again). A caller without devices
    /// (<see cref="HasNoDevices"/>) saves nothing (<see cref="DeviceAccessRefusedException"/>, <see cref="NoDevicesRefusal"/>).
    /// </summary>
    public static string ForSave(ICorePluginContext ctx, ICallContext caller, string? stored, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (HasNoDevices(caller))
        {
            throw new DeviceAccessRefusedException(NoDevicesRefusal);
        }

        if (isNew)
        {
            return caller.DeviceScopeKey;
        }

        var owner = Of(stored);
        return !Exists(ctx, owner) && caller.HasAllDevices ? ICallContext.AllDevicesScopeKey : owner;
    }

    /// <summary>
    /// The devices of an object's owner, for the checks of a save: the caller's own devices when the caller is of that scope,
    /// else (a user with all devices changing a group's object) the group's devices; none for a deleted group.
    /// </summary>
    public static IDeviceRepository DevicesOf(ICorePluginContext ctx, ICallContext caller, string owner)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(caller);
        if (string.Equals(owner, caller.DeviceScopeKey, StringComparison.OrdinalIgnoreCase))
        {
            return caller.Devices;
        }

        return ctx.DeviceScopes?.Devices(owner) ?? (owner == ICallContext.AllDevicesScopeKey ? caller.Devices : NoDevices.Instance);
    }

    /// <summary>
    /// Refuses device ids outside the owner's devices before a save (<see cref="DevicesNotAvailableException"/>, the same
    /// as for unknown ids). Objects of all devices keep removed devices as they did before device groups (their tiles say
    /// "Removed device"); objects of a group take only devices the group has now.
    /// </summary>
    public static async Task RequireDevicesAsync(ICorePluginContext ctx, ICallContext caller, string owner, IEnumerable<Guid> ids, CancellationToken ct)
    {
        if (owner == ICallContext.AllDevicesScopeKey && caller.HasAllDevices)
        {
            return;
        }

        var repository = DevicesOf(ctx, caller, owner);
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var known = (await repository.ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
        var missing = wanted.Count(id => !known.Contains(id));
        if (missing > 0)
        {
            throw new DevicesNotAvailableException(missing);
        }
    }

    /// <summary>
    /// A user with all devices who saves an object another scope owns (a group's view or dashboard): the device ids the
    /// stored object already had (<paramref name="stored"/>) that are not in the owner's devices now (they left the group or
    /// were removed). The save drops them from the object and says how many, so the group never gets a device outside it and
    /// the edit still succeeds. A device the save adds keeps the refusal of <see cref="RequireDevicesAsync"/> (NOT_FOUND), and
    /// so does every id of a caller of the owner's scope.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> LeftOwnerAsync(ICorePluginContext ctx, ICallContext caller, string owner, IEnumerable<Guid> ids, IEnumerable<Guid> stored, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(stored);
        if (!caller.HasAllDevices || string.Equals(owner, caller.DeviceScopeKey, StringComparison.OrdinalIgnoreCase))
        {
            return new HashSet<Guid>();
        }

        var wanted = ids.ToHashSet();
        wanted.IntersectWith(stored);
        if (wanted.Count == 0)
        {
            return wanted;
        }

        var known = (await DevicesOf(ctx, caller, owner).ListAsync(ct).ConfigureAwait(false)).Select(d => d.Id).ToHashSet();
        wanted.ExceptWith(known);
        return wanted;
    }

    private sealed class NoDevices : IDeviceRepository
    {
        public static readonly NoDevices Instance = new();

        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([]);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<IDeviceInfo?>(null);
    }
}

/// <summary>Device id checks of the converted page methods (SDK 0.4.0).</summary>
internal static class CallDevices
{
    /// <summary>
    /// The devices of <paramref name="ids"/> in the caller's view, in order and without duplicates; any id outside the view
    /// or unknown refuses the whole request (<see cref="DevicesNotAvailableException"/>, the same answer for both). One list
    /// read for many ids (30,000 ids: one pass), one lookup for a few.
    /// </summary>
    public static async Task<IReadOnlyList<IDeviceInfo>> RequireAsync(ICallContext caller, IEnumerable<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var found = new List<IDeviceInfo>(wanted.Count);
        var missing = 0;
        if (wanted.Count <= 8)
        {
            foreach (var id in wanted)
            {
                if (await caller.Devices.FindAsync(id, ct).ConfigureAwait(false) is { } device)
                {
                    found.Add(device);
                }
                else
                {
                    missing++;
                }
            }
        }
        else
        {
            var byId = (await caller.Devices.ListAsync(ct).ConfigureAwait(false)).ToDictionary(d => d.Id);
            foreach (var id in wanted)
            {
                if (byId.TryGetValue(id, out var device))
                {
                    found.Add(device);
                }
                else
                {
                    missing++;
                }
            }
        }

        return missing > 0 ? throw new DevicesNotAvailableException(missing) : found;
    }
}
