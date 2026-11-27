using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using Oadm.Plugins.LicensePlates.Alpv;
using Oadm.Plugins.LicensePlates.Log;
using Oadm.Plugins.LicensePlates.Plates;
using Oadm.Plugins.LicensePlates.Tasks;
using Oadm.Plugins.LicensePlates.Tools;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.LicensePlates;

/// <summary>The page backend (<see cref="LicensePlatesMethods"/>), its rights and its downloads.</summary>
public sealed partial class LicensePlatesPlugin
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, ImportState> _previews = new(StringComparer.Ordinal);

    private static string Rules(string method) => LicensePlateTools.Set.RulesMethodOf(method);

    /// <summary>The settings dialog is for Administrators; everything else needs a permission of its own.</summary>
    public UserRole RequiredRole(string method) => LicensePlatesMethods.IsSettings(Rules(method)) ? UserRole.Admin : UserRole.Operator;

    /// <summary>
    /// "show" (View License Plate Center): the plates, tags and cameras; "log": the log, its pictures and the stats; "export":
    /// the export; "plates": changes of plates and tags; "cameras": changes of cameras and their tasks; the settings: Administrators.
    /// </summary>
    public string? RequiredPermission(string method)
    {
        var rules = Rules(method);
        if (LicensePlatesMethods.IsSettings(rules))
        {
            return null;
        }

        if (rules == LicensePlatesMethods.ExportLog)
        {
            return LicensePlatePermissions.Export;
        }

        if (LicensePlatesMethods.IsLog(rules))
        {
            return LicensePlatePermissions.Log;
        }

        if (LicensePlatesMethods.IsPlateChange(rules))
        {
            return LicensePlatePermissions.Plates;
        }

        return LicensePlatesMethods.IsCameraChange(rules) ? LicensePlatePermissions.Cameras : PermissionKeys.Show;
    }

    /// <summary>The settings are server-wide (the log's folder).</summary>
    public bool RequiresAllDevices(string method) => LicensePlatesMethods.IsSettings(Rules(method));

    /// <summary>Every change (plates, tags, cameras, settings) and every export; never a read or a preview.</summary>
    public bool IsAudited(string method)
    {
        if (PluginTool.IsPreviewMethod(method))
        {
            return false;
        }

        var rules = Rules(method);
        return rules is LicensePlatesMethods.SavePlate or LicensePlatesMethods.DeletePlates or LicensePlatesMethods.ChangePlateTags
            or LicensePlatesMethods.SaveTag or LicensePlatesMethods.DeleteTag or LicensePlatesMethods.ImportSave
            or LicensePlatesMethods.AddCameras or LicensePlatesMethods.RemoveCameras or LicensePlatesMethods.SetAllowedTags
            or LicensePlatesMethods.SaveSetup or LicensePlatesMethods.CopySettings or LicensePlatesMethods.UseRecommendedImageSettings
            or LicensePlatesMethods.SaveSettings or LicensePlatesMethods.ExportLog;
    }

    /// <summary>What was done, never a plate's text (plates are personal data) or a secret.</summary>
    public string? AuditDetail(string method, string? payloadJson)
    {
        var json = LicensePlatesJson.Options;
        var tools = LicensePlateTools.Set.Handles(method);
        static string Count(int n, string one, string many) => n == 1 ? "1 " + one : n.ToString(CultureInfo.InvariantCulture) + " " + many;
        return Rules(method) switch
        {
            LicensePlatesMethods.SavePlate => "Saved a plate",
            LicensePlatesMethods.DeletePlates when !tools && PluginJson.TryParse<PlateTextsRequest>(payloadJson, json) is { } r => "Deleted " + Count(r.Plates?.Count ?? 0, "plate", "plates"),
            LicensePlatesMethods.DeletePlates => "Deleted plates",
            LicensePlatesMethods.ChangePlateTags when PluginJson.TryParse<ChangePlateTagsRequest>(payloadJson, json) is { } r => "Changed the tags of " + Count(r.Plates?.Count ?? 0, "plate", "plates"),
            LicensePlatesMethods.SaveTag when !tools && PluginJson.TryParse<SaveTagRequest>(payloadJson, json)?.Tag is { } t => "Saved plate tag " + t.Name?.Trim(),
            LicensePlatesMethods.SaveTag => "Saved a plate tag",
            LicensePlatesMethods.DeleteTag => "Deleted a plate tag",
            LicensePlatesMethods.ImportSave => "Imported plates",
            LicensePlatesMethods.AddCameras when PluginJson.TryParse<DevicesRequest>(payloadJson, json) is { } r => "Added " + Count(r.DeviceIds?.Count ?? 0, "camera", "cameras"),
            LicensePlatesMethods.RemoveCameras when PluginJson.TryParse<DevicesRequest>(payloadJson, json) is { } r => "Removed " + Count(r.DeviceIds?.Count ?? 0, "camera", "cameras"),
            LicensePlatesMethods.SetAllowedTags when PluginJson.TryParse<AllowedTagsRequest>(payloadJson, json) is { } r => "Changed the allowed tags of " + Count(r.DeviceIds?.Count ?? 0, "camera", "cameras"),
            LicensePlatesMethods.SaveSetup => "Set up License Plate Verifier on 1 camera",
            LicensePlatesMethods.CopySettings when PluginJson.TryParse<CopySettingsRequest>(payloadJson, json) is { } r => "Copied the settings of a camera to " + Count(r.TargetIds?.Count ?? 0, "camera", "cameras"),
            LicensePlatesMethods.UseRecommendedImageSettings when PluginJson.TryParse<DevicesRequest>(payloadJson, json) is { } r => "Used recommended image settings on " + Count(r.DeviceIds?.Count ?? 0, "camera", "cameras"),
            LicensePlatesMethods.SaveSettings => "Saved the settings",
            LicensePlatesMethods.ExportLog => "Exported plate log",
            _ => null,
        };
    }

    /// <summary>In-process calls without a user (the host itself, tests): as a user with all devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        InvokeAsync(new ContextCall(_ctx ?? throw new InvalidOperationException(LicensePlateTexts.NotRunning)), method, payloadJson, ct);

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        _scopes.TryAdd(caller.DeviceScopeKey, 0);
        if (LicensePlateTools.Set.Handles(method))
        {
            return await LicensePlateTools.Set.InvokeAsync(this, caller, method, payloadJson, ct).ConfigureAwait(false);
        }

        var json = LicensePlatesJson.Options;
        T Read<T>() => PluginJson.Parse<T>(payloadJson, json);
        T ReadOrEmpty<T>() => PluginJson.ParseOrEmpty<T>(payloadJson, json);
        string Write<T>(T value) => PluginJson.Serialize(value, json);
        return method switch
        {
            LicensePlatesMethods.Log => Write(await LogPageAsync(caller, ReadOrEmpty<LogPageRequest>(), ct).ConfigureAwait(false)),
            LicensePlatesMethods.LogCount => Write(new CountReply(await LogCountAsync(caller, ReadOrEmpty<LogQuery>(), ct).ConfigureAwait(false))),
            LicensePlatesMethods.LogStatus => Write(await LogStatusAsync(caller, ct).ConfigureAwait(false)),
            LicensePlatesMethods.Picture => Write(await PictureAsync(caller, Read<ReadIdRequest>().Id, ct).ConfigureAwait(false)),
            LicensePlatesMethods.Stats => Write(await StatsAsync(caller, Read<StatsRequest>(), ct).ConfigureAwait(false)),
            LicensePlatesMethods.Plates => Write(PlatesPage(ReadOrEmpty<PlatesRequest>())),
            LicensePlatesMethods.SavePlate => Write(await SavePlateAsync(Read<SavePlateRequest>(), ct).ConfigureAwait(false)),
            LicensePlatesMethods.DeletePlates => Write(new CountReply(await DeletePlatesAsync(Read<PlateTextsRequest>().Plates ?? [], ct).ConfigureAwait(false))),
            LicensePlatesMethods.ChangePlateTags => Write(new CountReply(await ChangePlateTagsAsync(Read<ChangePlateTagsRequest>(), ct).ConfigureAwait(false))),
            LicensePlatesMethods.Tags => Write(new TagsReply(Running.Store.TagInfos())),
            LicensePlatesMethods.SaveTag => Write(await SaveTagAsync(Read<SaveTagRequest>().Tag, ct).ConfigureAwait(false)),
            LicensePlatesMethods.DeleteTag => Write(await DeleteTagAsync(Read<DeleteTagRequest>(), ct).ConfigureAwait(false)),
            LicensePlatesMethods.ImportPreview => Write(await ImportPreviewAsync(caller, Read<ImportRequest>(), ct).ConfigureAwait(false)),
            LicensePlatesMethods.ImportRows => Write(ImportRows(caller, Read<ImportRowsRequest>())),
            LicensePlatesMethods.ImportSave => Write(await ImportSaveAsync(caller, Read<ImportSaveRequest>().PreviewId, ct).ConfigureAwait(false)),
            LicensePlatesMethods.Cameras => Write(await CamerasAsync(caller, ct).ConfigureAwait(false)),
            LicensePlatesMethods.AddCameras => Write(await AddCamerasAsync(caller, Read<DevicesRequest>().DeviceIds ?? [], ct).ConfigureAwait(false)),
            LicensePlatesMethods.RemoveCameras => Write(await RemoveCamerasAsync(caller, Read<DevicesRequest>().DeviceIds ?? [], ct).ConfigureAwait(false)),
            LicensePlatesMethods.SetAllowedTags => Write(await SetAllowedTagsAsync(caller, Read<AllowedTagsRequest>(), ct).ConfigureAwait(false)),
            LicensePlatesMethods.RefreshCameras => Write(await RefreshCamerasAsync(caller, Read<DevicesRequest>().DeviceIds ?? [], ct).ConfigureAwait(false)),
            LicensePlatesMethods.ReadSetup => Write(await ReadSetupAsync(caller, Read<DeviceRequest>().DeviceId, ct).ConfigureAwait(false)),
            LicensePlatesMethods.SaveSetup => Write(await SaveSetupAsync(caller, Read<SaveSetupRequest>(), ct).ConfigureAwait(false)),
            LicensePlatesMethods.CopySettings => Write(await CopySettingsAsync(caller, Read<CopySettingsRequest>(), ct).ConfigureAwait(false)),
            LicensePlatesMethods.UseRecommendedImageSettings => Write(await UseRecommendedAsync(caller, Read<DevicesRequest>().DeviceIds ?? [], ct).ConfigureAwait(false)),
            LicensePlatesMethods.Settings => Write(_settings),
            LicensePlatesMethods.SaveSettings => Write(await SaveSettingsAsync(Read<LicensePlateSettings>(), ct).ConfigureAwait(false)),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    // ---- device groups ----

    /// <summary>The cameras of the plugin the caller sees, with their devices (fail closed: a device the call does not find is left out).</summary>
    internal async Task<IReadOnlyList<(CameraEntry Entry, IDeviceInfo Device)>> VisibleCamerasAsync(ICallContext caller, CancellationToken ct)
    {
        var list = new List<(CameraEntry, IDeviceInfo)>();
        foreach (var entry in Running.Store.Cameras)
        {
            if (await caller.Devices.FindAsync(entry.DeviceId, ct).ConfigureAwait(false) is { } device)
            {
                _names[entry.DeviceId] = NameOf(device);
                list.Add((entry, device));
            }
        }

        return list;
    }

    private static string NameOf(IDeviceInfo device) => string.IsNullOrWhiteSpace(device.HostName) ? device.Address : device.HostName!;

    /// <summary>The cameras of the log the caller may see: every camera for users with all devices; else those of the group (fail closed).</summary>
    internal async Task<LogScope> LogScopeAsync(ICallContext caller, CancellationToken ct)
    {
        if (caller.HasAllDevices)
        {
            return LogScope.All;
        }

        var visible = new HashSet<Guid>();
        foreach (var id in Running.Store.Cameras.Select(c => c.DeviceId).Concat(_loggedCameras.Keys).Distinct())
        {
            if (await caller.Devices.FindAsync(id, ct).ConfigureAwait(false) is not null)
            {
                visible.Add(id);
            }
        }

        return new LogScope(visible);
    }

    /// <summary>The chosen device ids, each looked up with the call: one the caller cannot see answers like an unknown id.</summary>
    private static async Task<IReadOnlyList<IDeviceInfo>> RequireDevicesAsync(ICallContext caller, IEnumerable<Guid> ids, CancellationToken ct) =>
        await CallDevices.RequireAsync(caller, ids.Distinct(), ct).ConfigureAwait(false);

    // ---- log ----

    private PlateLogStore RequireLogStore() => Running.Log.Store ?? throw new InvalidOperationException(Running.Log.Problem ?? LicensePlateTexts.NoFolder);

    internal async Task<LogPage> LogPageAsync(ICallContext caller, LogPageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Running.Log.Store is not { } store)
        {
            return new LogPage([], null);
        }

        return await store.PageAsync(request with { Query = request.Query ?? new LogQuery() }, await LogScopeAsync(caller, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    internal async Task<int> LogCountAsync(ICallContext caller, LogQuery query, CancellationToken ct) =>
        Running.Log.Store is { } store ? await store.CountAsync(query, await LogScopeAsync(caller, ct).ConfigureAwait(false), ct).ConfigureAwait(false) : 0;

    private async Task<LogStatus> LogStatusAsync(ICallContext caller, CancellationToken ct)
    {
        var log = Running.Log;
        var settings = _settings;
        var store = log.Store;
        var zone = _time.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone);
        var midnight = new DateTimeOffset(local.Date, zone.GetUtcOffset(local.Date));
        return new LogStatus
        {
            ReadsToday = store is null ? 0 : await store.ReadsSinceAsync(midnight, await LogScopeAsync(caller, ct).ConfigureAwait(false), ct).ConfigureAwait(false),
            UsedBytes = store?.UsedBytes ?? 0,
            MaxBytes = (long)settings.MaxGigabytes * 1024 * 1024 * 1024,
            KeepDays = settings.KeepDays,
            HasFolder = log.HasFolder,
            Problem = log.Problem,
        };
    }

    private async Task<PictureReply> PictureAsync(ICallContext caller, long id, CancellationToken ct)
    {
        if (Running.Log.Store is not { } store)
        {
            return new PictureReply(null);
        }

        var bytes = await store.PictureAsync(id, await LogScopeAsync(caller, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return new PictureReply(bytes is null ? null : Convert.ToBase64String(bytes));
    }

    internal async Task<StatsReply> StatsAsync(ICallContext caller, StatsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.To <= request.From)
        {
            throw new ArgumentException("Choose an end after the start.");
        }

        if (request.To - request.From > LicensePlateLimits.MaxStatsRange)
        {
            throw new ArgumentException("Choose at most 31 days.");
        }

        if (Running.Log.Store is not { } store)
        {
            return new StatsReply(new ResultCounts(0, 0, 0), [], [], [], [], []);
        }

        return await store.StatsAsync(request, await LogScopeAsync(caller, ct).ConfigureAwait(false), id => _names.GetValueOrDefault(id) ?? id.ToString("D"), ct).ConfigureAwait(false);
    }

    // ---- plates ----

    internal PlatesPage PlatesPage(PlatesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (page, next, total) = Running.Store.Page(request.After, request.Limit, request.Search, request.TagId);
        return new PlatesPage(page, next, total);
    }

    /// <summary>The checks of a save (the page's and the tool's preview); never writes.</summary>
    internal (Plate Plate, Dictionary<string, string> Errors, Plate? Old) CheckPlate(SavePlateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var store = Running.Store;
        var (plate, errors) = PlateRules.Check(request.Plate ?? new Plate(), id => store.Tag(id) is not null);
        Plate? old = null;
        if (request.Original is { } originalText)
        {
            old = store.Find(PlateText.Normalize(originalText)) ?? throw new InvalidOperationException(LicensePlateTexts.PlateGone);
        }

        if (!errors.ContainsKey(PlateRules.TextField) && plate.Text != old?.Text && store.Find(plate.Text) is not null)
        {
            errors[PlateRules.TextField] = LicensePlateTexts.PlateExists;
        }

        if (old is null && errors.Count == 0 && store.PlateCount >= LicensePlateLimits.MaxPlates)
        {
            throw new InvalidOperationException(LicensePlateTexts.TooManyPlates);
        }

        return (plate, errors, old);
    }

    internal async Task<SavePlateReply> SavePlateAsync(SavePlateRequest request, CancellationToken ct)
    {
        var (plate, errors, old) = CheckPlate(request);
        if (errors.Count > 0)
        {
            return new SavePlateReply(null, errors);
        }

        await Running.Store.WritePlatesAsync([plate], old is not null && old.Text != plate.Text ? [old.Text] : [], ct).ConfigureAwait(false);
        PublishPlates();
        return new SavePlateReply(plate);
    }

    internal async Task<int> DeletePlatesAsync(IReadOnlyList<string> plates, CancellationToken ct)
    {
        var store = Running.Store;
        var texts = plates.Select(PlateText.Normalize).Where(t => store.Find(t) is not null).Distinct(StringComparer.Ordinal).ToList();
        await store.WritePlatesAsync([], texts, ct).ConfigureAwait(false);
        if (texts.Count > 0)
        {
            PublishPlates();
        }

        return texts.Count;
    }

    internal async Task<int> ChangePlateTagsAsync(ChangePlateTagsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var store = Running.Store;
        var add = (request.Add ?? []).Distinct().ToList();
        var remove = (request.Remove ?? []).ToHashSet();
        if (add.Any(t => store.Tag(t) is null))
        {
            throw new InvalidOperationException(LicensePlateTexts.TagGone);
        }

        var changed = new List<Plate>();
        foreach (var text in (request.Plates ?? []).Select(PlateText.Normalize).Distinct(StringComparer.Ordinal))
        {
            if (store.Find(text) is not { } plate)
            {
                continue;
            }

            List<Guid> tags = [.. plate.Tags.Where(t => !remove.Contains(t)).Concat(add.Where(t => !remove.Contains(t))).Distinct()];
            if (!tags.Order().SequenceEqual(plate.Tags.Order()))
            {
                changed.Add(plate with { Tags = tags });
            }
        }

        await store.WritePlatesAsync(changed, [], ct).ConfigureAwait(false);
        if (changed.Count > 0)
        {
            PublishPlates();
        }

        return changed.Count;
    }

    internal (PlateTag Tag, Dictionary<string, string> Errors) CheckTag(PlateTag? incoming)
    {
        var store = Running.Store;
        var tag = incoming ?? new PlateTag();
        var isNew = tag.Id == Guid.Empty || store.Tag(tag.Id) is null;
        var (checkedTag, errors) = PlateRules.Check(tag with { Id = isNew ? Guid.Empty : tag.Id }, store.Tags);
        if (isNew && store.Tags.Count >= LicensePlateLimits.MaxTags)
        {
            throw new InvalidOperationException(LicensePlateTexts.TooManyTags);
        }

        return (checkedTag, errors);
    }

    internal async Task<SaveTagReply> SaveTagAsync(PlateTag? incoming, CancellationToken ct)
    {
        var (tag, errors) = CheckTag(incoming);
        if (errors.Count > 0)
        {
            return new SaveTagReply(null, errors);
        }

        var saved = tag.Id == Guid.Empty ? tag with { Id = Guid.NewGuid() } : tag;
        await Running.Store.SaveTagAsync(saved, ct).ConfigureAwait(false);
        PublishPlates();
        return new SaveTagReply(saved);
    }

    internal async Task<DeleteTagReply> DeleteTagAsync(DeleteTagRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var store = Running.Store;
        var info = store.TagInfos().FirstOrDefault(t => t.Id == request.Id) ?? throw new InvalidOperationException(LicensePlateTexts.TagGone);
        if ((info.Plates > 0 || info.Cameras > 0) && !request.Confirmed)
        {
            return new DeleteTagReply(false, info.Plates, info.Cameras);
        }

        var cameras = store.Cameras.Where(c => c.AllowedTags.Contains(request.Id)).Select(c => c.DeviceId).ToList();
        await store.DeleteTagAsync(request.Id, ct).ConfigureAwait(false);
        PublishPlates();
        OnCamerasChanged(cameras);
        return new DeleteTagReply(true, info.Plates, info.Cameras);
    }

    // ---- import ----

    private sealed record ImportState(string Owner, DateTimeOffset Expires, ImportFormat Format, IReadOnlyList<ImportRow> Rows, IReadOnlyList<Plate> Save);

    private static string OwnerOf(ICallContext caller) => string.IsNullOrEmpty(caller.SessionId) ? "user:" + caller.UserName : "session:" + caller.SessionId;

    private ImportState RequirePreview(ICallContext caller, string? previewId)
    {
        var now = _time.GetUtcNow();
        foreach (var (key, state) in _previews.ToArray())
        {
            if (state.Expires <= now)
            {
                _previews.TryRemove(key, out _);
            }
        }

        return previewId is not null && _previews.TryGetValue(previewId, out var found) && found.Owner == OwnerOf(caller)
            ? found
            : throw new InvalidOperationException(LicensePlateTexts.PreviewGone);
    }

    internal async Task<ImportPreview> ImportPreviewAsync(ICallContext caller, ImportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var store = Running.Store;
        var extra = (request.TagIds ?? []).Distinct().ToList();
        if (extra.Any(t => store.Tag(t) is null))
        {
            throw new InvalidOperationException(LicensePlateTexts.TagGone);
        }

        var upload = await caller.Uploads.FindAsync(request.UploadId ?? string.Empty, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The uploaded file is no longer on the server. Upload it again.");
        if (upload.Size > LicensePlateLimits.MaxImportBytes)
        {
            throw new ArgumentException("The file is larger than 16 MB.");
        }

        string text;
        await using (var stream = await caller.Uploads.OpenReadAsync(upload.Id, ct).ConfigureAwait(false))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        var (format, parsed) = PlateCsv.Parse(text);
        var (rows, save) = BuildImport(store, format, parsed, extra);
        var id = Guid.NewGuid().ToString("N");
        _previews[id] = new ImportState(OwnerOf(caller), _time.GetUtcNow() + PreviewLifetime, format, rows, save);
        return new ImportPreview(
            id,
            format,
            rows.Count(r => r.Status == ImportRowStatus.New),
            rows.Count(r => r.Status == ImportRowStatus.Changed),
            rows.Count(r => r.Status == ImportRowStatus.Unchanged),
            rows.Count(r => r.Status == ImportRowStatus.Refused));
    }

    /// <summary>Each row of a file compared with the list: new, changed, unchanged or refused (with why), and the plates to save.</summary>
    internal static (IReadOnlyList<ImportRow> Rows, IReadOnlyList<Plate> Save) BuildImport(PlateStore store, ImportFormat format, IReadOnlyList<CsvPlateRow> parsed, IReadOnlyList<Guid> extraTags)
    {
        var byName = store.Tags.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ImportRow>(parsed.Count);
        var save = new List<Plate>();
        var count = store.PlateCount;
        ImportRow Refused(CsvPlateRow row, string error) =>
            new(row.Line, row.Plate?.Text ?? PlateText.Normalize(row.RawPlate), row.Plate?.Name ?? string.Empty, row.TagNames, row.Plate?.AllowedFrom, row.Plate?.AllowedTo, row.Plate?.DeleteWhenEnds ?? false, ImportRowStatus.Refused, error);

        foreach (var row in parsed)
        {
            if (row.Error is { } error || row.Plate is null)
            {
                rows.Add(Refused(row, row.Error ?? PluginJson.UnreadableRequest));
                continue;
            }

            var unknown = row.TagNames.FirstOrDefault(n => !byName.ContainsKey(n));
            if (unknown is not null)
            {
                rows.Add(Refused(row, "No plate tag is named " + unknown + "."));
                continue;
            }

            var existing = store.Find(PlateText.Normalize(row.Plate.Text));
            var tags = row.TagNames.Select(n => byName[n]).Concat(extraTags);
            var incoming = format == ImportFormat.Alpv && existing is not null
                ? existing with { Name = row.Plate.Name, Tags = [.. existing.Tags.Concat(tags).Distinct()] }
                : row.Plate with { Tags = [.. tags.Distinct()] };
            var (plate, errors) = PlateRules.Check(incoming, id => store.Tag(id) is not null);
            if (errors.Count > 0)
            {
                rows.Add(Refused(row, errors.Values.First()));
                continue;
            }

            if (!seen.Add(plate.Text))
            {
                rows.Add(Refused(row, "This plate is already in the file."));
                continue;
            }

            ImportRowStatus status;
            if (existing is null)
            {
                if (count >= LicensePlateLimits.MaxPlates)
                {
                    rows.Add(Refused(row, LicensePlateTexts.TooManyPlates));
                    continue;
                }

                count++;
                status = ImportRowStatus.New;
            }
            else
            {
                status = Same(existing, plate) ? ImportRowStatus.Unchanged : ImportRowStatus.Changed;
            }

            if (status != ImportRowStatus.Unchanged)
            {
                save.Add(plate);
            }

            rows.Add(new ImportRow(row.Line, plate.Text, plate.Name, store.NamesOf(plate.Tags), plate.AllowedFrom, plate.AllowedTo, plate.DeleteWhenEnds, status, null));
        }

        return (rows, save);
    }

    private static bool Same(Plate a, Plate b) =>
        a.Name == b.Name && a.AllowedFrom == b.AllowedFrom && a.AllowedTo == b.AllowedTo && a.DeleteWhenEnds == b.DeleteWhenEnds && a.Tags.Order().SequenceEqual(b.Tags.Order());

    private ImportRowsPage ImportRows(ICallContext caller, ImportRowsRequest request)
    {
        var state = RequirePreview(caller, request.PreviewId);
        var after = Math.Max(0, request.After);
        var limit = Math.Clamp(request.Limit, 1, LicensePlateLimits.MaxImportPage);
        var rows = state.Rows.Skip(after).Take(limit).ToList();
        return new ImportRowsPage(rows, after + rows.Count < state.Rows.Count ? after + rows.Count : null);
    }

    private async Task<ImportResult> ImportSaveAsync(ICallContext caller, string? previewId, CancellationToken ct)
    {
        var state = RequirePreview(caller, previewId);
        var store = Running.Store;
        var added = state.Save.Count(p => store.Find(p.Text) is null);
        if (store.PlateCount + added > LicensePlateLimits.MaxPlates)
        {
            throw new InvalidOperationException(LicensePlateTexts.TooManyPlates);
        }

        if (state.Save.SelectMany(p => p.Tags).Distinct().Any(t => store.Tag(t) is null))
        {
            throw new InvalidOperationException(LicensePlateTexts.TagGone);
        }

        await store.WritePlatesAsync(state.Save, [], ct).ConfigureAwait(false);
        _previews.TryRemove(previewId!, out _);
        PublishPlates();
        return new ImportResult(added, state.Save.Count - added);
    }

    // ---- cameras ----

    internal async Task<CamerasReply> CamerasAsync(ICallContext caller, CancellationToken ct)
    {
        var runtime = Running.Runtime;
        var rows = new List<CameraRow>();
        foreach (var (entry, device) in await VisibleCamerasAsync(caller, ct).ConfigureAwait(false))
        {
            var state = runtime.StateOf(entry.DeviceId);
            rows.Add(new CameraRow
            {
                DeviceId = entry.DeviceId,
                Address = device.Address,
                HostName = device.HostName,
                Model = device.Model,
                Site = device.SiteName,
                AlpvVersion = state.AlpvVersion,
                Status = state.Status,
                StatusText = LicensePlateTexts.Status(state.Status, state.Detail),
                Since = state.Status == CameraStatus.ConnectionLost ? state.LostSince : null,
                AllowedTags = entry.AllowedTags,
                ImageSettings = state.Image,
            });
        }

        rows.Sort((a, b) => string.Compare(a.Address, b.Address, StringComparison.OrdinalIgnoreCase));
        var log = Running.Log;
        return new CamerasReply(rows, rows.Count(r => r.Status is CameraStatus.Connected or CameraStatus.EventsOff), rows.Count, log.HasFolder ? log.Problem : null);
    }

    internal async Task<CamerasReply> AddCamerasAsync(ICallContext caller, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var devices = await RequireDevicesAsync(caller, ids, ct).ConfigureAwait(false);
        var (_, store, _, runtime, _) = Running;
        var now = _time.GetUtcNow();
        List<CameraEntry> added = [.. devices.Where(d => store.Camera(d.Id) is null).Select(d => new CameraEntry { DeviceId = d.Id, Added = now })];
        foreach (var device in devices)
        {
            _names[device.Id] = NameOf(device);
        }

        await store.WriteCamerasAsync(added, [], ct).ConfigureAwait(false);
        if (added.Count > 0)
        {
            await runtime.SyncAsync([.. store.Cameras.Select(c => c.DeviceId)]).ConfigureAwait(false);
            EventTypesChanged?.Invoke(this, EventArgs.Empty);
            OnCamerasChanged([.. added.Select(a => a.DeviceId)]);
        }

        return await CamerasAsync(caller, ct).ConfigureAwait(false);
    }

    internal async Task<CamerasReply> RemoveCamerasAsync(ICallContext caller, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var devices = await RequireDevicesAsync(caller, ids, ct).ConfigureAwait(false);
        var (_, store, _, runtime, _) = Running;
        List<Guid> removed = [.. devices.Where(d => store.Camera(d.Id) is not null).Select(d => d.Id)];
        await store.WriteCamerasAsync([], removed, ct).ConfigureAwait(false);
        if (removed.Count > 0)
        {
            await runtime.SyncAsync([.. store.Cameras.Select(c => c.DeviceId)]).ConfigureAwait(false);
            EventTypesChanged?.Invoke(this, EventArgs.Empty);
            OnCamerasChanged(removed);
            ReportProblems();
        }

        return await CamerasAsync(caller, ct).ConfigureAwait(false);
    }

    internal async Task<CamerasReply> SetAllowedTagsAsync(ICallContext caller, AllowedTagsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var devices = await RequireDevicesAsync(caller, request.DeviceIds ?? [], ct).ConfigureAwait(false);
        var store = Running.Store;
        var named = (request.Set ?? []).Concat(request.Add ?? []);
        if (named.Any(t => store.Tag(t) is null))
        {
            throw new InvalidOperationException(LicensePlateTexts.TagGone);
        }

        var changed = new List<CameraEntry>();
        foreach (var device in devices)
        {
            var entry = store.Camera(device.Id) ?? throw new InvalidOperationException(LicensePlateTexts.CameraGone);
            var remove = (request.Remove ?? []).ToHashSet();
            List<Guid> tags = request.Set is { } set
                ? [.. set.Distinct()]
                : [.. entry.AllowedTags.Concat(request.Add ?? []).Where(t => !remove.Contains(t)).Distinct()];
            if (!tags.Order().SequenceEqual(entry.AllowedTags.Order()))
            {
                changed.Add(entry with { AllowedTags = tags });
            }
        }

        await store.WriteCamerasAsync(changed, [], ct).ConfigureAwait(false);
        if (changed.Count > 0)
        {
            Running.Matcher.KeepOnly(store.Cameras.Select(c => (IReadOnlySet<Guid>)c.AllowedTags.ToHashSet()));
            OnCamerasChanged([.. changed.Select(c => c.DeviceId)]);
            PublishPlates(); // the tags' camera counts
        }

        return await CamerasAsync(caller, ct).ConfigureAwait(false);
    }

    private async Task<CamerasReply> RefreshCamerasAsync(ICallContext caller, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var devices = await RequireDevicesAsync(caller, ids, ct).ConfigureAwait(false);
        Running.Runtime.Recheck(devices.Where(d => Running.Store.Camera(d.Id) is not null).Select(d => d.Id));
        return await CamerasAsync(caller, ct).ConfigureAwait(false);
    }

    /// <summary>The camera of the plugin the caller sees; one outside the call answers like an unknown id.</summary>
    private async Task<CameraEntry> RequireCameraAsync(ICallContext caller, Guid id, CancellationToken ct)
    {
        await RequireDevicesAsync(caller, [id], ct).ConfigureAwait(false);
        return Running.Store.Camera(id) ?? throw new InvalidOperationException(LicensePlateTexts.CameraGone);
    }

    /// <summary>The Set up dialog's read, fresh from the camera (read only; ALPV's secrets are dropped while parsed).</summary>
    internal async Task<SetupRead> ReadSetupAsync(ICallContext caller, Guid id, CancellationToken ct)
    {
        var entry = await RequireCameraAsync(caller, id, ct).ConfigureAwait(false);
        var alpv = new AlpvClient(await caller.Vapix.CreateAsync(id, ct).ConfigureAwait(false));
        var app = await alpv.AppAsync(ct).ConfigureAwait(false);
        if (AlpvClient.Problem(app) is { } problem)
        {
            throw new InvalidOperationException(LicensePlateTexts.Status(problem) + ".");
        }

        var config = await alpv.ReadConfigAsync(ct).ConfigureAwait(false);
        if (!config.ReadsFirstSensor)
        {
            throw new InvalidOperationException(LicensePlateTexts.OtherSensor + ".");
        }

        var areas = await alpv.ReadAreasAsync(ct).ConfigureAwait(false);
        var events = AlpvClient.SwitchesOf(await alpv.ReadEventsAsync(ct).ConfigureAwait(false));
        return new SetupRead
        {
            DeviceId = id,
            AlpvVersion = app!.Version ?? string.Empty,
            Areas = [.. areas.Select(a => a.Area)],
            Recognition = config.Recognition,
            Events = events,
            Rotated = config.Rotated,
            LastRead = Running.Runtime.StateOf(id).LastRead,
            AllowedTags = entry.AllowedTags,
        };
    }

    internal async Task<TasksReply> SaveSetupAsync(ICallContext caller, SaveSetupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequireCameraAsync(caller, request.DeviceId, ct).ConfigureAwait(false);
        var payload = request.Setup ?? new SetupPayload();
        if (SetupRules.Check(payload).Values.FirstOrDefault() is { } error)
        {
            throw new ArgumentException(error);
        }

        if (request.AllowedTags is { } tags)
        {
            await SetAllowedTagsAsync(caller, new AllowedTagsRequest([request.DeviceId], Set: tags), ct).ConfigureAwait(false);
        }

        return new TasksReply(await RunTaskAsync(caller, LicensePlatesInfo.SetupTaskId, [request.DeviceId], PluginJson.Serialize(payload, LicensePlatesJson.Options), "Set up License Plate Verifier", ct).ConfigureAwait(false));
    }

    /// <summary>The payload Copy settings starts on the targets: the source's recognition (and its areas when asked), read fresh.</summary>
    internal async Task<SetupPayload> CopyPayloadAsync(ICallContext caller, CopySettingsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targets = (request.TargetIds ?? []).Where(t => t != request.SourceId).Distinct().ToList();

        // Every device first: one outside the call answers like an unknown id, before anything else is checked.
        await RequireDevicesAsync(caller, [request.SourceId, .. targets], ct).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            throw new ArgumentException("Choose the cameras to copy to.");
        }

        if (targets.Append(request.SourceId).Any(id => Running.Store.Camera(id) is null))
        {
            throw new InvalidOperationException(LicensePlateTexts.CameraGone);
        }

        var source = await ReadSetupAsync(caller, request.SourceId, ct).ConfigureAwait(false);
        return new SetupPayload
        {
            Recognition = source.Recognition,
            Areas = request.Areas ? [.. source.Areas.Where(a => a.Id == 1 || a.Active)] : null,
        };
    }

    internal async Task<TasksReply> CopySettingsAsync(ICallContext caller, CopySettingsRequest request, CancellationToken ct)
    {
        var payload = await CopyPayloadAsync(caller, request, ct).ConfigureAwait(false);
        var targets = request.TargetIds.Where(t => t != request.SourceId).Distinct().ToList();
        return new TasksReply(await RunTaskAsync(caller, LicensePlatesInfo.SetupTaskId, targets, PluginJson.Serialize(payload, LicensePlatesJson.Options), "Copy settings of License Plate Verifier", ct).ConfigureAwait(false));
    }

    internal async Task<TasksReply> UseRecommendedAsync(ICallContext caller, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var devices = await RequireDevicesAsync(caller, ids, ct).ConfigureAwait(false);
        List<Guid> cameras = [.. devices.Where(d => Running.Store.Camera(d.Id) is not null).Select(d => d.Id)];
        if (cameras.Count != devices.Count)
        {
            throw new InvalidOperationException(LicensePlateTexts.CameraGone);
        }

        return new TasksReply(await RunTaskAsync(caller, LicensePlatesInfo.ImageSettingsTaskId, cameras, "{}", "Use recommended image settings", ct).ConfigureAwait(false));
    }

    private static Task<IReadOnlyList<Guid>> RunTaskAsync(ICallContext caller, string taskId, List<Guid> devices, string payload, string audit, CancellationToken ct) =>
        devices.Count == 0
            ? Task.FromResult<IReadOnlyList<Guid>>([])
            : caller.Tasks.RunAsync(taskId, devices, payload, caller.UserName, new TaskAudit(audit), ct);

    // ---- settings ----

    /// <summary>The errors of the settings by field (the dialog's and the save's); empty when they are valid.</summary>
    internal Dictionary<string, string> CheckSettings(LicensePlateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (settings.Deviation is < 0 or > 2)
        {
            errors["deviation"] = "Choose Exact, 1 or 2.";
        }

        if (settings.WaitSeconds is < 1 or > 10)
        {
            errors["waitSeconds"] = "Wait 1 to 10 seconds.";
        }

        if (settings.KeepDays is < 1 or > 3650)
        {
            errors["keepDays"] = "Keep reads for 1 to 3,650 days.";
        }

        if (settings.MaxGigabytes is < 1 or > 100_000)
        {
            errors["maxGigabytes"] = "Enter 1 to 100,000 GB.";
        }

        if (!string.IsNullOrWhiteSpace(settings.Folder) && FolderProblem(settings.Folder.Trim()) is { } folder)
        {
            errors["folder"] = folder;
        }

        return errors;
    }

    /// <summary>Why a folder cannot hold the log; null when it can (it is created when missing).</summary>
    internal string? FolderProblem(string folder)
    {
        if (!Path.IsPathFullyQualified(folder))
        {
            return "Choose a folder on the server.";
        }

        var full = Path.GetFullPath(folder);
        if (_ctx?.DataDirectory is { } data && Path.GetDirectoryName(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { } pluginData
            && (full + Path.DirectorySeparatorChar).StartsWith(pluginData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "Choose a folder outside the plugins' data of OADM: that is part of every backup.";
        }

        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".oadm-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, [1]);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return "OADM cannot write to this folder.";
        }
    }

    internal async Task<SaveSettingsReply> SaveSettingsAsync(LicensePlateSettings incoming, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var errors = CheckSettings(incoming);

        // The host's folder checks (docs/spec/security.md "Folders a plugin writes to"), the same as the folder picker's;
        // after the plugin's own, which create a missing folder. Also for an unchanged folder: its rights may have changed.
        if (!errors.ContainsKey("folder") && !string.IsNullOrWhiteSpace(incoming.Folder) && _ctx?.FolderCheck is { } folders
            && await folders.CheckAsync(Path.GetFullPath(incoming.Folder.Trim()), ct).ConfigureAwait(false) is { } problem)
        {
            errors["folder"] = problem;
        }

        if (errors.Count > 0)
        {
            return new SaveSettingsReply(null, errors);
        }

        var settings = incoming with { Folder = string.IsNullOrWhiteSpace(incoming.Folder) ? null : Path.GetFullPath(incoming.Folder.Trim()) };

        var (ctx, _, matcher, _, log) = Running;
        await _settingsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ctx.Settings.SetAsync(SettingsKey, PluginJson.Serialize(settings, LicensePlatesJson.Options), ct).ConfigureAwait(false);
            _settings = settings;
            matcher.Deviation = settings.Deviation;
            await log.ApplyAsync(settings, ct).ConfigureAwait(false);
            await LoadLoggedCamerasAsync(log).ConfigureAwait(false);
        }
        finally
        {
            _settingsGate.Release();
        }

        PublishStatus();
        return new SaveSettingsReply(settings);
    }

    // ---- downloads ----

    public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) =>
        OpenDownloadAsync(new ContextCall(_ctx ?? throw new InvalidOperationException(LicensePlateTexts.NotRunning)), method, payloadJson, ct);

    public async Task<PluginDownload> OpenDownloadAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        switch (method)
        {
            case LicensePlatesMethods.ExportLog:
            {
                if (!caller.Has(PermissionKeys.Of(Id, LicensePlatePermissions.Log)))
                {
                    throw new DeviceAccessRefusedException("Your role does not allow: View plate log.");
                }

                var request = PluginJson.ParseOrEmpty<LogExportRequest>(payloadJson, LicensePlatesJson.Options);
                var (bytes, name, rows, total) = await ExportAsync(caller, request, ct).ConfigureAwait(false);
                return new PluginDownload(new MemoryStream(bytes), name, bytes.Length)
                {
                    AuditDetail = total > rows ? "Exported plate log (" + LicensePlateTexts.Newest(rows, total) + ")" : "Exported plate log (" + LicensePlateTexts.Reads(rows) + ")",
                };
            }

            case LicensePlatesMethods.ExportPlates:
            {
                var store = Running.Store;
                var bytes = PlateCsv.Export(store.AllPlates(), store.NamesOf);
                return new PluginDownload(new MemoryStream(bytes), "plates.csv", bytes.Length);
            }

            default:
                throw new ArgumentException($"Unknown download '{method}'.", nameof(method));
        }
    }

    /// <summary>The file of an export: the newest 10,000 rows of the filters, the caller's cameras only.</summary>
    internal async Task<(byte[] Bytes, string Name, int Rows, int Total)> ExportAsync(ICallContext caller, LogExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var store = RequireLogStore();
        await store.FlushAsync().ConfigureAwait(false);
        var query = request.Query ?? new LogQuery();
        var scope = await LogScopeAsync(caller, ct).ConfigureAwait(false);
        var total = await store.CountAsync(query, scope, ct).ConfigureAwait(false);
        var rows = await store.NewestAsync(query, scope, LicensePlateLimits.MaxExportRows, ct).ConfigureAwait(false);
        var clock = Oadm.Server.Logging.Export.LogExportClock.Of(request.TimeZone, request.UtcOffsetMinutes);
        var head = new PlateExportHead(FilterLines(request, clock), Environment.MachineName, caller.UserName, clock.Local(_time.GetUtcNow()), total);
        var day = clock.Local(_time.GetUtcNow()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (request.Format == ExportFormat.Pdf)
        {
            var pictures = new Dictionary<long, byte[]>();
            if (rows.Count <= LicensePlateLimits.MaxPdfPictureRows)
            {
                foreach (var row in rows.Where(r => r.HasPicture))
                {
                    if (await store.PictureAsync(row.Id, scope, ct).ConfigureAwait(false) is { } jpeg)
                    {
                        pictures[row.Id] = jpeg;
                    }
                }
            }

            var pdf = await Task.Run(() => PlateLogExport.Pdf(rows, head, clock, r => pictures.GetValueOrDefault(r.Id), ct), ct).ConfigureAwait(false);
            return (pdf, $"license-plate-log-{day}.pdf", rows.Count, total);
        }

        return (PlateLogExport.Csv(rows, head, clock), $"license-plate-log-{day}.csv", rows.Count, total);
    }

    /// <summary>The filter lines of the export's head.</summary>
    private List<string> FilterLines(LogExportRequest request, Oadm.Server.Logging.Export.LogExportClock clock)
    {
        var q = request.Query ?? new LogQuery();
        var lines = new List<string>();
        var range = q.From is null && q.To is null
            ? "All"
            : (q.From is { } f ? clock.Minutes(f) : "the start") + " to " + (q.To is { } t ? clock.Minutes(t) : clock.Minutes(_time.GetUtcNow()));
        lines.Add("Time: " + (string.IsNullOrWhiteSpace(request.RangeName) ? range : request.RangeName.Trim() + ", " + range));
        if (q.CameraIds is { Count: > 0 } cameras)
        {
            lines.Add("Cameras: " + string.Join(", ", cameras.Select(c => _names.GetValueOrDefault(c) ?? c.ToString("D"))));
        }

        if (q.Results is { Count: > 0 } results)
        {
            lines.Add("Result: " + string.Join(", ", results.Select(LicensePlateTexts.Result)));
        }

        if (q.Tags is { Count: > 0 } tags)
        {
            lines.Add("Tags: " + string.Join(", ", tags));
        }

        if (q.Area is { } area)
        {
            lines.Add("Area: " + area.ToString(CultureInfo.InvariantCulture));
        }

        if (q.Direction is { } direction)
        {
            lines.Add("Direction: " + LicensePlateTexts.Direction(direction));
        }

        if (!string.IsNullOrWhiteSpace(q.Country))
        {
            lines.Add("Country: " + q.Country.Trim());
        }

        var vehicle = string.Join(", ", new[] { q.View, q.VehicleType, q.VehicleColor, q.VehicleMake, q.VehicleModel }.Where(v => !string.IsNullOrWhiteSpace(v)));
        if (vehicle.Length > 0)
        {
            lines.Add("Vehicle: " + vehicle);
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            lines.Add("Search: " + q.Search.Trim());
        }

        return lines;
    }
}
