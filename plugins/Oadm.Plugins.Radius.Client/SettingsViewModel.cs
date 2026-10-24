using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Network;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.Radius.Client;

/// <summary>One cipher suite of Advanced: a check box with the suite's name.</summary>
public sealed partial class CipherSuiteViewModel(CipherSuiteOption suite, AdvancedViewModel owner) : ObservableObject
{
    public int Id => suite.Id;

    public string Name => suite.Name;

    /// <summary>An old CBC suite: listed only while the minimum is TLS 1.0 or 1.1.</summary>
    public bool IsLegacy => suite.Legacy;

    /// <summary>
    /// Can be negotiated: the server's certificate can use it (<see cref="CipherSuiteOption.Usable"/>) and the minimum version
    /// allows it (TLS 1.3 is always offered; the TLS 1.2 suites need a minimum of 1.2 or lower).
    /// </summary>
    public bool IsUsable(string? minimumTls) => suite.Usable && (suite.Tls13 || (RadiusRules.MinimumIndex(minimumTls) is { } min && (suite.Legacy ? min < 2 : min <= 2)));

    /// <summary>The server's certificate cannot use it: the hint below the suite's name; null otherwise.</summary>
    public string? KeyHint => suite.Usable ? null : AdvancedViewModel.UnusableSuiteHint;

    public bool HasKeyHint => !suite.Usable;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Listed for the chosen minimum.</summary>
    [ObservableProperty]
    public partial bool IsListed { get; set; } = !suite.Legacy;

    partial void OnIsCheckedChanged(bool value) => owner.CipherSuitesEdited();
}

/// <summary>
/// The Server card's form (docs/spec/radius-server.md "What the user sees"): Listen on, Port, Log entries kept. Advanced
/// (its own dialog) keeps the TLS settings; Save sends this form with the saved Advanced settings. Errors directly below
/// the fields.
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

    /// <summary>Keeps the form, takes the saved Advanced settings (after the Advanced dialog saved).</summary>
    public void ApplySaved(RadiusSettings settings) => _saved = settings ?? throw new ArgumentNullException(nameof(settings));

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

/// <summary>What the page asks of the view: the Advanced dialog.</summary>
public interface IAdvancedDialog
{
    /// <summary>Shows the dialog until it saved (true) or was closed.</summary>
    Task<bool> ShowAdvancedAsync(AdvancedViewModel dialog);
}

/// <summary>
/// The Advanced dialog (single operation): Minimum TLS version (the hint for 1.0 and 1.1), the cipher suites (old CBC suites
/// only below TLS 1.2, Reset to default, at least one usable checked) and Largest EAP packet (500..1,400, default 1,020).
/// Save sends the saved General settings with these.
/// </summary>
public sealed partial class AdvancedViewModel : ValidatingViewModel
{
    /// <summary>The hint below "Minimum TLS version" for TLS 1.0 and 1.1.</summary>
    public const string OldTlsHint = "Only for old devices: TLS 1.0 and 1.1 are no longer considered secure.";

    /// <summary>The hint below a cipher suite the server's certificate cannot use (an RSA suite with the ECDSA key).</summary>
    public const string UnusableSuiteHint = "Not usable with the server certificate.";

    public const string Title = "Advanced";

    public static IReadOnlyList<Choice<string>> TlsVersions { get; } = [.. RadiusLimits.TlsVersions.Select(v => new Choice<string>(v, "TLS " + v))];

    private readonly ICorePluginClientContext _ctx;
    private readonly RadiusSettings _saved;
    private bool _applying = true;

