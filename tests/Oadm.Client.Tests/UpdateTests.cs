using System.Net;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Updates;
using Oadm.Theme.Controls;

namespace Oadm.Client.Tests;

/// <summary>The new version check at client start and its dialog.</summary>
public sealed class UpdateTests
{
    private const string Notes = """
        ### Added

        - Upgrade firmware takes several files at once, one per model: each device installs the file for its model.
        - Upgrade firmware starts tasks only for the devices that install a file, not for those already up to date or with a
          file of another model. Plugin dialogs can do the same (SDK 0.5.0 `ShowForDevicesAsync`).

        ### Fixed

        - Enable IEEE 802.1X makes **AXIS OS 12.11** cameras log in at the switch again.
        - See [the docs](https://oadm.app) for the commit.
        """;

    private static JsonElement Release(string tag, bool prerelease = false, bool draft = false) =>
        JsonDocument.Parse(JsonSerializer.Serialize(new { tag_name = tag, prerelease, draft, body = Notes })).RootElement;

    [Fact]
    public void Only_a_released_version_counts()
    {
        Assert.Equal(new Version(4, 6, 0), GitHubReleaseCheck.Parse(Release("v4.6.0"))!.Version);
        Assert.Equal("V4.6.0", GitHubReleaseCheck.Parse(Release("v4.6.0"))!.VersionText);
        Assert.Null(GitHubReleaseCheck.Parse(Release("v4.6.0-rc.1", prerelease: true)));
        Assert.Null(GitHubReleaseCheck.Parse(Release("v4.6.0", draft: true)));
        Assert.Null(GitHubReleaseCheck.Parse(Release("nightly")));
        Assert.Equal(new Version(4, 5, 0), ClientVersion.Parse("4.5.0+24f72809"));
        Assert.True(ClientVersion.IsDevelopmentBuild("1.0.0"));
        Assert.False(ClientVersion.IsDevelopmentBuild("4.5.0"));
    }

    [Fact]
    public void Release_notes_become_groups_of_plain_entries()
    {
        IReadOnlyList<ReleaseNoteSection> sections = ReleaseNotes.Parse(Notes);

        Assert.Equal(["Added", "Fixed"], sections.Select(s => s.Title));
        Assert.True(sections[0].IsOk);
        Assert.True(sections[1].IsWarning);
        Assert.Equal(2, sections[0].Items.Count);
        Assert.EndsWith("with a file of another model. Plugin dialogs can do the same (SDK 0.5.0 ShowForDevicesAsync).", sections[0].Items[1], StringComparison.Ordinal);
        Assert.Equal("Enable IEEE 802.1X makes AXIS OS 12.11 cameras log in at the switch again.", sections[1].Items[0]);
        Assert.Equal("See the docs for the commit.", sections[1].Items[1]);
        Assert.Equal("4 changes", new UpdateViewModel(new ReleaseInfo(new Version(4, 6, 0), "V4.6.0", Notes), "4.5.0").ChangeCount);
    }

    private sealed class Fixture
    {
        public IReleaseCheck Check { get; } = Substitute.For<IReleaseCheck>();
        public InMemoryClientSettingsStore Settings { get; } = new();
        public IDialogService Dialogs { get; } = Substitute.For<IDialogService>();
        public IUrlLauncher Urls { get; } = Substitute.For<IUrlLauncher>();
        public List<UpdateViewModel> Shown { get; } = [];

        public Fixture(string latest = "v4.6.0")
        {
            Check.GetLatestAsync(Arg.Any<CancellationToken>()).Returns(GitHubReleaseCheck.Parse(Release(latest)));
            Dialogs.ShowUpdateAsync(Arg.Do<UpdateViewModel>(Shown.Add)).Returns(Task.CompletedTask);
        }

        public UpdateNotifier Create(string current = "4.5.0") =>
            new(Check, Settings, Dialogs, Urls, NullLogger<UpdateNotifier>.Instance) { CurrentVersion = current };
    }

