using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.LicensePlates.Plates;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LicensePlates.Tools;

/// <summary>
/// The tools of License Plate Center (docs/spec/mcp.md "Plugins", <see cref="ICorePlugin.Tools"/>): list and search plates,
/// add, change and delete plates and plate tags (preview then run), the cameras with their status, the log (pages, never
/// pictures) and the statistics. Each wraps a page method, so its rights and device group rules are the page's.
/// </summary>
internal static class LicensePlateTools
{
    public static readonly JsonSerializerOptions Json = PluginTool.CreateJsonOptions();

    public static readonly PluginPageTools<LicensePlatesPlugin> Set = new PluginPageTools<LicensePlatesPlugin>(Json)
        .Read<PlatesArgs, PlatesAnswer>(
            new PluginTool("plates", "License plates", "Lists the plates of License Plate Center with name, tags and when each is allowed. Search by the start of the plate or a name.") { Paged = true },
            LicensePlatesMethods.Plates,
            (plugin, _, input, _) => Task.FromResult(PlatesOf(plugin, input)))
        .Change<PlateSaveArgs, PlatePlan, PlateRow>(
            new PluginTool("plate_save", "Save plate", "Adds a plate or changes one (original: its plate now). Tags by name; times in the server's zone (yyyy-MM-dd HH:mm)."),
            LicensePlatesMethods.SavePlate,
            (plugin, _, input, _) => Task.FromResult(CheckPlate(plugin, input.Args)),
            async (plugin, _, input, plan, ct) =>
            {
                var reply = await plugin.SavePlateAsync(RequestOf(plugin, input.Args), ct).ConfigureAwait(false);
                return reply.Plate is { } saved ? RowOf(plugin, saved) : throw new PluginToolInputException(Fields(reply.Errors));
            })
        .Change<PlatesDeleteArgs, CountPlan, CountAnswer>(
            new PluginTool("plates_delete", "Delete plates", "Deletes plates from the list.") { Destructive = true },
            LicensePlatesMethods.DeletePlates,
            (plugin, _, input, _) => Task.FromResult(DeletePlan(plugin, input.Args)),
            async (plugin, _, input, _, ct) => new CountAnswer(await plugin.DeletePlatesAsync(input.Args.Plates, ct).ConfigureAwait(false)))
        .Read<NoArgs, TagsAnswer>(
            new PluginTool("tags", "Plate tags", "Lists the plate tags with how many plates and cameras use each. A camera allows the plates with its allowed tags."),
            LicensePlatesMethods.Tags,
            (plugin, _, _, _) => Task.FromResult(new TagsAnswer([.. plugin.Running.Store.TagInfos().Select(t => new TagRow(t.Id.ToString("D"), t.Name, ColorName(t.Color), t.Plates, t.Cameras))])))
        .Change<TagSaveArgs, TagPlan, TagRow>(
            new PluginTool("tag_save", "Save plate tag", "Adds a plate tag or renames and recolours one (id of an existing tag)."),
            LicensePlatesMethods.SaveTag,
            (plugin, _, input, _) => Task.FromResult(CheckTag(plugin, input.Args)),
            async (plugin, _, input, _, ct) =>
            {
                var reply = await plugin.SaveTagAsync(TagOf(plugin, input.Args), ct).ConfigureAwait(false);
                return reply.Tag is { } tag ? new TagRow(tag.Id.ToString("D"), tag.Name, ColorName(tag.Color), 0, 0) : throw new PluginToolInputException(Fields(reply.Errors));
            })
        .Change<TagDeleteArgs, TagDeletePlan, CountAnswer>(
            new PluginTool("tag_delete", "Delete plate tag", "Deletes a plate tag and takes it off every plate and camera.") { Destructive = true },
            LicensePlatesMethods.DeleteTag,
            (plugin, _, input, _) => Task.FromResult(TagDeletePlanOf(plugin, input.Args)),
            async (plugin, _, input, plan, ct) =>
            {
                await plugin.DeleteTagAsync(new DeleteTagRequest(Guid.Parse(input.Args.Id), Confirmed: true), ct).ConfigureAwait(false);
                return new CountAnswer(1);
            })
        .Read<NoArgs, CamerasAnswer>(
            new PluginTool("cameras", "License plate cameras", "Lists the cameras of License Plate Center with their status (connected, problems of ALPV), allowed tags and image settings.") { Paged = true },
            LicensePlatesMethods.Cameras,
            async (plugin, caller, input, ct) => CamerasOf(plugin, await plugin.CamerasAsync(caller, ct).ConfigureAwait(false), input.Page))
        .Read<LogArgs, LogAnswer>(
            new PluginTool("log", "License plate log", "The reads of the cameras, newest first: plate, result, camera, direction, confidence. Never a picture.") { Paged = true },
            LicensePlatesMethods.Log,
            async (plugin, caller, input, ct) => await LogOf(plugin, caller, input, ct).ConfigureAwait(false))
        .Read<StatsArgs, StatsReply>(
            new PluginTool("stats", "License plate statistics", "Reads by result and direction, per hour and per camera, and the most read plates, for at most 31 days."),
            LicensePlatesMethods.Stats,
            async (plugin, caller, input, ct) => await plugin.StatsAsync(caller, new StatsRequest
            {
                From = input.Args.From,
                To = input.Args.To,
                CameraIds = input.Args.CameraIds?.Select(c => Guid.TryParse(c, out var id) ? id : throw new PluginToolInputException("camera_ids", "Enter ids of cameras from oadm_license_plates_cameras.")).ToList(),
            }, ct).ConfigureAwait(false));

