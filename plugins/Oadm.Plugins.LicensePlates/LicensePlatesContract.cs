using System.Text.Json;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LicensePlates;

/// <summary>Ids and names shared by the server part, the client part and the tests (docs/spec/license-plate-center.md).</summary>
public static class LicensePlatesInfo
{
    public const string PluginId = "oadm.license-plates";
    public const string DisplayName = "License Plate Center";
    public const string IconKey = "car";

    /// <summary>The page task "Set up License Plate Verifier" (one per camera; only the page starts it).</summary>
    public const string SetupTaskId = "oadm.license-plates.setup";

    /// <summary>The page task "Use recommended image settings".</summary>
    public const string ImageSettingsTaskId = "oadm.license-plates.image-settings";

    /// <summary>The package name of AXIS License Plate Verifier in the application list.</summary>
    public const string AppName = "fflprapp";

    /// <summary>ALPV's own page, relative to the camera (the Set up footer opens it in Web View).</summary>
    public const string AlpvPage = "local/fflprapp/setup.html";

    /// <summary>The group and folder of the events in the rule dialog.</summary>
    public const string EventGroup = DisplayName;

    public const string PlateAllowedType = PluginId + ".plate-allowed";
    public const string PlateNotAllowedType = PluginId + ".plate-not-allowed";
    public const string UnknownPlateType = PluginId + ".unknown-plate";
    public const string PlateReadType = PluginId + ".plate-read";
    public const string CameraProblemType = PluginId + ".camera-problem";
    public const string AnyCameraProblemType = PluginId + ".any-camera-problem";

    /// <summary>The fields of the four read events (their placeholders).</summary>
    public const string PlateField = "plate";
    public const string ReadAsField = "readAs";
    public const string ListEntryField = "listEntry";
    public const string NameField = "name";
    public const string TagsField = "tags";
    public const string DirectionField = "direction";
    public const string AreaField = "area";
    public const string CountryField = "country";
    public const string ConfidenceField = "confidence";
    public const string VehicleTypeField = "vehicleType";
    public const string VehicleColourField = "vehicleColour";
    public const string ResultField = "result";

    /// <summary>The fields of the problem events.</summary>
    public const string ProblemField = "problem";
    public const string CameraField = "camera";
    public const string CountField = "count";
}

/// <summary>The limits of the plugin (docs/spec/license-plate-center.md "Rules that never break" 11, "Plates", "Log").</summary>
public static class LicensePlateLimits
{
    public const int MaxPlates = 100_000;
    public const int MaxTags = 200;
    public const int MinPlateLength = 2;
    public const int MaxPlateLength = 12;

    /// <summary>The most wildcard plates (with *) of the list; each is an index entry the matcher checks per read.</summary>
    public const int MaxWildcardPlates = 1_000;

    /// <summary>The most plates of the list a search by name looks for in the log.</summary>
    public const int MaxNamedPlates = 1_000;

    /// <summary>The fewest letters and digits besides * in a wildcard plate.</summary>
    public const int MinWildcardFixed = 3;
    public const int MaxNameLength = 100;
    public const int MaxTagNameLength = 64;

    /// <summary>The longest name of a camera in the plugin.</summary>
    public const int MaxCameraNameLength = 64;

    /// <summary>An export writes at most this many reads, newest first.</summary>
    public const int MaxExportRows = 10_000;

    /// <summary>The PDF has plate pictures only up to this many rows.</summary>
    public const int MaxPdfPictureRows = 2_000;

    /// <summary>Rows of one log page at most.</summary>
    public const int MaxLogPage = 500;

    /// <summary>Plates of one page of the plate list at most.</summary>
    public const int MaxPlatePage = 5_000;

    /// <summary>Rows of one page of the import preview at most.</summary>
    public const int MaxImportPage = 5_000;

    /// <summary>The largest import file.</summary>
    public const long MaxImportBytes = 16L * 1024 * 1024;

    /// <summary>The longest time range of the statistics.</summary>
    public static readonly TimeSpan MaxStatsRange = TimeSpan.FromDays(31);

    /// <summary>The read of a car is dropped this long after its last event.</summary>
    public static readonly TimeSpan CarTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A camera without events for this long has lost its connection (status and Camera problem).</summary>
    public static readonly TimeSpan LostDelay = TimeSpan.FromSeconds(30);

    /// <summary>Cameras with a problem are checked again this often.</summary>
    public static readonly TimeSpan ProblemRecheck = TimeSpan.FromMinutes(5);

    /// <summary>The log's cleanup runs this often.</summary>
    public static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    /// <summary>Plates with Delete when it ends are looked at this often (deleted within a minute after Allowed to).</summary>
    public static readonly TimeSpan EndedSweep = TimeSpan.FromSeconds(30);

    /// <summary>Cameras that connect at the same time.</summary>
    public const int ConnectParallelism = 16;
}

