using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Backup;

/// <summary>
/// The restore dialog: the backup (an uploaded file or one kept on the server), its password, Check (the server reads the
/// whole backup and says what it holds, nothing changes), then Restore after a confirmation. The server makes a backup of
/// its current state first, cancels running tasks and restarts.
/// </summary>
public sealed partial class RestoreViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private readonly RestoreRequest _source;
    private readonly int _activeTasks;

    /// <param name="source">The backup: <see cref="RestoreRequest.FileId"/> or <see cref="RestoreRequest.Name"/> (password empty).</param>
    /// <param name="sourceText">"site-a.oadmbackup" (the file) or the server backup's name.</param>
    /// <param name="activeTasks">Running or queued tasks the restore cancels.</param>
    public RestoreViewModel(IOadmApi api, IDialogService dialogs, RestoreRequest source, string sourceText, int activeTasks)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        SourceText = sourceText;
        _activeTasks = activeTasks;
        Validation.Rule(nameof(Password), () => string.IsNullOrEmpty(Password) ? "Enter the password of the backup." : null);
        Validation.Validate();
    }

    public string SourceText { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(RestoreCommand))]
    public partial string Password { get; set; } = "";

    /// <summary>What the checked backup holds ("Devices: 24", ...); empty before Check.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChecked))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial IReadOnlyList<string> Lines { get; private set; } = [];

    /// <summary>"Backup of site-a from 2026-10-13 02:00, OADM 2.6.0".</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(RestoreCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public bool IsChecked => Lines.Count > 0;

    /// <summary>What the restore does, below the summary.</summary>
    public string Consequences => _activeTasks > 0
        ? string.Create(CultureInfo.InvariantCulture, $"Restore replaces everything on this server. The current state is saved on the server first. {_activeTasks:N0} running {(_activeTasks == 1 ? "task is" : "tasks are")} cancelled. Everyone logs in again.")
        : "Restore replaces everything on this server. The current state is saved on the server first. Everyone logs in again.";

    /// <summary>The server's answer after Restore (the dialog closes with it).</summary>
    public string? ResultMessage { get; private set; }

    public string? CheckBlockedReason => IsBusy ? null : Validation.FirstErrorOf([nameof(Password)]);

    public string? RestoreBlockedReason => IsBusy ? null : !IsChecked ? "Check the backup first." : Validation.FirstErrorOf([nameof(Password)]);

    /// <summary>True: restored (the server restarts); false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    partial void OnPasswordChanged(string value)
    {
        // Another password must be checked again.
        Lines = [];
        Summary = null;
        OnPropertyChanged(nameof(RestoreBlockedReason));
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(CheckBlockedReason));
        OnPropertyChanged(nameof(RestoreBlockedReason));
        CheckCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private RestoreRequest Request()
    {
        var request = _source.Clone();
        request.Password = Password;
        return request;
    }

    private bool CanCheck() => !IsBusy && !string.IsNullOrEmpty(Password);

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        Validation.ShowAll();
        IsBusy = true;
        try
        {
            var info = await _api.InspectBackupAsync(Request(), CancellationToken.None).ConfigureAwait(true);
            if (info.WrongPassword)
            {
                Validation.SetServerError(nameof(Password), "The password is wrong.");
            }
            else if (!string.IsNullOrEmpty(info.Problem))
            {
                Validation.SetServerError(nameof(Password), info.Problem);
            }
            else
            {
                var created = info.Created?.ToDateTimeOffset().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                Summary = $"Backup of {info.ServerName} from {created}, OADM {info.OadmVersion}";
                Lines = [.. info.Lines];
            }
        }
        catch (Exception ex) when (ex is RpcException or IOException or HttpRequestException)
        {
            Validation.SetServerError(nameof(Password), ServerErrors.Message(ex));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(RestoreBlockedReason));
        }
    }

    private bool CanRestore() => !IsBusy && IsChecked && !string.IsNullOrEmpty(Password);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        if (!await _dialogs.ConfirmAsync("Restore backup", $"{Summary}.\n\n{Consequences}", "Restore").ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var reply = await _api.RestoreBackupAsync(Request(), CancellationToken.None).ConfigureAwait(true);
            ResultMessage = reply.Message;
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is RpcException or IOException or HttpRequestException)
        {
            Validation.SetServerError(nameof(Password), ServerErrors.Message(ex));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
