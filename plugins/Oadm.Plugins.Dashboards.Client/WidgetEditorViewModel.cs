using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

using Avalonia;
using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Events;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>A choice of a select field with its label.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A color of the color select: its name, label and the theme brush for the swatch.</summary>
public sealed record ColorOption(string Name, string Label)
{
    public IBrush? Brush => Resource(WidgetColors.BrushKey(Name)) as IBrush;

    public override string ToString() => Label;

    internal static object? Resource(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value : null;
}

/// <summary>An icon of the icon select.</summary>
public sealed record IconOption(string Key, string Label)
{
    public Geometry? Data => ColorOption.Resource("Icon." + Key) as Geometry;

    public override string ToString() => Label;
}

/// <summary>A threshold row of a Number widget: from this value on, this color.</summary>
public sealed partial class ThresholdRow : ValidatingViewModel
{
    private readonly WidgetEditorViewModel _owner;

    internal ThresholdRow(WidgetEditorViewModel owner, string value, ColorOption color)
    {
        _owner = owner;
        ValueText = value;
        Color = color;
        Validation.Rule(nameof(ValueText), () => FieldValues.TryNumber(ValueText, out _) ? null : "Enter a number.");
    }

    [ObservableProperty]
    public partial string ValueText { get; set; }

    [ObservableProperty]
    public partial ColorOption Color { get; set; }

#pragma warning disable CA1822 // Bound per row in XAML (compiled bindings need instance members).
    public IReadOnlyList<ColorOption> Colors => WidgetEditorViewModel.ColorOptions;
#pragma warning restore CA1822

    partial void OnValueTextChanged(string value) => _owner.Apply();

    partial void OnColorChanged(ColorOption oldValue, ColorOption newValue)
    {
        if (newValue is null && oldValue is not null)
        {
            Color = oldValue;
            return;
        }

        _owner.Apply();
    }

    [RelayCommand]
    private void Remove() => _owner.RemoveThreshold(this);

    internal void ShowAllErrors() => Validation.ShowAll();
}

/// <summary>
/// The panel of the selected widget while a dashboard is edited: the device (the shared device picker for one device),
/// the value (the event's fields seen so far; Active for an on / off event), the type, the widget and its settings.
/// Every change goes to the widget at once, so the grid shows it.
/// </summary>
public sealed partial class WidgetEditorViewModel : ValidatingViewModel
{
    public static readonly IReadOnlyList<ColorOption> ColorOptions = [.. WidgetColors.All.Select(c => new ColorOption(c.Name, c.Label))];

    public static readonly IReadOnlyList<IconOption> IconOptions = [.. WidgetIcons.All.Select(i => new IconOption(i.Key, i.Label))];

    public static readonly IReadOnlyList<Choice<int?>> DecimalOptions =
        [new(null, "As received"), .. Enumerable.Range(0, DashboardsPluginInfo.MaxDecimals + 1).Select(n => new Choice<int?>(n, n.ToString(CultureInfo.CurrentCulture)))];

    private readonly DashboardsPageViewModel _page;
    private bool _loading = true;
    private string _defaultName = string.Empty;

    internal WidgetEditorViewModel(DashboardsPageViewModel page, WidgetViewModel target)
    {
        _page = page;
        Target = target;
        var w = target.Widget;
        Name = w.Name;
        IconSize = w.IconSize;
        FontSize = w.FontSize;
        Color = ColorNamed(w.Color);
        OnIcon = IconNamed(w.OnIcon);
        OffIcon = IconNamed(w.OffIcon);
        OnColor = ColorNamed(w.OnColor);
        OffColor = ColorNamed(w.OffColor);
        OnText = w.OnText;
        OffText = w.OffText;
        Decimals = DecimalOptions.FirstOrDefault(d => d.Value == w.Decimals) ?? DecimalOptions[0];
        Unit = w.Unit;
        foreach (var threshold in w.Thresholds)
        {
            Thresholds.Add(new ThresholdRow(this, threshold.Value.ToString(CultureInfo.InvariantCulture), ColorNamed(threshold.Color)));
        }

        Validation.Rule(nameof(Name), () => DashboardRules.WidgetNameProblem(Name));
        Validation.Rule(nameof(Unit), () => Unit.Length > DashboardsPluginInfo.MaxUnitLength ? DashboardRules.UnitTooLong : null);
        RefreshCatalog();
        _defaultName = DefaultName();
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = _defaultName;
        }

        _loading = false;
        Apply();
    }

    /// <summary>The widget the panel changes.</summary>
    public WidgetViewModel Target { get; }

    public bool IsVideo => Target.Widget.Kind == WidgetKind.Video;

    public bool IsValue => !IsVideo;

