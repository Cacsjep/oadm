using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.EdgeStorage;

/// <summary>Ids, names and limits shared by the server part, the page, the fake mode and the tests.</summary>
public static class EdgeStorageInfo
{
    public const string PluginId = "oadm.edge-storage";

    public const string DisplayName = "Edge storage";

    public const string IconKey = "hardDrive";

    /// <summary>The task the page starts (not in the device menus).</summary>
    public const string TaskPluginId = "oadm.edge-storage.task";

    /// <summary>Rows per pushed event; more changed rows go out in several events.</summary>
    public const int MaxRowsPerEvent = 1_000;

    /// <summary>Wear alert level when the camera does not say (its default is 90 %).</summary>
    public const int DefaultWearAlert = 90;
}

/// <summary>Page backend methods (<c>InvokeAsync</c>), the rights they need and the live event topics.</summary>
public static class EdgeStorageMethods
{
    /// <summary><see cref="ReadRequest"/> -> <see cref="StorageState"/>: reads the SD cards of the chosen cameras once; rows and progress follow as events.</summary>
    public const string Read = "read";

    /// <summary>{} -> <see cref="StorageState"/>: the current rows and counts, without asking any device.</summary>
    public const string GetState = "getState";

    /// <summary><see cref="RunRequest"/> with <see cref="StorageActions.Check"/> or <see cref="StorageActions.Retention"/> -> task ids. Right "manage".</summary>
    public const string Change = "change";

    /// <summary><see cref="RunRequest"/> with format, repair or encryption -> task ids. Right "format".</summary>
    public const string Format = "format";

    /// <summary>Event: <see cref="RowsEvent"/>, changed and removed rows (batched every 500 ms).</summary>
    public const string RowsTopic = "rows";

    /// <summary>Event: <see cref="StorageState"/> without rows: progress and counts changed.</summary>
    public const string StateTopic = "state";

    /// <summary>Right "Change storage settings": check the SD card and set retention (Operator yes).</summary>
    public const string ManagePermission = "manage";

    /// <summary>Right "Format and encrypt storage": format, repair, encryption on or off (deletes recordings; Operator no).</summary>
    public const string FormatPermission = "format";
}

/// <summary>What a task does with the SD card.</summary>
public static class StorageActions
{
    public const string Check = "check";

    public const string Repair = "repair";

    public const string Format = "format";

    public const string EncryptionOn = "encryptionOn";

    public const string EncryptionOff = "encryptionOff";

    public const string Retention = "retention";

    /// <summary>The actions of <see cref="EdgeStorageMethods.Change"/>; the others belong to <see cref="EdgeStorageMethods.Format"/>.</summary>
    public static bool IsChange(string? action) => action is Check or Retention;

    public static bool IsKnown(string? action) => action is Check or Repair or Format or EncryptionOn or EncryptionOff or Retention;

    /// <summary>The task name ("Format SD card", "Set SD card retention to 30 days").</summary>
    public static string TaskName(string? action, int? days) => action switch
    {
        Check => "Check SD card",
        Repair => "Repair SD card",
        Format => "Format SD card",
        EncryptionOn => "Encrypt SD card",
        EncryptionOff => "Turn off SD card encryption",
        Retention when days is 0 => "Remove SD card retention limit",
        Retention when days is 1 => "Set SD card retention to 1 day",
        Retention => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Set SD card retention to {days:N0} days"),
        _ => EdgeStorageInfo.DisplayName,
    };
}

/// <summary>The SD card state of a row.</summary>
public static class CardStates
{
    /// <summary>Not read yet.</summary>
    public const string Reading = "Reading";

    /// <summary>In use, nothing to do.</summary>
    public const string Ok = "OK";

    /// <summary>In use, but worn past the camera's alert level or full and not cleaned up.</summary>
    public const string Warning = "Warning";

    /// <summary>Failed, not in use, read-only, locked or encrypted and cannot be opened.</summary>
    public const string Problem = "Problem";

    /// <summary>The slot is empty.</summary>
    public const string NoCard = "No SD card";

    /// <summary>The camera could not be read; <see cref="StorageRow.Text"/> says why.</summary>
    public const string Error = "Error";
}

/// <summary>
/// One camera with an SD card slot (it lists the disk management API). Sizes in kB as the camera sends them; null when not
/// known. <see cref="WearPercent"/> null when the card does not report it.
/// </summary>
public sealed record StorageRow(
    Guid DeviceId,
    string Address,
    string? Model,
    string State,
    string? Text,
    long? TotalKb = null,
    long? FreeKb = null,
    int? WearPercent = null,
    int? WearAlertPercent = null,
    bool? Encrypted = null,
    int? RetentionDays = null,
    bool Mounted = false,
    DateTimeOffset? ReadUtc = null)
{
    /// <summary>Used share of the card in percent (0..100), null without a size.</summary>
    public int? UsedPercent => TotalKb is > 0 && FreeKb is { } free ? (int)Math.Round(100.0 * (TotalKb.Value - free) / TotalKb.Value) : null;

    /// <summary>A card is in the slot (the tools apply).</summary>
    public bool HasCard => State is CardStates.Ok or CardStates.Warning or CardStates.Problem;
}

/// <summary>
/// Progress of the read and the counts. <see cref="Rows"/> is filled in the replies of read and getState, empty in the
/// <see cref="EdgeStorageMethods.StateTopic"/> event.
/// </summary>
public sealed record StorageState(
    bool Reading,
    int Done,
    int Total,
    int Ok,
    int Warnings,
    int Problems,
    int NoCard,
    int Failed,
    DateTimeOffset? ReadUtc,
    IReadOnlyList<StorageRow> Rows);

/// <summary>Changed rows, and the devices that left the table (removed from OADM, no SD card slot).</summary>
public sealed record RowsEvent(IReadOnlyList<StorageRow> Rows, IReadOnlyList<Guid> Removed);

/// <summary>
/// A run of the page: the action on these devices. <see cref="RetentionDays"/> for <see cref="StorageActions.Retention"/>
/// (0..7000, 0 = no age limit); <see cref="Passphrase"/> for <see cref="StorageActions.EncryptionOn"/> (kept in memory
/// only, never stored or logged). <see cref="Owner"/>: the client's user and computer, the tasks' owner.
/// </summary>
/// <summary>Request of <see cref="EdgeStorageMethods.Read"/>: the cameras of the page's choice (null: every camera, older pages).</summary>
public sealed record ReadRequest(IReadOnlyList<Guid>? DeviceIds);

public sealed record RunRequest(string Action, IReadOnlyList<Guid> DeviceIds, int? RetentionDays = null, string? Passphrase = null, string? Owner = null)
{
    /// <summary>Never print the passphrase (records print every property).</summary>
    public override string ToString() => $"{Action} on {DeviceIds.Count} devices";
}

/// <summary>The payload of one task (the run without the devices).</summary>
public sealed record TaskPayload(string Action, int? RetentionDays = null, string? Passphrase = null)
{
    /// <summary>Never print the passphrase (records print every property).</summary>
    public override string ToString() => $"{Action} {RetentionDays}";
}

/// <summary>JSON of the page contract (camelCase, like the other core plugins).</summary>
public static class EdgeStorageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json) =>
        JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Options)
        ?? throw new ArgumentException("Missing payload.", nameof(json));
}
