using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Firmware.Client;

/// <summary>A version from axis.com in a device's select field.</summary>
public sealed record OnlineChoice(OnlineTrack Track, OnlineVersion Version)
{
    public override string ToString() => Version.Version;
}

/// <summary>A local .bin chosen for a device.</summary>
public sealed record FileChoice(string Path, FirmwareImageInfo Image)
{
    public override string ToString() => Image.FileName;
}

/// <summary>The last entry of every select field: opens the file picker for that device.</summary>
public sealed class ChooseFileAction
{
    public const string Text = "Choose file";

    public static ChooseFileAction Instance { get; } = new();

    private ChooseFileAction()
    {
    }

    public override string ToString() => Text;
}

/// <summary>
/// The choices of one model, shared by all its devices (100,000 devices: one list per model, never one per device): the
/// tracks of axis.com with their versions newest first, the files chosen for devices of the model, "Choose file" last.
/// </summary>
public sealed class FirmwareModelChoices
{
    private readonly Dictionary<string, GroupedChoice> _byVersion = new(StringComparer.Ordinal);
    private readonly List<GroupedChoice> _files = [];
    private static readonly GroupedChoice ChooseFile = new(ChooseFileAction.Text, ChooseFileAction.Instance, IsHeader: false);

    internal FirmwareModelChoices(string key)
    {
        Key = key;
        Choices.ReplaceAll(Build());
    }

    /// <summary><see cref="OnlineFirmware.Normalize"/> of the model.</summary>
    public string Key { get; }

    /// <summary>What axis.com lists for the model; null until read or when not listed.</summary>
    public OnlineModel? Online { get; private set; }

    public bool IsListed => Online?.IsListed == true;

    public RangeObservableCollection<GroupedChoice> Choices { get; } = [];

    /// <summary>The entry of a listed version; null when the model does not list it.</summary>
    public GroupedChoice? Version(string? version) => version is not null && _byVersion.TryGetValue(version, out var choice) ? choice : null;

    internal void SetOnline(OnlineModel online)
    {
        Online = online;
        _byVersion.Clear();
        Choices.ReplaceAll(Build());
    }

    /// <summary>The entry of a chosen file, added before "Choose file" the first time (the same file serves several devices).</summary>
    internal GroupedChoice AddFile(FileChoice file)
    {
        if (_files.FirstOrDefault(f => f.Value is FileChoice c && string.Equals(c.Path, file.Path, StringComparison.Ordinal)) is { } known)
        {
            return known;
        }

        var choice = new GroupedChoice(file.Image.FileName, file, IsHeader: false);
        _files.Add(choice);
        Choices.Insert(Choices.Count - 1, choice);
        return choice;
    }

    private List<GroupedChoice> Build()
    {
        var list = new List<GroupedChoice>();
        if (Online is { IsListed: true } online)
        {
            foreach (var track in online.Tracks)
            {
                list.Add(new GroupedChoice(track.Name, null, IsHeader: true));
                foreach (var v in track.Versions)
                {
                    var choice = new GroupedChoice(v.Version, new OnlineChoice(track, v), IsHeader: false);
                    _byVersion.TryAdd(v.Version, choice);
                    list.Add(choice);
                }
            }
        }

        list.AddRange(_files);
        list.Add(ChooseFile);
        return list;
    }
}

/// <summary>One selected device in the dialog table, with the firmware it installs.</summary>
public sealed partial class FirmwareDeviceRow(IDeviceInfo device) : ObservableObject
{
    private GroupedChoice? _selected;

    public IDeviceInfo Device { get; } = device;

    public string Address => Device.Address;

    public string Model => Device.Model ?? string.Empty;

    /// <summary>The choices of the device's model.</summary>
    public FirmwareModelChoices? ModelChoices { get; internal set; }

    public IReadOnlyList<GroupedChoice> Choices => ModelChoices?.Choices ?? (IReadOnlyList<GroupedChoice>)[];

    /// <summary>Raised when the user changed <see cref="SelectedChoice"/> (not when the dialog set it).</summary>
    internal Action<FirmwareDeviceRow, GroupedChoice?>? Chosen { get; set; }

