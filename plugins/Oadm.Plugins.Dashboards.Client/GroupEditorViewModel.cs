using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Dashboards.Client;

/// <summary>
/// The panel of several selected widgets while a dashboard is edited: what they share. Width and Height in cells for all;
/// Show name for value widgets; the On / Off settings when all are On / Off; font size and color when all are Text or
/// Number; decimals and unit when all are Number. A setting where they differ shows "Mixed" (an empty select or field)
/// until it is changed; a change goes to every selected widget at once, nothing else of them changes. Copy and remove act
/// on the whole selection.
/// </summary>
public sealed partial class GroupEditorViewModel : ValidatingViewModel
{
    public const string MixedText = "Mixed";

    private readonly DashboardTab _tab;
    private readonly IReadOnlyList<WidgetViewModel> _widgets;
    private bool _loading = true;

    internal GroupEditorViewModel(DashboardTab tab, IReadOnlyList<WidgetViewModel> widgets)
    {
        _tab = tab;
        _widgets = widgets;
        var all = widgets.Select(w => w.Widget).ToList();
        ShowValueSettings = all.All(w => w.IsValue);
        ShowOnOff = all.All(w => w.Kind == WidgetKind.OnOff);
        ShowText = all.All(w => w.Kind is WidgetKind.Text or WidgetKind.Number);
        ShowNumber = all.All(w => w.Kind == WidgetKind.Number);

        WidthText = Same(all, w => (int?)w.Width)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        HeightText = Same(all, w => (int?)w.Height)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        WidthWatermark = WidthText.Length == 0 ? MixedText : string.Empty;
        HeightWatermark = HeightText.Length == 0 ? MixedText : string.Empty;
        ShowName = Same(all, w => (bool?)w.ShowName);
        OnIcon = Same(all, w => w.OnIcon) is { } onIcon ? WidgetEditorViewModel.IconOptions.FirstOrDefault(i => i.Key == onIcon) : null;
        OffIcon = Same(all, w => w.OffIcon) is { } offIcon ? WidgetEditorViewModel.IconOptions.FirstOrDefault(i => i.Key == offIcon) : null;
        OnColor = ColorOf(Same(all, w => w.OnColor));
        OffColor = ColorOf(Same(all, w => w.OffColor));
        Color = ColorOf(Same(all, w => w.Color));
        OnText = Same(all, w => w.OnText) ?? string.Empty;
        OffText = Same(all, w => w.OffText) ?? string.Empty;
        Unit = Same(all, w => w.Unit) ?? string.Empty;
        IconSizeMixed = Same(all, w => (int?)w.IconSize) is null;
        FontSizeMixed = Same(all, w => (int?)w.FontSize) is null;
        IconSize = all[0].IconSize;
        FontSize = all[0].FontSize;
        Decimals = all.Select(w => w.Decimals).Distinct().Count() == 1
            ? WidgetEditorViewModel.DecimalOptions.FirstOrDefault(d => d.Value == all[0].Decimals)
            : null;
        UnitMixed = Same(all, w => w.Unit) is null;
        OnTextMixed = Same(all, w => w.OnText) is null;
        OffTextMixed = Same(all, w => w.OffText) is null;

        Validation.Rule(nameof(WidthText), () => SizeProblem(WidthText));
        Validation.Rule(nameof(HeightText), () => SizeProblem(HeightText));
        Validation.Rule(nameof(Unit), () => Unit.Length > DashboardsPluginInfo.MaxUnitLength ? DashboardRules.UnitTooLong : null);
        _loading = false;
    }

    /// <summary>"5 widgets selected".</summary>
    public string Title => string.Create(CultureInfo.CurrentCulture, $"{_widgets.Count:N0} widgets selected");

    public bool ShowValueSettings { get; }

    public bool ShowOnOff { get; }

    public bool ShowText { get; }

    public bool ShowNumber { get; }

    /// <summary>Every selected widget has a device: they can be copied for other devices.</summary>
    public bool CanCopy => _widgets.All(w => w.CanCopy);

    [ObservableProperty]
    public partial string WidthText { get; set; }

    [ObservableProperty]
    public partial string HeightText { get; set; }

    /// <summary>Null while the widgets differ (the select shows "Mixed").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNameChoice))]
    public partial bool? ShowName { get; set; }

    /// <summary>The "Show name" select (Yes, No; "Mixed" while they differ).</summary>
    public Choice<bool>? ShowNameChoice
    {
        get => ShowName is { } show ? WidgetEditorViewModel.YesNoOptions.First(o => o.Value == show) : null;
        set
        {
            if (value is not null)
            {
                ShowName = value.Value;
            }
        }
    }

    [ObservableProperty]
    public partial IconOption? OnIcon { get; set; }

    [ObservableProperty]
    public partial IconOption? OffIcon { get; set; }

    [ObservableProperty]
    public partial ColorOption? OnColor { get; set; }

    [ObservableProperty]
    public partial ColorOption? OffColor { get; set; }

    [ObservableProperty]
    public partial ColorOption? Color { get; set; }

    [ObservableProperty]
    public partial string OnText { get; set; }

    [ObservableProperty]
    public partial string OffText { get; set; }

    [ObservableProperty]
    public partial string Unit { get; set; }

