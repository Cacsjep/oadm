using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using Oadm.Contracts.Updates;

namespace Oadm.Server.Tests.Updates;

/// <summary>The shared parts of self update (Oadm.Contracts.Updates): names, installed detection, installer calls, the download.</summary>
public sealed class UpdateSharedTests
{
    private static readonly Version V490 = new(4, 9, 0);

    [Theory]
    [InlineData(UpdateOs.Windows, Architecture.X64, UpdateApp.Server, "OADM-4.9.0-win-x64.msi")]
    [InlineData(UpdateOs.Windows, Architecture.X64, UpdateApp.Client, "OADM-4.9.0-win-x64.msi")]
    [InlineData(UpdateOs.Linux, Architecture.X64, UpdateApp.Server, "oadm-server_4.9.0_amd64.deb")]
    [InlineData(UpdateOs.Linux, Architecture.Arm64, UpdateApp.Client, "oadm-client_4.9.0_arm64.deb")]
    [InlineData(UpdateOs.MacOs, Architecture.X64, UpdateApp.Server, "OADM-4.9.0-osx-x64.pkg")]
    [InlineData(UpdateOs.MacOs, Architecture.Arm64, UpdateApp.Client, "OADM-4.9.0-osx-x64.pkg")]
    [InlineData(UpdateOs.Windows, Architecture.Arm64, UpdateApp.Server, null)]
    public void EachPlatformTakesItsInstallerByExactName(UpdateOs os, Architecture arch, UpdateApp app, string? expected) =>
        Assert.Equal(expected, new UpdatePlatform(os, arch).InstallerName(app, V490));

    [Theory]
    [InlineData("v4.9.0", true)]
    [InlineData("4.9.0", true)]
    [InlineData("4.9.0-rc.1", false)]
    [InlineData("v4.9.0-rc.2", false)]
    [InlineData("nonsense", false)]
    public void OnlyPlainVersionsAreStable(string text, bool stable) => Assert.Equal(stable, UpdateVersions.ParseStable(text) is not null);

    [Theory]
    [InlineData("0.1.0-dev", true)]
    [InlineData("1.0.0", true)]
    [InlineData("4.8.0", false)]
    public void DevelopmentBuildsAreNeverUpdated(string version, bool development) => Assert.Equal(development, UpdateVersions.IsDevelopmentBuild(version));

    private static InstallFacts Facts(UpdateOs os, string baseDirectory, bool service, params string[] files) =>
        new(os, baseDirectory, @"C:\Program Files", service, f => files.Contains(f));

