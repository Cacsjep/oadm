using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Overlays.Client;

/// <summary>A place in the Position choice.</summary>
public sealed record PositionChoice(OverlayPosition Position)
{
    public string Name => OverlayNames.Of(Position);

    public override string ToString() => Name;
}

/// <summary>A colour in the colour choices.</summary>
public sealed record ColorChoice(OverlayColor Color)
{
    public string Name => OverlayNames.Of(Color);

    public override string ToString() => Name;
}

/// <summary>A modifier the user can put into the text: a plain name and the code the camera replaces.</summary>
public sealed record ModifierChoice(string Name, string Code)
{
    public override string ToString() => Name;
}

/// <summary>The modifiers the dialog offers (recorded getOverlayModifiers of AXIS OS 12.11 has every one of them).</summary>
public static class OverlayModifiers
{
    public static ModifierChoice Date { get; } = new("Date", "%F");

    public static ModifierChoice Time { get; } = new("Time", "%X");

    public static ModifierChoice DayOfWeek { get; } = new("Day of the week", "%a");

    public static ModifierChoice DateAndTime { get; } = new("Date and time", "%c");

    public static ModifierChoice CameraName { get; } = new("Camera name", "#n");

    public static ModifierChoice IpAddress { get; } = new("IP address", "#i");

    public static ModifierChoice MacAddress { get; } = new("MAC address", "#M");

    public static ModifierChoice FrameRate { get; } = new("Frame rate", "#R");

    public static ModifierChoice Bitrate { get; } = new("Bitrate", "#b");

    /// <summary>The list behind More values, in this order (Dynamic text 1 to 16 follow in a submenu).</summary>
    public static IReadOnlyList<ModifierChoice> More { get; } = [DayOfWeek, DateAndTime, CameraName, IpAddress, MacAddress, FrameRate, Bitrate];

    /// <summary>Dynamic text 1 to 16 (#D1 to #D16), set on the camera by its own dynamic text calls.</summary>
    public static IReadOnlyList<ModifierChoice> DynamicTexts { get; } =
        [.. Enumerable.Range(1, 16).Select(i => new ModifierChoice(string.Create(CultureInfo.InvariantCulture, $"Dynamic text {i}"), string.Create(CultureInfo.InvariantCulture, $"#D{i}")))];
}

/// <summary>A menu entry that inserts one modifier into its row's text.</summary>
public sealed record ModifierAction(string Name, IRelayCommand Command);

/// <summary>The limits the dialog checks against: those of the camera it read, else the API's.</summary>
public sealed record OverlayLimits(int MaxTextLength, int MinFontSize, int MaxFontSize)
{
    public static OverlayLimits Default { get; } = new(OverlayRules.MaxTextLength, OverlayRules.MinFontSize, OverlayRules.MaxFontSize);

    public static OverlayLimits Of(OverlayCapabilities? capabilities) => capabilities is null
        ? Default
        : new(capabilities.MaxTextLength, capabilities.MinFontSize ?? OverlayRules.MinFontSize, capabilities.MaxFontSize ?? OverlayRules.MaxFontSize);

    /// <summary>The Size header's tooltip: the camera's range, and that empty is its default.</summary>
    public string SizeTip => this == Default
        ? "Empty: the camera's default"
        : string.Create(CultureInfo.InvariantCulture, $"{MinFontSize} to {MaxFontSize}; empty: the camera's default");
}

/// <summary>
/// One overlay of the dialog's list: text with modifiers inserted at the caret, position, size, colours. Its errors show below
/// its own inputs (<see cref="ValidatingViewModel"/>).
/// </summary>
public sealed partial class OverlayRowViewModel : ValidatingViewModel
{
    public const string WholeNumber = "Enter a whole number.";

    private readonly Func<OverlayLimits> _limits;
    private readonly Action<OverlayRowViewModel> _remove;
    private readonly IReadOnlyList<double>? _custom;

