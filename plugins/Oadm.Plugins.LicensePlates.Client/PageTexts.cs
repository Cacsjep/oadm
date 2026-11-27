using System.Globalization;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>The page's own texts (CLAUDE.md "Wording"); statuses, results and refusals are the shared <see cref="LicensePlateTexts"/>.</summary>
public static class PageTexts
{
    public const string Subtitle = "One plate list for every camera with License Plate Verifier.";

    public const string LogTab = "Log";
    public const string PlatesTab = "Plates";
    public const string CamerasTab = "Cameras";
    public const string StatsTab = "Stats";

    // ---- shared ----
    public const string Cameras = "cameras";
    public const string ChooseCameras = "Choose cameras";
    public const string Search = "Search plates or names";
    public const string SearchCameras = "Search cameras";
    public const string AllTags = "All tags";
    public const string NoTags = "No tags";
    public const string Delete = "Delete";
    public const string Remove = "Remove";
    public const string Save = "Save";
    public const string Refresh = "Refresh";
    public const string Settings = "Settings";

    // ---- columns ----
    public const string Time = "Time";
    public const string Camera = "Camera";
    public const string Picture = "Picture";
    public const string Plate = "Plate";
    public const string Name = "Name";
    public const string Tags = "Tags";
    public const string Result = "Result";
    public const string Direction = "Direction";
    public const string Confidence = "Confidence";
    public const string AllowedFrom = "Allowed from";
    public const string AllowedTo = "Allowed to";
    public const string Address = "Address";
    public const string Site = "Site";
    public const string Alpv = "ALPV";
    public const string Status = "Status";
    public const string AllowedTags = "Allowed tags";
    public const string ImageSettings = "Image settings";
    public const string Line = "Line";
    public const string Reads = "Reads";

    // ---- Log ----
    public const string Pause = "Pause";
    public const string Resume = "Resume";
    public const string Live = "Live";
    public const string Paused = "Paused";
    public const string AddToPlates = "Add to plates";
    public const string AddToPlatesTip = "Select an unknown plate first";
    public const string ClearFilters = "Clear filters";
    public const string AllResults = "All results";
    public const string AllAreas = "All areas";
    public const string AllDirections = "All directions";
    public const string Country = "Country";
    public const string AllCountries = "All countries";
    public const string None = "None";
    public const string State = "State";
    public const string ReadingMakes = "Reading the makes";
    public const string Vehicle = "Vehicle";
    public const string View = "View";
    public const string Type = "Type";
    public const string Colour = "Colour";
    public const string ReadAsColumn = "Read as";
    public const string VehicleTypeColumn = "Vehicle type";
    public const string VehicleColorColumn = "Vehicle colour";
    public const string VehicleMakeColumn = "Vehicle make";
    public const string VehicleModelColumn = "Vehicle model";
    public const string Make = "Make";
    public const string Model = "Model";
    public const string Any = "Any";
    public const string Front = "Front";
    public const string Rear = "Rear";
    public const string ReadingLog = "Reading the log";
    public const string NoReads = "No reads yet. Add cameras on the Cameras tab.";
    public const string NoMatchingReads = "No reads match the filters.";
    public const string ChooseCamerasFirst = "Choose the cameras first.";
    public const string Exporting = "Making the file";
    public const string ExportTitle = "Export plate log";

    // ---- Plates ----
    public const string AddPlate = "Add plate";
    public const string EditPlate = "Edit plate";
    public const string Import = "Import";
    public const string Export = "Export";
    public const string ChangeTags = "Change tags";
    public const string ManageTags = "Manage tags";
    public const string ReadingPlates = "Reading the plates";
    public const string NoPlates = "No plates yet. Add a plate or import a list.";
    public const string NoMatchingPlates = "No plates match the search.";
    public const string DeletePlatesTitle = "Delete plates";
    public const string DeleteWhenEnds = "Delete when it ends";
    public const string DeleteWhenEndsTip = "Only with Allowed to";
    public const string EndedTip = "Ended. The plate is no longer allowed.";
    public const string EnterPlate = "Enter the plate.";
    public const string PlatesFile = "plates.csv";

    // ---- Import ----
    public const string ImportTitle = "Import plates";
    public const string ChooseFile = "Choose file";
    public const string TagsForEvery = "Tags for every plate";
    public const string ReadingFile = "Reading the file";
    public const string ImportButton = "Import";
    public const string ChooseFileFirst = "Choose a file first.";
    public const string NothingToImport = "No new or changed plates in the file.";
    public const string RefusedRows = "Refused rows are not imported.";