/// <summary>The permissions of the plugin (keys of <see cref="ICorePlugin.Permissions"/>).</summary>
public static class LicensePlatePermissions
{
    /// <summary>"View License Plate Center": the page, the plates, the cameras and their status.</summary>
    public const string View = PermissionKeys.Show;

    /// <summary>"View plate log": the Log and Stats tabs and the live reads.</summary>
    public const string Log = "log";

    /// <summary>"Export plate log": the CSV and PDF of the log (besides <see cref="Log"/>).</summary>
    public const string Export = "export";

    /// <summary>"Manage plates": plates, plate tags, import.</summary>
    public const string Plates = "plates";

    /// <summary>"Set up cameras": add and remove cameras, allowed tags, Set up, Copy settings, recommended image settings.</summary>
    public const string Cameras = "cameras";
}

/// <summary>
/// The backend methods of the client part (<c>InvokeAsync</c>), the downloads (<c>OpenDownloadAsync</c>) and the live event
/// topics. Each method's permission is in <see cref="LicensePlatesPlugin.RequiredPermission"/>; the plugin README "Client
/// contract" lists them.
/// </summary>
public static class LicensePlatesMethods
{
    // ---- Log ("log") ----

    /// <summary><see cref="LogPageRequest"/> -> <see cref="LogPage"/>: reads newest first, older or newer than a cursor.</summary>
    public const string Log = "log";

    /// <summary><see cref="LogQuery"/> -> <see cref="CountReply"/>: how many reads the filters match (the export's "of 48,213").</summary>
    public const string LogCount = "logCount";

    /// <summary>() -> <see cref="LogStatus"/>: reads of today, the log's size, its folder's problem.</summary>
    public const string LogStatus = "logStatus";

    /// <summary><see cref="ReadIdRequest"/> -> <see cref="PictureReply"/>: the plate crop of one read (JPEG, base64).</summary>
    public const string Picture = "picture";

    /// <summary>Download: <see cref="LogExportRequest"/> -> the CSV or PDF ("export").</summary>
    public const string ExportLog = "exportLog";

    /// <summary><see cref="StatsRequest"/> -> <see cref="StatsReply"/>.</summary>
    public const string Stats = "stats";

    // ---- Plates ("show" reads, "plates" changes) ----

    /// <summary><see cref="PlatesRequest"/> -> <see cref="PlatesPage"/>: plates in plate order, paged.</summary>
    public const string Plates = "plates";

    /// <summary><see cref="SavePlateRequest"/> -> <see cref="SavePlateReply"/>.</summary>
    public const string SavePlate = "savePlate";

    /// <summary><see cref="PlateTextsRequest"/> -> <see cref="CountReply"/>.</summary>
    public const string DeletePlates = "deletePlates";

    /// <summary><see cref="ChangePlateTagsRequest"/> -> <see cref="CountReply"/>.</summary>
    public const string ChangePlateTags = "changePlateTags";

    /// <summary>() -> <see cref="TagsReply"/>: the plate tags with the plates and cameras that use each.</summary>
    public const string Tags = "tags";

    /// <summary><see cref="SaveTagRequest"/> -> <see cref="SaveTagReply"/>.</summary>
    public const string SaveTag = "saveTag";

    /// <summary><see cref="DeleteTagRequest"/> -> <see cref="DeleteTagReply"/>: asks first when the tag is in use.</summary>
    public const string DeleteTag = "deleteTag";

    /// <summary><see cref="ImportRequest"/> -> <see cref="ImportPreview"/>: reads an uploaded CSV, saves nothing.</summary>
    public const string ImportPreview = "importPreview";

    /// <summary><see cref="ImportRowsRequest"/> -> <see cref="ImportRowsPage"/>: the rows of a preview, paged.</summary>
    public const string ImportRows = "importRows";

    /// <summary><see cref="ImportSaveRequest"/> -> <see cref="ImportResult"/>: saves a preview in one transaction.</summary>
    public const string ImportSave = "importSave";

    /// <summary>Download: () -> every plate as CSV in OADM's columns ("show").</summary>
    public const string ExportPlates = "exportPlates";

    // ---- Cameras ("show" reads, "cameras" changes) ----

    /// <summary>() -> <see cref="CamerasReply"/>: the caller's cameras of the plugin with status.</summary>
    public const string Cameras = "cameras";

    /// <summary><see cref="DevicesRequest"/> -> <see cref="CamerasReply"/>.</summary>
    public const string AddCameras = "addCameras";

    /// <summary><see cref="DevicesRequest"/> -> <see cref="CamerasReply"/>. Nothing on the camera changes.</summary>
    public const string RemoveCameras = "removeCameras";

    /// <summary><see cref="AllowedTagsRequest"/> -> <see cref="CamerasReply"/>.</summary>
    public const string SetAllowedTags = "setAllowedTags";

