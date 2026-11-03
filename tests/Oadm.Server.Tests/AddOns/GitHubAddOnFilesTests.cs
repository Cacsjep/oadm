using System.Security.Cryptography;
using System.Text;

using Oadm.Contracts.Updates;
using Oadm.Core.Plugins.AddOns;
using Oadm.Server.AddOns;

namespace Oadm.Server.Tests.AddOns;

/// <summary>
/// The release files of add-ons over the self update download (docs/spec/add-ons.md rule 2): only the release of the server's
/// version, checked against its SHA256SUMS.txt, with a fake feed (never a real download).
/// </summary>
public sealed class GitHubAddOnFilesTests : IDisposable
{
    private const string Version = "4.11.0";
    private static readonly string FileName = AddOnCatalog.ReleaseFileName(AddOnCatalog.TextRecognitionId, Version, "win-x64");
    private static readonly byte[] Content = [1, 2, 3, 4, 5];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "oadm-addon-files-" + Guid.NewGuid().ToString("N"));

    private string Target => Path.Combine(_folder, ".download", FileName);

    [Fact]
    public async Task TheFileOfTheServersReleaseIsDownloadedAndChecked()
    {
        var feed = new Feed(Hash(Content));
        var files = new GitHubAddOnFiles(feed, new UpdateDownloader(feed, new NoDiskLimit()));
        var progress = new List<int>();

        await files.DownloadAsync(Version, FileName, Target, new InlineProgress(progress.Add), CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(Target));
        Assert.Equal(new Version(4, 11, 0), feed.AskedFor);
        Assert.Equal(100, progress[^1]);
        Assert.True(await files.CanReachAsync(Version, CancellationToken.None));
    }

    [Fact]
    public async Task AWrongChecksumIsDamagedAndDeleted()
    {
        var feed = new Feed(Hash([9, 9]));
        var files = new GitHubAddOnFiles(feed, new UpdateDownloader(feed, new NoDiskLimit()));

        var failed = await Assert.ThrowsAsync<AddOnException>(() => files.DownloadAsync(Version, FileName, Target, null, CancellationToken.None));

        Assert.Equal(AddOnTexts.Damaged, failed.Message);
        Assert.False(File.Exists(Target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!));
    }

    [Fact]
    public async Task WithoutTheReleaseNothingIsDownloaded()
    {
        var feed = new Feed(Hash(Content)) { Release = false };
        var files = new GitHubAddOnFiles(feed, new UpdateDownloader(feed, new NoDiskLimit()));

        var failed = await Assert.ThrowsAsync<AddOnException>(() => files.DownloadAsync(Version, FileName, Target, null, CancellationToken.None));

        Assert.Equal(AddOnTexts.DownloadFailed, failed.Message);
        Assert.False(File.Exists(Target));
        Assert.False(await files.CanReachAsync(Version, CancellationToken.None));
        Assert.False(await files.CanReachAsync("4.11.0-rc.1", CancellationToken.None)); // never a pre-release
    }

    [Fact]
    public void RefusalsOfTheDownloadSayWhatTheAddOnPageSays()
    {
        Assert.Equal(AddOnTexts.Damaged, GitHubAddOnFiles.TextOf(UpdateTexts.WrongChecksum));
        Assert.Equal(AddOnTexts.Damaged, GitHubAddOnFiles.TextOf(UpdateTexts.NoChecksum));
        Assert.Equal(AddOnInstallTexts.NotEnoughSpace, GitHubAddOnFiles.TextOf(UpdateTexts.NotEnoughSpace));
        Assert.Equal(AddOnTexts.DownloadFailed, GitHubAddOnFiles.TextOf(UpdateTexts.DownloadFailed));
        Assert.Equal(AddOnTexts.DownloadFailed, GitHubAddOnFiles.TextOf(UpdateTexts.NoInstaller));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Never created.
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Feed(string hash) : IReleaseFeed
    {
        private const string Base = GitHubReleaseFeed.DownloadPrefix + "v" + Version + "/";

        public bool Release { get; init; } = true;

        public Version? AskedFor { get; private set; }

        public Task<ReleaseOffer?> LatestAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ReleaseOffer?> ByVersionAsync(Version version, CancellationToken ct)
        {
            AskedFor = version;
            return Task.FromResult(Release
                ? new ReleaseOffer(version, "v" + Version, string.Empty,
                [
                    new ReleaseAsset(FileName, Content.Length, new Uri(Base + FileName)),
                    new ReleaseAsset(UpdateDownloader.ChecksumsName, 200, new Uri(Base + UpdateDownloader.ChecksumsName)),
                ])
                : null);
        }

        public async Task DownloadAsync(Uri url, Stream target, long maxBytes, IProgress<long>? progress, CancellationToken ct)
        {
            var bytes = url.AbsolutePath.EndsWith(UpdateDownloader.ChecksumsName, StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes($"{hash}  {FileName}\n")
                : Content;
            await target.WriteAsync(bytes, ct);
            progress?.Report(bytes.Length);
        }
    }

    private sealed class NoDiskLimit : IDiskSpace
    {
        public long? AvailableBytes(string folder) => null;
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
