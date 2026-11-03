using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Contracts.Updates;
using Oadm.Contracts.V1;

namespace Oadm.Client.Updates;

/// <summary>What the new version dialog offers (docs/spec/self-update.md).</summary>
public enum UpdateOffer
{
    /// <summary>Open download page only (not an Administrator, not installed, a server without self update).</summary>
    DownloadPage,

    /// <summary>Install now: the server (and a client on its computer) updates itself.</summary>
    Server,

    /// <summary>Update client: this client to the server's version.</summary>
    Client,
}

/// <summary>Where the dialog is: the offer, the steps, the server away for its installer, the result.</summary>
public enum UpdatePhase
{
    Offer,
    Working,
    Away,
    Result,
}

/// <summary>One step of the list: Download the update, Check the download, Install the update.</summary>
public sealed partial class UpdateStepRow(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsCurrent), nameof(IsDone), nameof(IsFailed))]
    public partial UpdateStepState State { get; set; }

    /// <summary>"42 %" while the download runs.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    public bool IsPending => State == UpdateStepState.Pending;

    public bool IsCurrent => State == UpdateStepState.Current;

    public bool IsDone => State == UpdateStepState.Done;

    public bool IsFailed => State == UpdateStepState.Failed;
}

public enum UpdateStepState
{
    Pending,
    Current,
    Done,
    Failed,
}

/// <summary>Runs what the dialog's main button starts (the notifier wires it to the server or the client updater).</summary>
public interface IUpdateActions
{
    /// <summary>Install now: the server's update (its steps arrive through <see cref="UpdateViewModel.ApplyServerProgress"/>).</summary>
    Task StartServerAsync(string version, CancellationToken ct);

    /// <summary>Update client: download, check, then the helper installs and this client closes.</summary>
    Task UpdateClientAsync(string version, Action<UpdateStep, int> progress, CancellationToken ct);
}

