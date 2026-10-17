using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Vapix;

namespace Oadm.Core.Persistence;

/// <summary>
/// JSON form of <see cref="Devices.Device.Apis"/> in the Devices table:
/// <c>[{"id":"user-management","version":"1.2","name":"User management","status":"official"}]</c>.
/// Unreadable content yields an empty list (the next full refresh rewrites it).
/// </summary>
public static class DeviceApiJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IReadOnlyList<DeviceApi>? apis) =>
        JsonSerializer.Serialize((apis ?? []).Select(a => new Row(a.Id, a.Version, a.Name, a.Status)).ToArray(), Options);

    /// <summary>Most different API lists kept for sharing (models x firmware versions of a site; more are read unshared).</summary>
    public const int MaxSharedLists = 2_000;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<DeviceApi>> Shared = new(StringComparer.Ordinal);

    private static int _sharedCount;

    /// <summary>Number of different API lists kept for sharing (diagnostics and tests).</summary>
    public static int SharedCount => Volatile.Read(ref _sharedCount);

    /// <summary>
    /// <see cref="Deserialize"/>, but devices with the same list (same model and firmware) get the same read-only list
    /// object: 30,000 devices with about 100 APIs each are a few hundred lists in memory instead of 3 million objects
    ///. The lists are immutable, so sharing is safe.
    /// </summary>
    public static IReadOnlyList<DeviceApi> DeserializeShared(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        if (Shared.TryGetValue(json, out var known))
        {
            return known;
        }

        var list = Deserialize(json);
        if (Volatile.Read(ref _sharedCount) >= MaxSharedLists)
        {
            return list; // full: read unshared (a server start empties the cache)
        }

        if (Shared.TryAdd(json, list))
        {
            Interlocked.Increment(ref _sharedCount);
            return list;
        }

        return Shared[json];
    }

    public static IReadOnlyList<DeviceApi> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var rows = JsonSerializer.Deserialize<Row[]>(json, Options) ?? [];
            return [.. rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Version))
                .Select(r => new DeviceApi(r.Id!, r.Version!, r.Name, r.Status))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record Row(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("status")] string? Status);
}
