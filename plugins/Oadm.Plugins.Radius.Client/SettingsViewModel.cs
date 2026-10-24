using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Client.Network;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.Radius.Client;

/// <summary>One cipher suite of Advanced: a check box with the suite's name.</summary>
public sealed partial class CipherSuiteViewModel(CipherSuiteOption suite, SettingsViewModel owner) : ObservableObject
{
    public int Id => suite.Id;

    public string Name => suite.Name;

    /// <summary>An old CBC suite: listed only while the minimum is TLS 1.0 or 1.1.</summary>
    public bool IsLegacy => suite.Legacy;

    /// <summary>Can be negotiated with the minimum version (TLS 1.3 is always offered; the TLS 1.2 suites need a minimum of 1.2 or lower).</summary>
    public bool IsUsable(string? minimumTls) => suite.Tls13 || (RadiusRules.MinimumIndex(minimumTls) is { } min && (suite.Legacy ? min < 2 : min <= 2));

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Listed for the chosen minimum.</summary>
    [ObservableProperty]
    public partial bool IsListed { get; set; } = !suite.Legacy;

    public bool MayManage => owner.MayManage;

    internal void NotifyRole() => OnPropertyChanged(nameof(MayManage));

    partial void OnIsCheckedChanged(bool value) => owner.CipherSuitesEdited();
}

/// <summary>
/// The General card with Advanced (docs/spec/radius-server.md "What the user sees"): Listen on, Port, Log entries kept;
/// Minimum TLS version (the hint for 1.0 and 1.1), the cipher suites (old CBC suites only below TLS 1.2, Reset to default,
/// at least one checked) and Largest fragment. Errors directly below the fields; the page's Save sends it.
/// </summary>
public sealed partial class SettingsViewModel : ValidatingViewModel
{
    /// <summary>The hint below "Minimum TLS version" for TLS 1.0 and 1.1.</summary>
    public const string OldTlsHint = "Only for old devices: TLS 1.0 and 1.1 are no longer considered secure.";

    public static IReadOnlyList<Choice<string>> TlsVersions { get; } = [.. RadiusLimits.TlsVersions.Select(v => new Choice<string>(v, "TLS " + v))];

    private readonly Func<bool> _mayManage;
    private bool _applying;