    // ---- Tags dialog ----
    public const string NewTag = "New tag";
    public const string CreateTag = "Create tag";
    public const string Cancel = "Cancel";
    public const string Ok = "OK";
    public const string Close = "Close";
    public const string RenameOrRecolor = "Rename or recolor";
    public const string DeleteTag = "Delete tag";
    public const string DeleteTagTitle = "Delete plate tag";
    public const string Color = "Color";
    public const string SearchTags = "Search tags";
    public const string NoPlateTags = "No plate tags yet. Create one with New tag.";
    public const string Saving = "Saving";

    // ---- Cameras ----
    public const string AddCameras = "Add cameras";
    public const string SetUp = "Set up";
    public const string CopySettings = "Copy settings";
    public const string UseRecommended = "Use recommended image settings";
    public const string UseRecommendedTitle = "Use recommended image settings";
    public const string UseRecommendedConfirm = "Use recommended";
    public const string RemoveCamerasTitle = "Remove cameras";
    public const string ReadingCameras = "Reading the cameras";
    public const string NoCameras = "No cameras yet. Add cameras with License Plate Verifier.";
    public const string NoMatchingCameras = "No cameras match the search.";
    public const string Recommended = "Recommended";
    public const string ImageSettingsMatch = "As ALPV recommends";
    public const string ImageSettingsDiffer = "Not as ALPV recommends";
    public const string NotReadYet = "Not read yet";
    public const string ChooseOneCamera = "Select one camera";

    // ---- Set up ----
    public const string SetUpTitle = "Set up License Plate Verifier";
    public const string Areas = "Areas";
    public const string Area = "Area";
    public const string Area1 = "Area 1";
    public const string Area2 = "Area 2";
    public const string AddArea2 = "Add area 2";
    public const string RemoveArea2 = "Remove area 2";
    public const string Recognition = "Recognition";
    public const string Region = "Region";
    public const string CameraLocation = "Camera location";
    public const string PlateWidth = "Plate width in pixels";
    public const string Characters = "Number of characters";
    public const string Threshold = "Confidence threshold";
    public const string ThresholdRange = "Choose 20 to 100 %.";
    public const string EnterName = "Enter a name.";
    public const string NameTooLong = "Use at most 64 characters.";
    public const string Plates = "Plates";
    public const string LastRead = "Last read";
    public const string AlpvPageLink = "Open the ALPV page";
    public const string AlpvPageTip = "Barrier, relay and storage";
    public const string ReadingCamera = "Reading the camera";
    public const string AreaHint = "Drag a corner to move it. Double-click an edge to add a corner, a corner to remove it.";
    public const string DirectionTip = "Drag the arrow's head: the driving direction that counts as In";
    public const string From = "From";
    public const string To = "To";
    public const string SetUpStarted = "Set up started";

    // ---- Copy settings ----
    public const string CopyTitle = "Copy settings";
    public const string CopyFrom = "From";
    public const string CopyTo = "To";
    public const string CopyAreas = "Copy the areas too";
    public const string CopyAreasTip = "Only for cameras with the same view";
    public const string Copy = "Copy";
    public const string ChooseTargetsFirst = "Choose the cameras to copy to.";

    // ---- Stats ----
    public const string Today = "Today";
    public const string Last7Days = "Last 7 days";
    public const string Last30Days = "Last 30 days";
    public const string Last24Hours = "Last 24 hours";
    public const string LastHour = "Last hour";
    public const string Total = "Total";
    public const string PlateConfidence = "Plate confidence per camera";
    public const string VehicleConfidence = "Vehicle confidence per camera";
    public const string ReadsOverTime = "Reads over time";
    public const string ConfidenceOverTime = "Confidence over time";
    public const string NoVehicleConfidence = "No camera sends a vehicle confidence.";
    public const string AverageConfidence = "Average confidence";
    public const string ReadingStats = "Reading the stats";
    public const string NoStats = "No reads in this time.";

