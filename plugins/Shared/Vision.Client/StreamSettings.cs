using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Shared.Vision.Client;

/// <summary>A video source of the camera as the Stream settings dialog lists it: 1 for the first, and its name.</summary>
public sealed record StreamSource(int Number, string Name);

/// <summary>
/// The Stream settings dialog (docs/spec/ocr.md "Page", docs/spec/vision-center.md "Nodes", Camera): Video source,
/// Resolution and, while Watching, Frames per second, with the server's choices. OK hands the values to the caller (Not saved
/// like any edit); Cancel changes nothing. A refusal of the server for one of them shows below its field here too.
/// </summary>
public sealed partial class StreamSettingsViewModel : ValidatingViewModel
{
    public StreamSettingsViewModel(
        IReadOnlyList<StreamSource> sources,
        StreamSource? source,
        IReadOnlyList<PictureResolution> resolutions,
        PictureResolution resolution,
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

    public IReadOnlyList<StreamSource> VideoSources { get; }

    [ObservableProperty]
    public partial StreamSource? SelectedSource { get; set; }

    public IReadOnlyList<PictureResolution> Resolutions { get; }

    [ObservableProperty]
    public partial PictureResolution SelectedResolution { get; set; }

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
