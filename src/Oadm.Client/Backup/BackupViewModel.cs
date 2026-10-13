using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Backup;

/// <summary>One backup kept on the server.</summary>
public sealed record ServerBackupRow(ServerBackup Backup)
{
    public string Name => Backup.Name;

    public string CreatedText => Backup.Created?.ToDateTimeOffset().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";

    public string KindText => Backup.Kind == "before-restore" ? "Before a restore" : "Automatic";

    public string SizeText => FileSizeText.Format(Backup.Size);
}

/// <summary>
/// The Settings page's card "Backup and restore" (administrators): Back up now (password, then the file is saved on this
/// computer), Restore from a file or from a backup the server keeps, the automatic backups (on, time, how many kept, their
/// password) and the report of the last restore.
/// </summary>
public sealed partial class BackupViewModel : ValidatingViewModel
{
    public static readonly FileType BackupFileType = new("OADM backup", "oadmbackup", "application/octet-stream");

    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private bool _hasPassword;

    public BackupViewModel(IOadmApi api, IDialogService dialogs)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Validation.Rule(nameof(TimeOfDay), () => TimeOnly.TryParseExact(TimeOfDay, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "Enter a time like 02:00.");
        Validation.Rule(nameof(SchedulePassword), () =>
            string.IsNullOrEmpty(SchedulePassword)
                ? (ScheduleEnabled && !_hasPassword ? "Enter a password for the automatic backups." : null)
                : BackupPasswordViewModel.PasswordProblem(SchedulePassword));
        Validation.Rule(nameof(ScheduleConfirmPassword), () => ScheduleConfirmPassword == SchedulePassword ? null : "The passwords do not match.");
        Validation.Validate();
    }

