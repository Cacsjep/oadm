using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;
using Oadm.Sdk.Licensing;

namespace Oadm.Client.Licensing;

/// <summary>
/// The Settings page's card "License" (docs/spec/licensing.md): the server ID with a copy button, the state of the OADM PRO
/// license, Start 30-day trial (once per server), Import license and Remove license (administrators) and the link to
/// activate a bought license on oadm.app. OADM itself never depends on the license.
/// </summary>
public sealed partial class LicenseViewModel : ObservableObject
{
    /// <summary>The page on oadm.app that turns a bought license code into a license file.</summary>
    public const string ActivatePage = "https://oadm.app/activate/";

    public static readonly FileType LicenseFileType = new("OADM license", "oadmlicense", "text/plain");

    private readonly IOadmApi _api;
    private readonly IDialogService? _dialogs;
    private readonly IClipboardService? _clipboard;
    private readonly IUrlLauncher? _launcher;
    private readonly Shell.UserSession? _session;

    public LicenseViewModel(IOadmApi api, IDialogService? dialogs = null, IClipboardService? clipboard = null, IUrlLauncher? launcher = null, Shell.UserSession? session = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs;
        _clipboard = clipboard;
        _launcher = launcher;
        _session = session;
        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Shell.UserSession.IsAdmin))
                {
                    OnPropertyChanged(nameof(IsAdmin));
                    OnPropertyChanged(nameof(CanRemove));
                    OnPropertyChanged(nameof(ShowTrialButton));
                    OnPropertyChanged(nameof(ShowTrialAdminLine));
                    ImportCommand.NotifyCanExecuteChanged();
                    RemoveCommand.NotifyCanExecuteChanged();
                    StartTrialCommand.NotifyCanExecuteChanged();
                }
            };
        }
    }

    /// <summary>Administrators import and remove licenses; everyone else reads the card.</summary>
    public bool IsAdmin => _session?.IsAdmin ?? true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerId))]
    public partial string ServerId { get; private set; } = "";

    public bool HasServerId => ServerId.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLicense), nameof(IsActive), nameof(IsProblem), nameof(IsTrial), nameof(CanRemove), nameof(ShowStartTrial), nameof(ShowTrialButton),
        nameof(ShowTrialAdminLine), nameof(ShowActivate))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(StartTrialCommand))]
    public partial ProLicenseState State { get; private set; } = new() { Reason = ProLicenseTexts.NoLicense };

    /// <summary>"Up to 500 devices, until 2028-10-22", "No license", "Expired on 2028-10-22." ...</summary>
    public string StatusText => State.Reason;

    /// <summary>"Licensed to ACME GmbH" while a license is imported.</summary>
    public string CustomerText => HasLicense && State.Customer.Length > 0 ? "Licensed to " + State.Customer : "";

    public bool HasLicense => State.Status != Contracts.V1.ProLicenseStatus.ProLicenseNone;

    public bool IsActive => State.Status == Contracts.V1.ProLicenseStatus.ProLicenseActive && !State.Trial;

    public bool IsTrial => State.Status == Contracts.V1.ProLicenseStatus.ProLicenseActive && State.Trial;

    public bool IsProblem => HasLicense && State.Status != Contracts.V1.ProLicenseStatus.ProLicenseActive;

    /// <summary>Remove license while a key is imported (a server before the trial in OADM: any license but a trial).</summary>
    public bool CanRemove => IsAdmin && (State.HasKey || (HasLicense && !State.Trial));

    /// <summary>The trial row: no trial ran on this server yet and OADM PRO is not active.</summary>
    public bool ShowStartTrial => State.TrialAvailable;

    /// <summary>Start 30-day trial for administrators.</summary>
    public bool ShowTrialButton => ShowStartTrial && IsAdmin;

    /// <summary><see cref="ProLicenseTexts.TrialAdminLine"/> for everyone else.</summary>
    public bool ShowTrialAdminLine => ShowStartTrial && !IsAdmin;

    /// <summary>"Activate license" unless a bought license is active.</summary>
    public bool ShowActivate => !IsActive;

    /// <summary>A start of the trial that failed: below the trial button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrialError))]
    public partial string? TrialError { get; private set; }

    public bool HasTrialError => !string.IsNullOrEmpty(TrialError);

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(RemoveCommand), nameof(StartTrialCommand))]
    public partial bool IsBusy { get; private set; }

    public async Task LoadAsync()
    {
        try
        {
            Apply(await _api.GetLicenseAsync(CancellationToken.None).ConfigureAwait(true));
            Message = null;
        }
        catch (NotSupportedException)
        {
            // A connection without licenses: the card keeps "No license".
        }
        catch (RpcException ex)
        {
            Show(ex.Status.Detail, error: true);
        }
    }

    [RelayCommand]
    private async Task CopyServerIdAsync()
    {
        if (_clipboard is not null && HasServerId)
        {
            await _clipboard.SetTextAsync(ServerId).ConfigureAwait(true);
        }
    }

    private bool CanImport => IsAdmin && !IsBusy && _dialogs is not null;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        var file = await _dialogs!.OpenFileAsync("Import license", LicenseFileType, LicenseFile.MaxLength).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        if (file.IsTooLarge)
        {
            Show(ProLicenseTexts.NotValid, error: true);
            return;
        }

        await ImportTextAsync(Encoding.UTF8.GetString(file.Content)).ConfigureAwait(true);
    }

    /// <summary>Sends a license key to the server (Import license after the file was read; tests).</summary>
    public async Task ImportTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            IsBusy = true;
            Apply(await _api.ImportLicenseAsync(text, CancellationToken.None).ConfigureAwait(true));
            Show("License imported.", error: false);
            LicenseChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (RpcException ex)
        {
            Show(ex.Status.Detail, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRemoveNow => CanRemove && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemoveNow))]
    private async Task RemoveAsync()
    {
        if (_dialogs is not null
            && !await _dialogs.ConfirmAsync("Remove license", "OADM PRO plugins stop working on this server. OADM itself keeps working.", "Remove license").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            IsBusy = true;
            Apply(await _api.RemoveLicenseAsync(CancellationToken.None).ConfigureAwait(true));
            Message = null;
            LicenseChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (RpcException ex)
        {
            Show(ex.Status.Detail, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Raised after this client imported or removed the license or started the trial: PRO plugins may have turned on or off.
    /// </summary>
    public event EventHandler? LicenseChanged;

    /// <summary>The activation page of oadm.app with this server's ID.</summary>
    public Uri ActivateUri => new(ActivatePage + "?server=" + Uri.EscapeDataString(ServerId));

    private bool CanStartTrial => IsAdmin && !IsBusy && ShowStartTrial;

    /// <summary>Start 30-day trial on the card; a failure shows below the button.</summary>
    [RelayCommand(CanExecute = nameof(CanStartTrial))]
    private async Task StartTrialAsync()
    {
        TrialError = null;
        try
        {
            await StartTrialCoreAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            TrialError = ex.Status.Detail;
            await LoadAsync().ConfigureAwait(true); // e.g. started meanwhile from another client: the row goes away
        }
    }

    /// <summary>
    /// Starts the trial for a plugin page (the 802.1X and EST servers' off state, <c>ICorePluginClientContext.StartProTrialAsync</c>):
    /// the card shows the new state and <see cref="LicenseChanged"/> follows. Throws the server's refusal.
    /// </summary>
    public Task<ProLicenseInfo> StartTrialFromPageAsync(CancellationToken ct) => StartTrialCoreAsync(ct);

    private async Task<ProLicenseInfo> StartTrialCoreAsync(CancellationToken ct)
    {
        try
        {
            IsBusy = true;
            Message = null;
            var state = await _api.StartTrialAsync(ct).ConfigureAwait(true);
            Apply(state);
            TrialError = null;
            LicenseChanged?.Invoke(this, EventArgs.Empty);
            return ToInfo(state);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The SDK form of the server's license state (plugin pages).</summary>
    public static ProLicenseInfo ToInfo(ProLicenseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new ProLicenseInfo(
            (Oadm.Sdk.Licensing.ProLicenseStatus)(int)state.Status,
            state.Reason,
            state.Customer.Length > 0 ? state.Customer : null,
            state.Plan.Length > 0 ? state.Plan : null,
            state.HasMaxDevices ? state.MaxDevices : null,
            DateOnly.TryParseExact(state.Expires, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day)
                ? day : null,
            state.Trial) { TrialAvailable = state.TrialAvailable };
    }

    [RelayCommand]
    private Task ActivateAsync() => _launcher?.OpenAsync(ActivateUri) ?? Task.CompletedTask;

    private void Apply(ProLicenseState state)
    {
        ServerId = state.ServerId;
        State = state;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CustomerText));
    }

    private void Show(string text, bool error)
    {
        MessageIsError = error;
        Message = text;
    }
}
