using System.Text.Json;

namespace Oadm.Samples.FirmwareOverview;

/// <summary>One row of the page: a device and its AXIS OS, or why it could not be read.</summary>
public sealed record FirmwareRow(Guid DeviceId, string Address, string Model, string? Version, string? Error);

/// <summary>Everything the page shows. Version goes up with every change, so the page can ignore older states.</summary>
public sealed record FirmwareState(int Version, bool Reading, IReadOnlyList<FirmwareRow> Rows);

/// <summary>The contract between the server part and the page: method names, the event topic and the JSON format.</summary>
public static class FirmwareOverview
{
    public const string PluginId = "acme.firmware-overview";

    public const string GetState = "getState";
    public const string Refresh = "refresh";
    public const string StateTopic = "state";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