    public RangeObservableCollection<ServerBackupRow> Backups { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveScheduleCommand))]
    public partial bool ScheduleEnabled { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveScheduleCommand))]
    public partial string TimeOfDay { get; set; } = "02:00";

    [ObservableProperty]
    public partial int Keep { get; set; } = 7;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveScheduleCommand))]
    public partial string SchedulePassword { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveScheduleCommand))]
    public partial string ScheduleConfirmPassword { get; set; } = "";

    /// <summary>"Unchanged" while the server has a password (the field stays empty).</summary>
    [ObservableProperty]
    public partial string PasswordPlaceholder { get; private set; } = "";

    /// <summary>"Kept in C:\ProgramData\OADM\backups on the server."</summary>
    [ObservableProperty]
    public partial string? FolderText { get; private set; }

    /// <summary>"Last automatic backup: 2026-10-13 02:00" or its error.</summary>
    [ObservableProperty]
    public partial string? LastAutomaticText { get; private set; }

    [ObservableProperty]
    public partial bool LastAutomaticFailed { get; private set; }

    /// <summary>"Last restore: site-a.oadmbackup on 2026-10-13 14:02 by admin@PC-1".</summary>
    [ObservableProperty]
    public partial string? LastRestoreText { get; private set; }

    [ObservableProperty]
    public partial bool LastRestoreFailed { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> LastRestoreLines { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand), nameof(RestoreFromFileCommand), nameof(RestoreCommand), nameof(DownloadCommand), nameof(DeleteCommand), nameof(SaveScheduleCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial double Progress { get; private set; }

    /// <summary>What runs now ("Saving the backup: 12.0 of 18.7 MB"), or the outcome of the last action.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    public bool HasBackups => Backups.Count > 0;

    /// <summary>The outcome line under the buttons (not while the progress row shows).</summary>
    public bool ShowStatus => !IsBusy && !string.IsNullOrEmpty(Status);

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowStatus));

    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(ShowStatus));

    public bool HasLastRestore => LastRestoreText is not null;

    public int ActiveTasks { get; private set; }

    public string? SaveScheduleBlockedReason => IsBusy ? null : Validation.FirstErrorOf([nameof(TimeOfDay), nameof(SchedulePassword), nameof(ScheduleConfirmPassword)]);

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(SaveScheduleBlockedReason));
        SaveScheduleCommand.NotifyCanExecuteChanged();
    }

    partial void OnScheduleEnabledChanged(bool value) => Validation.Validate();

    partial void OnSchedulePasswordChanged(string value) => Validation.Validate();

    /// <summary>Reads the backups, the schedule and the last restore (the Settings page opens or Refresh).</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            Apply(await _api.GetBackupStateAsync(CancellationToken.None).ConfigureAwait(true), resetForm: true);
        }
        catch (Exception ex) when (ex is RpcException or IOException or HttpRequestException or NotSupportedException)
        {
            Show(ServerErrors.Message(ex), error: true);
        }
    }

    private void Apply(BackupState state, bool resetForm)
    {
        ActiveTasks = state.ActiveTasks;
        _hasPassword = state.HasPassword;
        PasswordPlaceholder = state.HasPassword ? "Unchanged" : "";
        if (resetForm)
        {
            ScheduleEnabled = state.Schedule?.Enabled ?? false;
            TimeOfDay = string.IsNullOrEmpty(state.Schedule?.TimeOfDay) ? "02:00" : state.Schedule.TimeOfDay;
            Keep = state.Schedule?.Keep is > 0 ? state.Schedule.Keep : 7;
            SchedulePassword = "";
            ScheduleConfirmPassword = "";
            Validation.Reset();
        }

        FolderText = string.IsNullOrEmpty(state.Folder) ? null : $"Kept in {state.Folder} on the server.";
        Backups.ReplaceAll(state.Backups.Select(b => new ServerBackupRow(b)));
        OnPropertyChanged(nameof(HasBackups));
        LastAutomaticFailed = !string.IsNullOrEmpty(state.LastAutomaticError);
        LastAutomaticText = state.LastAutomatic is null
            ? null
            : LastAutomaticFailed
                ? $"The last automatic backup failed: {state.LastAutomaticError}"
                : "Last automatic backup: " + Local(state.LastAutomatic);
        var restore = state.LastRestore;
        if (restore is null || string.IsNullOrEmpty(restore.Source))
        {
            LastRestoreText = null;
            LastRestoreLines = [];
        }
        else
        {
            LastRestoreFailed = !string.IsNullOrEmpty(restore.Error);
            LastRestoreText = LastRestoreFailed
                ? $"The restore of {restore.Source} on {Local(restore.Applied)} failed; the server kept its data: {restore.Error}"
                : $"Last restore: {restore.Source} (OADM {restore.FromVersion}) on {Local(restore.Applied)} by {restore.RequestedBy}";
            LastRestoreLines = [.. restore.Lines];
        }

        OnPropertyChanged(nameof(HasLastRestore));
        Validation.Validate();
    }

    private static string Local(Google.Protobuf.WellKnownTypes.Timestamp? time) =>
        time?.ToDateTimeOffset().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";

    private void Show(string? text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    private bool CanAct() => !IsBusy;

    /// <summary>Back up now: the password, where to save it, then the server makes it and the client saves it.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task BackUpNowAsync()
    {
        var password = new BackupPasswordViewModel();
        if (!await _dialogs.ShowBackupPasswordAsync(password).ConfigureAwait(true))
        {
            return;
        }

        var file = await _dialogs.CreateFileAsync("Save backup", $"oadm-backup-{DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.oadmbackup", BackupFileType).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        await SaveAsync(file, "Making the backup", () => _api.CreateBackupAsync(password.Password, CancellationToken.None)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DownloadAsync(ServerBackupRow? row)
    {
        if (row is null)
        {
            return;
        }

        var file = await _dialogs.CreateFileAsync("Save backup", row.Name, BackupFileType).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        await SaveAsync(file, "Downloading the backup", () => _api.DownloadBackupAsync(row.Name, CancellationToken.None)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DeleteAsync(ServerBackupRow? row)
    {
        if (row is null || !await _dialogs.ConfirmAsync("Delete backup", $"Delete the backup {row.Name} from the server?", "Delete").ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(null, async () =>
        {
            Apply(await _api.DeleteBackupAsync(row.Name, CancellationToken.None).ConfigureAwait(true), resetForm: false);
            Show(null);
        }).ConfigureAwait(true);
    }

    /// <summary>Restore from a file of this computer: uploaded to the server first, then the restore dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RestoreFromFileAsync()
    {
        var path = await _dialogs.PickFilePathAsync("Choose a backup", BackupFileType).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        string? fileId = null;
        await RunAsync("Uploading the backup", async () =>
        {
            var progress = new Progress<double>(v => Progress = Math.Clamp(v * 100, 0, 100));
            fileId = (await _api.UploadFileAsync(path, progress, CancellationToken.None).ConfigureAwait(true)).Id;
            Show(null);
        }).ConfigureAwait(true);
        if (fileId is not null)
        {
            await ShowRestoreAsync(new RestoreRequest { FileId = fileId }, Path.GetFileName(path)).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RestoreAsync(ServerBackupRow? row) =>
        row is null ? Task.CompletedTask : ShowRestoreAsync(new RestoreRequest { Name = row.Name }, row.Name);

    private async Task ShowRestoreAsync(RestoreRequest source, string sourceText)
    {
        var restore = new RestoreViewModel(_api, _dialogs, source, sourceText, ActiveTasks);
        if (await _dialogs.ShowRestoreAsync(restore).ConfigureAwait(true))
        {
            Show(restore.ResultMessage);
        }
        else if (source.SourceCase == RestoreRequest.SourceOneofCase.FileId)
        {
            // The uploaded backup is not needed any more (it holds the server's key, encrypted).
            try
            {
                await _api.DeleteUploadAsync(source.FileId, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is RpcException or IOException or HttpRequestException)
            {
                // Removed by the upload retention later.
            }
        }
    }

    private bool CanSaveSchedule() => !IsBusy && Validation.IsValid;

    [RelayCommand(CanExecute = nameof(CanSaveSchedule))]
    private async Task SaveScheduleAsync()
    {
        Validation.ShowAll();
        if (!Validation.IsValid)
        {
            return;
        }

        await RunAsync(null, async () =>
        {
            var schedule = new BackupSchedule { Enabled = ScheduleEnabled, TimeOfDay = TimeOfDay, Keep = Keep, Password = SchedulePassword };
            Apply(await _api.SetBackupScheduleAsync(schedule, CancellationToken.None).ConfigureAwait(true), resetForm: true);
            Show("Automatic backups saved.");
        }).ConfigureAwait(true);
    }

    /// <summary>Writes a backup from the server into the chosen file; a failure removes the cut file.</summary>
    private async Task SaveAsync(SavedFile file, string what, Func<Task<Oadm.Sdk.Plugins.PluginDownload>> open)
    {
        var saved = false;
        try
        {
            await RunAsync(what, async () =>
            {
                var download = await open().ConfigureAwait(true);
                await CopyAsync(download, file.Content, "Saving the backup").ConfigureAwait(true);
                saved = true;
                Show($"Backup saved as {file.Name}.");
            }).ConfigureAwait(true);
        }
        finally
        {
            if (saved)
            {
                await file.Content.DisposeAsync().ConfigureAwait(true);
            }
            else if (file.Delete is { } delete)
            {
                try
                {
                    await delete().ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The message already says the save failed.
                }
            }
            else
            {
                await file.Content.DisposeAsync().ConfigureAwait(true);
            }
        }
    }

    private async Task CopyAsync(Oadm.Sdk.Plugins.PluginDownload download, Stream target, string what)
    {
        await using (download.ConfigureAwait(true))
        {
            var buffer = new byte[256 * 1024];
            long copied = 0;
            int read;
            while ((read = await download.Content.ReadAsync(buffer).ConfigureAwait(true)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(true);
                copied += read;
                if (download.Length is > 0 and var total)
                {
                    Progress = Math.Min(100, 100.0 * copied / total);
                    Show($"{what}: {FileSizeText.Format(copied)} of {FileSizeText.Format(total)}");
                }
            }
        }
    }

    private async Task RunAsync(string? what, Func<Task> action)
    {
        IsBusy = true;
        Progress = 0;
        Show(what);
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is RpcException or IOException or HttpRequestException or UnauthorizedAccessException)
        {
            Show(ServerErrors.Message(ex), error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
