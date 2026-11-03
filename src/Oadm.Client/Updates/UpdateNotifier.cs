using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.Updates;
using Oadm.Contracts.V1;

namespace Oadm.Client.Updates;

/// <summary>
/// Once per client start, after the main window opened: asks for the newest release (2 s at most) and shows the new version
/// dialog when it is newer than this client. Off with "Don't tell me about new versions" (Settings, Client card turns it on
/// again); never in a development build. For an Administrator (docs/spec/self-update.md) the dialog offers Install now
/// when the installed server is older than the release, or Update client when the server is newer than this installed
/// client; it follows the server's update to its result (also one another Administrator started), closes this client for
/// the installer on the server's computer and shows the result the helper restarted it with.
/// </summary>
public sealed partial class UpdateNotifier(
    IReleaseCheck check, IClientSettingsStore settings, IDialogService dialogs, IUrlLauncher urls, ILogger<UpdateNotifier> logger,
    AppOptions? options = null, IOadmApi? api = null, Shell.UserSession? session = null, ServerUpdateTracker? tracker = null,
    ClientUpdater? client = null, Tasks.TaskStore? tasks = null, TimeProvider? time = null) : IUpdateActions
{
    /// <summary>How often and how long the client asks for a result the server is still waiting for.</summary>
    public static readonly TimeSpan ResultPoll = TimeSpan.FromSeconds(10);

    public const int ResultAttempts = 30;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _done;
    private bool _hooked;
    private UpdateViewModel? _open;

    /// <summary>This client's version, or the one of <c>--as-version</c> / <c>OADM_CLIENT_VERSION</c>; tests set another.</summary>
    internal string CurrentVersion { get; init; } = options?.AsVersion ?? ClientVersion.Text;

    /// <summary>The dialog open now (tests).</summary>
    internal UpdateViewModel? Open => _open;

    /// <summary>Never throws: the check is started without waiting for it.</summary>
    public async Task ShowIfNewerAsync(CancellationToken ct)
    {
        Hook();
        try
        {
            await CheckAsync(ct).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A failed check never disturbs the client.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCheckFailed(logger, ex.Message);
        }
    }

    private bool IsAdmin => session?.IsAdmin ?? false;

    private void Hook()
    {
        if (_hooked)
        {
            return;
        }

        _hooked = true;
        try
        {
            client?.CleanUp(); // the helper copies of an earlier update
        }
        catch (IOException)
        {
        }

        if (tracker is null)
        {
            return;
        }

        tracker.ProgressChanged += (_, progress) => _open?.ApplyServerProgress(progress);
        tracker.InstallerStartsHere += (_, version) =>
        {
            // Windows and macOS: the server's installer holds this client too; it closes and its helper starts the new one.
            if (client is { UpdatesWithServer: true } && api is not null)
            {
                LogClosingForServer(logger, version);
                try
                {
                    client.CloseForServer(version, api.ServerAddress);
                }
#pragma warning disable CA1031 // A helper that cannot start leaves this client open (the installer may ask to close it).
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogHelperFailed(logger, ex.Message);
                }
            }
        };
        tracker.ServerBack += (_, version) => _ = ShowServerResultAsync(version, CancellationToken.None);
        tracker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerUpdateTracker.IsNotBack) && tracker.IsNotBack)
            {
                _open?.ShowResult(UpdateOutcome.NotBack());
            }
        };
        if (tasks is not null)
        {
            tasks.Changed += (_, _) =>
            {
                if (_open is { Offer: UpdateOffer.Server } vm)
                {
                    vm.ActiveTasks = tasks.ActiveCount;
                }
            };
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        if (options?.Updated is { } updated)
        {
            await ShowUpdatedAsync(updated, ct).ConfigureAwait(true);
        }

        if (!settings.Current.TellAboutNewVersions)
        {
            LogSkipped(logger, "turned off (Settings, Client card)");
            return;
        }

        if (ClientVersion.IsDevelopmentBuild(CurrentVersion))
        {
            LogDevelopmentBuild(logger, CurrentVersion);
            return;
        }

        ReleaseInfo? latest = await check.GetLatestAsync(ct).ConfigureAwait(true);
        UpdateInfo? server = await ServerInfoAsync(ct).ConfigureAwait(true);
        UpdateViewModel? vm = Offer(latest, server);
        if (vm is null)
        {
            if (latest is null)
            {
                LogSkipped(logger, "no answer from GitHub within 2 s");
            }
            else
            {
                LogNotNewer(logger, latest.VersionText, CurrentVersion);
            }

            return;
        }

        LogNewVersion(logger, vm.Title, CurrentVersion);
        await ShowAsync(vm).ConfigureAwait(true);
    }

    /// <summary>The dialog this start shows; null when nothing is newer.</summary>
    internal UpdateViewModel? Offer(ReleaseInfo? latest, UpdateInfo? server)
    {
        var current = ClientVersion.Parse(CurrentVersion);
        if (server is { Installed: true } && latest is not null && server.Running is null
            && !UpdateVersions.IsDevelopmentBuild(server.ServerVersion) && UpdateVersions.IsOlder(server.ServerVersion, UpdateVersions.Plain(latest.Version)))
        {
            // Install now: the server (and a client on its computer) to the latest release.
            return new UpdateViewModel(latest, CurrentVersion, UpdateOffer.Server, this) { ActiveTasks = tasks?.ActiveCount ?? server.ActiveTasks };
        }

        if (server is not null && client is { CanUpdateClient: true } && !(server.SameComputer && client.UpdatesWithServer)
            && !UpdateVersions.IsDevelopmentBuild(server.ServerVersion) && UpdateVersions.IsOlder(CurrentVersion, server.ServerVersion)
            && UpdateVersions.ParseStable(server.ServerVersion) is { } serverVersion)
        {
            // Update client: this client to the server's version.
            var notes = latest is not null && latest.Version == serverVersion ? latest.Notes : string.Empty;
            return new UpdateViewModel(new ReleaseInfo(serverVersion, ClientVersion.Format(serverVersion), notes), CurrentVersion, UpdateOffer.Client, this);
        }

        return latest is not null && current is not null && latest.Version > current ? new UpdateViewModel(latest, CurrentVersion) : null;
    }

    /// <summary>UpdateService.Get for an Administrator; null for other users and servers without self update.</summary>
    private async Task<UpdateInfo?> ServerInfoAsync(CancellationToken ct)
    {
        if (api is null || !IsAdmin)
        {
            return null;
        }

        try
        {
            return await api.GetUpdateAsync(ct).ConfigureAwait(true);
        }
        catch (Grpc.Core.RpcException ex)
        {
            LogNoServerInfo(logger, ex.Status.Detail);
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private async Task ShowAsync(UpdateViewModel vm)
    {
        _open = vm;
        try
        {
            await dialogs.ShowUpdateAsync(vm).ConfigureAwait(true);
        }
        finally
        {
            _open = null;
        }

        if (vm.DontTellAgain)
        {
            settings.Current.TellAboutNewVersions = false;
            settings.Save();
        }

        if (vm.OpenDownloadPage)
        {
            await urls.OpenAsync(new Uri(UpdateViewModel.DownloadPage)).ConfigureAwait(true);
        }
    }

    /// <summary>The helper restarted this client: its own update or the server's.</summary>
    private async Task ShowUpdatedAsync(UpdatedArguments updated, CancellationToken ct)
    {
        if (updated.Kind == UpdatedArguments.Client)
        {
            var outcome = UpdateOutcome.OfClient(ClientVersion.Text, updated.To, updated.Reason);
            await ShowAsync(UpdateViewModel.ForResult(outcome, offersDownloadPage: !outcome.IsDone)).ConfigureAwait(true);
            return;
        }

        await ShowServerResultAsync(updated.To, ct).ConfigureAwait(true);
    }

    /// <summary>After the reconnect: "Server updated to V4.9.0" or why it failed (Administrators; the record of the server).</summary>
    private async Task ShowServerResultAsync(string version, CancellationToken ct)
    {
        try
        {
            // The server that came back may still wait for its helper's result (an MSI that rolled back): ask a few times.
            UpdateInfo? info = null;
            for (int attempt = 0; attempt < ResultAttempts; attempt++)
            {
                info = await ServerInfoAsync(ct).ConfigureAwait(true);
                if (info?.Last is not { Result: "running" })
                {
                    break;
                }

                await Task.Delay(ResultPoll, _time, ct).ConfigureAwait(true);
            }

            if (info is not { Last: { } last } || last.ToVersion != version
                || UpdateOutcome.OfServer(last, info.ServerVersion, info.OlderSiteNodes) is not { } outcome)
            {
                return;
            }

            if (_open is { } vm)
            {
                vm.ShowResult(outcome);
                return;
            }

            await ShowAsync(UpdateViewModel.ForResult(outcome)).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The result is a notice; a failure is logged only.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCheckFailed(logger, ex.Message);
        }
    }

    // IUpdateActions: what Install now and Update client start.
    async Task IUpdateActions.StartServerAsync(string version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        LogStarting(logger, version);
        await api.StartUpdateAsync(version, ct).ConfigureAwait(true);
    }

    Task IUpdateActions.UpdateClientAsync(string version, Action<UpdateStep, int> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        LogUpdatingClient(logger, version);
        return client.UpdateAsync(version, progress, ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "New version check: nothing to show, the latest release {Latest} is not newer than {Current}")]
    private static partial void LogNotNewer(ILogger logger, string latest, string current);

    [LoggerMessage(Level = LogLevel.Information, Message = "New version check: nothing to show, development build {Current} (--as-version or OADM_CLIENT_VERSION to try it)")]
    private static partial void LogDevelopmentBuild(ILogger logger, string current);

    [LoggerMessage(Level = LogLevel.Information, Message = "New version check: nothing to show, {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "New version check failed: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Title} (this client: {Current})")]
    private static partial void LogNewVersion(ILogger logger, string title, string current);

    [LoggerMessage(Level = LogLevel.Information, Message = "New version check: the server's update state cannot be read: {Reason}")]
    private static partial void LogNoServerInfo(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Install now: the server updates to {Version}")]
    private static partial void LogStarting(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update client: this client updates to {Version}")]
    private static partial void LogUpdatingClient(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The update helper could not be started: {Reason}")]
    private static partial void LogHelperFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server's installer starts on this computer: the client closes and its helper opens OADM {Version} afterwards")]
    private static partial void LogClosingForServer(ILogger logger, string version);
}
