using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Compliance;

/// <summary>
/// One cell of a per-check column: the result of one check on one device as an icon-only <see cref="StatusChip"/> (passed
/// green, failed red or amber by the check's severity, the rest neutral, empty when the check is not for the device), with
/// the result, the detail and the times as tooltip. Follows <see cref="DeviceCompliance.ResultsChanged"/> while shown.
/// </summary>
public sealed class ComplianceResultCell : ContentControl
{
    public static readonly StyledProperty<DeviceCompliance?> ComplianceProperty =
        AvaloniaProperty.Register<ComplianceResultCell, DeviceCompliance?>(nameof(Compliance));

    private readonly StatusChip _chip = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private DeviceCompliance? _watched;

    public ComplianceResultCell(ComplianceStore store, string checkId)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        CheckId = checkId ?? throw new ArgumentNullException(nameof(checkId));
        Content = _chip;
    }

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
        _chip.IsOk = level == ComplianceLevel.Passed;
        _chip.IsWarning = level == ComplianceLevel.Warning;
        _chip.IsError = level == ComplianceLevel.Problem;
        ToolTip.SetTip(this, TooltipOf(result));
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
