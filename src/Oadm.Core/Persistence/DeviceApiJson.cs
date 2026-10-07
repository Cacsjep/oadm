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
