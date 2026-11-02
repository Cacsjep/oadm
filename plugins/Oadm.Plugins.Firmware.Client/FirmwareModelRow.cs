using System.Collections;
using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Firmware.Client;

/// <summary>A version from axis.com in a row's select field.</summary>
public sealed record OnlineChoice(OnlineTrack Track, OnlineVersion Version)
{
    public override string ToString() => Version.Version;
}

/// <summary>A local .bin chosen for a row.</summary>
public sealed record FileChoice(string Path, FirmwareImageInfo Image)
{
    public override string ToString() => Image.FileName;
}

/// <summary>The last entry of every row's select field: opens the file picker for that row.</summary>
public sealed class ChooseFileAction
{
    public static ChooseFileAction Instance { get; } = new();

    private ChooseFileAction()
    {
    }

    public override string ToString() => FirmwareModelRow.ChooseFileText;
}

/// <summary>
/// One row of the firmware table (README "Firmware online", Dialog): one model and track of the selected devices, with the
/// version from axis.com or the chosen file its devices install.
/// </summary>
public sealed partial class FirmwareModelRow : ObservableObject, INotifyDataErrorInfo
{
    public const string ChooseFileText = "Choose file";

    private readonly Action<FirmwareModelRow> _changed;
    private readonly Action<FirmwareModelRow> _chooseFile;
    private bool _quiet;

    internal FirmwareModelRow(string model, int? major, Action<FirmwareModelRow> changed, Action<FirmwareModelRow> chooseFile)
    {
        Model = model;
        Key = OnlineFirmware.Normalize(model);
        Major = major;
        _changed = changed;
        _chooseFile = chooseFile;
        Choices = BuildChoices();
    }

    /// <summary>The model as the devices report it ("P3265-V").</summary>
    public string Model { get; }

    /// <summary><see cref="OnlineFirmware.Normalize"/> of the model.</summary>
    internal string Key { get; }

    /// <summary>The AXIS OS major of the row's devices; null for devices of unknown version.</summary>
    public int? Major { get; }

    /// <summary>The selected devices of this model and track.</summary>
    internal List<FirmwareDeviceRow> DeviceRows { get; } = [];

    [ObservableProperty]
    public partial string DevicesText { get; private set; } = string.Empty;

    /// <summary>"11.11.160", or the lowest and highest version of the row's devices.</summary>
    [ObservableProperty]
    public partial string FirmwareNow { get; private set; } = string.Empty;

    /// <summary>What axis.com lists for the model; null until read or when not listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListed))]
    public partial OnlineModel? Online { get; private set; }

    public bool IsListed => Online?.IsListed == true;

    /// <summary>The track names with their versions (newest first), the chosen file, and "Choose file" last.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GroupedChoice> Choices { get; private set; }

    /// <summary>The version or file; null: nothing chosen (the field shows "Choose file").</summary>
    [ObservableProperty]
    public partial GroupedChoice? SelectedChoice { get; set; }

    /// <summary>The chosen local file of the row, also while a version is selected (it stays in the list).</summary>
    public FileChoice? File { get; private set; }

    public OnlineChoice? OnlineVersion => SelectedChoice?.Value as OnlineChoice;

    public FileChoice? ChosenFile => SelectedChoice?.Value as FileChoice;

