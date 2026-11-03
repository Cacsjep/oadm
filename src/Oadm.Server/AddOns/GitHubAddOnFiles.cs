using Oadm.Contracts.Updates;
using Oadm.Core.Plugins.AddOns;

namespace Oadm.Server.AddOns;

/// <summary>
/// The add-on files of OADM's own GitHub release (docs/spec/add-ons.md rule 2): the release of the server's version through
/// <see cref="IReleaseFeed"/> and the download and check of self update (<see cref="UpdateDownloader"/>: the release's
/// <c>SHA256SUMS.txt</c>, <see cref="GitHubReleaseFeed.AllowedHosts"/>, HTTPS, size limit, timeouts, free space).
/// </summary>
public sealed class GitHubAddOnFiles(IReleaseFeed feed, UpdateDownloader downloader) : IAddOnFiles
{
    public async Task<bool> CanReachAsync(string version, CancellationToken ct) =>
        UpdateVersions.ParseStable(version) is { } stable && await feed.ByVersionAsync(stable, ct).ConfigureAwait(false) is not null;

    public async Task DownloadAsync(string version, string fileName, string targetPath, IProgress<int>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var target = Path.GetFullPath(targetPath);
        var folder = Path.GetDirectoryName(target)!;
        try
        {
            var release = UpdateVersions.ParseStable(version) is { } stable
                ? await feed.ByVersionAsync(stable, ct).ConfigureAwait(false)
                : null;
            if (release is null)
            {
                throw new AddOnException(AddOnTexts.DownloadFailed);
            }

            var downloaded = await downloader.DownloadAsync(
                release,
                fileName,
                folder,
                (step, percent) =>
                {
                    if (step == UpdateStep.Download)
                    {
                        progress?.Report(percent);
                    }
                },
                ct).ConfigureAwait(false);
            if (!string.Equals(Path.GetFullPath(downloaded), target, StringComparison.Ordinal))
            {
                File.Move(downloaded, target, overwrite: true);
            }
        }
        catch (UpdateRefusedException ex)
        {
            TryDelete(target);
            throw new AddOnException(TextOf(ex.Message), ex);
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>The add-on text of a refusal of the self update download.</summary>
    public static string TextOf(string updateText) => updateText switch
    {
        UpdateTexts.WrongChecksum or UpdateTexts.NoChecksum or UpdateTexts.TooBig or UpdateTexts.WrongName => AddOnTexts.Damaged,
        UpdateTexts.NotEnoughSpace => AddOnInstallTexts.NotEnoughSpace,
        _ => AddOnTexts.DownloadFailed,
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The .download folder is emptied at the next start.
        }
    }
}