    /// <summary>
    /// The version or file this device installs; null: nothing chosen (the field shows "Choose file"). "Choose file" is no
    /// value of its own: it opens the picker and the field keeps its value. A null from the view (a recycled grid row) is
    /// ignored; nothing in the list is null.
    /// </summary>
    public GroupedChoice? SelectedChoice
    {
        get => _selected;
        set
        {
            if (value is null || ReferenceEquals(value, _selected) || value.IsHeader)
            {
                OnPropertyChanged();
                return;
            }

            if (value.Value is ChooseFileAction)
            {
                // The field gets its value back once the combo box finished its own selection change.
                if (SynchronizationContext.Current is { } ui)
                {
                    ui.Post(_ => OnPropertyChanged(nameof(SelectedChoice)), null);
                }

                Chosen?.Invoke(this, value);
                return;
            }

            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OnlineVersion));
            OnPropertyChanged(nameof(ChosenFile));
            Chosen?.Invoke(this, value);
        }
    }

    /// <summary>Sets the choice without raising <see cref="Chosen"/> (the buttons, a file for many devices).</summary>
    internal void SetChoice(GroupedChoice? choice)
    {
        if (ReferenceEquals(choice, _selected))
        {
            return;
        }

        _selected = choice;
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(OnlineVersion));
        OnPropertyChanged(nameof(ChosenFile));
    }

    public OnlineChoice? OnlineVersion => _selected?.Value as OnlineChoice;

    public FileChoice? ChosenFile => _selected?.Value as FileChoice;

    /// <summary>The image the device installs, as judged (the version from axis.com or the chosen file).</summary>
    internal FirmwareImageInfo? Image { get; set; }

    [ObservableProperty]
    public partial string? CurrentVersion { get; set; } = device.FirmwareVersion;

    /// <summary>True while the status query of this row runs (the Firmware cell shows "Reading").</summary>
    [ObservableProperty]
    public partial bool IsReadingStatus { get; set; }

    /// <summary>False when the device does not offer fwmgr 1.x (from the status query).</summary>
    [ObservableProperty]
    public partial bool Supported { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VerdictText), nameof(Message), nameof(IsOk), nameof(IsWarn), nameof(IsError), nameof(WillInstall))]
    public partial FirmwareCheck? Check { get; set; }

    public string VerdictText => Check?.Verdict switch
    {
        null => "Choose a file",
        FirmwareVerdict.Upgrade => Check.PathAdvice is null ? "Upgrade" : "Not recommended",
        FirmwareVerdict.DeviceValidates => "Device checks",
        FirmwareVerdict.AlreadyUpToDate => "Up to date",
        FirmwareVerdict.Downgrade => "Downgrade",
        FirmwareVerdict.DowngradeBlocked => "Downgrade blocked",
        FirmwareVerdict.WrongProduct => "Wrong product",
        FirmwareVerdict.InvalidFile => "Invalid file",
        FirmwareVerdict.UnknownVersionRefused => "Refused",
        FirmwareVerdict.NoFileForModel => "No file",
        FirmwareVerdict.AppsBlock => "Apps block it",
        FirmwareVerdict.SignInNeeded => "Sign in",
        FirmwareVerdict.NotListed => "Not on axis.com",
        _ => "Not supported",
    };

    public string Message => Check?.Message ?? string.Empty;

    public bool IsOk => Check is { Verdict: FirmwareVerdict.Upgrade, PathAdvice: null };

    /// <summary>A downgrade, a file the device checks, an upgrade off the recommended path (it still runs), or a sign-in.</summary>
    public bool IsWarn => Check?.Verdict is FirmwareVerdict.Downgrade or FirmwareVerdict.DeviceValidates or FirmwareVerdict.SignInNeeded || Check?.PathAdvice is not null;

    public bool IsError => Check is { WillInstall: false, IsNoOp: false } && Check.Verdict != FirmwareVerdict.SignInNeeded;

    public bool WillInstall => Check?.WillInstall == true;

    /// <summary>The device's installed applications from the status query; null until read or when not readable.</summary>
    internal IReadOnlyList<InstalledApp>? Apps { get; set; }

    /// <summary>The status query was started for this row (preloaded or because the grid showed it).</summary>
    internal bool StatusRequested { get; set; }
}
