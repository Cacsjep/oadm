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

/// <summary>Credentials page: eye button reveals the stored password, copy button copies it; administrators only.</summary>
public sealed class CredentialsPageTests
{
    private static CredentialsViewModel Create(DevicesFixture f, ServerConnection connection, UserSession? session = null) =>
        new(f.Api, connection, f.Clipboard, session, NullLogger<CredentialsViewModel>.Instance);

    [Fact]
    public async Task Operators_never_read_the_list_and_lose_it_when_the_role_changes()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        using var f = new DevicesFixture(api);
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        var session = new UserSession();
        session.SignIn(new Oadm.Contracts.V1.UserInfo { UserName = "anna", Role = Oadm.Contracts.V1.UserRole.Admin }, "fake");
        CredentialsViewModel vm = Create(f, connection, session);
        await vm.LoadAsync();
        Assert.NotEmpty(vm.Credentials);

        session.SignIn(new Oadm.Contracts.V1.UserInfo { UserName = "otto", Role = Oadm.Contracts.V1.UserRole.Operator }, "fake");
        Assert.Empty(vm.Credentials);
        Assert.True(vm.HasNoCredentials);
        await vm.LoadAsync();
        Assert.Empty(vm.Credentials);
    }

    [Fact]
    public async Task Adding_a_credential_reloads_the_list_and_reports_below_the_user_name_on_failure()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        using var f = new DevicesFixture(api);
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        CredentialsViewModel vm = Create(f, connection);
        await vm.LoadAsync();
        int before = vm.Credentials.Count;

        vm.NewCredentialUserName = "service";
        vm.NewCredentialPassword = "Service-pass-1";
        await vm.AddCredentialCommand.ExecuteAsync(null);

        Assert.Equal(before + 1, vm.Credentials.Count);
        Assert.Equal("Added. OADM tries service on every device it finds.", vm.CredentialMessage);
        Assert.Equal("", vm.NewCredentialPassword);
        Assert.False(vm.HasErrors);

        await vm.RemoveCredentialCommand.ExecuteAsync(vm.Credentials.Single(c => c.UserName == "service"));
        Assert.Equal(before, vm.Credentials.Count);
    }

    [Fact]
    public async Task Eye_button_reveals_the_password_and_hides_it_again()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        using var f = new DevicesFixture(api);
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        CredentialsViewModel vm = Create(f, connection);
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
        CredentialsViewModel vm = Create(f, connection);
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
        CredentialsViewModel vm = Create(f, connection);
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
    public async Task Credentials_page_shows_its_header_and_a_revealed_password()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
            CredentialsViewModel vm = Create(f, connection);
            await vm.LoadAsync();
            Assert.Equal(["root", "operator"], vm.Credentials.Select(c => c.UserName).ToArray());

            var window = new Window { Width = 1000, Height = 1200, Content = new CredentialsView { DataContext = vm } };
            window.Show();
            await vm.ToggleRevealCredentialCommand.ExecuteAsync(vm.Credentials[0]);
            Dispatcher.UIThread.RunJobs();

            List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            Assert.Contains("Fake-root-pass1", texts);
            Assert.Contains(CredentialItemViewModel.MaskedText, texts);
            Assert.Contains("Credentials", texts);
            Assert.Contains("Passwords OADM tries when it adds devices.", texts);
            Assert.DoesNotContain("Credential list", texts); // no card title repeating the page title
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
                frame.Save(Path.Combine(outDir, "client-credentials-revealed.png"));
            }

            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done); // the async body ran to the end (a Func<Task> body would not be awaited)
    }
}
