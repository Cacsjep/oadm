using Google.Protobuf.Collections;

using SdkDeviceApi = Oadm.Sdk.Vapix.DeviceApi;
using ProtoDeviceApi = Oadm.Contracts.V1.DeviceApi;

namespace Oadm.Client.Devices;

/// <summary>
/// Shares identical VAPIX API lists between device rows. Scale: 5,000 devices with about 100 APIs each
/// are 500,000 <see cref="SdkDeviceApi"/> objects, yet a site has only a few distinct lists (one per model
/// and firmware). Interned lists are immutable and compared by content; an unchanged list returns the
/// same instance, so a row can tell "no change" by reference. UI thread only (the device store).
/// </summary>
public static class DeviceApiLists
{
    /// <summary>Above this many distinct lists the table is cleared (it only ever saves memory).</summary>
    public const int MaxDistinctLists = 4096;

    private static readonly Dictionary<int, List<IReadOnlyList<SdkDeviceApi>>> Lists = [];
    private static int _count;

    /// <summary>Number of distinct lists kept (tests and diagnostics).</summary>
    public static int Count => _count;

    /// <summary>The shared list with the same content as <paramref name="apis"/>.</summary>
    public static IReadOnlyList<SdkDeviceApi> Intern(RepeatedField<ProtoDeviceApi> apis)
    {
        ArgumentNullException.ThrowIfNull(apis);
        if (apis.Count == 0)
        {
            return [];
        }

        int hash = 0;
        foreach (ProtoDeviceApi api in apis)
        {
            hash = HashCode.Combine(hash, api.Id, api.Version, api.Name, api.Status);
        }

        lock (Lists)
        {
            if (Lists.TryGetValue(hash, out List<IReadOnlyList<SdkDeviceApi>>? candidates))
            {
                foreach (IReadOnlyList<SdkDeviceApi> candidate in candidates)
                {
                    if (SameContent(candidate, apis))
                    {
                        return candidate;
                    }
                }
            }
            else
            {
                if (_count >= MaxDistinctLists)
                {
                    Lists.Clear();
                    _count = 0;
                }

                candidates = [];
                Lists[hash] = candidates;
            }

            IReadOnlyList<SdkDeviceApi> created = [.. apis.Select(a => new SdkDeviceApi(
                string.Intern(a.Id), string.Intern(a.Version), string.IsNullOrEmpty(a.Name) ? null : a.Name, string.IsNullOrEmpty(a.Status) ? null : string.Intern(a.Status)))];
            candidates.Add(created);
            _count++;
            return created;
        }
    }

    private static bool SameContent(IReadOnlyList<SdkDeviceApi> list, RepeatedField<ProtoDeviceApi> apis)
    {
        if (list.Count != apis.Count)
        {
            return false;
        }

        for (int i = 0; i < list.Count; i++)
        {
            SdkDeviceApi a = list[i];
            ProtoDeviceApi b = apis[i];
            if (a.Id != b.Id || a.Version != b.Version || (a.Name ?? "") != b.Name || (a.Status ?? "") != b.Status)
            {
                return false;
            }
        }

        return true;
    }
}