/// <summary>
/// "OADM V4.6.0 is available": the version this client has, the release notes behind What's new, "Don't tell me about new
/// versions", Later and Open download page (the download page of the website). For an Administrator of an installed server
/// (docs/spec/self-update.md) the main button is Install now (or Update client) and the dialog turns into the steps, the
/// server away for its installer and the result.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    public const string DownloadPage = UpdateTexts.DownloadPage;

    private readonly IUpdateActions? _actions;
    private readonly string _version;

    public UpdateViewModel(ReleaseInfo release, string currentVersion, UpdateOffer offer = UpdateOffer.DownloadPage, IUpdateActions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        _actions = actions;
        _version = UpdateVersions.Plain(release.Version);
        Offer = actions is null ? UpdateOffer.DownloadPage : offer;
        Title = $"OADM {release.VersionText} is available";
        Current = "You have " + (ClientVersion.Parse(currentVersion) is { } v ? ClientVersion.Format(v) : currentVersion);
        Sections = ReleaseNotes.Parse(release.Notes);
        int changes = Sections.Sum(s => s.Items.Count);
        ChangeCount = changes == 1 ? "1 change" : string.Create(CultureInfo.InvariantCulture, $"{changes} changes");
        Steps = [new UpdateStepRow(UpdateTexts.StepDownload), new UpdateStepRow(UpdateTexts.StepCheck), new UpdateStepRow(UpdateTexts.StepInstall)];
    }

    /// <summary>A dialog that only shows a result (after a restart by the helper, or another client's update).</summary>
    public static UpdateViewModel ForResult(UpdateOutcome outcome, bool offersDownloadPage = false)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var vm = new UpdateViewModel(new ReleaseInfo(new Version(0, 0, 0), string.Empty, string.Empty), "0.0.0");
        vm.ShowResult(outcome, offersDownloadPage);
        return vm;
    }

    public UpdateOffer Offer { get; }

    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>"You have V4.5.0"; in a result the first line.</summary>
    [ObservableProperty]
    public partial string Current { get; private set; }

    public IReadOnlyList<ReleaseNoteSection> Sections { get; }

    public bool HasNotes => Sections.Count > 0 && IsOffer;

    /// <summary>"4 changes", right of What's new.</summary>
    public string ChangeCount { get; }

    [ObservableProperty]
    public partial bool IsNotesOpen { get; set; }

    /// <summary>"Don't tell me about new versions": saved when the window closes.</summary>
    [ObservableProperty]
    public partial bool DontTellAgain { get; set; }

    /// <summary>Open download page was clicked.</summary>
    public bool OpenDownloadPage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffer), nameof(IsWorking), nameof(IsResult), nameof(ShowsSteps), nameof(HasNotes), nameof(ShowsInstall),
        nameof(ShowsDownloadPrimary), nameof(ShowsDownloadSecondary), nameof(CloseText), nameof(ShowsDontTell))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial UpdatePhase Phase { get; private set; } = UpdatePhase.Offer;

    public bool IsOffer => Phase == UpdatePhase.Offer;

    public bool IsWorking => Phase is UpdatePhase.Working or UpdatePhase.Away;

    public bool IsResult => Phase == UpdatePhase.Result;

    public bool ShowsSteps => Phase is UpdatePhase.Working or UpdatePhase.Away;

    public bool ShowsDontTell => IsOffer;

    public ObservableCollection<UpdateStepRow> Steps { get; }

    /// <summary>Install now or Update client is offered (filled accent; Open download page becomes secondary).</summary>
    public bool ShowsInstall => Offer != UpdateOffer.DownloadPage && IsOffer;

    public string InstallText => Offer == UpdateOffer.Client ? "Update client" : "Install now";

    public bool ShowsDownloadPrimary => Offer == UpdateOffer.DownloadPage && IsOffer;

    /// <summary>Open download page as the secondary button: beside Install now, and after a problem before the installer.</summary>
    public bool ShowsDownloadSecondary => (Offer != UpdateOffer.DownloadPage && IsOffer) || (IsResult && OffersDownloadPage);

    /// <summary>The result offers Open download page (nothing was installed: install it by hand).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDownloadSecondary))]
    public partial bool OffersDownloadPage { get; private set; }

    public string CloseText => IsOffer ? "Later" : "Close";

    /// <summary>Running tasks of the server: Install now waits for 0 (rule 6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallTip))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial int ActiveTasks { get; set; }

    /// <summary>"3 tasks are running" while Install now is disabled.</summary>
    public string? InstallTip => Offer == UpdateOffer.Server && ActiveTasks > 0 ? UpdateTexts.TasksRunning(ActiveTasks) : null;

    /// <summary>Why the update stopped before the installer (nothing changed); Open download page stays.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    public partial bool ResultOk { get; private set; }

    /// <summary>The result's lines: the reason, the installer log, the database copy, the site node hint.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> ResultLines { get; private set; } = [];

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Later() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenDownload()
    {
        OpenDownloadPage = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanInstall() => IsOffer && _actions is not null && !(Offer == UpdateOffer.Server && ActiveTasks > 0);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        Problem = null;
        Phase = UpdatePhase.Working;
        SetStep(UpdateStep.Download, 0);
        try
        {
            if (Offer == UpdateOffer.Server)
            {
                await _actions!.StartServerAsync(_version, CancellationToken.None).ConfigureAwait(true);
                ServerAway(_version);
            }
            else
            {
                // The download reports from other threads: the steps change on the dialog's thread only.
                var context = SynchronizationContext.Current;
                void Report(UpdateStep step, int percent)
                {
                    if (context is null)
                    {
                        SetStep(step, percent);
                    }
                    else
                    {
                        context.Post(_ => SetStep(step, percent), null);
                    }
                }

                await _actions!.UpdateClientAsync(_version, Report, CancellationToken.None).ConfigureAwait(true);
                SetStep(UpdateStep.Install, 0);
            }
        }
        catch (UpdateRefusedException ex)
        {
            Fail(ex.Message);
        }
        catch (Grpc.Core.RpcException ex)
        {
            Fail(ex.StatusCode is Grpc.Core.StatusCode.FailedPrecondition or Grpc.Core.StatusCode.PermissionDenied ? ex.Status.Detail : UpdateTexts.DownloadFailed);
        }
#pragma warning disable CA1031 // Whatever else goes wrong (disk, rights, starting the helper) is shown, never a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Fail(ex is System.ComponentModel.Win32Exception or InvalidOperationException ? UpdateTexts.HelperFailed : UpdateTexts.DownloadFailed);
        }
    }

    /// <summary>A step of the server's update (Watch); the install step means the server goes away now.</summary>
    public void ApplyServerProgress(UpdateProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Phase is UpdatePhase.Result or UpdatePhase.Offer)
        {
            return;
        }

        if (progress.Finished)
        {
            if (progress.Failed.Length > 0 && Phase == UpdatePhase.Away)
            {
                Fail(progress.Failed);
            }

            return;
        }

        var step = progress.Step switch
        {
            UpdateStepKind.UpdateStepDownload => UpdateStep.Download,
            UpdateStepKind.UpdateStepCheck => UpdateStep.Check,
            _ => UpdateStep.Install,
        };
        SetStep(step, progress.Percent);
        if (step == UpdateStep.Install)
        {
            ServerAway(progress.ToVersion);
        }
    }

    /// <summary>The installer runs: "Updating the server to V4.9.0" until the server is back.</summary>
    public void ServerAway(string version)
    {
        SetStep(UpdateStep.Install, 0);
        Title = UpdateTexts.Updating(version);
        Phase = UpdatePhase.Away;
    }

    /// <summary>The result after the reconnect (or "The server did not come back").</summary>
    public void ShowResult(UpdateOutcome outcome, bool offersDownloadPage = false)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        OffersDownloadPage = offersDownloadPage;
        Title = outcome.Title;
        Current = string.Empty;
        ResultOk = outcome.IsDone;
        ResultLines = outcome.Lines;
        foreach (var row in Steps)
        {
            row.Detail = string.Empty;
            row.State = outcome.IsDone ? UpdateStepState.Done : row.State == UpdateStepState.Current ? UpdateStepState.Failed : row.State;
        }

        Phase = UpdatePhase.Result;
    }

    private void Fail(string reason)
    {
        foreach (var row in Steps.Where(r => r.IsCurrent))
        {
            row.State = UpdateStepState.Failed;
            row.Detail = string.Empty;
        }

        Problem = reason;
        OffersDownloadPage = true;
        Title = UpdateTexts.ClientFailed;
        Current = string.Empty;
        ResultOk = false;
        ResultLines = [reason];
        Phase = UpdatePhase.Result;
    }

    private void SetStep(UpdateStep step, int percent)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            var row = Steps[i];
            row.State = i < (int)step ? UpdateStepState.Done : i == (int)step ? UpdateStepState.Current : UpdateStepState.Pending;
            row.Detail = i == (int)step && step == UpdateStep.Download && percent > 0 ? string.Create(CultureInfo.CurrentCulture, $"{percent} %") : string.Empty;
        }
    }
}