    /// <summary><see cref="RenameCameraRequest"/> -> <see cref="RenameCameraReply"/>: the camera's name in the plugin (never the device's in OADM).</summary>
    public const string RenameCamera = "renameCamera";

    /// <summary><see cref="DevicesRequest"/> -> <see cref="CamerasReply"/>: reads ALPV of these cameras again (in the background).</summary>
    public const string RefreshCameras = "refreshCameras";

    /// <summary><see cref="DeviceRequest"/> -> <see cref="SetupRead"/>: the Set up dialog's read, fresh from the camera ("show").</summary>
    public const string ReadSetup = "readSetup";

    /// <summary><see cref="SaveSetupRequest"/> -> <see cref="TasksReply"/>: the allowed tags, then the task Set up.</summary>
    public const string SaveSetup = "saveSetup";

    /// <summary><see cref="CopySettingsRequest"/> -> <see cref="TasksReply"/>: one Set up task per target.</summary>
    public const string CopySettings = "copySettings";

    /// <summary><see cref="DevicesRequest"/> -> <see cref="TasksReply"/>: one task Use recommended image settings per camera.</summary>
    public const string UseRecommendedImageSettings = "useRecommendedImageSettings";

    /// <summary>() -> <see cref="Oadm.Plugins.LicensePlates.AlpvLists"/>: ALPV's regions, countries, states, vehicle types and colours ("show").</summary>
    public const string AlpvLists = "alpvLists";

    /// <summary><see cref="VehicleNamesRequest"/> -> <see cref="VehicleNamesReply"/>: the vehicle makes ALPV knows, read from a connected camera ("log").</summary>
    public const string VehicleMakes = "vehicleMakes";

    /// <summary><see cref="VehicleNamesRequest"/> (with the make) -> <see cref="VehicleNamesReply"/>: the models of a make ("log").</summary>
    public const string VehicleModels = "vehicleModels";

    // ---- Settings (Administrators) ----

    /// <summary>() -> <see cref="LicensePlateSettings"/>.</summary>
    public const string Settings = "settings";

    /// <summary><see cref="LicensePlateSettings"/> -> <see cref="SaveSettingsReply"/>.</summary>
    public const string SaveSettings = "saveSettings";

    // ---- Live events ----

    /// <summary>Event: a JSON array with the id of each camera that has new or changed reads (to the users who see the camera); read <see cref="Log"/> with <c>After</c>.</summary>
    public const string ReadsTopic = "reads";

    /// <summary>Event: a JSON array with the id of each camera whose row changed (status, tags, image settings, added, removed).</summary>
    public const string CamerasTopic = "cameras";

    /// <summary>Event: <c>{}</c> after a change of the plates or plate tags; read <see cref="Plates"/> and <see cref="Tags"/> again.</summary>
    public const string PlatesTopic = "plates";

    /// <summary>Event: <c>{}</c> when the log's status changed (a folder problem, the settings); read <see cref="LogStatus"/>.</summary>
    public const string StatusTopic = "status";

    /// <summary>The methods that need the plate log permission.</summary>
    public static bool IsLog(string method) => method is Log or LogCount or LogStatus or Picture or Stats or VehicleMakes or VehicleModels;

    /// <summary>The methods that change plates or plate tags.</summary>
    public static bool IsPlateChange(string method) =>
        method is SavePlate or DeletePlates or ChangePlateTags or SaveTag or DeleteTag or ImportSave or ImportPreview or ImportRows;

    /// <summary>The methods that change cameras or start tasks on them.</summary>
    public static bool IsCameraChange(string method) =>
        method is AddCameras or RemoveCameras or SetAllowedTags or RefreshCameras or SaveSetup or CopySettings or UseRecommendedImageSettings or RenameCamera;

    /// <summary>The settings dialog (Administrators).</summary>
    public static bool IsSettings(string method) => method is Settings or SaveSettings;
}

/// <summary>The JSON options of the plugin's settings, records, page calls and events.</summary>
public static class LicensePlatesJson
{
    public static readonly JsonSerializerOptions Options = PluginJson.CreateOptions(PluginJsonEnums.CamelCase);
}

// ---------------------------------------------------------------------------------------------------------------------
// Plates and plate tags

/// <summary>The 8 tag colours, numbered like the device tags (the client's TagPalette).</summary>
public enum PlateTagColor
{
    Blue = 1,
    Green = 2,
    Teal = 3,
    Amber = 4,
    Orange = 5,
    Red = 6,
    Pink = 7,
    Violet = 8,
}

/// <summary>A plate tag: says where a plate is allowed (each camera names the tags allowed there).</summary>
public sealed record PlateTag
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public PlateTagColor Color { get; init; } = PlateTagColor.Blue;
}

