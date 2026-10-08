using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Settings;
using Oadm.Client.Shell;

namespace Oadm.Client.Tests;

/// <summary>Credential list on the Settings page: eye button reveals the stored password, copy button copies it.</summary>
public sealed class SettingsCredentialRevealTests
{
    private static SettingsViewModel Create(DevicesFixture f, ServerConnection connection) =>
        new(f.Api, connection, f.Clipboard, NullLogger<SettingsViewModel>.Instance);

    [Fact]
    public async Task Eye_button_reveals_the_password_and_hides_it_again()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        using var f = new DevicesFixture(api);
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        SettingsViewModel vm = Create(f, connection);
        await api.AddCredentialAsync("service", "Reveal-me-42", CancellationToken.None);
        await vm.LoadAsync();

        CredentialItemViewModel item = vm.Credentials.Single(c => c.UserName == "service");
        Assert.False(item.IsRevealed);
        Assert.Equal(CredentialItemViewModel.MaskedText, item.PasswordText);
        Assert.Equal("Show password", item.RevealTooltip);

        await vm.ToggleRevealCredentialCommand.ExecuteAsync(item);
        Assert.True(item.IsRevealed);
        Assert.Equal("Reveal-me-42", item.PasswordText);
        Assert.Equal("Hide password", item.RevealTooltip);

        await vm.ToggleRevealCredentialCommand.ExecuteAsync(item);
        Assert.False(item.IsRevealed);
        Assert.Null(item.Password); // forgotten, loaded again on the next click
        Assert.Equal(CredentialItemViewModel.MaskedText, item.PasswordText);
    }

    [Fact]
    public async Task Copy_button_copies_the_password_without_revealing_it()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        using var f = new DevicesFixture(api);
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        SettingsViewModel vm = Create(f, connection);
        await api.AddCredentialAsync("service", "Copy-me-42", CancellationToken.None);
        await vm.LoadAsync();
        CredentialItemViewModel item = vm.Credentials.Single(c => c.UserName == "service");

        await vm.CopyCredentialPasswordCommand.ExecuteAsync(item);

        await f.Clipboard.Received(1).SetTextAsync("Copy-me-42");
        Assert.False(item.IsRevealed);
        Assert.False(vm.CredentialMessageIsError);
        Assert.Equal("Password of service copied to the clipboard.", vm.CredentialMessage);
    }

    [Fact]
    public async Task A_removed_entry_reports_the_server_message()
    {
        using var f = new DevicesFixture();
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        SettingsViewModel vm = Create(f, connection);
        f.Api.RevealCredentialAsync("gone", Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new RpcException(new Status(StatusCode.NotFound, "Credential 'gone' not found.")));
        var item = new CredentialItemViewModel("gone", "root", "");

        await vm.ToggleRevealCredentialCommand.ExecuteAsync(item);
        Assert.False(item.IsRevealed);
        Assert.True(vm.CredentialMessageIsError);
        Assert.Equal("Showing the password failed: Credential 'gone' not found.", vm.CredentialMessage);

        await vm.CopyCredentialPasswordCommand.ExecuteAsync(item);
        await f.Clipboard.DidNotReceive().SetTextAsync(Arg.Any<string>());
        Assert.Equal("Copying the password failed: Credential 'gone' not found.", vm.CredentialMessage);
    }

    [Fact]
    public async Task Settings_page_shows_a_revealed_password_and_no_client_card()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
            SettingsViewModel vm = Create(f, connection);
            await vm.LoadAsync();
            Assert.Equal(["root", "operator"], vm.Credentials.Select(c => c.UserName).ToArray());

            var window = new Window { Width = 1000, Height = 1200, Content = new SettingsView { DataContext = vm } };
            window.Show();
            await vm.ToggleRevealCredentialCommand.ExecuteAsync(vm.Credentials[0]);
            Dispatcher.UIThread.RunJobs();

            List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("Fake-root-pass1", texts);
            Assert.Contains(CredentialItemViewModel.MaskedText, texts);
            Assert.DoesNotContain("This client", texts);
            Assert.DoesNotContain(texts, t => t.Contains("never shown again", StringComparison.Ordinal));
            List<object?> tips = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("iconOnly")).Select(ToolTip.GetTip).ToList();
            Assert.Equal(2, tips.Count(t => (t as string) == "Copy password"));
            Assert.Equal(1, tips.Count(t => (t as string) == "Hide password"));
            Assert.Contains("Show password", tips);

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-settings-credential-revealed.png"));
            }

            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done); // the async body ran to the end (a Func<Task> body would not be awaited)
    }
}