    public OverlayRowViewModel(Func<OverlayLimits> limits, Action<OverlayRowViewModel> remove, TextOverlay? overlay = null)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _remove = remove ?? throw new ArgumentNullException(nameof(remove));
        overlay ??= new TextOverlay();
        _custom = overlay.Position == OverlayPosition.Custom && OverlayRules.IsCustomPlace(overlay.CustomPosition) ? overlay.CustomPosition : null;
        Positions = [.. Enum.GetValues<OverlayPosition>().Where(p => p != OverlayPosition.Custom || _custom is not null).Select(p => new PositionChoice(p))];
        Text = overlay.Text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        SelectedPosition = Positions.FirstOrDefault(p => p.Position == overlay.Position) ?? Positions[3];
        SizeText = overlay.FontSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SelectedTextColor = Colors.First(c => c.Color == overlay.TextColor);
        SelectedBackgroundColor = Colors.First(c => c.Color == overlay.BackgroundColor);
        SelectedOutlineColor = Colors.First(c => c.Color == overlay.OutlineColor);
        Validation
            .Rule(nameof(Text), () => OverlayRules.TextError(Text, _limits().MaxTextLength))
            .Rule(nameof(SizeText), () => SizeError(SizeText, _limits()));
        Validation.Validate();
    }

    public IReadOnlyList<PositionChoice> Positions { get; }

    public static IReadOnlyList<ColorChoice> Colors { get; } = [.. Enum.GetValues<OverlayColor>().Select(c => new ColorChoice(c))];

    public static ModifierChoice DateModifier => OverlayModifiers.Date;

    public static ModifierChoice TimeModifier => OverlayModifiers.Time;

    /// <summary>The Dynamic text submenu of this row: each entry inserts its code here.</summary>
    public IReadOnlyList<ModifierAction> DynamicTextActions => _dynamicTextActions ??= [.. OverlayModifiers.DynamicTexts.Select(m => new ModifierAction(m.Name, new RelayCommand(() => Insert(m))))];

    private IReadOnlyList<ModifierAction>? _dynamicTextActions;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>The caret of the text box: a modifier goes in there.</summary>
    [ObservableProperty]
    public partial int CaretIndex { get; set; }

    [ObservableProperty]
    public partial PositionChoice SelectedPosition { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ColorChoice SelectedTextColor { get; set; }

    [ObservableProperty]
    public partial ColorChoice SelectedBackgroundColor { get; set; }

    [ObservableProperty]
    public partial ColorChoice SelectedOutlineColor { get; set; }

    /// <summary>Remove matching compares only the text: the other fields are hidden.</summary>
    [ObservableProperty]
    public partial bool ShowStyle { get; set; } = true;

    /// <summary>Raised after any input changed (the dialog sums up).</summary>
    public event EventHandler? Changed;

    /// <summary>Inserts the modifier's code at the caret (a space before it when it would stick to a word).</summary>
    [RelayCommand]
    private void Insert(ModifierChoice? modifier)
    {
        if (modifier is null)
        {
            return;
        }

        var text = Text ?? string.Empty;
        var at = Math.Clamp(CaretIndex, 0, text.Length);
        var code = at > 0 && !char.IsWhiteSpace(text[at - 1]) ? " " + modifier.Code : modifier.Code;
        Text = text.Insert(at, code);
        CaretIndex = at + code.Length;
    }

    [RelayCommand]
    private void Remove() => _remove(this);

    /// <summary>The user tried to apply: every error of the row shows.</summary>
    public void ShowErrors() => Validation.ShowAll();

    /// <summary>The row is valid (shown errors or not).</summary>
    public bool IsValid => Validation.IsValid;

    /// <summary>Re-runs the rules after the limits changed (another camera was read).</summary>
    public void Revalidate()
    {
        Validation.Validate();
    }

    public TextOverlay ToOverlay() => new()
    {
        Text = OverlayText.Normalize(Text),
        Position = SelectedPosition.Position,
        CustomPosition = SelectedPosition.Position == OverlayPosition.Custom ? _custom : null,
        TextColor = SelectedTextColor.Color,
        BackgroundColor = SelectedBackgroundColor.Color,
        OutlineColor = SelectedOutlineColor.Color,
        FontSize = int.TryParse(SizeText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : null,
    };

    internal static string? SizeError(string text, OverlayLimits limits)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? OverlayRules.SizeError(size, limits.MinFontSize, limits.MaxFontSize)
            : WholeNumber;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Text) or nameof(SelectedPosition) or nameof(SizeText) or nameof(SelectedTextColor)
            or nameof(SelectedBackgroundColor) or nameof(SelectedOutlineColor))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnValidationChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