/// <summary>
/// A plate of the list. <see cref="Text"/> is its key: upper case, letters and digits only. The times are wall-clock times of
/// the server's time zone (no offset), as the user entered them.
/// </summary>
public sealed record Plate
{
    public string Text { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>The ids of its plate tags; a plate without tags is allowed nowhere.</summary>
    public IReadOnlyList<Guid> Tags { get; init; } = [];

    public DateTime? AllowedFrom { get; init; }

    public DateTime? AllowedTo { get; init; }

    /// <summary>The server deletes the plate within a minute after <see cref="AllowedTo"/> (only with an Allowed to).</summary>
    public bool DeleteWhenEnds { get; init; }
}

public sealed record PlatesRequest
{
    /// <summary>The page starts after this plate (plate order); null: the first page.</summary>
    public string? After { get; init; }

    public int Limit { get; init; } = LicensePlateLimits.MaxPlatePage;

    /// <summary>Plates whose text starts with it or whose name holds it; null: every plate.</summary>
    public string? Search { get; init; }

    /// <summary>Only plates with this tag.</summary>
    public Guid? TagId { get; init; }
}

public sealed record PlatesPage(IReadOnlyList<Plate> Plates, string? Next, int Total);

/// <summary>A new plate (<see cref="Original"/> null) or a change of the plate <see cref="Original"/> (its text may change).</summary>
public sealed record SavePlateRequest(Plate Plate, string? Original = null);

/// <summary>The saved plate, or the errors by field (<c>text</c>, <c>name</c>, <c>tags</c>, <c>allowedFrom</c>, <c>allowedTo</c>, <c>deleteWhenEnds</c>).</summary>
public sealed record SavePlateReply(Plate? Plate, IReadOnlyDictionary<string, string>? Errors = null);

public sealed record PlateTextsRequest(IReadOnlyList<string> Plates);

public sealed record ChangePlateTagsRequest(IReadOnlyList<string> Plates, IReadOnlyList<Guid> Add, IReadOnlyList<Guid> Remove);

public sealed record CountReply(int Count);

public sealed record TagInfo(Guid Id, string Name, PlateTagColor Color, int Plates, int Cameras);

public sealed record TagsReply(IReadOnlyList<TagInfo> Tags);

public sealed record SaveTagRequest(PlateTag Tag);

/// <summary>The saved tag, or the errors by field (<c>name</c>, <c>color</c>).</summary>
public sealed record SaveTagReply(PlateTag? Tag, IReadOnlyDictionary<string, string>? Errors = null);

/// <summary>Deletes a tag; a tag in use is deleted only with <see cref="Confirmed"/> (it is then removed from plates and cameras).</summary>
public sealed record DeleteTagRequest(Guid Id, bool Confirmed = false);

/// <summary><see cref="Deleted"/> false: the tag is in use and was kept; ask, then send <c>Confirmed</c>.</summary>
public sealed record DeleteTagReply(bool Deleted, int Plates, int Cameras);

/// <summary>An uploaded CSV (the caller's upload id) and the tags to give every imported plate.</summary>
public sealed record ImportRequest(string UploadId, IReadOnlyList<Guid>? TagIds = null);

public enum ImportFormat
{
    /// <summary>OADM's columns: plate, name, tags, allowed from, allowed to, delete when it ends.</summary>
    Oadm = 0,

    /// <summary>ALPV's export: plate, date, description (the description is the name, the date is ignored).</summary>
    Alpv = 1,
}

public enum ImportRowStatus
{
    New = 0,
    Changed = 1,
    Unchanged = 2,
    Refused = 3,
}

/// <summary>The preview of an import (kept 10 minutes for the caller's session): the counts; its rows with <see cref="LicensePlatesMethods.ImportRows"/>.</summary>
public sealed record ImportPreview(string PreviewId, ImportFormat Format, int New, int Changed, int Unchanged, int Refused, string? Error = null);

/// <summary>One row of a preview: the plate as it will be saved, its status and, for a refused row, why.</summary>
public sealed record ImportRow(int Line, string Plate, string Name, IReadOnlyList<string> Tags, DateTime? AllowedFrom, DateTime? AllowedTo, bool DeleteWhenEnds, ImportRowStatus Status, string? Error);

public sealed record ImportRowsRequest(string PreviewId, int After = 0, int Limit = LicensePlateLimits.MaxImportPage);

/// <summary>Rows after the index <c>After</c>; <see cref="Next"/> is the index for the next page, null at the end.</summary>
public sealed record ImportRowsPage(IReadOnlyList<ImportRow> Rows, int? Next);

public sealed record ImportSaveRequest(string PreviewId);

public sealed record ImportResult(int Added, int Changed);

// ---------------------------------------------------------------------------------------------------------------------
// Cameras

/// <summary>The status of a camera of the plugin (docs/spec/license-plate-center.md "Cameras").</summary>
public enum CameraStatus
{
    /// <summary>Added, not checked or connected yet.</summary>
    Connecting = 0,
    Connected = 1,

