using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>About page: terms of use, client and server version, the third-party notices.</summary>
public sealed class AboutAndLicensesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-about-" + Guid.NewGuid().ToString("N"));

    public AboutAndLicensesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Versions_drop_the_source_revision()
    {
        Assert.DoesNotContain('+', AboutViewModel.VersionOf(typeof(AboutViewModel).Assembly));
        Assert.False(string.IsNullOrWhiteSpace(new AboutViewModel(Substitute.For<IOadmApi>()).ClientVersion));
    }

    [Fact]
    public void Terms_of_use_come_first_and_match_TERMS_md()
    {
        var vm = new AboutViewModel(Substitute.For<IOadmApi>());
        var repo = Path.GetDirectoryName(ThirdPartyNotices.DefaultCandidates(AppContext.BaseDirectory)[^1])!; // the repository root
        var terms = File.ReadAllText(Path.Combine(repo, "TERMS.md")).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

        Assert.Equal("Terms of use", vm.TermsTitle);
        Assert.StartsWith("OADM is free, open source software, provided \"as is\"", vm.TermsText, StringComparison.Ordinal);
        Assert.Contains("not affiliated with, sponsored by, or endorsed by Axis Communications AB", vm.TermsText, StringComparison.Ordinal);
        Assert.Equal(terms, vm.TermsTitle + "\n\n" + vm.TermsText);
    }

    [Fact]
    public async Task Server_version_comes_from_the_server_settings()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new ServerSettings { ServerVersion = "1.2.0" }, new ServerSettings());
        var vm = new AboutViewModel(api);
        Assert.Equal("Not connected", vm.ServerVersion);

        await vm.LoadServerVersionAsync();
        Assert.Equal("1.2.0", vm.ServerVersion);

        await vm.LoadServerVersionAsync();
        Assert.Equal("Unknown (older server)", vm.ServerVersion);

        api.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns<ServerSettings>(_ => throw new InvalidOperationException("offline"));
        await vm.LoadServerVersionAsync();
        Assert.Equal("Not connected", vm.ServerVersion);
    }

    [Fact]
    public async Task Licenses_are_read_from_the_first_notices_file_when_first_shown()
    {
        var file = Path.Combine(_dir, ThirdPartyNotices.FileName);
        await File.WriteAllTextAsync(file, "OADM third-party notices\nAvalonia 12.0.4\n  License: MIT\n");
        var vm = new AboutViewModel(Substitute.For<IOadmApi>(), notices: new ThirdPartyNotices([Path.Combine(_dir, "missing.txt"), file]));
        Assert.Null(vm.NoticesText);
        Assert.Equal("Show licenses", vm.LicensesButtonText);

        await vm.ToggleLicensesCommand.ExecuteAsync(null);
        Assert.True(vm.IsShowingLicenses);
        Assert.Equal("Hide licenses", vm.LicensesButtonText);
        Assert.StartsWith("OADM third-party notices", vm.NoticesText, StringComparison.Ordinal);
        Assert.Equal("From " + file, vm.NoticesSource);

        await vm.ToggleLicensesCommand.ExecuteAsync(null);
        Assert.False(vm.IsShowingLicenses);
        File.Delete(file);
        await vm.ToggleLicensesCommand.ExecuteAsync(null);
        Assert.StartsWith("OADM third-party notices", vm.NoticesText, StringComparison.Ordinal); // read once
    }

    [Fact]
    public async Task Missing_notices_file_is_explained()
    {
        var vm = new AboutViewModel(Substitute.For<IOadmApi>(), notices: new ThirdPartyNotices([Path.Combine(_dir, "missing.txt")]));

        await vm.ToggleLicensesCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.NoticesText);
        Assert.Contains("was not found", vm.NoticesSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Notices_are_looked_up_next_to_the_exe_in_the_app_bundle_and_in_the_repository()
    {
        var candidates = ThirdPartyNotices.DefaultCandidates(AppContext.BaseDirectory);

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, ThirdPartyNotices.FileName), candidates[0]);
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", ThirdPartyNotices.FileName)), candidates[1]);
        Assert.EndsWith("THIRD-PARTY-NOTICES.md", candidates[^1], StringComparison.Ordinal); // tests run in the repository
        Assert.NotNull(ThirdPartyNotices.Default.Find());
    }

    [Fact]
    public async Task About_page_shows_versions_and_the_license_texts()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var file = Path.Combine(_dir, ThirdPartyNotices.FileName);
        await File.WriteAllTextAsync(file, string.Join('\n', Enumerable.Range(1, 200).Select(i => $"Package.{i} 1.0.{i}\n  License: MIT\n  Used by: client")));
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
            var about = new AboutViewModel(f.Api, connection, new ThirdPartyNotices([file]));
            await about.LoadServerVersionAsync();
            await about.ToggleLicensesCommand.ExecuteAsync(null);

            var window = new Window { Width = 1000, Height = 1400, Content = new AboutPageView { DataContext = about } };
            window.Show();
            window.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
            Dispatcher.UIThread.RunJobs();

            List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("About", texts);
            Assert.Contains("Version and licenses.", texts);
            Assert.DoesNotContain("About and licenses", texts); // no card title repeating the page title
            Assert.Contains("Terms of use", texts);
            Assert.True(texts.IndexOf("Terms of use") < texts.IndexOf(AboutViewModel.LicenseText)); // terms before the license
            Assert.Contains(FakeOadmApi.FakeServerVersion, texts);
            Assert.Contains(about.ClientVersion, texts);
            var code = window.GetVisualDescendants().OfType<CodeView>().Single();
            Assert.StartsWith("Package.1 1.0.1", code.DisplayedText, StringComparison.Ordinal);

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-about.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }
}