    // ---- plates ----

    private static PlatesAnswer PlatesOf(LicensePlatesPlugin plugin, PluginToolInput<PlatesArgs> input)
    {
        var store = plugin.Running.Store;
        Guid? tag = input.Args.Tag is { Length: > 0 } name
            ? store.Tags.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? throw new PluginToolInputException("tag", "No plate tag has this name.")
            : null;
        var (page, next, total) = store.Page(input.Page.After, input.Page.Limit, input.Args.Search, tag);
        var (rows, more) = input.Page.Take(page.Select(p => RowOf(plugin, p)), Json);
        return new PlatesAnswer(total, rows, more || next is not null ? rows[^1].Plate : null);
    }

    private static PlateRow RowOf(LicensePlatesPlugin plugin, Plate plate) =>
        new(plate.Text, plate.Name, plugin.Running.Store.NamesOf(plate.Tags), Time(plate.AllowedFrom), Time(plate.AllowedTo), plate.DeleteWhenEnds);

    private static string? Time(DateTime? time) => time?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static DateTime? ParseTime(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTime.TryParseExact(text.Trim(), ["yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new PluginToolInputException(field, "Enter a time as yyyy-MM-dd HH:mm.");
    }

    private static SavePlateRequest RequestOf(LicensePlatesPlugin plugin, PlateSaveArgs args)
    {
        var store = plugin.Running.Store;
        var tags = new List<Guid>();
        foreach (var name in args.Tags ?? [])
        {
            tags.Add(store.Tags.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new PluginToolInputException("tags", "No plate tag is named " + name + "."));
        }

        var original = args.Original is { Length: > 0 } o ? o : null;
        var existing = original is null ? null : store.Find(PlateText.Normalize(original));
        return new SavePlateRequest(new Plate
        {
            Text = args.Plate,
            Name = args.Name ?? existing?.Name ?? string.Empty,
            Tags = args.Tags is null && existing is not null ? existing.Tags : tags,
            AllowedFrom = ParseTime(args.AllowedFrom, "allowed_from"),
            AllowedTo = ParseTime(args.AllowedTo, "allowed_to"),
            DeleteWhenEnds = args.DeleteWhenEnds ?? args.AllowedTo is { Length: > 0 },
        }, original);
    }

    private static PlatePlan CheckPlate(LicensePlatesPlugin plugin, PlateSaveArgs args)
    {
        var (plate, errors, old) = plugin.CheckPlate(RequestOf(plugin, args));
        if (errors.Count > 0)
        {
            throw new PluginToolInputException(Fields(errors));
        }

        return new PlatePlan(RowOf(plugin, plate), old is null);
    }

    private static IEnumerable<KeyValuePair<string, string>> Fields(IReadOnlyDictionary<string, string>? errors) =>
        (errors ?? new Dictionary<string, string>()).Select(e => new KeyValuePair<string, string>(e.Key switch
        {
            PlateRules.TextField => "plate",
            PlateRules.FromField => "allowed_from",
            PlateRules.ToField => "allowed_to",
            PlateRules.DeleteField => "delete_when_ends",
            _ => e.Key,
        }, e.Value));

    private static CountPlan DeletePlan(LicensePlatesPlugin plugin, PlatesDeleteArgs args)
    {
        var store = plugin.Running.Store;
        var found = args.Plates.Select(PlateText.Normalize).Distinct(StringComparer.Ordinal).Count(t => store.Find(t) is not null);
        return found == 0 ? throw new PluginToolInputException("plates", LicensePlateTexts.PlateGone) : new CountPlan(found, "plate");
    }

    // ---- tags ----

    private static readonly Dictionary<string, PlateTagColor> Colors =
        Enum.GetValues<PlateTagColor>().ToDictionary(c => c.ToString().ToLowerInvariant(), c => c, StringComparer.OrdinalIgnoreCase);

    private static string ColorName(PlateTagColor color) => color.ToString().ToLowerInvariant();

    private static PlateTag TagOf(LicensePlatesPlugin plugin, TagSaveArgs args)
    {
        var store = plugin.Running.Store;
        var id = Guid.Empty;
        if (args.Id is { Length: > 0 } text && (!Guid.TryParse(text, out id) || store.Tag(id) is null))
        {
            throw new PluginToolInputException("id", LicensePlateTexts.TagGone);
        }

        var color = args.Color is { Length: > 0 } c
            ? Colors.TryGetValue(c.Trim(), out var known) ? known : throw new PluginToolInputException("color", "Choose blue, green, teal, amber, orange, red, pink or violet.")
            : id != Guid.Empty ? store.Tag(id)!.Color : (PlateTagColor)(store.Tags.Count % 8 + 1);
        return new PlateTag { Id = id, Name = args.Name, Color = color };
    }

    private static TagPlan CheckTag(LicensePlatesPlugin plugin, TagSaveArgs args)
    {
        var (tag, errors) = plugin.CheckTag(TagOf(plugin, args));
        return errors.Count > 0 ? throw new PluginToolInputException(errors) : new TagPlan(tag.Name, ColorName(tag.Color), tag.Id == Guid.Empty);
    }

    private static TagDeletePlan TagDeletePlanOf(LicensePlatesPlugin plugin, TagDeleteArgs args)
    {
        var info = Guid.TryParse(args.Id, out var id) ? plugin.Running.Store.TagInfos().FirstOrDefault(t => t.Id == id) : null;
        return info is null ? throw new PluginToolInputException("id", LicensePlateTexts.TagGone) : new TagDeletePlan(info.Name, info.Plates, info.Cameras);
    }

    // ---- cameras and log ----

    private static CamerasAnswer CamerasOf(LicensePlatesPlugin plugin, CamerasReply reply, PluginToolPage page)
    {
        var store = plugin.Running.Store;
        var all = reply.Cameras
            .Select(c => new CameraToolRow(c.DeviceId.ToString("D"), c.Address, c.Site, c.AlpvVersion, c.StatusText, c.Since?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                store.NamesOf(c.AllowedTags), c.ImageSettings is { } image ? image.Recommended : null))
            .OrderBy(c => c.DeviceId, StringComparer.Ordinal)
            .ToList();
        var (rows, more) = page.Take(all.Where(c => page.Includes(c.DeviceId)), Json);
        return new CamerasAnswer(all.Count, reply.Connected, rows, more && rows.Count > 0 ? rows[^1].DeviceId : null);
    }

    /// <summary>The sort key of a log row, newest first in ordinal order.</summary>
    internal static string KeyOf(long timeMs, long id) =>
        string.Create(CultureInfo.InvariantCulture, $"{long.MaxValue - timeMs:D19}{long.MaxValue - id:D19}");

    internal static LogCursor? CursorOf(string? key)
    {
        if (key is not { Length: 38 }
            || !long.TryParse(key.AsSpan(0, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var time)
            || !long.TryParse(key.AsSpan(19), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        return new LogCursor(long.MaxValue - time, long.MaxValue - id);
    }

    private static async Task<LogAnswer> LogOf(LicensePlatesPlugin plugin, ICallContext caller, PluginToolInput<LogArgs> input, CancellationToken ct)
    {
        var args = input.Args;
        IReadOnlyList<ReadResult>? results = args.Result switch
        {
            null or "" => null,
            "allowed" => [ReadResult.Allowed],
            "not_allowed" => [ReadResult.NotAllowed],
            "unknown" => [ReadResult.Unknown],
            _ => throw new PluginToolInputException("result", "Choose allowed, not_allowed or unknown."),
        };
        Guid? camera = args.CameraId is { Length: > 0 } c ? Guid.TryParse(c, out var g) ? g : throw new PluginToolInputException("camera_id", "Enter the id of a camera from oadm_license_plates_cameras.") : null;
        var query = new LogQuery { From = args.From, To = args.To, Results = results, Search = args.Search, CameraIds = camera is { } one ? [one] : null };
        var page = await plugin.LogPageAsync(caller, new LogPageRequest { Query = query, Before = CursorOf(input.Page.After), Limit = input.Page.Limit }, ct).ConfigureAwait(false);
        var rows = page.Rows.Select(r => new LogRow(
            KeyOf(r.Time.ToUnixTimeMilliseconds(), r.Id),
            r.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            r.CameraId.ToString("D"),
            r.Camera,
            r.Gap ? null : r.Plate,
            r.ReadAs,
            r.Gap ? "connection_lost" : r.Result switch { ReadResult.Allowed => "allowed", ReadResult.NotAllowed => "not_allowed", _ => "unknown" },
            r.Gap ? null : LicensePlateTexts.Reason(r.Result, r.Reason, r.ReasonTime),
            r.Gap ? null : r.Name,
            r.Tags,
            r.Gap ? null : LicensePlateTexts.Direction(r.Direction),
            r.Gap ? null : r.Confidence));
        var (taken, more) = input.Page.Take(rows, Json);
        var next = (more || page.Next is not null) && taken.Count > 0 ? taken[^1].Key : null;
        return new LogAnswer(taken, next is not null, next);
    }

    // ---- arguments and answers ----

    internal sealed record NoArgs;

    internal sealed record PlatesArgs(
        [property: Description("Plates that start with it, or whose name holds it."), MaxLength(100)] string? Search = null,
        [property: Description("Only plates with this plate tag (its name)."), MaxLength(LicensePlateLimits.MaxTagNameLength)] string? Tag = null);

    internal sealed record PlateRow(
        [property: Description("The plate (letters and digits, upper case).")] string Plate,
        [property: Description("Its name.")] string Name,
        [property: Description("Its plate tags.")] IReadOnlyList<string> Tags,
        [property: Description("Allowed from (server time); null: always.")] string? AllowedFrom,
        [property: Description("Allowed to (server time); null: no end.")] string? AllowedTo,
        [property: Description("The plate is deleted when its Allowed to passes.")] bool DeleteWhenEnds);

    internal sealed record PlatesAnswer(
        [property: Description("Plates that match.")] int Total,
        [property: Description("This page.")] IReadOnlyList<PlateRow> Plates,
        [property: Description("The cursor of the next page.")] string? Next);

    internal sealed record PlateSaveArgs(
        [property: Description("The plate (spaces, dashes and dots are left out)."), Required, MaxLength(32)] string Plate,
        [property: Description("The plate it changes; empty for a new plate."), MaxLength(32)] string? Original = null,
        [property: Description("Its name."), MaxLength(LicensePlateLimits.MaxNameLength)] string? Name = null,
        [property: Description("Its plate tags by name; a plate without tags is allowed nowhere."), MaxLength(LicensePlateLimits.MaxTags)] IReadOnlyList<string>? Tags = null,
        [property: Description("Allowed from, yyyy-MM-dd HH:mm in the server's time."), MaxLength(20)] string? AllowedFrom = null,
        [property: Description("Allowed to, yyyy-MM-dd HH:mm in the server's time."), MaxLength(20)] string? AllowedTo = null,
        [property: Description("Delete the plate when Allowed to passes (default: on with an Allowed to).")] bool? DeleteWhenEnds = null);

    internal sealed record PlatePlan(
        [property: Description("The plate as it will be saved.")] PlateRow Plate,
        [property: Description("A new plate.")] bool IsNew) : PluginToolPlan(1, "plate");

    internal sealed record PlatesDeleteArgs(
        [property: Description("The plates to delete."), Required, MinLength(1), MaxLength(1000)] IReadOnlyList<string> Plates);

    internal sealed record CountPlan(int Count, string What) : PluginToolPlan(Count, What);

    internal sealed record CountAnswer([property: Description("How many changed.")] int Count);

    internal sealed record TagRow(
        [property: Description("Its id.")] string Id,
        [property: Description("Its name.")] string Name,
        [property: Description("Its colour.")] string Color,
        [property: Description("Plates with the tag.")] int Plates,
        [property: Description("Cameras that allow it.")] int Cameras);

    internal sealed record TagsAnswer([property: Description("Every plate tag.")] IReadOnlyList<TagRow> Tags);

    internal sealed record TagSaveArgs(
        [property: Description("Its name (unique, no comma)."), Required, MaxLength(LicensePlateLimits.MaxTagNameLength)] string Name,
        [property: Description("The id of the tag it changes; empty for a new tag."), MaxLength(40)] string? Id = null,
        [property: Description("blue, green, teal, amber, orange, red, pink or violet."), MaxLength(10)] string? Color = null);

    internal sealed record TagPlan(
        [property: Description("Its name.")] string Name,
        [property: Description("Its colour.")] string Color,
        [property: Description("A new tag.")] bool IsNew) : PluginToolPlan(1, "plate tag");

    internal sealed record TagDeleteArgs([property: Description("The tag's id."), Required, MaxLength(40)] string Id);

    internal sealed record TagDeletePlan(
        [property: Description("The tag.")] string Name,
        [property: Description("Plates that lose the tag.")] int Plates,
        [property: Description("Cameras that lose the tag.")] int Cameras) : PluginToolPlan(1, "plate tag");

    internal sealed record CameraToolRow(
        [property: Description("The device id.")] string DeviceId,
        [property: Description("Its address.")] string Address,
        [property: Description("Its site; null on the server's network.")] string? Site,
        [property: Description("ALPV's version.")] string? Alpv,
        [property: Description("Connected, or what keeps it from taking part.")] string Status,
        [property: Description("Since when the connection is lost (UTC).")] string? Since,
        [property: Description("The plate tags allowed at the camera.")] IReadOnlyList<string> AllowedTags,
        [property: Description("The image settings are as ALPV recommends; null: not read yet.")] bool? RecommendedImageSettings);

    internal sealed record CamerasAnswer(
        [property: Description("Cameras of License Plate Center the caller sees.")] int Total,
        [property: Description("How many are connected.")] int Connected,
        [property: Description("This page.")] IReadOnlyList<CameraToolRow> Cameras,
        [property: Description("The cursor of the next page.")] string? Next);

    internal sealed record LogArgs(
        [property: Description("Reads from (UTC or with an offset).")] DateTimeOffset? From = null,
        [property: Description("Reads before (UTC or with an offset).")] DateTimeOffset? To = null,
        [property: Description("allowed, not_allowed or unknown."), MaxLength(12)] string? Result = null,
        [property: Description("Plates that start with it, or names that hold it."), MaxLength(100)] string? Search = null,
        [property: Description("Only this camera (its device id)."), MaxLength(40)] string? CameraId = null);

    internal sealed record LogRow(
        [property: Description("The row's sort key.")] string Key,
        [property: Description("When (UTC).")] string Time,
        [property: Description("The camera's device id.")] string CameraId,
        [property: Description("The camera.")] string Camera,
        [property: Description("The plate of the list, or the text read.")] string? Plate,
        [property: Description("The text read when it differs from the plate.")] string? ReadAs,
        [property: Description("allowed, not_allowed, unknown, or connection_lost for a gap.")] string Result,
        [property: Description("The result with its reason.")] string? Reason,
        [property: Description("The plate's name.")] string? Name,
        [property: Description("The plate's tags at the time.")] IReadOnlyList<string> Tags,
        [property: Description("In, Out or Unknown.")] string? Direction,
        [property: Description("Confidence, 0 to 100.")] int? Confidence);

    internal sealed record LogAnswer(
        [property: Description("This page, newest first.")] IReadOnlyList<LogRow> Reads,
        [property: Description("Older reads follow.")] bool More,
        [property: Description("The cursor of the next page.")] string? Next);

    internal sealed record StatsArgs(
        [property: Description("From (UTC or with an offset)."), Required] DateTimeOffset From,
        [property: Description("To (UTC or with an offset), at most 31 days after From."), Required] DateTimeOffset To,
        [property: Description("Only these cameras (device ids)."), MaxLength(1000)] IReadOnlyList<string>? CameraIds = null);
}