    /// <summary>No connection for 30 s (<see cref="CameraRow.Since"/>).</summary>
    ConnectionLost = 2,
    EventsOff = 3,
    AlpvStopped = 4,
    AlpvNotInstalled = 5,
    AlpvNoLicense = 6,
    AlpvNeedsVersion3 = 7,
    OtherSensor = 8,
    SiteOffline = 9,

    /// <summary>OADM cannot work with the device (credentials, certificate, unreachable): <see cref="CameraRow.StatusText"/>.</summary>
    DeviceProblem = 10,
}

/// <summary>One of ALPV's image settings: the camera's value and the one ALPV recommends (ALPV's own units).</summary>
public sealed record ImageValue(string Name, string? Current, string? Recommended)
{
    public bool Matches => string.Equals(Current, Recommended, StringComparison.Ordinal);
}

/// <summary>The column Image settings: whether they are as ALPV recommends (<c>isparamsoptimal</c>) and each value.</summary>
public sealed record ImageSettingsInfo(bool Recommended, IReadOnlyList<ImageValue> Values);

public sealed record CameraRow
{
    public Guid DeviceId { get; init; }

    /// <summary>The camera's name in the plugin: its own, else the device's host name, else its address.</summary>
    public string Name { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public string? HostName { get; init; }

    public string? Model { get; init; }

    public string? Site { get; init; }

    /// <summary>ALPV's version; null when not installed or not read yet.</summary>
    public string? AlpvVersion { get; init; }

    public CameraStatus Status { get; init; }

    /// <summary>The status in words ("Connection lost", "Site offline: Building B").</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>Since when the connection is lost (<see cref="CameraStatus.ConnectionLost"/>).</summary>
    public DateTimeOffset? Since { get; init; }

    public IReadOnlyList<Guid> AllowedTags { get; init; } = [];

    /// <summary>Null until the configuration was read.</summary>
    public ImageSettingsInfo? ImageSettings { get; init; }
}

/// <summary>The caller's cameras, "41 of 42 cameras connected", and a problem of the log store (null: none).</summary>
public sealed record CamerasReply(IReadOnlyList<CameraRow> Cameras, int Connected, int Total, string? LogProblem = null);

public sealed record DevicesRequest(IReadOnlyList<Guid> DeviceIds);

public sealed record DeviceRequest(Guid DeviceId);

/// <summary>A new name of a camera in the plugin (required, at most 64 characters, unique among the plugin's cameras).</summary>
public sealed record RenameCameraRequest(Guid DeviceId, string Name);

/// <summary>The refusal of the name (below the field), or null when it was saved.</summary>
public sealed record RenameCameraReply(string? Error = null);

/// <summary>The allowed tags of the cameras: <see cref="Set"/> replaces them; else <see cref="Add"/> and <see cref="Remove"/>.</summary>
public sealed record AllowedTagsRequest(IReadOnlyList<Guid> DeviceIds, IReadOnlyList<Guid>? Set = null, IReadOnlyList<Guid>? Add = null, IReadOnlyList<Guid>? Remove = null);

/// <summary>A point of an area, 0 to 1000 relative to the picture on each axis (as ALPV keeps it).</summary>
public sealed record AreaPoint(int X, int Y);

/// <summary>An area of ALPV: 3 to 8 corners and the driving direction that counts as In (thousandths of a degree, 0 to 360000).</summary>
public sealed record AlpvArea
{
    /// <summary>1 or 2.</summary>
    public int Id { get; init; } = 1;

    public bool Active { get; init; } = true;

    public int Angle { get; init; }

    public IReadOnlyList<AreaPoint> Corners { get; init; } = [];
}

/// <summary>
/// The recognition settings of ALPV that OADM sets. Null: the camera's configuration does not have the field (it is not
/// shown and never sent).
/// </summary>
public sealed record RecognitionSettings
{
    /// <summary><c>lprUseCase</c>: the code of a region of <see cref="AlpvLists.Regions"/> as text ("5" Europe).</summary>
    public string? Region { get; init; }

    /// <summary><c>CountryPriority</c>: a code of <see cref="AlpvLists.Countries"/> ("AUT", "US"); "0" none.</summary>
    public string? CameraLocation { get; init; }

    /// <summary><c>StatePriority</c>: a code of <see cref="AlpvLists.States"/> ("CMX"); empty none.</summary>
    public string? State { get; init; }

    /// <summary><c>plate.plate_min_size</c>, plate width in pixels.</summary>
    public int? PlateWidthMin { get; init; }

    /// <summary><c>plate.plate_max_size</c>.</summary>
    public int? PlateWidthMax { get; init; }

    /// <summary><c>plate_min_size_symb</c>, number of characters.</summary>
    public int? CharactersMin { get; init; }

    /// <summary><c>plate_max_size_symb</c>.</summary>
    public int? CharactersMax { get; init; }

