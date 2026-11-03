using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Contracts.Updates;
using Oadm.Contracts.V1;

namespace Oadm.Client.Updates;

/// <summary>
/// What this client knows about a server update (docs/spec/self-update.md): the steps from <c>UpdateService.Watch</c>, the
/// server going away for the installer ("Updating the server to V4.9.0" instead of "Connection lost"), not back after 10
/// minutes, and the reconnect after it. Changed on the UI thread (<see cref="Shell.ServerConnection"/> posts the steps).
/// </summary>
public sealed partial class ServerUpdateTracker(Infrastructure.IUiDispatcher ui, TimeProvider? time = null) : ObservableObject
{
    /// <summary>After this long without the server the client says "The server did not come back".</summary>
    public static readonly TimeSpan NotBackAfter = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private ITimer? _notBack;

    /// <summary>The last step of the running update; null while none runs.</summary>
    [ObservableProperty]
    public partial UpdateProgress? Progress { get; private set; }

    /// <summary>The version the server updates to once the installer runs (the server may go away); null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AwayText))]
    public partial string? UpdatingTo { get; private set; }

    /// <summary>The server has been away for <see cref="NotBackAfter"/> during its update.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AwayText))]
    public partial bool IsNotBack { get; private set; }

    /// <summary>The banner's text while the server is away for its update; null when it is away for another reason.</summary>
    public string? AwayText => UpdatingTo is not { } to ? null
        : IsNotBack ? UpdateTexts.NotBack + ". " + UpdateTexts.NotBackHelp
        : UpdateTexts.Updating(to);

    /// <summary>A step of the running update (also from another client's Install now).</summary>
    public event EventHandler<UpdateProgress>? ProgressChanged;

    /// <summary>
    /// The installer starts and this client runs on the server's computer (Windows, macOS: the installer replaces it): it
    /// starts its helper and closes. The argument is the version.
    /// </summary>
    public event EventHandler<string>? InstallerStartsHere;

    /// <summary>The server is back after its update went away (the version it updated to); the client asks for the result.</summary>
    public event EventHandler<string>? ServerBack;

    /// <summary>The update ended while the server kept running (refused or failed before the installer stopped it).</summary>
    public event EventHandler<UpdateProgress>? EndedWithoutRestart;

    /// <summary>One message of the Watch stream.</summary>
    public void Apply(UpdateProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Finished)
        {
            Progress = null;
            UpdatingTo = null;
            StopTimer();
            IsNotBack = false;
            EndedWithoutRestart?.Invoke(this, progress);
            ProgressChanged?.Invoke(this, progress);
            return;
        }

        Progress = progress;
        ProgressChanged?.Invoke(this, progress);
        if (progress.Step == UpdateStepKind.UpdateStepInstall && UpdatingTo != progress.ToVersion)
        {
            UpdatingTo = progress.ToVersion;
            if (progress.SameComputer)
            {
                InstallerStartsHere?.Invoke(this, progress.ToVersion);
            }
        }
    }

    /// <summary>The connection to the server is gone: during an update the 10 minutes start.</summary>
    public void Disconnected()
    {
        if (UpdatingTo is null || _notBack is not null)
        {
            return;
        }

        _notBack = _time.CreateTimer(_ => ui.Post(() => IsNotBack = UpdatingTo is not null), null, NotBackAfter, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The device stream connected again: after an update the result is asked for.</summary>
    public void Connected()
    {
        StopTimer();
        if (UpdatingTo is not { } to)
        {
            return;
        }

        UpdatingTo = null;
        Progress = null;
        IsNotBack = false;
        ServerBack?.Invoke(this, to);
    }

    private void StopTimer()
    {
        _notBack?.Dispose();
        _notBack = null;
    }
}
