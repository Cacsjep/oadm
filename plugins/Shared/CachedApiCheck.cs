using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Shared;

/// <summary>
/// Cheap compatibility check for <c>ITaskPlugin.CanRun</c> on the cached API list. The host calls CanRun for every
/// device times every task plugin on each ListTaskPlugins (5,000 devices x N plugins), so this is one pass over the
/// list without LINQ, sorting or parsing the required version per call. Same semantics as
/// <see cref="DeviceApiExtensions.Supports"/>: an entry with the same id and major version and at least the required
/// version (unparsable versions count as 0.0). Linked as source into each plugin assembly (internal there).
/// </summary>
internal static class CachedApiCheck
{
    private static readonly Version Unparsable = new(0, 0);

    /// <summary>True when <paramref name="apis"/> lists <paramref name="apiId"/> with the major version of <paramref name="required"/> and at least that version.</summary>
    public static bool Supports(IReadOnlyList<DeviceApi> apis, string apiId, Version required)
    {
        ArgumentNullException.ThrowIfNull(apis);
        for (var i = 0; i < apis.Count; i++)
        {
            var api = apis[i];
            if (api.Id.Length != apiId.Length || !string.Equals(api.Id, apiId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = Version.TryParse(api.Version, out var parsed) ? parsed : Unparsable;
            if (version.Major == required.Major && version >= required)
            {
                return true;
            }
        }

        return false;
    }
}