    /// <summary><c>plate.threshold</c>, 0 to 1.</summary>
    public double? ConfidenceThreshold { get; init; }
}

/// <summary>ALPV's event switches OADM needs.</summary>
public sealed record EventSwitches(bool New, bool Update, bool Lost)
{
    public bool AllOn => New && Update && Lost;
}

/// <summary>The Set up dialog's read of one camera, fresh from the camera.</summary>
public sealed record SetupRead
{
    public Guid DeviceId { get; init; }

    public string AlpvVersion { get; init; } = string.Empty;

    /// <summary>Both areas as ALPV keeps them (area 2 always exists; used when active).</summary>
    public IReadOnlyList<AlpvArea> Areas { get; init; } = [];

    public RecognitionSettings Recognition { get; init; } = new();

    public EventSwitches Events { get; init; } = new(false, false, false);

    /// <summary>The picture is rotated in ALPV: areas are set up on ALPV's page ("Rotated pictures are set up on the ALPV page").</summary>
    public bool Rotated { get; init; }

    public DateTimeOffset? LastRead { get; init; }

    public IReadOnlyList<Guid> AllowedTags { get; init; } = [];

    /// <summary>ALPV's page on the camera, for Web View.</summary>
    public string AlpvPage { get; init; } = LicensePlatesInfo.AlpvPage;
}

/// <summary>
/// The payload of the task "Set up License Plate Verifier". Each part is optional: null leaves it as it is. Recognition
/// values that equal the camera's are not sent; fields the camera does not have are never sent. Events are always turned on.
/// </summary>
public sealed record SetupPayload
{
    /// <summary>Area 1 and, when used, area 2 (area 2 missing: it is turned off).</summary>
    public IReadOnlyList<AlpvArea>? Areas { get; init; }

    public RecognitionSettings? Recognition { get; init; }
}

/// <summary>Saves the allowed tags of the camera (null: unchanged) and starts the task with <see cref="Setup"/>.</summary>
public sealed record SaveSetupRequest(Guid DeviceId, SetupPayload Setup, IReadOnlyList<Guid>? AllowedTags = null);

/// <summary>Recognition and the event switches of <see cref="SourceId"/> to <see cref="TargetIds"/>; the areas only with <see cref="Areas"/>.</summary>
public sealed record CopySettingsRequest(Guid SourceId, IReadOnlyList<Guid> TargetIds, bool Areas = false);

public sealed record TasksReply(IReadOnlyList<Guid> TaskIds);

/// <summary>The vehicle makes (no <see cref="Make"/>) or the models of a make, read from a connected camera of <see cref="CameraIds"/>
/// (null: any camera of the caller); kept per ALPV version.</summary>
public sealed record VehicleNamesRequest(IReadOnlyList<Guid>? CameraIds = null, string? Make = null);

/// <summary>The names as ALPV lists them; <see cref="Problem"/> when no camera could answer ("No camera is connected.").</summary>
public sealed record VehicleNamesReply(IReadOnlyList<string> Names, string? Problem = null);

// ---------------------------------------------------------------------------------------------------------------------
// The log

public enum ReadResult
{
    Allowed = 0,
    NotAllowed = 1,
    Unknown = 2,
}

/// <summary>Why a read got its result (the tooltip of the Result column).</summary>
public enum ReadReason
{
    /// <summary>The plate of the list, read exactly.</summary>
    Exact = 0,

    /// <summary>A plate of the list within the allowed deviation (<see cref="PlateRead.ReadAs"/> holds the camera's text).</summary>
    Near = 1,

    /// <summary>The plate has no tag allowed at this camera.</summary>
    NoTagHere = 2,

    /// <summary>Before Allowed from (<see cref="PlateRead.ReasonTime"/>).</summary>
    NotYet = 3,

    /// <summary>After Allowed to (<see cref="PlateRead.ReasonTime"/>).</summary>
    Ended = 4,

    /// <summary>Equally close to two allowed plates.</summary>
    SeveralPlates = 5,

    /// <summary>No plate of the list.</summary>
    NoPlate = 6,
}

public enum PlateDirection
{
    Unknown = 0,
    In = 1,
    Out = 2,
}

/// <summary>One row of the log: a read, or a gap ("Connection lost from 13:58 to 14:06", <see cref="Gap"/>).</summary>
public sealed record PlateRead
{
    public long Id { get; init; }

    public DateTimeOffset Time { get; init; }

    public Guid CameraId { get; init; }

    /// <summary>The camera as it was named at the time (host name or address).</summary>
    public string Camera { get; init; } = string.Empty;

    /// <summary>The plate of the list, or the text the camera read when no plate matched.</summary>
    public string Plate { get; init; } = string.Empty;

    /// <summary>The camera's text when it differs from <see cref="Plate"/> (a near match); null otherwise.</summary>
    public string? ReadAs { get; init; }