    public SettingsViewModel(Func<bool> mayManage)
    {
        _mayManage = mayManage ?? throw new ArgumentNullException(nameof(mayManage));
        MinimumTls = TlsVersions[DefaultTlsIndex];
        Validation
            .Rule(nameof(Port), () => ParseInt(Port) is >= 1 and <= 65535 ? null : RadiusRules.PortRange)
            .Rule(nameof(LogKept), () => ParseInt(LogKept) is >= RadiusLimits.MinLogKept and <= RadiusLimits.MaxLogKept
                ? null : RadiusRules.LogKeptRange)
            .Rule(nameof(LargestFragment), () => ParseInt(LargestFragment) is >= RadiusLimits.MinFragment and <= RadiusLimits.MaxFragment
                ? null : RadiusRules.FragmentRange)
            .Rule(nameof(CipherSuites), () => CipherSuites.Any(s => s.IsListed && s.IsChecked && s.IsUsable(MinimumTls.Value)) ? null : RadiusRules.CheckCipherSuite);
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TlsHint))]
    public partial Choice<string> MinimumTls { get; set; }

    [ObservableProperty]
    public partial string LargestFragment { get; set; } = RadiusLimits.DefaultFragment.ToString(CultureInfo.InvariantCulture);

    /// <summary>Advanced is open (collapsed at first: the defaults fit almost every network).</summary>
    [ObservableProperty]
    public partial bool ShowAdvanced { get; set; }

    /// <summary>The cipher suites the server lists, in its order; only <see cref="CipherSuiteViewModel.IsListed"/> ones show.</summary>
    public Sdk.Client.Collections.RangeObservableCollection<CipherSuiteViewModel> CipherSuites { get; } = [];

    /// <summary>The error of the cipher suites (a group of check boxes: shown below it by the field).</summary>
    public string? CipherSuitesError => Validation[nameof(CipherSuites)];

    /// <summary>"Only for old devices ..." for TLS 1.0 and 1.1; null otherwise.</summary>
    public string? TlsHint => IsBelowTls12(MinimumTls.Value) ? OldTlsHint : null;

    private static int DefaultTlsIndex => RadiusRules.MinimumIndex(RadiusLimits.DefaultMinimumTls) ?? 2;

    private static bool IsBelowTls12(string? version) => RadiusRules.MinimumIndex(version) is < 2;

    public bool MayManage => _mayManage();

    /// <summary>Fills the form from the server (the change of another client too); nothing shows as an error afterwards.</summary>
    public void Apply(RadiusState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _applying = true;
        try
        {
            var settings = state.Settings;
            Listen.Apply(state.Interfaces, settings.ListenOn, settings.ListenOnName);
            Listen.Select(settings.ListenOn);
            Port = settings.Port.ToString(CultureInfo.InvariantCulture);
            LogKept = settings.LogKept.ToString("N0", CultureInfo.InvariantCulture);
            LargestFragment = settings.LargestFragment.ToString(CultureInfo.InvariantCulture);
            MinimumTls = TlsVersions.FirstOrDefault(v => v.Value == settings.MinimumTls) ?? TlsVersions[DefaultTlsIndex];
            var checkedIds = settings.CipherSuites.ToHashSet();
            CipherSuites.ReplaceAll(state.CipherSuitesListed.Select(s => new CipherSuiteViewModel(s, this) { IsChecked = checkedIds.Contains(s.Id) }));
            UpdateListed(lowered: false);
        }
        finally
        {
            _applying = false;
        }

        Validation.Reset();
        OnPropertyChanged(nameof(CipherSuitesError));
    }

    /// <summary>The settings of the form (valid only while <see cref="ValidatingViewModel.IsFormValid"/>).</summary>
    public RadiusSettings ToSettings(bool vlansEnabled, int? defaultVlan) => new()
    {
        ListenOn = Listen.SelectedId ?? InterfaceOptions.AllId,
        Port = ParseInt(Port) ?? RadiusLimits.DefaultPort,
        LogKept = ParseInt(LogKept) ?? RadiusLimits.DefaultLogKept,
        MinimumTls = MinimumTls.Value,
        CipherSuites = [.. CipherSuites.Where(s => s.IsListed && s.IsChecked).Select(s => s.Id)],
        LargestFragment = ParseInt(LargestFragment) ?? RadiusLimits.DefaultFragment,
        VlansEnabled = vlansEnabled,
        DefaultVlan = defaultVlan,
    };

    /// <summary>A save was tried: every error shows.</summary>
    public void ShowAllErrors()
    {
        Validation.ShowAll();
        OnPropertyChanged(nameof(CipherSuitesError));
    }

    /// <summary>The server refused a field (shown below it).</summary>
    public bool ShowServerError(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var property = message switch
        {
            RadiusRules.PortRange => nameof(Port),
            RadiusRules.LogKeptRange => nameof(LogKept),
            RadiusRules.FragmentRange => nameof(LargestFragment),
            RadiusRules.CheckCipherSuite => nameof(CipherSuites),
            RadiusRules.ChooseTls => nameof(MinimumTls),
            _ => null,
        } ?? (message.Contains("port", StringComparison.OrdinalIgnoreCase) ? nameof(Port)
            : message.Contains("entries", StringComparison.OrdinalIgnoreCase) ? nameof(LogKept)
            : message.Contains("fragment", StringComparison.OrdinalIgnoreCase) ? nameof(LargestFragment)
            : message.Contains("cipher", StringComparison.OrdinalIgnoreCase) ? nameof(CipherSuites)
            : message.Contains("interface", StringComparison.OrdinalIgnoreCase) ? nameof(SelectedInterface)
            : null);
        if (property is null)
        {
            return false;
        }

        Validation.SetServerError(property, message);
        OnPropertyChanged(nameof(CipherSuitesError));
        return true;
    }

    internal void NotifyRole()
    {
        OnPropertyChanged(nameof(MayManage));
        foreach (var suite in CipherSuites)
        {
            suite.NotifyRole();
        }
    }

    internal void CipherSuitesEdited()
    {
        if (_applying)
        {
            return;
        }

        Validation.Touch(nameof(CipherSuites));
        OnPropertyChanged(nameof(CipherSuitesError));
    }

    /// <summary>Reset to default: every suite listed for the chosen minimum checked (the secure default set).</summary>
    [RelayCommand]
    private void ResetCipherSuites()
    {
        _applying = true;
        try
        {
            foreach (var suite in CipherSuites)
            {
                suite.IsChecked = suite.IsListed;
            }
        }
        finally
        {
            _applying = false;
        }

        CipherSuitesEdited();
    }

    partial void OnMinimumTlsChanged(Choice<string> oldValue, Choice<string> newValue)
    {
        if (!_applying && oldValue is not null)
        {
            UpdateListed(lowered: !IsBelowTls12(oldValue.Value) && IsBelowTls12(newValue.Value));
            CipherSuitesEdited();
        }
    }

    /// <summary>The old suites show only below TLS 1.2 and are checked when the minimum is lowered there.</summary>
    private void UpdateListed(bool lowered)
    {
        var legacy = IsBelowTls12(MinimumTls.Value);
        foreach (var suite in CipherSuites.Where(s => s.IsLegacy))
        {
            suite.IsListed = legacy;
            if (lowered)
            {
                suite.IsChecked = true;
            }
        }
    }

    protected override void OnValidationChanged() => OnPropertyChanged(nameof(CipherSuitesError));

    private static int? ParseInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) ? value : null;
}