    [Fact]
    public async Task A_newer_release_shows_the_dialog_once()
    {
        var f = new Fixture();
        UpdateNotifier notifier = f.Create();

        await notifier.ShowIfNewerAsync(CancellationToken.None);
        await notifier.ShowIfNewerAsync(CancellationToken.None); // a second main window (log in again): not again

        UpdateViewModel shown = Assert.Single(f.Shown);
        Assert.Equal("OADM V4.6.0 is available", shown.Title);
        Assert.Equal("You have V4.5.0", shown.Current);
        await f.Urls.DidNotReceive().OpenAsync(Arg.Any<Uri>());
    }

    [Theory]
    [InlineData("v4.5.0", "4.5.0")] // the same version
    [InlineData("v4.4.2", "4.5.0")] // older
    [InlineData("v9.0.0", "1.0.0")] // a development build never asks
    public async Task Nothing_is_shown_without_a_newer_release(string latest, string current)
    {
        var f = new Fixture(latest);

        await f.Create(current).ShowIfNewerAsync(CancellationToken.None);

        Assert.Empty(f.Shown);
    }

    [Fact]
    public async Task Dont_tell_me_is_saved_and_turns_the_check_off()
    {
        var f = new Fixture();
        f.Dialogs.ShowUpdateAsync(Arg.Any<UpdateViewModel>()).Returns(ci =>
        {
            var vm = ci.Arg<UpdateViewModel>();
            vm.DontTellAgain = true;
            vm.OpenDownloadCommand.Execute(null);
            return Task.CompletedTask;
        });

        await f.Create().ShowIfNewerAsync(CancellationToken.None);

        Assert.False(f.Settings.Current.TellAboutNewVersions);
        await f.Urls.Received(1).OpenAsync(new Uri(UpdateViewModel.DownloadPage));
        var next = new Fixture { Settings = { Current = { TellAboutNewVersions = false } } };
        await next.Create().ShowIfNewerAsync(CancellationToken.None);
        await next.Check.DidNotReceive().GetLatestAsync(Arg.Any<CancellationToken>()); // off: GitHub is not even asked
    }

    [Fact]
    public async Task GitHub_that_does_not_answer_within_2_seconds_shows_nothing()
    {
        var time = new FakeTimeProvider();
        var handler = new SilentHandler();
        using var http = new HttpClient(handler);
        var check = new GitHubReleaseCheck(http, time);

        Task<ReleaseInfo?> latest = check.GetLatestAsync(CancellationToken.None);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => handler.Asked, "the request was sent");
        time.Advance(GitHubReleaseCheck.Timeout);

        Assert.Null(await latest.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(GitHubReleaseCheck.LatestUrl, handler.Url);
        Assert.Contains("OADM-Client", handler.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_answer_shows_nothing()
    {
        using var http = new HttpClient(new AnswerHandler(HttpStatusCode.Forbidden)); // e.g. GitHub's rate limit
        Assert.Null(await new GitHubReleaseCheck(http).GetLatestAsync(CancellationToken.None));
    }

    private sealed class SilentHandler : HttpMessageHandler
    {
        public volatile bool Asked;
        public string? Url;
        public string UserAgent = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            UserAgent = request.Headers.UserAgent.ToString();
            Asked = true;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("never");
        }
    }

    private sealed class AnswerHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    [Fact]
    public async Task The_dialog_renders_with_the_notes_closed_and_open()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var owner = new Window { Width = 800, Height = 900 };
            owner.Show();
            var vm = new UpdateViewModel(GitHubReleaseCheck.Parse(Release("v4.6.0"))!, "4.5.0");
            var window = new UpdateWindow { DataContext = vm };
            window.Show(owner);
            Dispatcher.UIThread.RunJobs();
            List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            Assert.Contains("OADM V4.6.0 is available", texts);
            Assert.Contains("You have V4.5.0", texts);
            Assert.DoesNotContain("ADDED", texts); // the notes start closed
            Assert.True(Assert.Single(window.GetVisualDescendants().OfType<HeroBand>()).ShowsUpdate);
            Capture(window, outDir, "update-dialog.png");

            vm.IsNotesOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "ADDED");
            Capture(window, outDir, "update-dialog-notes.png");

            vm.LaterCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.IsVisible); // Later closes it
            owner.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }
}