    /// <summary>The plate of the list that decided the read, as entered ("AXIS12**" for a wildcard plate); null for an unknown plate.</summary>
    public string? ListEntry { get; init; }

    public ReadResult Result { get; init; }

    public ReadReason Reason { get; init; }

    /// <summary>The Allowed from or Allowed to of <see cref="ReadReason.NotYet"/> and <see cref="ReadReason.Ended"/>.</summary>
    public DateTime? ReasonTime { get; init; }

    /// <summary>The plate's name at the time of the read.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The plate's tag names at the time of the read.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public PlateDirection Direction { get; init; }

    public int? Area { get; init; }

    public string? Country { get; init; }

    /// <summary><c>lpConfidence</c> times 100, 0 to 100.</summary>
    public int Confidence { get; init; }

    public string? View { get; init; }

    public string? VehicleType { get; init; }

    public string? VehicleColor { get; init; }

    public string? VehicleMake { get; init; }

    public string? VehicleModel { get; init; }

    /// <summary><c>vehicleConfidence</c> times 100; null when the camera sends none.</summary>
    public int? VehicleConfidence { get; init; }

    /// <summary>The plate's state (<c>regionCode</c>, "CMX"); null without one.</summary>
    public string? State { get; init; }

    /// <summary>The log kept the plate crop (<see cref="LicensePlatesMethods.Picture"/>).</summary>
    public bool HasPicture { get; init; }

    /// <summary>A gap row: the connection was lost from <see cref="Time"/> to <see cref="Until"/>.</summary>
    public bool Gap { get; init; }

    public DateTimeOffset? Until { get; init; }
}

/// <summary>The filters of the Log tab (all optional; null cameras: every camera of the caller).</summary>
public sealed record LogQuery
{
    public IReadOnlyList<Guid>? CameraIds { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public IReadOnlyList<ReadResult>? Results { get; init; }

    /// <summary>Reads that had one of these plate tag names.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    public int? Area { get; init; }

    public PlateDirection? Direction { get; init; }

    public string? Country { get; init; }

    public string? View { get; init; }

    public string? VehicleType { get; init; }

    public string? VehicleColor { get; init; }

    public string? VehicleMake { get; init; }

    public string? VehicleModel { get; init; }

    /// <summary>
    /// Plates that start with it, or the reads of the plates of the list whose name holds its words (the plate's current name,
    /// found in the plate list first; decision 2026-11-27).
    /// </summary>
    public string? Search { get; init; }

    /// <summary>The plates of the list the search's name found (the server fills it; never sent).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> NamedPlates { get; init; } = [];
}

/// <summary>A position in the log (newest first: time, then id).</summary>
public sealed record LogCursor(long TimeMs, long Id);

/// <summary>
/// A page of the log: <see cref="Before"/> gives the older rows after it (by time); <see cref="After"/> the rows stored after
/// the read with its id, whatever their time (the live list: a read that waited for its best read and a gap row carry an
/// earlier time than reads stored before them; newest first, at most <see cref="Limit"/>); neither: the newest rows.
/// </summary>
public sealed record LogPageRequest
{
    public LogQuery Query { get; init; } = new();

    public LogCursor? Before { get; init; }

    public LogCursor? After { get; init; }

    public int Limit { get; init; } = 200;
}

/// <summary>Rows newest first; <see cref="Next"/>: the cursor of the next older page, null at the end.</summary>
public sealed record LogPage(IReadOnlyList<PlateRead> Rows, LogCursor? Next)
{
    /// <summary>More plates of the list than <see cref="LicensePlateLimits.MaxNamedPlates"/> have a name with the search: only the first are searched.</summary>
    public bool TooManyNames { get; init; }
}

public sealed record LogStatus
{
    /// <summary>Reads of today (server day) of the caller's cameras.</summary>
    public int ReadsToday { get; init; }

    public long UsedBytes { get; init; }

    public long MaxBytes { get; init; }

    public int KeepDays { get; init; }

    /// <summary>A folder is set.</summary>
    public bool HasFolder { get; init; }

    /// <summary>Why reads are not stored now; null: they are.</summary>
    public string? Problem { get; init; }
}

public sealed record ReadIdRequest(long Id);

/// <summary>The plate crop as JPEG in base64; null when the log has none.</summary>
public sealed record PictureReply(string? Jpeg);

public enum ExportFormat
{
    Csv = 0,
    Pdf = 1,
}

/// <summary>An export of what the Log tab shows. The times are written in the user's zone (<see cref="TimeZone"/>, else the offset).</summary>
public sealed record LogExportRequest
{
    public LogQuery Query { get; init; } = new();

    public ExportFormat Format { get; init; }

    /// <summary>The user's time zone (Windows or IANA id).</summary>
    public string? TimeZone { get; init; }