    /// <param name="saved">The saved settings of the server (General is sent as saved).</param>
    /// <param name="listed">Every cipher suite of the server.</param>
    public AdvancedViewModel(ICorePluginClientContext ctx, RadiusSettings saved, IReadOnlyList<CipherSuiteOption> listed)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
        ArgumentNullException.ThrowIfNull(listed);
        MinimumTls = TlsVersions.FirstOrDefault(v => v.Value == saved.MinimumTls) ?? TlsVersions[RadiusRules.MinimumIndex(RadiusLimits.DefaultMinimumTls) ?? 2];
        LargestPacket = saved.LargestFragment.ToString("N0", CultureInfo.InvariantCulture);
        var checkedIds = saved.CipherSuites.ToHashSet();
        CipherSuites = [.. listed.Select(s => new CipherSuiteViewModel(s, this) { IsChecked = checkedIds.Contains(s.Id) })];
        UpdateListed(lowered: false);
        _applying = false;
        Validation
            .Rule(nameof(LargestPacket), () => SettingsViewModel.ParseInt(LargestPacket) is >= RadiusLimits.MinFragment and <= RadiusLimits.MaxFragment
                ? null : RadiusRules.FragmentRange)
            .Rule(nameof(CipherSuites), () => CipherSuites.Any(s => s.IsListed && s.IsChecked && s.IsUsable(MinimumTls.Value)) ? null : RadiusRules.CheckCipherSuite);
        Validation.Validate();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TlsHint))]
    public partial Choice<string> MinimumTls { get; set; }

    /// <summary>Bytes (500..1,400).</summary>
    [ObservableProperty]
    public partial string LargestPacket { get; set; }

    /// <summary>The cipher suites the server lists, in its order; only <see cref="CipherSuiteViewModel.IsListed"/> ones show (at most 13).</summary>
    public IReadOnlyList<CipherSuiteViewModel> CipherSuites { get; }

    /// <summary>The error of the cipher suites (a group of check boxes: shown below it by the field).</summary>
    public string? CipherSuitesError => Validation[nameof(CipherSuites)];

    /// <summary>"Only for old devices ..." for TLS 1.0 and 1.1; null otherwise.</summary>
    public string? TlsHint => IsBelowTls12(MinimumTls.Value) ? OldTlsHint : null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>The user may change the settings (else the dialog is read only and has no Save).</summary>
    public bool MayManage { get; init; } = true;

    /// <summary>The server's state after the save (null until then).</summary>
    public RadiusState? Saved { get; private set; }

    /// <summary>The dialog closes (true = saved).</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The settings of the dialog with the saved General settings.</summary>
    public RadiusSettings ToSettings() => _saved with
    {
        MinimumTls = MinimumTls.Value,
        CipherSuites = [.. CipherSuites.Where(s => s.IsListed && s.IsChecked).Select(s => s.Id)],
        LargestFragment = SettingsViewModel.ParseInt(LargestPacket) ?? RadiusLimits.DefaultFragment,
    };

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

    private bool CanSave() => MayManage && !IsBusy && IsFormValid;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validation.ShowAll();
        OnPropertyChanged(nameof(CipherSuitesError));
        if (!IsFormValid)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Saved = RadiusJson.Deserialize<RadiusState>(await _ctx.InvokeAsync(RadiusMethods.SaveSettings, RadiusJson.Serialize(ToSettings()), CancellationToken.None).ConfigureAwait(true));
            CloseRequested?.Invoke(this, true);
        }
#pragma warning disable CA1031 // Shown below the field it belongs to, else in a message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var message = ServerErrors.Message(ex);
            var field = message switch
            {
                RadiusRules.FragmentRange => nameof(LargestPacket),
                RadiusRules.ChooseTls => nameof(MinimumTls),
                _ => message.Contains("cipher", StringComparison.OrdinalIgnoreCase) ? nameof(CipherSuites) : null,
            };
            if (field is not null)
            {
                Validation.SetServerError(field, message);
                OnPropertyChanged(nameof(CipherSuitesError));
            }
            else
            {
                await _ctx.ShowMessageAsync(Title, message).ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

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

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(CipherSuitesError));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static bool IsBelowTls12(string? version) => RadiusRules.MinimumIndex(version) is < 2;
}