    [ObservableProperty]
    public partial Choice<int?>? Decimals { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconSizeValue), nameof(IconSizeText))]
    public partial int IconSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontSizeValue), nameof(FontSizeText))]
    public partial int FontSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconSizeText))]
    public partial bool IconSizeMixed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontSizeText))]
    public partial bool FontSizeMixed { get; private set; }

    public bool UnitMixed { get; }

    public bool OnTextMixed { get; }

    public bool OffTextMixed { get; }

    /// <summary>"Mixed" as the empty text of a field whose widgets differ.</summary>
    public string UnitWatermark => UnitMixed ? MixedText : string.Empty;

    public string OnTextWatermark => OnTextMixed ? MixedText : string.Empty;

    public string OffTextWatermark => OffTextMixed ? MixedText : string.Empty;

    /// <summary>"Mixed" in Width / Height while the widgets differ.</summary>
    public string WidthWatermark { get; private init; } = string.Empty;

    public string HeightWatermark { get; private init; } = string.Empty;

    public double IconSizeValue
    {
        get => IconSize;
        set => IconSize = (int)Math.Round(value);
    }

    public double FontSizeValue
    {
        get => FontSize;
        set => FontSize = (int)Math.Round(value);
    }

    public string IconSizeText => IconSizeMixed ? MixedText : string.Create(CultureInfo.CurrentCulture, $"{IconSize} % of the widget");

    public string FontSizeText => FontSizeMixed ? MixedText : string.Create(CultureInfo.CurrentCulture, $"{FontSize} % of the widget");

#pragma warning disable CA1822 // Bound in XAML (compiled bindings need instance members).
    public IReadOnlyList<ColorOption> Colors => WidgetEditorViewModel.ColorOptions;

    public IReadOnlyList<IconOption> Icons => WidgetEditorViewModel.IconOptions;

    public IReadOnlyList<Choice<int?>> DecimalChoices => WidgetEditorViewModel.DecimalOptions;

    public IReadOnlyList<Choice<bool>> YesNoChoices => WidgetEditorViewModel.YesNoOptions;
#pragma warning restore CA1822

    /// <summary>Copies of every selected widget for the chosen devices.</summary>
    [RelayCommand]
    private async Task CopyForDevicesAsync()
    {
        var devices = await _tab.PickCopyDevicesAsync(_widgets).ConfigureAwait(true);
        if (devices.Count > 0)
        {
            _tab.CopyFor(_widgets, devices);
        }
    }

    /// <summary>Removes every selected widget.</summary>
    [RelayCommand]
    private void RemoveAll() => _tab.RemoveSelection();

    partial void OnWidthTextChanged(string value) => Resize();

    partial void OnHeightTextChanged(string value) => Resize();

    partial void OnShowNameChanged(bool? value)
    {
        if (value is { } show)
        {
            Each(w => w with { ShowName = show });
        }
    }

    partial void OnOnIconChanged(IconOption? value) => Set(value, v => w => w with { OnIcon = v.Key });

    partial void OnOffIconChanged(IconOption? value) => Set(value, v => w => w with { OffIcon = v.Key });

    partial void OnOnColorChanged(ColorOption? value) => Set(value, v => w => w with { OnColor = v.Name });

    partial void OnOffColorChanged(ColorOption? value) => Set(value, v => w => w with { OffColor = v.Name });

    partial void OnColorChanged(ColorOption? value) => Set(value, v => w => w with { Color = v.Name });

    partial void OnOnTextChanged(string value) => Each(w => w with { OnText = value.Trim() });

    partial void OnOffTextChanged(string value) => Each(w => w with { OffText = value.Trim() });

    partial void OnUnitChanged(string value)
    {
        if (value.Trim().Length <= DashboardsPluginInfo.MaxUnitLength)
        {
            Each(w => w with { Unit = value.Trim() });
        }
    }

    partial void OnDecimalsChanged(Choice<int?>? value) => Set(value, v => w => w with { Decimals = v.Value });

    partial void OnIconSizeChanged(int value)
    {
        if (!_loading)
        {
            IconSizeMixed = false;
            Each(w => w with { IconSize = Math.Clamp(value, DashboardsPluginInfo.MinShare, DashboardsPluginInfo.MaxShare) });
        }
    }

    partial void OnFontSizeChanged(int value)
    {
        if (!_loading)
        {
            FontSizeMixed = false;
            Each(w => w with { FontSize = Math.Clamp(value, DashboardsPluginInfo.MinShare, DashboardsPluginInfo.MaxShare) });
        }
    }

    internal void ShowAllErrors() => Validation.ShowAll();

    /// <summary>The panel closes: nothing more is written.</summary>
    internal void Detach() => _loading = true;

    private void Resize()
    {
        if (_loading || SizeProblem(WidthText) is not null || SizeProblem(HeightText) is not null)
        {
            return;
        }

        _tab.ResizeSelection(ParseSize(WidthText), ParseSize(HeightText));
    }

    /// <summary>A select that writes no choice (its list changed, or "Mixed") changes nothing.</summary>
    private void Set<T>(T? value, Func<T, Func<Widget, Widget>> change)
        where T : class
    {
        if (value is not null)
        {
            Each(change(value));
        }
    }

    private void Each(Func<Widget, Widget> change)
    {
        if (_loading)
        {
            return;
        }

        foreach (var widget in _widgets)
        {
            widget.Update(change(widget.Widget));
        }
    }

    private static int? ParseSize(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? SizeProblem(string text) =>
        string.IsNullOrWhiteSpace(text) || ParseSize(text) is >= 2 and <= 20 ? null : "Enter 2 to 20 cells.";

    private static T? Same<T>(List<Widget> widgets, Func<Widget, T> value)
    {
        var first = value(widgets[0]);
        return widgets.All(w => EqualityComparer<T>.Default.Equals(value(w), first)) ? first : default;
    }

    private static ColorOption? ColorOf(string? name) => name is null ? null : WidgetEditorViewModel.ColorOptions.FirstOrDefault(c => c.Name == name);
}
