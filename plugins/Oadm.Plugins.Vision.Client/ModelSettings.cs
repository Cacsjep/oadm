using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Vision.Client;

/// <summary>A choice of a select of the Settings dialog: the value and its name in plain words.</summary>
public sealed record SettingChoice<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// A model's Settings (docs/spec/vision-center.md "Models": the user confirms or picks the format; Fit and Scaling a choice per
/// model, since the wrong one loses accuracy without an error; a free input size): Format (the formats of the model's kind),
/// Fit, Scaling and, for a model without a fixed input, the input size (a multiple of 32 from 32 to 2048). One operation; the
/// errors and the server's refusals below their fields.
/// </summary>
public sealed partial class ModelSettingsViewModel : ValidatingViewModel
{
    /// <param name="model">The stored model.</param>
    /// <param name="guided">The Import model wizard: each select shows whether it holds what the check found (a check mark,
    /// else a warning and what the check found below it).</param>
    public ModelSettingsViewModel(VisionModel model, bool guided = false)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        IsGuided = guided;
        Formats = model.Kind == ModelKind.Detection
            ? [new(ModelFormat.Yolo, "YOLO"), new(ModelFormat.YoloWithSuppression, "YOLO with suppression"), new(ModelFormat.Detr, "DETR"),
                new(ModelFormat.RfDetr, "RF-DETR"), new(ModelFormat.DetrWithLabels, "DETR with labels"), new(ModelFormat.Yolox, "YOLOX"),
                new(ModelFormat.RtDetr, "RT-DETR")]
            : [new(ModelFormat.Classification, "Classification")];
        Format = Formats.FirstOrDefault(f => f.Value == model.Format) ?? Formats[0];
        Fit = Fits.First(f => f.Value == model.Fit);
        Scaling = Scalings.First(s => s.Value == model.Scaling);
        InputWidth = model.InputWidth.ToString(CultureInfo.InvariantCulture);
        InputHeight = model.InputHeight.ToString(CultureInfo.InvariantCulture);
        Validation
            .Rule(nameof(Format), () => null)
            .Rule(nameof(InputWidth), () => SideProblem(InputWidth))
            .Rule(nameof(InputHeight), () => SideProblem(InputHeight));
        Validation.Validate();
    }

    /// <summary>OK (true) or Cancel (false): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public VisionModel Model { get; }

    /// <summary>The wizard's form: the selects mark what the check found (the Settings dialog has no marks).</summary>
    public bool IsGuided { get; }

    public ModelKind Kind => Model.Kind;

    /// <summary>The info icons' texts (their drawings in <see cref="ModelSettingDiagram"/>), the same in dialog and wizard.</summary>
    public static string FormatInfo => VisionTexts.FormatInfo;

    public static string FitInfo => VisionTexts.FitInfo;

    public static string ScalingInfo => VisionTexts.ScalingInfo;

    /// <summary>The wizard: the select holds what the check found (a green check).</summary>
    public bool FormatFound => IsGuided && Format.Value == Model.Format;

    /// <summary>The wizard: the user chose another value than the check found (a warning, what was found below).</summary>
    public bool FormatChanged => IsGuided && Format.Value != Model.Format;

    public bool FitFound => IsGuided && Fit.Value == Model.Fit;

    public bool FitChanged => IsGuided && Fit.Value != Model.Fit;

    public bool ScalingFound => IsGuided && Scaling.Value == Model.Scaling;

    public bool ScalingChanged => IsGuided && Scaling.Value != Model.Scaling;

    public string? FormatHint => FormatChanged ? VisionTexts.CheckFound(Formats.First(f => f.Value == Model.Format).Name) : null;

    public string? FitHint => FitChanged ? VisionTexts.CheckFound(Fits.First(f => f.Value == Model.Fit).Name) : null;

    public string? ScalingHint => ScalingChanged ? VisionTexts.CheckFound(Scalings.First(s => s.Value == Model.Scaling).Name) : null;

    partial void OnFormatChanged(SettingChoice<ModelFormat> value) => Notify(nameof(FormatFound), nameof(FormatChanged), nameof(FormatHint));

    partial void OnFitChanged(SettingChoice<ModelFit> value) => Notify(nameof(FitFound), nameof(FitChanged), nameof(FitHint));

    partial void OnScalingChanged(SettingChoice<ModelScaling> value) => Notify(nameof(ScalingFound), nameof(ScalingChanged), nameof(ScalingHint));

    private void Notify(params string[] names)
    {
        foreach (var name in names)
        {
            OnPropertyChanged(name);
        }
    }

    public static string Title => VisionTexts.ModelSettings;

    public IReadOnlyList<SettingChoice<ModelFormat>> Formats { get; }

    public static IReadOnlyList<SettingChoice<ModelFit>> Fits { get; } =
        [new(ModelFit.Letterbox, "Letterbox"), new(ModelFit.Stretch, "Stretch"), new(ModelFit.KeepShapePad, "Keep shape and pad")];

    public static IReadOnlyList<SettingChoice<ModelScaling>> Scalings { get; } =
        [new(ModelScaling.ZeroToOne, "0 to 1"), new(ModelScaling.ImageNet, "ImageNet"), new(ModelScaling.ZeroTo255Bgr, "0 to 255, BGR")];

    [ObservableProperty]
    public partial SettingChoice<ModelFormat> Format { get; set; }

    [ObservableProperty]
    public partial SettingChoice<ModelFit> Fit { get; set; }

    [ObservableProperty]
    public partial SettingChoice<ModelScaling> Scaling { get; set; }

    /// <summary>The input size can be chosen (the model does not fix it).</summary>
    public bool CanSize => !Model.FixedInput;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string InputWidth { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string InputHeight { get; set; }

    /// <summary>The model with these settings (what updateModel gets).</summary>
    public VisionModel ToModel() => Model with
    {
        Format = Format.Value,
        Fit = Fit.Value,
        Scaling = Scaling.Value,
        InputWidth = CanSize && int.TryParse(InputWidth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : Model.InputWidth,
        InputHeight = CanSize && int.TryParse(InputHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : Model.InputHeight,
    };

    /// <summary>
    /// The server's refusal: below the field it is about (the format, the input size) and the dialog opens again; null when it
    /// belongs to no field (the host's message shows it).
    /// </summary>
    public bool ShowRefusal(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is ModelTexts.OutputsDoNotFit or ModelTexts.FormatOfOtherKind)
        {
            Validation.SetServerError(nameof(Format), message);
            return true;
        }

        if (CanSize && (message == ModelTexts.InputSizeRule || message == ModelTexts.InputSizeFixed))
        {
            Validation.SetServerError(nameof(InputWidth), message);
            return true;
        }

        return false;
    }

    private string? SideProblem(string text) =>
        !CanSize || (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var side)
            && side is >= ModelLimits.MinInputSide and <= ModelLimits.MaxInputSide && side % 32 == 0)
            ? null
            : ModelTexts.InputSizeRule;

    private bool CanOk() => IsFormValid;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Validation.ShowAll();
        if (IsFormValid)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected override void OnValidationChanged() => OkCommand.NotifyCanExecuteChanged();
}