    [Fact]
    public void OnlyAnInstalledServerUpdatesItself()
    {
        Assert.True(Installation.IsServerInstalled(Facts(UpdateOs.Windows, @"C:\Program Files\OADM\Server\", true)));
        Assert.False(Installation.IsServerInstalled(Facts(UpdateOs.Windows, @"C:\Program Files\OADM\Server\", false))); // a console run
        Assert.False(Installation.IsServerInstalled(Facts(UpdateOs.Windows, @"D:\oadm-portable\", true))); // the ZIP as a service
        Assert.True(Installation.IsServerInstalled(Facts(UpdateOs.Linux, "/opt/oadm/server/", true, "/var/lib/dpkg/info/oadm-server.list")));
        Assert.True(Installation.IsServerInstalled(Facts(UpdateOs.Linux, "/opt/oadm/server", true, "/var/lib/dpkg/info/oadm-server:arm64.list")));
        Assert.False(Installation.IsServerInstalled(Facts(UpdateOs.Linux, "/opt/oadm/server/", true))); // copied there, not by dpkg
        Assert.False(Installation.IsServerInstalled(Facts(UpdateOs.Linux, "/home/me/oadm/", true, "/var/lib/dpkg/info/oadm-server.list")));
        Assert.True(Installation.IsServerInstalled(Facts(UpdateOs.MacOs, "/Library/Application Support/OADM/server/", true, Installation.MacServerDaemon)));
        Assert.False(Installation.IsServerInstalled(Facts(UpdateOs.MacOs, "/Library/Application Support/OADM/server/", true)));
    }

    [Fact]
    public void OnlyAnInstalledClientOffersUpdateClient()
    {
        Assert.True(Installation.CanUpdateClient(Facts(UpdateOs.Windows, @"C:\Program Files\OADM\Client\", false)));
        Assert.False(Installation.CanUpdateClient(Facts(UpdateOs.Windows, @"G:\odam\src\Oadm.Client\bin\Debug\net10.0\", false))); // dotnet run
        Assert.True(Installation.CanUpdateClient(Facts(UpdateOs.Linux, "/opt/oadm/client/", false, "/var/lib/dpkg/info/oadm-client.list", "/usr/bin/pkexec")));
        Assert.False(Installation.CanUpdateClient(Facts(UpdateOs.Linux, "/opt/oadm/client/", false, "/var/lib/dpkg/info/oadm-client.list"))); // no pkexec
        Assert.True(Installation.CanUpdateClient(Facts(UpdateOs.MacOs, "/Applications/OADM.app/Contents/MacOS/", false)));
        Assert.False(Installation.CanUpdateClient(Facts(UpdateOs.MacOs, "/Users/me/Downloads/OADM-Client/", false)));
    }

    [Fact]
    public void SilentInstallsKeepTheSetupTypeAndWriteALog()
    {
        var msi = InstallerCommands.For(UpdateOs.Windows, InstallMode.Silent, @"C:\ProgramData\OADM\update\OADM-4.9.0-win-x64.msi", @"C:\ProgramData\OADM\update\install.log");
        Assert.EndsWith("msiexec.exe", msi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/i", @"C:\ProgramData\OADM\update\OADM-4.9.0-win-x64.msi", "/qn", "/norestart", "/l*v", @"C:\ProgramData\OADM\update\install.log"], msi.Arguments);
        Assert.DoesNotContain(msi.Arguments, a => a.StartsWith("ADDLOCAL", StringComparison.OrdinalIgnoreCase)); // rule 10: no feature properties

        var visible = InstallerCommands.For(UpdateOs.Windows, InstallMode.Visible, "x.msi", "x.log");
        Assert.Contains("/passive", visible.Arguments);
        Assert.Contains("/norestart", visible.Arguments);

        var apt = InstallerCommands.For(UpdateOs.Linux, InstallMode.Silent, "/var/lib/oadm/update/oadm-server_4.9.0_amd64.deb", "/var/lib/oadm/update/install.log");
        Assert.Equal(InstallerCommands.AptGet, apt.FileName);
        Assert.Equal(["install", "-y", "-o", "DPkg::Lock::Timeout=300", "/var/lib/oadm/update/oadm-server_4.9.0_amd64.deb"], apt.Arguments);
        Assert.Equal("noninteractive", apt.Environment["DEBIAN_FRONTEND"]);
        Assert.Equal("C", apt.Environment["LC_ALL"]); // English messages: the busy lock is recognized on every system

        var pkexec = InstallerCommands.For(UpdateOs.Linux, InstallMode.Visible, "/tmp/oadm-client_4.9.0_amd64.deb", "/tmp/install.log");
        Assert.Equal(InstallerCommands.Pkexec, pkexec.FileName);
        Assert.Equal(["/usr/bin/env", "DEBIAN_FRONTEND=noninteractive", "LC_ALL=C", InstallerCommands.AptGet, "install", "-y", "-o", "DPkg::Lock::Timeout=300", "/tmp/oadm-client_4.9.0_amd64.deb"], pkexec.Arguments);

        var pkg = InstallerCommands.For(UpdateOs.MacOs, InstallMode.Silent, "/x/OADM-4.9.0-osx-x64.pkg", "/x/install.log", "/x/choices.xml");
        Assert.Equal(InstallerCommands.MacInstaller, pkg.FileName);
        Assert.Equal(["-pkg", "/x/OADM-4.9.0-osx-x64.pkg", "-target", "/", "-applyChoiceChangesXML", "/x/choices.xml"], pkg.Arguments);
        Assert.Equal(InstallerCommands.MacInstallLog, pkg.LogPath);

        var prompt = InstallerCommands.For(UpdateOs.MacOs, InstallMode.Visible, "/tmp/it's here/OADM.pkg", "/tmp/l.log");
        Assert.Equal(InstallerCommands.Osascript, prompt.FileName);
        Assert.Equal("-e", prompt.Arguments[0]);
        Assert.Contains("with administrator privileges", prompt.Arguments[1], StringComparison.Ordinal);
        Assert.Contains("'/tmp/it'\\\\''s here/OADM.pkg'", prompt.Arguments[1], StringComparison.Ordinal); // quoted for the shell inside AppleScript
    }

    [Fact]
    public void TheMacChoiceFileLeavesTheServerOut()
    {
        var xml = System.Xml.Linq.XDocument.Parse(InstallerCommands.MacChoices());
        var values = xml.Descendants("dict").Single().Elements().Select(e => e.Value).ToList();
        Assert.Equal(["attributeSetting", "0", "choiceAttribute", "selected", "choiceIdentifier", "com.oadm.choice.server"], values);
    }

    [Theory]
    [InlineData(UpdateOs.Windows, 0, "", InstallOutcome.Done, null)]
    [InlineData(UpdateOs.Windows, 3010, "", InstallOutcome.DoneRestartNeeded, "Restart the computer to finish the update")]
    [InlineData(UpdateOs.Windows, 1618, "", InstallOutcome.Busy, "Another installation is running. Try again later.")]
    [InlineData(UpdateOs.Windows, 1603, "", InstallOutcome.Failed, "The installer failed (code 1603).")]
    [InlineData(UpdateOs.Linux, 0, "", InstallOutcome.Done, null)]
    [InlineData(UpdateOs.Linux, 100, "E: Could not get lock /var/lib/dpkg/lock-frontend.", InstallOutcome.Busy, "Another installation is running. Try again later.")]
    [InlineData(UpdateOs.Linux, 100, "E: Sub-process /usr/bin/dpkg returned an error code (1)", InstallOutcome.Failed, "The installer failed.")]
    [InlineData(UpdateOs.MacOs, 0, "", InstallOutcome.Done, null)]
    [InlineData(UpdateOs.MacOs, 1, "installer: The install failed.", InstallOutcome.Failed, "The installer failed.")]
    public void InstallerResultsAreReadNotGuessed(UpdateOs os, int code, string output, InstallOutcome outcome, string? text)
    {
        var result = InstallerCommands.Read(os, InstallMode.Silent, code, output, "log");
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(text, result.Text);
        if (os == UpdateOs.MacOs && code != 0)
        {
            Assert.Equal("/var/log/install.log", result.LogPath);
        }
    }

    [Fact]
    public void ACancelledPromptIsCancelled()
    {
        Assert.Equal(InstallOutcome.Cancelled, InstallerCommands.Read(UpdateOs.Linux, InstallMode.Visible, 126, "", "l").Outcome);
        Assert.Equal(InstallOutcome.Cancelled, InstallerCommands.Read(UpdateOs.MacOs, InstallMode.Visible, 1, "execution error: User canceled. (-128)", "l").Outcome);
        Assert.Equal(InstallOutcome.Cancelled, InstallerCommands.Read(UpdateOs.Windows, InstallMode.Visible, 1602, "", "l").Outcome);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData(" *")]
    public void ChecksumsAreFoundInBothSha256SumFormats(string separator)
    {
        var hash = new string('a', 64);
        var sums = $"{new string('b', 64)}  OADM-4.9.0-win-x64.msi.bak\n{hash}{separator}OADM-4.9.0-win-x64.msi\r\n";
        Assert.Equal(Convert.FromHexString(hash), UpdateDownloader.FindHash(sums, "OADM-4.9.0-win-x64.msi"));
        Assert.Null(UpdateDownloader.FindHash(sums, "oadm-server_4.9.0_amd64.deb"));
    }

    [Fact]
    public async Task TheDownloadIsCheckedAndKept()
    {
        using var folder = new TempFolder();
        var feed = FakeReleaseFeed.With("OADM-4.9.0-win-x64.msi", Encoding.UTF8.GetBytes("installer"));
        var steps = new List<UpdateStep>();
        var path = await new UpdateDownloader(feed, new FakeDiskSpace(long.MaxValue)).DownloadAsync(feed.Release, "OADM-4.9.0-win-x64.msi", folder.Path, (s, _) => steps.Add(s), CancellationToken.None);

        Assert.Equal("installer", await File.ReadAllTextAsync(path));
        Assert.Equal([UpdateStep.Download, UpdateStep.Check], steps.Distinct());
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task AWrongChecksumDeletesTheDownload()
    {
        using var folder = new TempFolder();
        var feed = FakeReleaseFeed.With("OADM-4.9.0-win-x64.msi", Encoding.UTF8.GetBytes("installer"), sumsOf: Encoding.UTF8.GetBytes("other"));

        var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() =>
            new UpdateDownloader(feed, new FakeDiskSpace(long.MaxValue)).DownloadAsync(feed.Release, "OADM-4.9.0-win-x64.msi", folder.Path, null, CancellationToken.None));

        Assert.Equal(UpdateTexts.WrongChecksum, refused.Message);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task AFileOtherThanTheReleaseDownloadIsRefused()
    {
        using var folder = new TempFolder();
        var feed = FakeReleaseFeed.With("OADM-4.9.0-win-x64.msi", Encoding.UTF8.GetBytes("installer"), url: "https://example.com/OADM-4.9.0-win-x64.msi");

        var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() =>
            new UpdateDownloader(feed, new FakeDiskSpace(long.MaxValue)).DownloadAsync(feed.Release, "OADM-4.9.0-win-x64.msi", folder.Path, null, CancellationToken.None));

        Assert.Equal(UpdateTexts.WrongName, refused.Message);
        Assert.Equal(0, feed.Downloads);
    }

    [Fact]
    public async Task AReleaseWithoutTheExactNameIsRefused()
    {
        using var folder = new TempFolder();
        var feed = FakeReleaseFeed.With("OADM-4.9.0-win-x64.msi.exe", Encoding.UTF8.GetBytes("installer"));

        var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() =>
            new UpdateDownloader(feed, new FakeDiskSpace(long.MaxValue)).DownloadAsync(feed.Release, "OADM-4.9.0-win-x64.msi", folder.Path, null, CancellationToken.None));

        Assert.Equal(UpdateTexts.NoInstaller, refused.Message);
    }

    [Fact]
    public async Task AnInstallerOver400MbIsRefusedBeforeTheDownload()
    {
        using var folder = new TempFolder();
        var feed = FakeReleaseFeed.With("OADM-4.9.0-win-x64.msi", Encoding.UTF8.GetBytes("installer"), size: UpdateDownloader.MaxInstallerBytes + 1);

        var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() =>
            new UpdateDownloader(feed, new FakeDiskSpace(long.MaxValue)).DownloadAsync(feed.Release, "OADM-4.9.0-win-x64.msi", folder.Path, null, CancellationToken.None));

        Assert.Equal(UpdateTexts.TooBig, refused.Message);
        Assert.Equal(0, feed.Downloads);
    }

    [Fact]
    public async Task TooLittleSpaceIsRefusedBeforeTheDownload()
    {
        using var folder = new TempFolder();
        var content = new byte[1000];
        var feed = FakeReleaseFeed.With("OADM-4.9.0-win-x64.msi", content);

        var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() =>
            new UpdateDownloader(feed, new FakeDiskSpace(2999)).DownloadAsync(feed.Release, "OADM-4.9.0-win-x64.msi", folder.Path, null, CancellationToken.None));

        Assert.Equal(UpdateTexts.NotEnoughSpace, refused.Message);
        Assert.Equal(0, feed.Downloads);
        await new UpdateDownloader(feed, new FakeDiskSpace(3000)).DownloadAsync(feed.Release, "OADM-4.9.0-win-x64.msi", folder.Path, null, CancellationToken.None);
    }

    [Fact]
    public async Task ADownloadLargerThanTheReleaseSaysIsStopped()
    {
        using var folder = new TempFolder();
        var feed = FakeReleaseFeed.With("OADM-4.9.0-win-x64.msi", Encoding.UTF8.GetBytes("installer"), size: 3);

        var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() =>
            new UpdateDownloader(feed, new FakeDiskSpace(long.MaxValue)).DownloadAsync(feed.Release, "OADM-4.9.0-win-x64.msi", folder.Path, null, CancellationToken.None));

        Assert.Equal(UpdateTexts.TooBig, refused.Message);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    /// <summary>Answers with a body that never sends a byte (a stalled download), or from another host after a redirect.</summary>
    private sealed class StallingHandler(string? finalUrl = null) : HttpMessageHandler
    {
        private volatile bool _reading;

        public bool Reading => _reading;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(this)) };
            response.RequestMessage = finalUrl is null ? request : new HttpRequestMessage(HttpMethod.Get, finalUrl);
            return Task.FromResult(response);
        }

        private sealed class StallingStream(StallingHandler owner) : MemoryStream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner._reading = true;
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }
    }

    [Fact]
    public async Task AStalledDownloadStopsAfterAMinuteWithoutBytes()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var handler = new StallingHandler();
        using var http = new HttpClient(handler);
        var feed = new GitHubReleaseFeed(http, "OADM-Test/1.0", time);

        var download = feed.DownloadAsync(new Uri(GitHubReleaseFeed.DownloadPrefix + "v4.9.0/OADM-4.9.0-win-x64.msi"), new MemoryStream(), 1000, null, CancellationToken.None);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => handler.Reading, "the download waits for bytes");
        time.Advance(GitHubReleaseFeed.IdleTimeout);

        var refused = await Assert.ThrowsAsync<UpdateRefusedException>(() => download);
        Assert.Equal(UpdateTexts.DownloadFailed, refused.Message);
    }

    [Theory]
    [InlineData("https://objects.githubusercontent.com/x/OADM-4.9.0-win-x64.msi", false)]
    [InlineData("https://release-assets.githubusercontent.com/x", false)]
    [InlineData("https://evil.example.com/OADM-4.9.0-win-x64.msi", true)]
    [InlineData("http://objects.githubusercontent.com/x", true)]
    public async Task ARedirectEndsOnlyAtGitHubsDownloadHosts(string finalUrl, bool refused)
    {
        Assert.Equal(!refused, GitHubReleaseFeed.IsAllowedFinal(new Uri(finalUrl)));
        if (refused)
        {
            using var http = new HttpClient(new StallingHandler(finalUrl));
            var feed = new GitHubReleaseFeed(http, "OADM-Test/1.0", new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
            var ex = await Assert.ThrowsAsync<UpdateRefusedException>(() =>
                feed.DownloadAsync(new Uri(GitHubReleaseFeed.DownloadPrefix + "v4.9.0/OADM-4.9.0-win-x64.msi"), new MemoryStream(), 1000, null, CancellationToken.None));
            Assert.Equal(UpdateTexts.DownloadFailed, ex.Message);
        }
    }

    [Fact]
    public void TheGitHubAnswerNeverOffersAPreRelease()
    {
        static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement;
        Assert.Null(GitHubReleaseFeed.Parse(Json("""{"tag_name":"v4.9.0","prerelease":true,"assets":[]}""")));
        Assert.Null(GitHubReleaseFeed.Parse(Json("""{"tag_name":"v4.9.0-rc.1","assets":[]}""")));
        Assert.Null(GitHubReleaseFeed.Parse(Json("""{"tag_name":"v4.9.0","draft":true}""")));
        var release = GitHubReleaseFeed.Parse(Json("""
            {"tag_name":"v4.9.0","body":"notes","assets":[{"name":"SHA256SUMS.txt","size":10,"browser_download_url":"https://github.com/Cacsjep/oadm/releases/download/v4.9.0/SHA256SUMS.txt"}]}
            """));
        Assert.NotNull(release);
        Assert.Equal(V490, release.Version);
        Assert.Equal("SHA256SUMS.txt", Assert.Single(release.Assets).Name);
    }
}

/// <summary>A release of 4.9.0 with one installer and its SHA256SUMS.txt; the downloads come from memory.</summary>
internal sealed class FakeReleaseFeed : IReleaseFeed
{
    private readonly Dictionary<Uri, byte[]> _files = [];

    public required ReleaseOffer Release { get; init; }

    public int Downloads { get; private set; }

    public bool Unreachable { get; set; }

    public static FakeReleaseFeed With(string name, byte[] content, byte[]? sumsOf = null, string? url = null, long? size = null, string version = "4.9.0")
    {
        var prefix = GitHubReleaseFeed.DownloadPrefix + "v" + version + "/";
        var installerUrl = new Uri(url ?? prefix + name);
        var sums = Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData(sumsOf ?? content)) + "  " + name + "\n");
        var sumsUrl = new Uri(prefix + UpdateDownloader.ChecksumsName);
        var feed = new FakeReleaseFeed
        {
            Release = new ReleaseOffer(Version.Parse(version), "v" + version, "### Added\n- Self update", [
                new ReleaseAsset(name, size ?? content.Length, installerUrl),
                new ReleaseAsset(UpdateDownloader.ChecksumsName, sums.Length, sumsUrl),
            ]),
        };
        feed._files[installerUrl] = content;
        feed._files[sumsUrl] = sums;
        return feed;
    }

    public Exception? Throws { get; set; }

    public Task<ReleaseOffer?> LatestAsync(CancellationToken ct) => Throws is { } ex ? Task.FromException<ReleaseOffer?>(ex) : Task.FromResult(Unreachable ? null : Release);

    public Task<ReleaseOffer?> ByVersionAsync(Version version, CancellationToken ct) => Task.FromResult(Unreachable || version != Release.Version ? null : Release);

    public async Task DownloadAsync(Uri url, Stream target, long maxBytes, IProgress<long>? progress, CancellationToken ct)
    {
        if (!GitHubReleaseFeed.IsReleaseDownload(url))
        {
            throw new UpdateRefusedException(UpdateTexts.WrongName);
        }

        if (!url.AbsolutePath.EndsWith(UpdateDownloader.ChecksumsName, StringComparison.Ordinal))
        {
            Downloads++;
        }

        var content = _files[url];
        if (content.Length > maxBytes)
        {
            throw new UpdateRefusedException(UpdateTexts.TooBig);
        }

        await target.WriteAsync(content, ct);
        progress?.Report(content.Length);
    }
}

internal sealed class FakeDiskSpace(long free) : IDiskSpace
{
    public long Free { get; set; } = free;

    public long? AvailableBytes(string folder) => Free;
}

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oadm-update-tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
