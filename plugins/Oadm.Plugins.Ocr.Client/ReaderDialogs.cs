using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>The small dialogs of the reader's settings column, shown by the view (tests answer them in place).</summary>
public interface IReaderDialogs
{
    /// <summary>Stream settings: true when the user chose OK.</summary>
    Task<bool> EditStreamAsync(StreamSettingsViewModel form);

    /// <summary>Rename of an area: true when the user chose Rename.</summary>
    Task<bool> RenameAreaAsync(RenameAreaViewModel form);
}

/// <summary>
/// The Stream settings dialog (docs/spec/ocr.md "Page"): Video source, Resolution and, while Watching, Frames per second,
/// with the server's choices. OK hands the values to the reader (Not saved like any edit); Cancel changes nothing. A refusal
/// of the server for one of them shows below its field here too.
/// </summary>
public sealed partial class StreamSettingsViewModel : ValidatingViewModel
{
    public StreamSettingsViewModel(
        IReadOnlyList<CameraSource> sources,
        CameraSource? source,
        IReadOnlyList<Resolution> resolutions,
        Resolution resolution,
        IReadOnlyList<int> fpsChoices,
        int fps,
        bool showFps,
        bool isLoading,
        IReadOnlyDictionary<string, string?>? errors = null)
    {
        VideoSources = sources ?? throw new ArgumentNullException(nameof(sources));
        Resolutions = resolutions ?? throw new ArgumentNullException(nameof(resolutions));
        FpsChoices = fpsChoices ?? throw new ArgumentNullException(nameof(fpsChoices));
        SelectedSource = source;
        SelectedResolution = resolution;
        Fps = fps;
        ShowFps = showFps;
        IsLoading = isLoading;

        // No checks of their own: the rules only let an edit clear the server's refusal below the field.
        Validation
            .Rule(nameof(SelectedSource), () => null)
            .Rule(nameof(SelectedResolution), () => null)
            .Rule(nameof(Fps), () => null);
        foreach (var (property, error) in errors ?? new Dictionary<string, string?>())
        {
            if (!string.IsNullOrEmpty(error))
            {
                Validation.SetServerError(property, error);
            }
        }

        Validation.Validate();
    }

    /// <summary>OK (true) or Cancel (false): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public const string Title = "Stream settings";

    public IReadOnlyList<CameraSource> VideoSources { get; }

    [ObservableProperty]
    public partial CameraSource? SelectedSource { get; set; }

    public IReadOnlyList<Resolution> Resolutions { get; }

    [ObservableProperty]
    public partial Resolution SelectedResolution { get; set; }

    public IReadOnlyList<int> FpsChoices { get; }

    [ObservableProperty]
    public partial int Fps { get; set; }

    /// <summary>Frames per second only while Watching.</summary>
    public bool ShowFps { get; }

    /// <summary>The camera's sources and sizes are still being read: the selects wait.</summary>
    public bool IsLoading { get; }

    [RelayCommand]
    private void Ok() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}

/// <summary>
/// The Rename dialog of an area (docs/spec/ocr.md "Page"): one Name field, required, unique within the reader, at most
/// <see cref="ReaderRules.MaxNameLength"/> characters; the error below the field, Rename off while there is one.
/// </summary>
public sealed partial class RenameAreaViewModel : ValidatingViewModel
{
    private readonly Func<string, bool> _taken;

    public RenameAreaViewModel(string name, Func<string, bool> taken)
    {
        _taken = taken ?? throw new ArgumentNullException(nameof(taken));
        Name = name ?? string.Empty;
        Validation.Rule(nameof(Name), () => ReaderRules.NameProblem(Name) ?? (_taken(Name.Trim()) ? ReaderRules.AreaNameTaken(Name.Trim()) : null));
        Validation.Validate();
    }

    /// <summary>Rename (true) or Cancel (false): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public const string Title = "Rename area";

    public static int MaxNameLength => ReaderRules.MaxNameLength;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    public partial string Name { get; set; }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        Validation.ShowAll();
        if (IsFormValid)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    private bool CanRename() => IsFormValid;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected override void OnValidationChanged() => RenameCommand.NotifyCanExecuteChanged();
}
