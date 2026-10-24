using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Network;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.Radius.Client;

/// <summary>
/// The Server card's form (docs/spec/radius-server.md "What the user sees"): Listen on, Port, Log entries kept. The TLS
/// settings are PKI's 802.1X server policy. Errors directly below the fields.
/// </summary>
public sealed partial class SettingsViewModel : ValidatingViewModel
{
    private readonly Func<bool> _mayManage;
    private RadiusSettings _saved = new();

    public SettingsViewModel(Func<bool> mayManage)
    {
        _mayManage = mayManage ?? throw new ArgumentNullException(nameof(mayManage));
        Validation
            .Rule(nameof(Port), () => ParseInt(Port) is >= 1 and <= 65535 ? null : RadiusRules.PortRange)
            .Rule(nameof(LogKept), () => ParseInt(LogKept) is >= RadiusLimits.MinLogKept and <= RadiusLimits.MaxLogKept
                ? null : RadiusRules.LogKeptRange);
        Validation.Validate();
        Listen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InterfaceSelection.Selected))
            {
                OnPropertyChanged(nameof(SelectedInterface));
            }
        };
    }

    /// <summary>The "Listen on" select (shared with the NTP, DHCP and EST server pages).</summary>
    public InterfaceSelection Listen { get; } = new();

    public InterfaceOption? SelectedInterface
    {
        get => Listen.Selected;
        set => Listen.Selected = value;
    }

    [ObservableProperty]
    public partial string Port { get; set; } = RadiusLimits.DefaultPort.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string LogKept { get; set; } = RadiusLimits.DefaultLogKept.ToString("N0", CultureInfo.InvariantCulture);

    public bool MayManage => _mayManage();

    /// <summary>Fills the form from the server (the change of another client too); nothing shows as an error afterwards.</summary>
    public void Apply(RadiusState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _saved = state.Settings;
        Listen.Apply(state.Interfaces, _saved.ListenOn, _saved.ListenOnName);
        Listen.Select(_saved.ListenOn);
        Port = _saved.Port.ToString(CultureInfo.InvariantCulture);
        LogKept = _saved.LogKept.ToString("N0", CultureInfo.InvariantCulture);
        Validation.Reset();
    }

    /// <summary>The saved settings with this form (valid only while <see cref="ValidatingViewModel.IsFormValid"/>).</summary>
    public RadiusSettings ToSettings() => _saved with
    {
        ListenOn = Listen.SelectedId ?? InterfaceOptions.AllId,
        Port = ParseInt(Port) ?? RadiusLimits.DefaultPort,
        LogKept = ParseInt(LogKept) ?? RadiusLimits.DefaultLogKept,
    };

    /// <summary>A save was tried: every error shows.</summary>
    public void ShowAllErrors() => Validation.ShowAll();

    /// <summary>The server refused a field (shown below it).</summary>
    public bool ShowServerError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var property = message switch
        {
            RadiusRules.PortRange => nameof(Port),
            RadiusRules.LogKeptRange => nameof(LogKept),
            _ => null,
        } ?? (message.Contains("port", StringComparison.OrdinalIgnoreCase) ? nameof(Port)
            : message.Contains("entries", StringComparison.OrdinalIgnoreCase) ? nameof(LogKept)
            : message.Contains("interface", StringComparison.OrdinalIgnoreCase) ? nameof(SelectedInterface)
            : null);
        if (property is null)
        {
            return false;
        }

        Validation.SetServerError(property, message);
        return true;
    }

    internal void NotifyRole() => OnPropertyChanged(nameof(MayManage));

    internal static int? ParseInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) ? value : null;
}