    /// <summary>The event of the widget, as the Events list names it.</summary>
    public string EventName => _page.FindEvent(Target.Widget.TypeId)?.Name ?? DashboardTexts.EventGone;

    /// <summary>The event comes from devices: the Device row shows.</summary>
    public bool ShowDevice => IsValue && (_page.FindEvent(Target.Widget.TypeId)?.DeviceScope ?? Target.Widget.DeviceId is not null);

    /// <summary>"10.0.0.48", "Choose a device", or the removed device.</summary>
    public string DeviceText => Target.Widget.DeviceId is null ? "Choose a device"
        : Target.Device?.Address ?? Target.Message ?? "Choose a device";

    public bool NeedsDevice => ShowDevice && Target.Widget.DeviceId is null;

    public ObservableCollection<Choice<CatalogField>> Fields { get; } = [];

    [ObservableProperty]
    public partial Choice<CatalogField>? SelectedField { get; set; }

    /// <summary>"Trigger the event once on the device." while the event's values were not seen yet.</summary>
    [ObservableProperty]
    public partial bool ShowTriggerHint { get; private set; }

    public ObservableCollection<Choice<FieldType>> Types { get; } = [];

    [ObservableProperty]
    public partial Choice<FieldType>? SelectedType { get; set; }

    public ObservableCollection<Choice<WidgetKind>> Kinds { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnOff), nameof(ShowText), nameof(ShowNumber))]
    public partial Choice<WidgetKind>? SelectedKind { get; set; }

    public bool ShowOnOff => IsValue && SelectedKind?.Value == WidgetKind.OnOff;

    public bool ShowText => IsValue && SelectedKind?.Value is WidgetKind.Text or WidgetKind.Number;

    public bool ShowNumber => IsValue && SelectedKind?.Value == WidgetKind.Number;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconSizeValue), nameof(IconSizeText))]
    public partial int IconSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontSizeValue), nameof(FontSizeText))]
    public partial int FontSize { get; set; }

    /// <summary>The slider of <see cref="IconSize"/>.</summary>
    public double IconSizeValue
    {
        get => IconSize;
        set => IconSize = (int)Math.Round(value);
    }

    /// <summary>The slider of <see cref="FontSize"/>.</summary>
    public double FontSizeValue
    {
        get => FontSize;
        set => FontSize = (int)Math.Round(value);
    }

    /// <summary>"60 % of the widget".</summary>
    public string IconSizeText => SizeText(IconSize);

    public string FontSizeText => SizeText(FontSize);

    private static string SizeText(int share) => string.Create(CultureInfo.CurrentCulture, $"{share} % of the widget");

    [ObservableProperty]
    public partial ColorOption Color { get; set; }

    [ObservableProperty]
    public partial IconOption OnIcon { get; set; }

    [ObservableProperty]
    public partial IconOption OffIcon { get; set; }

    [ObservableProperty]
    public partial ColorOption OnColor { get; set; }

    [ObservableProperty]
    public partial ColorOption OffColor { get; set; }

    [ObservableProperty]
    public partial string OnText { get; set; }

    [ObservableProperty]
    public partial string OffText { get; set; }

    [ObservableProperty]
    public partial Choice<int?> Decimals { get; set; }

    [ObservableProperty]
    public partial string Unit { get; set; }

    public ObservableCollection<ThresholdRow> Thresholds { get; } = [];

    public bool CanAddThreshold => Thresholds.Count < DashboardsPluginInfo.MaxThresholds;

#pragma warning disable CA1822 // Bound per row in XAML (compiled bindings need instance members).
    public IReadOnlyList<ColorOption> Colors => ColorOptions;

    public IReadOnlyList<IconOption> Icons => IconOptions;

    public IReadOnlyList<Choice<int?>> DecimalChoices => DecimalOptions;