    // ---- Settings ----
    public const string SettingsTitle = "License Plate Center settings";
    public const string Matching = "Matching";
    public const string Deviation = "Allowed character deviation";
    public const string Exact = "Exact";
    public const string OneCharacter = "1 character";
    public const string TwoCharacters = "2 characters";
    public const string WaitSeconds = "Wait for the best read (s)";
    public const string Log = "Log";
    public const string KeepDays = "Keep reads for (days)";
    public const string MaxSize = "Maximum size (GB)";
    public const string Folder = "Folder on the server";
    public const string FolderHeading = "Folder for the plate log";
    public const string KeepPictures = "Keep plate pictures";
    public const string KeepUnknown = "Keep unknown plates in the log";



    public const string ReadingSettings = "Reading the settings";

    /// <summary>"Delete 3 plates? They are no longer allowed anywhere."</summary>
    public static string DeletePlatesMessage(int count) =>
        (count == 1 ? "Delete 1 plate?" : string.Create(CultureInfo.CurrentCulture, $"Delete {count:N0} plates?")) + " They are no longer allowed anywhere.";

    /// <summary>"Remove 2 cameras from License Plate Center? Nothing on the cameras changes."</summary>
    public static string RemoveCamerasMessage(int count) =>
        (count == 1 ? "Remove 1 camera" : string.Create(CultureInfo.CurrentCulture, $"Remove {count:N0} cameras")) + " from License Plate Center? Nothing on the cameras changes.";

    /// <summary>"Use the image settings ALPV recommends on 3 cameras? One task per camera."</summary>
    public static string UseRecommendedMessage(int count) =>
        "Use the image settings ALPV recommends on " + CameraCount(count) + "? One task per camera.";

    /// <summary>"Delete the plate tag Garage A? 12 plates and 2 cameras use it; it is removed from them."</summary>
    public static string DeleteTagMessage(string name, int plates, int cameras) =>
        $"Delete the plate tag {name}? " + Uses(plates, cameras) + " use it; it is removed from them.";

    public static string CameraCount(int count) => count == 1 ? "1 camera" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} cameras");

    public static string PlateCount(int count) => count == 1 ? "1 plate" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} plates");

    /// <summary>"1,204 plates", "1,204 plates · 3 selected".</summary>
    public static string PlatesFooter(int shown, int selected) =>
        PlateCount(shown) + (selected > 0 ? string.Create(CultureInfo.CurrentCulture, $" · {selected:N0} selected") : string.Empty);

    /// <summary>"Live · 1,204 reads today · Kept for 30 days, 3.1 of 20 GB used".</summary>
    public static string LogFooter(bool paused, LogStatus? status)
    {
        var parts = new List<string> { paused ? Paused : Live };
        if (status is not null)
        {
            parts.Add(LicensePlateTexts.Reads(status.ReadsToday) + " today");
            if (status.HasFolder)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"Kept for {status.KeepDays} days, {Gigabytes(status.UsedBytes)} of {Gigabytes(status.MaxBytes)} GB used"));
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"12 new, 3 changed, 40 unchanged, 1 refused".</summary>
    public static string ImportCounts(ImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var parts = new List<string>
        {
            string.Create(CultureInfo.CurrentCulture, $"{preview.New:N0} new"),
            string.Create(CultureInfo.CurrentCulture, $"{preview.Changed:N0} changed"),
        };
        if (preview.Unchanged > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{preview.Unchanged:N0} unchanged"));
        }

        if (preview.Refused > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{preview.Refused:N0} refused"));
        }

        return string.Join(", ", parts);
    }

    /// <summary>The status word of an import row.</summary>
    public static string ImportStatus(ImportRowStatus status) => status switch
    {
        ImportRowStatus.New => "New",
        ImportRowStatus.Changed => "Changed",
        ImportRowStatus.Unchanged => "Unchanged",
        _ => "Refused",
    };

    /// <summary>"Set up started on 1 camera"; "Copy started on 3 cameras".</summary>
    public static string TasksStarted(string what, int count) => $"{what} started on {CameraCount(count)}";

    /// <summary>"3.1" (one decimal below 10 GB).</summary>
    public static string Gigabytes(long bytes)
    {
        var gb = bytes / 1_000_000_000d;
        return gb < 10 ? gb.ToString("0.0", CultureInfo.CurrentCulture) : gb.ToString("0", CultureInfo.CurrentCulture);
    }

    /// <summary>"42 %".</summary>
    public static string Percent(double share) => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(share * 100):0} %");

    private static string Uses(int plates, int cameras) => (plates, cameras) switch
    {
        (> 0, > 0) => PlateCount(plates) + " and " + CameraCount(cameras),
        (> 0, _) => PlateCount(plates),
        _ => CameraCount(cameras),
    };
}
