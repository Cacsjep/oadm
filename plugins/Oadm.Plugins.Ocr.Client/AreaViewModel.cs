using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>
/// One area of the reader being edited: its name, where it is on the picture and Any text or a Format. The rules show their
/// error directly below the field (CLAUDE.md "validation errors directly below the field").
/// </summary>
public sealed partial class AreaViewModel : ValidatingViewModel
{
    private readonly Func<AreaViewModel, string, bool> _nameTaken;
    private string _keptFormat = string.Empty;

    public AreaViewModel(Area area, Func<AreaViewModel, string, bool> nameTaken)
    {
        ArgumentNullException.ThrowIfNull(area);
        _nameTaken = nameTaken ?? throw new ArgumentNullException(nameof(nameTaken));
        Name = area.Name;
        Box = area.Box;
        var isFormat = area.Expected.Kind == ExpectedTextKind.Format;
        _keptFormat = area.Expected.Format ?? string.Empty;
        FormatText = isFormat ? _keptFormat : string.Empty;
        IsFormat = isFormat;
        Validation
            .Rule(nameof(Name), NameError)
            .Rule(nameof(FormatText), () => IsFormat ? ReaderRules.FormatProblem(FormatText) : null);
        Validation.Validate();
    }

    /// <summary>Raised after any change the reader must know (dirty state, the picture's overlay).</summary>
    public event EventHandler? Changed;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial RelativeBox Box { get; set; }

    /// <summary>Format instead of Any text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mode), nameof(FormatPlaceholder))]
    public partial bool IsFormat { get; set; }

    /// <summary>The format field's example while Format is chosen; empty for Any text (the field is off then).</summary>
    public string FormatPlaceholder => IsFormat ? FormatExample : string.Empty;

    /// <summary>The example in the empty format field.</summary>
    public const string FormatExample = "AAAU ###### #";

    /// <summary>The choices of the area's Mode select.</summary>
    public static IReadOnlyList<string> Modes { get; } = [OcrTexts.AnyText, OcrTexts.Format];

    /// <summary>The Mode select: <see cref="OcrTexts.AnyText"/> or <see cref="OcrTexts.Format"/>.</summary>
    public string Mode
    {
        get => IsFormat ? OcrTexts.Format : OcrTexts.AnyText;
        set => IsFormat = value == OcrTexts.Format;
    }

    [ObservableProperty]
    public partial string FormatText { get; set; }

    /// <summary>Shown in the picture with a solid border.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The format field's tooltip (no hint line below it: each area is one row).</summary>
    public static string FormatHint => OcrInfo.FormatHint;

    /// <summary>The longest name and format (the text boxes' limits).</summary>
    public static int MaxNameLength => ReaderRules.MaxNameLength;

    public static int MaxFormatLength => ReaderRules.MaxFormatLength;

    /// <summary>The area as saved.</summary>
    public Area ToArea() => new()
    {
        Name = Name.Trim(),
        Box = Box,
        Expected = IsFormat ? new ExpectedText { Kind = ExpectedTextKind.Format, Format = FormatText.Trim() } : ExpectedText.AnyText,
    };

    /// <summary>The name's rule again after another area's name changed.</summary>
    internal void Revalidate() => Validation.Validate();

    /// <summary>
    /// A refusal of the server for this area (<see cref="OcrFields.AreaName"/>, <see cref="OcrFields.AreaFormat"/>,
    /// <see cref="OcrFields.AreaBox"/>): the format's below the format, the others below the name.
    /// </summary>
    internal void SetServerError(string part, string message) =>
        Validation.SetServerError(part == "format" && IsFormat ? nameof(FormatText) : nameof(Name), message);

    /// <summary>Every error shows (Save was tried).</summary>
    internal void ShowAllErrors() => Validation.ShowAll();

    internal bool IsValid => Validation.IsValid;

    internal string? FirstError => Validation.FirstError;

    /// <summary>An error came or went: the reader's Save follows.</summary>
    public event EventHandler? ValidityChanged;

    protected override void OnValidationChanged() => ValidityChanged?.Invoke(this, EventArgs.Empty);

    private string? NameError() =>
        ReaderRules.NameProblem(Name) ?? (_nameTaken(this, Name.Trim()) ? ReaderRules.AreaNameTaken(Name.Trim()) : null);

    partial void OnNameChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnBoxChanged(RelativeBox value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnIsFormatChanged(bool value)
    {
        // Any text: the format field is empty and off; the format typed before comes back with Format.
        if (value)
        {
            if (FormatText.Length == 0)
            {
                FormatText = _keptFormat;
            }
        }
        else
        {
            _keptFormat = FormatText;
            FormatText = string.Empty;
        }

        Validation.Validate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFormatTextChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);
}
