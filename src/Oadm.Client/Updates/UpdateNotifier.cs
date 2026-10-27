using Microsoft.Extensions.Logging;

using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;

namespace Oadm.Client.Updates;

/// <summary>
/// Once per client start, after the main window opened: asks for the newest release (2 s at most) and shows the new version
/// dialog when it is newer than this client. Off with "Don't tell me about new versions" (Settings, Client card turns it on
/// again); never in a development build.
/// </summary>
public sealed partial class UpdateNotifier(
    IReleaseCheck check, IClientSettingsStore settings, IDialogService dialogs, IUrlLauncher urls, ILogger<UpdateNotifier> logger,
    AppOptions? options = null)
{
    private bool _done;

    /// <summary>This client's version, or the one of <c>--as-version</c> / <c>OADM_CLIENT_VERSION</c>; tests set another.</summary>
    internal string CurrentVersion { get; init; } = options?.AsVersion ?? ClientVersion.Text;

    /// <summary>Never throws: the check is started without waiting for it.</summary>
    public async Task ShowIfNewerAsync(CancellationToken ct)
    {
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

    private async Task CheckAsync(CancellationToken ct)
    {
        if (_done)
        {
            return;
        }

        _done = true;
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
        if (latest is null)
        {
            LogSkipped(logger, "no answer from GitHub within 2 s");
            return;
        }

        if (ClientVersion.Parse(CurrentVersion) is not { } current || latest.Version <= current)
        {
            LogNotNewer(logger, latest.VersionText, CurrentVersion);
            return;
        }

        LogNewVersion(logger, latest.VersionText, CurrentVersion);
        var vm = new UpdateViewModel(latest, CurrentVersion);
        await dialogs.ShowUpdateAsync(vm).ConfigureAwait(true);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "New version check: nothing to show, the latest release {Latest} is not newer than {Current}")]
    private static partial void LogNotNewer(ILogger logger, string latest, string current);

    [LoggerMessage(Level = LogLevel.Information, Message = "New version check: nothing to show, development build {Current} (--as-version or OADM_CLIENT_VERSION to try it)")]
    private static partial void LogDevelopmentBuild(ILogger logger, string current);

    [LoggerMessage(Level = LogLevel.Information, Message = "New version check: nothing to show, {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "New version check failed: {Reason}")]
    private static partial void LogCheckFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "OADM {Latest} is available (this client: {Current})")]
    private static partial void LogNewVersion(ILogger logger, string latest, string current);
}
