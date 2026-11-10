using Avalonia.Media.Imaging;

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

    /// <summary>A row's cut-out, larger.</summary>
    Task ShowCutOutAsync(CutOutViewModel form);
}

/// <summary>
/// The dialog of a row's cut-out (docs/spec/ocr.md "The last 20 reads"): the picture at its real pixels, a small one enlarged by
/// a whole number so its pixels stay sharp, with the text, the area and the time. The caller owns the picture.
/// </summary>
public sealed partial class CutOutViewModel(ResultRow row, Bitmap picture)
{
    /// <summary>The widest the picture shows (<see cref="OcrLimits.MaxCutoutSide"/>, the window's content).</summary>
    public const double MaxWidth = OcrLimits.MaxCutoutSide;

    /// <summary>A small picture fills about this height at most.</summary>
    public const double MaxHeight = 360;

    /// <summary>Enlarged at most this many times.</summary>
    public const int MaxScale = 8;

    public static string Title => OcrTexts.CutOut;

    public Bitmap Picture { get; } = picture ?? throw new ArgumentNullException(nameof(picture));

    public string Text { get; } = (row ?? throw new ArgumentNullException(nameof(row))).Text;

    public string Area { get; } = row.Area;

    public string Time { get; } = row.Time;

    /// <summary>The whole number the picture's pixels are enlarged by.</summary>
    public int Scale => Math.Clamp((int)Math.Min(MaxWidth / Picture.PixelSize.Width, MaxHeight / Picture.PixelSize.Height), 1, MaxScale);

    public double ShownWidth => Picture.PixelSize.Width * Scale;

    public double ShownHeight => Picture.PixelSize.Height * Scale;

    /// <summary>Close: the view closes the window.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
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
