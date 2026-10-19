using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Compliance;

/// <summary>
/// One cell of a per-check column: the result of one check on one device as a <see cref="StatusChip"/> (passed green,
/// failed red or amber by the check's severity, checking violet and spinning, the rest neutral, empty when the check is not
/// for the device), with <see cref="ShowText"/> the first line of the detail next to the icon ("Checking..." while
/// checking), the result, the detail and the times as tooltip. Follows <see cref="DeviceCompliance.ResultsChanged"/> while
/// shown.
/// </summary>
public sealed class ComplianceResultCell : ContentControl
{
    public static readonly StyledProperty<DeviceCompliance?> ComplianceProperty =
        AvaloniaProperty.Register<ComplianceResultCell, DeviceCompliance?>(nameof(Compliance));

    private readonly StatusChip _chip = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private DeviceCompliance? _watched;

    public ComplianceResultCell(ComplianceStore store, string checkId, bool showText = false)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        CheckId = checkId ?? throw new ArgumentNullException(nameof(checkId));
        ShowText = showText;
        Content = _chip;
        Background = Avalonia.Media.Brushes.Transparent; // the whole cell shows the tooltip, not only the icon's lines
    }

    /// <summary>The result text next to the icon (passed shows the icon only).</summary>
    public bool ShowText { get; }

    public ComplianceStore Store { get; }

    public string CheckId { get; }

    /// <summary>The device's compliance (bound to the row's <c>Compliance</c>).</summary>
    public DeviceCompliance? Compliance
    {
        get => GetValue(ComplianceProperty);
        set => SetValue(ComplianceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ComplianceProperty)
        {
            Watch(IsAttached ? Compliance : null);
            Update();
        }
    }

    private bool IsAttached => TopLevel.GetTopLevel(this) is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(Compliance);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
    }

    private void Watch(DeviceCompliance? compliance)
    {
        if (_watched == compliance)
        {
            return;
        }

        if (_watched is not null)
        {
            _watched.ResultsChanged -= OnResultsChanged;
        }

        _watched = compliance;
        if (compliance is not null)
        {
            compliance.ResultsChanged += OnResultsChanged;
        }
    }

    private void OnResultsChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var result = Compliance?.Get(CheckId);
        var check = Store.FindCheck(CheckId);
        _chip.IsVisible = result is not null;
        if (result is null)
        {
            ToolTip.SetTip(this, null);
            return;
        }

        var level = DeviceCompliance.LevelOf(result, check);
        _chip.IsAccent = result.Checking;
        _chip.IsOk = !result.Checking && level == ComplianceLevel.Passed;
        _chip.IsWarning = !result.Checking && level == ComplianceLevel.Warning;
        _chip.IsError = !result.Checking && level == ComplianceLevel.Problem;
        _chip.Text = !ShowText ? null
            : result.Checking ? "Checking..."
            : result.Outcome == ComplianceOutcome.CompliancePassed ? null
            : result.Detail.Length > 0 ? CellText(result.Detail)
            : ComplianceViewModel.ResultText(result);
        ToolTip.SetTip(this, result.Checking ? "Checking..." : TooltipOf(result));
    }

    /// <summary>
    /// The detail on one line: "1 setting differs: ImageSource.I0.CaptureFrequency: 60Hz (file: 50Hz)"; several lines are
    /// joined with "; " after the first.
    /// </summary>
    public static string CellText(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var lines = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length <= 1 ? detail.Trim() : lines[0] + ": " + string.Join("; ", lines.Skip(1));
    }

    /// <summary>"Failed since 14:05, checked 14:30" and the detail below.</summary>
    public static string TooltipOf(ComplianceResultEntry result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var head = ComplianceViewModel.ResultText(result).Split(':', 2)[0];
        if (result.SinceUnixMs > 0 && result.Outcome is ComplianceOutcome.ComplianceFailed or ComplianceOutcome.CompliancePassed)
        {
            head += " since " + Time(result.SinceUnixMs);
        }

        if (result.CheckedUnixMs > 0)
        {
            head += ", checked " + Time(result.CheckedUnixMs);
        }

        return result.Detail.Length == 0 ? head : head + "\n" + result.Detail;
    }

    private static string Time(long unixMs)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }
}