#pragma warning restore CA1822

    /// <summary>Errors of the panel and its threshold rows.</summary>
    public bool HasAnyErrors => HasErrors || Thresholds.Any(t => t.HasErrors);

    [RelayCommand]
    private async Task ChooseDeviceAsync()
    {
        if (await _page.PickDeviceAsync(Target).ConfigureAwait(true) is { } device)
        {
            var before = DefaultName();
            Target.Device = _page.DeviceOf(device);
            Target.Update(Target.Widget with { DeviceId = device });
            FollowDefaultName(before);
            OnPropertyChanged(nameof(DeviceText));
            OnPropertyChanged(nameof(NeedsDevice));
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddThreshold))]
    private void AddThreshold()
    {
        var last = Thresholds.Count == 0 ? 0 : (FieldValues.TryNumber(Thresholds[^1].ValueText, out var n) ? n : 0);
        Thresholds.Add(new ThresholdRow(this, (last + 10).ToString(CultureInfo.InvariantCulture), ColorOptions.First(c => c.Name == "Amber")));
        AddThresholdCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddThreshold));
        Apply();
    }

    internal void RemoveThreshold(ThresholdRow row)
    {
        Thresholds.Remove(row);
        AddThresholdCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddThreshold));
        Apply();
    }

    /// <summary>The catalog changed (a value was seen for the first time): the fields and the suggestion follow.</summary>
    internal void RefreshCatalog()
    {
        var wasLoading = _loading;
        _loading = true;
        var catalogEvent = _page.FindEvent(Target.Widget.TypeId);
        var fields = catalogEvent?.Fields ?? [];
        var current = Target.Widget.Field;
        Fields.Clear();
        foreach (var field in fields)
        {
            Fields.Add(new Choice<CatalogField>(field, field.Label));
        }

        // A saved field that was not seen since the server started stays choosable under its name.
        if (current is not null && fields.All(f => f.Name != current))
        {
            Fields.Add(new Choice<CatalogField>(new CatalogField(current, current == DashboardsPluginInfo.ActiveField ? DashboardsPluginInfo.ActiveLabel : current, EventValueKind.Text), current));
        }

        ShowTriggerHint = IsValue && (catalogEvent is null || catalogEvent.HasUnseenFields || fields.Count == 0);
        SelectedField = Fields.FirstOrDefault(f => f.Value.Name == current) ?? Fields.FirstOrDefault();
        RefreshTypes();
        OnPropertyChanged(nameof(EventName));
        OnPropertyChanged(nameof(ShowDevice));
        OnPropertyChanged(nameof(NeedsDevice));
        _loading = wasLoading;
    }

    partial void OnSelectedFieldChanged(Choice<CatalogField>? oldValue, Choice<CatalogField>? newValue)
    {
        if (_loading)
        {
            return;
        }

        if (newValue is null && oldValue is not null)
        {
            SelectedField = oldValue; // a select that lost its list never empties the choice
            return;
        }

        var before = _defaultName;
        RefreshTypes();
        Apply();
        FollowDefaultName(before);
    }

    partial void OnSelectedTypeChanged(Choice<FieldType>? oldValue, Choice<FieldType>? newValue)
    {
        if (newValue is null && oldValue is not null && !_loading)
        {
            SelectedType = oldValue;
            return;
        }

        if (!_loading)
        {
            RefreshKinds();
            Apply();
        }
    }

    partial void OnSelectedKindChanged(Choice<WidgetKind>? oldValue, Choice<WidgetKind>? newValue)
    {
        if (newValue is null && oldValue is not null && !_loading)
        {
            SelectedKind = oldValue;
            return;
        }

        Apply();
    }

    partial void OnNameChanged(string value) => Apply();

    partial void OnIconSizeChanged(int value) => Apply();

    partial void OnFontSizeChanged(int value) => Apply();

    partial void OnColorChanged(ColorOption oldValue, ColorOption newValue) => Keep(newValue, oldValue, v => Color = v);

    partial void OnOnIconChanged(IconOption oldValue, IconOption newValue) => Keep(newValue, oldValue, v => OnIcon = v);

    partial void OnOffIconChanged(IconOption oldValue, IconOption newValue) => Keep(newValue, oldValue, v => OffIcon = v);

    partial void OnOnColorChanged(ColorOption oldValue, ColorOption newValue) => Keep(newValue, oldValue, v => OnColor = v);

    partial void OnOffColorChanged(ColorOption oldValue, ColorOption newValue) => Keep(newValue, oldValue, v => OffColor = v);

    partial void OnOnTextChanged(string value) => Apply();

    partial void OnOffTextChanged(string value) => Apply();

    partial void OnDecimalsChanged(Choice<int?> oldValue, Choice<int?> newValue) => Keep(newValue, oldValue, v => Decimals = v);

    /// <summary>A select field that writes no choice (its list changed) keeps the one before; anything else is applied.</summary>
    private void Keep<T>(T? value, T? before, Action<T> restore)
        where T : class
    {
        if (value is null && before is not null)
        {
            restore(before);
            return;
        }

        Apply();
    }

    partial void OnUnitChanged(string value) => Apply();

    /// <summary>The type select: Automatic names the kind it takes ("Automatic (Number)").</summary>
    private void RefreshTypes()
    {
        var wasLoading = _loading;
        _loading = true;
        var chosen = SelectedType?.Value ?? Target.Widget.Type;
        var automatic = SelectedField?.Value.Kind ?? EventValueKind.Text;
        Types.Clear();
        Types.Add(new Choice<FieldType>(FieldType.Automatic, "Automatic (" + KindLabel(automatic) + ")"));
        Types.Add(new Choice<FieldType>(FieldType.YesNo, "Yes / no"));
        Types.Add(new Choice<FieldType>(FieldType.Number, "Number"));
        Types.Add(new Choice<FieldType>(FieldType.Text, "Text"));
        SelectedType = Types.First(t => t.Value == chosen);
        RefreshKinds();
        _loading = wasLoading;
    }

    /// <summary>The widgets that fit the type; the current one when it fits, else the best one.</summary>
    private void RefreshKinds()
    {
        var wasLoading = _loading;
        _loading = true;
        var current = SelectedKind?.Value ?? Target.Widget.Kind;
        var kind = FieldValues.Effective(SelectedType?.Value ?? FieldType.Automatic, SelectedField?.Value.Kind ?? EventValueKind.Text);
        Kinds.Clear();
        foreach (var fit in FieldValues.WidgetsFor(kind))
        {
            Kinds.Add(new Choice<WidgetKind>(fit, KindLabel(fit)));
        }

        SelectedKind = Kinds.FirstOrDefault(k => k.Value == current) ?? Kinds[0];
        _loading = wasLoading;
    }

    /// <summary>Writes the panel to the widget (the grid shows it at once).</summary>
    internal void Apply()
    {
        if (_loading)
        {
            return;
        }

        var widget = Target.Widget with
        {
            Name = Name.Trim(),
            Field = IsValue ? SelectedField?.Value.Name ?? Target.Widget.Field : null,
            Type = IsValue ? SelectedType?.Value ?? FieldType.Automatic : FieldType.Automatic,
            Kind = IsValue ? SelectedKind?.Value ?? Target.Widget.Kind : WidgetKind.Video,
            IconSize = Math.Clamp(IconSize, DashboardsPluginInfo.MinShare, DashboardsPluginInfo.MaxShare),
            FontSize = Math.Clamp(FontSize, DashboardsPluginInfo.MinShare, DashboardsPluginInfo.MaxShare),
            Color = Color.Name,
            OnIcon = OnIcon.Key,
            OffIcon = OffIcon.Key,
            OnColor = OnColor.Name,
            OffColor = OffColor.Name,
            OnText = OnText.Trim(),
            OffText = OffText.Trim(),
            Decimals = Decimals.Value,
            Unit = Unit.Trim().Length <= DashboardsPluginInfo.MaxUnitLength ? Unit.Trim() : Target.Widget.Unit,
            Thresholds = [.. Thresholds
                .Where(t => FieldValues.TryNumber(t.ValueText, out _))
                .Select(t => new Threshold(FieldValues.TryNumber(t.ValueText, out var v) ? v : 0, t.Color.Name))
                .OrderBy(t => t.Value)],
        };
        if (DashboardRules.WidgetNameProblem(widget.Name) is not null)
        {
            widget = widget with { Name = Target.Widget.Name };
        }

        Target.Update(widget);
        _page.EditorChanged();
    }

    internal void ShowAllErrors()
    {
        Validation.ShowAll();
        foreach (var row in Thresholds)
        {
            row.ShowAllErrors();
        }
    }

    /// <summary>The panel closes: nothing more is written.</summary>
    internal void Detach() => _loading = true;

    /// <summary>"Temperature · 10.0.0.48"; a server event just the field.</summary>
    private string DefaultName()
    {
        if (IsVideo)
        {
            return Target.Device?.Address ?? string.Empty;
        }

        var field = SelectedField?.Value.Label ?? _page.FindEvent(Target.Widget.TypeId)?.Name ?? string.Empty;
        return Target.Device?.Address is { } address ? field + " · " + address : field;
    }

    /// <summary>A name the user did not change follows the field and the device.</summary>
    private void FollowDefaultName(string before)
    {
        var next = DefaultName();
        if (Name == before || string.IsNullOrWhiteSpace(Name))
        {
            Name = next;
        }

        _defaultName = next;
    }

    private static ColorOption ColorNamed(string name) => ColorOptions.FirstOrDefault(c => c.Name == name) ?? ColorOptions[0];

    private static IconOption IconNamed(string key) => IconOptions.FirstOrDefault(i => i.Key == key) ?? IconOptions[0];

    private static string KindLabel(EventValueKind kind) => kind switch
    {
        EventValueKind.YesNo => "Yes / no",
        EventValueKind.Number => "Number",
        _ => "Text",
    };

    private static string KindLabel(WidgetKind kind) => kind switch
    {
        WidgetKind.OnOff => "On / Off",
        WidgetKind.Number => "Number",
        WidgetKind.Video => "Live video",
        _ => "Text",
    };
}