    /// <summary>The problem of this row's choice, directly below its field (a refused file, a version no longer listed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>The error belongs to the field: Avalonia shows it directly below the select with a red border (theme).</summary>
    public bool HasErrors => Error is not null;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName) =>
        Error is { } error && (string.IsNullOrEmpty(propertyName) || propertyName == nameof(SelectedChoice)) ? new[] { error } : Array.Empty<string>();

    partial void OnErrorChanged(string? value) => ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(SelectedChoice)));

    /// <summary>A line below the field that is no error ("Sign in to MyAxis to download from axis.com.").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; set; }

    public bool HasNotice => Notice is not null && Error is null;

    /// <summary>"Up to date" when every device of the row runs the chosen version.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; set; }

    public bool HasStatus => Status is not null;

    /// <summary>True while the chosen older version is checked on axis.com.</summary>
    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    /// <summary>A problem found after the choice: the file could not be read, the version is no longer listed.</summary>
    internal string? CheckError { get; set; }

    /// <summary>Two files of the selection for this row's model (from <c>SelectFilesAsync</c>).</summary>
    internal bool DuplicateFile { get; set; }

    /// <summary>The checked file of an older version (size and SHA-256 only known for the newest of a track).</summary>
    internal OnlineFileInfo? CheckedVersion { get; set; }

    internal void SetOnline(OnlineModel? online)
    {
        var keep = SelectedChoice?.Value;
        Online = online;
        Rebuild(keep);
    }

    /// <summary>Sets the row to the version (as listed); false when the model does not list it.</summary>
    internal bool SelectVersion(string? version)
    {
        var choice = version is null ? null : Choices.FirstOrDefault(c => c.Value is OnlineChoice o && o.Version.Version == version);
        if (choice is null)
        {
            return false;
        }

        SelectedChoice = choice;
        return true;
    }

    /// <summary>The row installs this file; it stays in the list after "Choose file".</summary>
    internal void SelectFile(FileChoice file)
    {
        File = file;
        DuplicateFile = false;
        CheckedVersion = null;
        Rebuild(file);
    }

    /// <summary>Drops the chosen file; a selected version stays.</summary>
    internal void ClearFile()
    {
        if (File is null)
        {
            return;
        }

        File = null;
        DuplicateFile = false;
        Rebuild(SelectedChoice?.Value is OnlineChoice online ? online : null);
    }

    /// <summary>The value restored into the field without raising a change.</summary>
    private void RestoreChoice(object? value)
    {
        _quiet = true;
        try
        {
            SelectedChoice = Find(value);
        }
        finally
        {
            _quiet = false;
        }
    }

    internal void UpdateDevices()
    {
        DevicesText = DeviceRows.Count == 1 ? "1 device" : string.Create(CultureInfo.InvariantCulture, $"{DeviceRows.Count:N0} devices");
        AxisOsVersion? low = null, high = null;
        string? unknown = null;
        foreach (var row in DeviceRows)
        {
            if (AxisOsVersion.TryParse(row.CurrentVersion) is not { } v)
            {
                unknown ??= "Unknown";
                continue;
            }

            low = low is null || v < low ? v : low;
            high = high is null || v > high ? v : high;
        }

        FirmwareNow = low is null ? unknown ?? string.Empty : low == high ? low.Text : $"{low} to {high}";
    }

    partial void OnSelectedChoiceChanged(GroupedChoice? oldValue, GroupedChoice? newValue)
    {
        if (_quiet)
        {
            return;
        }

        if (newValue?.Value is ChooseFileAction)
        {
            // Not a choice of its own: the field keeps its value until a file was picked. On the UI thread the field gets
            // its value back after the combo box finished its own selection change.
            var restore = oldValue?.Value;
            if (SynchronizationContext.Current is { } ui)
            {
                ui.Post(_ =>
                {
                    if (SelectedChoice?.Value is ChooseFileAction)
                    {
                        RestoreChoice(restore); // not when a file was picked meanwhile
                    }
                }, null);
            }
            else
            {
                RestoreChoice(restore);
            }

            _chooseFile(this);
            return;
        }

        DuplicateFile = false;
        CheckedVersion = null;
        OnPropertyChanged(nameof(OnlineVersion));
        OnPropertyChanged(nameof(ChosenFile));
        _changed(this);
    }

    private void Rebuild(object? keep)
    {
        _quiet = true;
        try
        {
            Choices = BuildChoices();
            SelectedChoice = Find(keep);
        }
        finally
        {
            _quiet = false;
        }

        OnPropertyChanged(nameof(OnlineVersion));
        OnPropertyChanged(nameof(ChosenFile));
        _changed(this);
    }

    private GroupedChoice? Find(object? value) =>
        value is null ? null : Choices.FirstOrDefault(c => Equals(c.Value, value) || (value is OnlineChoice o && c.Value is OnlineChoice n && n.Version.Version == o.Version.Version));

    private List<GroupedChoice> BuildChoices()
    {
        var list = new List<GroupedChoice>();
        if (Online is { IsListed: true } online)
        {
            foreach (var track in online.Tracks)
            {
                list.Add(new GroupedChoice(track.Name, null, IsHeader: true));
                list.AddRange(track.Versions.Select(v => new GroupedChoice(v.Version, new OnlineChoice(track, v), IsHeader: false)));
            }
        }

        if (File is { } file)
        {
            list.Add(new GroupedChoice(file.Image.FileName, file, IsHeader: false));
        }

        list.Add(new GroupedChoice(ChooseFileText, ChooseFileAction.Instance, IsHeader: false));
        return list;
    }
}