    public int UtcOffsetMinutes { get; init; }

    /// <summary>The name of the time filter for the head ("Last 24 hours"); null: From and To.</summary>
    public string? RangeName { get; init; }

    /// <summary>The Log's chosen columns in their order (<see cref="LogColumns"/> keys); null: the default columns.</summary>
    public IReadOnlyList<string>? Columns { get; init; }
}

/// <summary>The steps of the Stats' charts: hours (Today, Last 24 hours) or days in the user's zone (Last 7 and 30 days).</summary>
public enum StatsStep
{
    Hour = 0,
    Day = 1,
}

public sealed record StatsRequest
{
    public IReadOnlyList<Guid>? CameraIds { get; init; }

    public DateTimeOffset From { get; init; }

    public DateTimeOffset To { get; init; }

    /// <summary>Hours or days; days begin at midnight in <see cref="TimeZone"/>.</summary>
    public StatsStep Step { get; init; }

    /// <summary>The user's time zone (Windows or IANA id); null: <see cref="UtcOffsetMinutes"/>.</summary>
    public string? TimeZone { get; init; }

    public int UtcOffsetMinutes { get; init; }
}

public sealed record ResultCounts(int Allowed, int NotAllowed, int Unknown)
{
    public int Total => Allowed + NotAllowed + Unknown;
}

/// <summary>The confidence bands of the Log and the Stats: below <see cref="Error"/> the error colour, below <see cref="Warning"/> the warning colour.</summary>
public static class ConfidenceLevels
{
    public const int Error = 60;
    public const int Warning = 75;

    /// <summary>The lower edge of the best band.</summary>
    public const int High = 90;
}

/// <summary>The top cards of the Stats: the reads of the chosen cameras in the last hour, 24 hours, 7 days and all the log holds.</summary>
public sealed record ReadTotals(long LastHour, long Last24Hours, long Last7Days, long Total);

/// <summary>
/// One camera of the Stats over the chosen time: its reads, its average plate confidence (0 to 100), its average vehicle
/// confidence (null: the camera sent none, no vehicle detection) and one value per step for the charts (oldest first;
/// a step without reads has no confidence: null).
/// </summary>
public sealed record CameraSeries(
    Guid CameraId,
    string Camera,
    int Reads,
    double? Confidence,
    double? VehicleConfidence,
    IReadOnlyList<int> ReadsPerStep,
    IReadOnlyList<double?> ConfidencePerStep);

/// <summary>
/// The Stats tab (decisions 2026-11-27): the top cards (<see cref="Totals"/>, the chosen cameras, independent of the time),
/// and for the chosen time the reads by result, the overall average confidence, the Total line (<see cref="TotalReads"/>,
/// <see cref="TotalConfidence"/>) and one <see cref="CameraSeries"/> per camera, busiest first. <see cref="Start"/> is the
/// start of the first step; there are <see cref="Steps"/> steps of <see cref="Step"/>.
/// </summary>
public sealed record StatsReply
{
    public ReadTotals Totals { get; init; } = new(0, 0, 0, 0);

    public ResultCounts Results { get; init; } = new(0, 0, 0);

    /// <summary>The average plate confidence of every read of the time, 0 when there is none.</summary>
    public double AverageConfidence { get; init; }

    public DateTimeOffset Start { get; init; }

    public StatsStep Step { get; init; }

    public int Steps { get; init; }

    public IReadOnlyList<int> TotalReads { get; init; } = [];

    public IReadOnlyList<double?> TotalConfidence { get; init; } = [];

    public IReadOnlyList<CameraSeries> Cameras { get; init; } = [];
}

// ---------------------------------------------------------------------------------------------------------------------
// Settings

/// <summary>The settings dialog (Administrators). The folder is part of the backup, the log is not.</summary>
public sealed record LicensePlateSettings
{
    /// <summary>Allowed character deviation: 0 (Exact), 1 or 2.</summary>
    public int Deviation { get; init; } = 1;

    /// <summary>Wait for the best read, 1 to 10 seconds.</summary>
    public int WaitSeconds { get; init; } = 3;

    /// <summary>Keep reads for (days).</summary>
    public int KeepDays { get; init; } = 30;

    /// <summary>Maximum size of the log (GB).</summary>
    public int MaxGigabytes { get; init; } = 20;

    /// <summary>The log's folder on the server; null: none chosen yet, nothing is stored.</summary>
    public string? Folder { get; init; }

    public bool KeepPictures { get; init; } = true;

    public bool KeepUnknown { get; init; } = true;
}

/// <summary>The saved settings, or the errors by field (<c>deviation</c>, <c>waitSeconds</c>, <c>keepDays</c>, <c>maxGigabytes</c>, <c>folder</c>).</summary>
public sealed record SaveSettingsReply(LicensePlateSettings? Settings, IReadOnlyDictionary<string, string>? Errors = null);
