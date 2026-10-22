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
/// license, Import license and Remove license (administrators) and the links to start a trial or activate a license on
/// oadm.app. OADM itself never depends on the license.
/// </summary>
public sealed partial class LicenseViewModel : ObservableObject
{
    /// <summary>The page on oadm.app that turns a license code or a trial request into a license file.</summary>
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
                    ImportCommand.NotifyCanExecuteChanged();
                    RemoveCommand.NotifyCanExecuteChanged();
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
    [NotifyPropertyChangedFor(nameof(HasLicense), nameof(IsActive), nameof(IsProblem), nameof(IsTrial), nameof(CanRemove), nameof(ShowStartTrial), nameof(ShowActivate), nameof(ShowLinks))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial ProLicenseState State { get; private set; } = new() { Reason = ProLicenseTexts.NoLicense };

    /// <summary>"Up to 500 devices, until 2028-10-22", "No license", "Expired on 2028-10-22." ...</summary>
    public string StatusText => State.Reason;

    /// <summary>"Licensed to ACME GmbH" while a license is imported.</summary>
    public string CustomerText => HasLicense && State.Customer.Length > 0 ? "Licensed to " + State.Customer : "";

    public bool HasLicense => State.Status != Contracts.V1.ProLicenseStatus.ProLicenseNone;

    public bool IsActive => State.Status == Contracts.V1.ProLicenseStatus.ProLicenseActive && !State.Trial;

    public bool IsTrial => State.Status == Contracts.V1.ProLicenseStatus.ProLicenseActive && State.Trial;

    public bool IsProblem => HasLicense && State.Status != Contracts.V1.ProLicenseStatus.ProLicenseActive;

    public bool CanRemove => IsAdmin && HasLicense;

    /// <summary>"Start 30-day trial" while the server has no license.</summary>
    public bool ShowStartTrial => !HasLicense;

    /// <summary>"Activate license" unless a bought license is active.</summary>
    public bool ShowActivate => !IsActive;

    public bool ShowLinks => ShowStartTrial || ShowActivate;

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(RemoveCommand))]
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

    /// <summary>Raised after this client imported or removed the license: PRO plugins may have turned on or off.</summary>
    public event EventHandler? LicenseChanged;

    /// <summary>The trial page of oadm.app with this server's ID.</summary>
    public Uri StartTrialUri => new(ActivatePage + "?server=" + Uri.EscapeDataString(ServerId) + "&trial=1");

    /// <summary>The activation page of oadm.app with this server's ID.</summary>
    public Uri ActivateUri => new(ActivatePage + "?server=" + Uri.EscapeDataString(ServerId));

    [RelayCommand]
    private Task StartTrialAsync() => _launcher?.OpenAsync(StartTrialUri) ?? Task.CompletedTask;

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
