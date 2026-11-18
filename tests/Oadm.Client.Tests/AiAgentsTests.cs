using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Contracts.Mcp;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// The Settings card AI, section MCP, in the client (docs/spec/mcp.md): Turn on, Port, Listen on, the certificate, Download CA
/// certificate (read only for other roles), Connect an agent (one row per client, Copy and Download) and the access keys
/// (administrators: the list, Create with the key shown once, Delete). Headless, with screenshots in OADM_SCREENSHOT_DIR.
/// </summary>
public sealed class AiAgentsTests
{
    private static UserSession Session(UserRole role, params string[] permissions)
    {
        var session = new UserSession();
        var user = new UserInfo
        {
            UserName = role == UserRole.Admin ? "admin" : "tech",
            Role = role,
            RoleId = role == UserRole.Admin ? "00000000-0000-0000-0000-000000000001" : "00000000-0000-0000-0000-000000000002",
            RoleName = role == UserRole.Admin ? "Administrator" : "Operator",
            AllPermissions = role == UserRole.Admin,
        };
        user.Permissions.AddRange(permissions);
        session.SignIn(user, "fake");
        return session;
    }

    [Fact]
    public async Task TheCardTurnsTheServerOnSavesThePortAndSavesTheCaCertificate()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        dialogs.SaveFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<byte[]>()).Returns("oadm-ca.pem");
        var card = new AiAgentsViewModel(api, dialogs, Session(UserRole.Admin));
        await card.LoadAsync();

        Assert.False(card.IsOn);
        Assert.Equal("Off", card.StatusText);
        Assert.True(card.ShowTurnOn);
        Assert.False(card.SaveCommand.CanExecute(null));
        Assert.Equal(AiAgentsViewModel.NoChanges, card.SaveTip);
        Assert.Equal(McpTexts.CertificateOnTurnOn, card.CertificateText); // the first Turn on gives the policy MCP server one
        Assert.False(card.CanDownloadCa);

        await card.TurnOnCommand.ExecuteAsync(null);
        Assert.Equal("MCP Server Certificate (OADM Root CA FAKE), until 2036-11-14", card.CertificateText);
        Assert.True(card.Running);
        Assert.Equal("Running on port 5082", card.StatusText);
        Assert.True(card.ShowTurnOff);

        card.Port = "80";
        Assert.Equal(AiAgentsViewModel.PortRange, card.ErrorOf(nameof(AiAgentsViewModel.Port)));
        Assert.False(card.SaveCommand.CanExecute(null));
        card.Port = "5090";
        Assert.True(card.SaveCommand.CanExecute(null));
        await card.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Running on port 5090", card.StatusText);

        await card.DownloadCaCommand.ExecuteAsync(null);
        await dialogs.Received(1).SaveFileAsync("Download CA certificate", "oadm-ca.pem", Arg.Any<FileType>(), Arg.Is<byte[]>(b => System.Text.Encoding.ASCII.GetString(b).Contains("BEGIN CERTIFICATE", StringComparison.Ordinal)));
        Assert.Equal("Saved oadm-ca.pem.", card.Message);

        await card.TurnOffCommand.ExecuteAsync(null);
        Assert.False(card.IsOn);
    }

    [Fact]
    public async Task WithoutPkiTheCardSaysWhyAndOffersNoTurnOn()
    {
        var api = Substitute.For<IOadmApi>();
        var state = new McpState { Settings = new McpSettings { Port = 5082 }, Status = "Off", PkiAvailable = false };
        api.GetMcpAsync(Arg.Any<CancellationToken>()).Returns(state);
        var card = new AiAgentsViewModel(api, Substitute.For<IDialogService>(), Session(UserRole.Admin));
        await card.LoadAsync();
        Assert.Equal("PKI is not running. The server log says why.", card.CertificateText);
        Assert.False(card.TurnOnCommand.CanExecute(null));
        Assert.Equal("PKI is not running. The server log says why.", card.TurnOnTip);
        Assert.False(card.CanDownloadCa);
    }

    [Fact]
    public async Task AnInterfaceTheServerDoesNotKnowShowsItsErrorDirectlyBelowListenOn()
    {
        var api = Substitute.For<IOadmApi>();
        var state = new McpState { Settings = new McpSettings { Port = 5082 }, Status = McpTexts.Off, PkiAvailable = true };
        state.Interfaces.Add(new McpInterface { Id = "all", Label = "All interfaces", Name = "All interfaces" });
        state.Interfaces.Add(new McpInterface { Id = "eth0", Label = "Ethernet - 10.0.0.17", Name = "Ethernet", Addresses = { "10.0.0.17" } });
        state.Interfaces.Add(new McpInterface { Id = "eth1", Label = "Ethernet 2 - 10.1.0.17", Name = "Ethernet 2", Addresses = { "10.1.0.17" } });
        api.GetMcpAsync(Arg.Any<CancellationToken>()).Returns(state);
        api.SetMcpAsync(Arg.Any<McpSettings>(), Arg.Any<CancellationToken>())
            .Returns<McpState>(_ => throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, McpTexts.UnknownInterface)));
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var card = new AiAgentsViewModel(api, Substitute.For<IDialogService>(), Session(UserRole.Admin));
            await card.LoadAsync();
            var window = new Window { Width = 900, Height = 700, Content = new AiPageView { DataContext = card, Margin = new Avalonia.Thickness(24) } };
            window.Show();
            Pump();
            Pump();

            card.SelectedInterface = card.Listen.Find("eth0");
            await card.SaveCommand.ExecuteAsync(null);
            Pump();
            Assert.Equal(McpTexts.UnknownInterface, card.ErrorOf(nameof(AiAgentsViewModel.SelectedInterface)));
            Assert.Null(card.Message); // never under the buttons
            Assert.False(card.SaveCommand.CanExecute(null));
            var listenOn = window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.FormField>().Single(f => f.Label == "Listen on" && f.IsEffectivelyVisible);
            Assert.Contains(McpTexts.UnknownInterface, listenOn.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Save(window, outDir, "client-settings-ai-mcp-interface-error.png");

            card.SelectedInterface = card.Listen.Find("eth1"); // another choice clears it
            Pump();
            Assert.Null(card.ErrorOf(nameof(AiAgentsViewModel.SelectedInterface)));
            Assert.True(card.SaveCommand.CanExecute(null));
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task TheSettingsPageShowsTheCardToEveryUserAndOnlyAdministratorsChangeIt()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            foreach (var (role, name) in new[] { (UserRole.Admin, "client-page-ai.png"), (UserRole.Operator, "client-page-ai-operator.png") })
            {
                var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
                using var f = new DevicesFixture(api);
                using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerConnection>.Instance);
                var session = Session(role);
                var card = new AiAgentsViewModel(api, Substitute.For<IDialogService>(), session);
                await card.LoadAsync();
                if (role == UserRole.Admin)
                {
                    await card.TurnOnCommand.ExecuteAsync(null);
                }

                var window = new Window { Width = 1520, Height = 1100, Content = new AiPageView { DataContext = card, Margin = new Avalonia.Thickness(24) } };
                window.Show();
                Pump();
                var view = Assert.Single(window.GetVisualDescendants().OfType<AiPageView>(), c => c.IsEffectivelyVisible);
                Pump();
                var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
                Assert.Equal(role == UserRole.Admin, view.FindControl<Button>("McpSave")!.IsEffectivelyVisible);
                Assert.Equal(role == UserRole.Admin, view.FindControl<TextBox>("McpPort")!.IsEffectivelyVisible);
                Save(window, outDir, name);
                window.Close();
            }

            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task ConnectAnAgentShowsTheConfigurationsOfThisServerWithCopyAndDownloadSkillSavesTheZip()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerConnection>.Instance);
            var dialogs = Substitute.For<IDialogService>();
            dialogs.SaveFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<byte[]>()).Returns("oadm-skill.zip");
            var clipboard = Substitute.For<IClipboardService>();
            clipboard.SetTextAsync(Arg.Any<string>()).Returns(true);
            var session = Session(UserRole.Admin);
            var card = new AiAgentsViewModel(api, dialogs, session, clipboard);
            await card.LoadAsync();
            Assert.False(card.HasConnections); // off: no address to give

            await card.TurnOnCommand.ExecuteAsync(null);
            Assert.True(card.HasConnections);
            Assert.Equal(["Claude Code", ".mcp.json", "VS Code"], card.Connections.Select(c => c.Title));
            var code = card.Connections[0].Text;
            Assert.Equal("claude mcp add --transport http oadm https://oadm-server.local:5082/mcp --header \"Authorization: Bearer ${OADM_KEY}\"", code);
            Assert.All(card.Connections, c => Assert.Contains("https://oadm-server.local:5082/mcp", c.Text, StringComparison.Ordinal));
            Assert.Contains("\"password\": true", card.Connections[2].Text, StringComparison.Ordinal);
            Assert.Contains("\"servers\"", card.Connections[2].Text, StringComparison.Ordinal);
            Assert.All(card.Connections, c => Assert.DoesNotContain("npx", c.Text, StringComparison.OrdinalIgnoreCase));
            Assert.All(card.Connections, c => Assert.DoesNotContain("mcp-remote", c.Text, StringComparison.OrdinalIgnoreCase));
            Assert.All(card.Connections, c => Assert.DoesNotContain("oadm_", c.Text, StringComparison.Ordinal)); // never a key

            await card.CopyCommand.ExecuteAsync(card.Connections[0]);
            await clipboard.Received(1).SetTextAsync(code);
            Assert.Equal("Copied Claude Code.", card.Message);

            await card.DownloadSkillCommand.ExecuteAsync(null);
            await dialogs.Received(1).SaveFileAsync("Download skill", "oadm-skill.zip", Arg.Any<FileType>(), Arg.Is<byte[]>(b => b.Length > 4 && b[0] == (byte)'P' && b[1] == (byte)'K'));
            Assert.Equal("Saved oadm-skill.zip.", card.Message);

            var window = new Window { Width = 1520, Height = 1500, Content = new AiPageView { DataContext = card, Margin = new Avalonia.Thickness(24) } };
            window.Show();
            Pump();
            var view = Assert.Single(window.GetVisualDescendants().OfType<AiPageView>(), c => c.IsEffectivelyVisible);
            Pump();
            Assert.True(view.FindControl<Control>("ConnectAgent")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("McpDownloadSkill")!.IsEffectivelyVisible);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.CodeView>(), c => c.IsEffectivelyVisible); // one compact row each, no file blocks
            Assert.Equal(["Claude Code", ".mcp.json", "VS Code"], view.FindControl<ComboBox>("ConnectClient")!.Items.OfType<AgentConnection>().Select(c => c.Title));
            Assert.Equal(0, view.FindControl<ComboBox>("ConnectClient")!.SelectedIndex);
            Assert.True(view.FindControl<Button>("ConnectCopy")!.IsEffectivelyVisible);
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Connect an agent", texts);
            Save(window, outDir, "client-page-ai-mcp-connect.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Theory]
    [InlineData("oadm-server.local|10.0.0.17", "oadm-server.local")]
    [InlineData("10.0.0.17|oadm-server.local", "oadm-server.local")]
    [InlineData("10.0.0.17", "10.0.0.17")]
    [InlineData("fd00::17", "[fd00::17]")]
    [InlineData("", null)]
    [InlineData("10.0.0.17|127.0.0.1|::1|oadm-server.local|localhost", "oadm-server.local")] // the loopback names are never handed out
    [InlineData("127.0.0.1|::1|10.0.0.17|localhost", "10.0.0.17")]
    public void TheAgentConnectsToANameOfTheCertificate(string names, string? host) =>
        Assert.Equal(host, AgentConnections.HostOf(names.Split('|', StringSplitOptions.RemoveEmptyEntries)));

    private static readonly IReadOnlyList<RoleOption> FormRoles =
    [
        new(FakeOadmApi.AdministratorRoleId, "Administrator", RoleKind.Administrator),
        new(FakeOadmApi.OperatorRoleId, "Operator", RoleKind.Operator),
        new(FakeOadmApi.ViewerRoleId, "Viewer", RoleKind.Viewer),
    ];

    [Theory]
    [InlineData("a$(x)", false)]
    [InlineData("quote\"d", false)]
    [InlineData("back`tick", false)]
    [InlineData("semi;colon", false)]
    [InlineData("Lobby-bot 2.0_a", true)]
    public void AKeyNameAllowsOnlyLettersDigitsSpaceDashUnderscoreAndDotWithTheErrorBelowTheField(string name, bool fine)
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var form = new AccessKeyFormViewModel(api, null, FormRoles, [DeviceAccessOption.All]) { Role = FormRoles[2] };
        form.Name = name;
        Assert.Equal(fine ? null : AccessKeyWords.NameCharacters, form.ErrorOf(nameof(AccessKeyFormViewModel.Name)));
        Assert.Equal(fine, form.CreateCommand.CanExecute(null));
    }

    [Fact]
    public void DesktopSetupStartsWithNoRoleOrDeviceScopeAndExpiresIn30Days()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var lobby = new DeviceAccessOption("g1", "Lobby");
        var form = new AccessKeyFormViewModel(api, null, FormRoles, [DeviceAccessOption.All, lobby], desktopSetup: true);
        Assert.Equal("Set up a desktop app", form.Title);
        Assert.Equal(30, form.Lifetime.Days);
        Assert.Null(form.Role);
        Assert.Null(form.Devices);
        Assert.False(form.CreateCommand.CanExecute(null));

        form.Role = FormRoles[1];
        Assert.False(form.CreateCommand.CanExecute(null));
        form.Devices = lobby;
        Assert.True(form.CreateCommand.CanExecute(null));
    }

    [Fact]
    public void TheFormNeedsANameARoleAndDevicesAndAnAdministratorKeyHasAllDevices()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var form = new AccessKeyFormViewModel(api, null, FormRoles, [DeviceAccessOption.All, new DeviceAccessOption("g1", "Lobby")]);
        Assert.Null(form.ErrorOf(nameof(AccessKeyFormViewModel.Name))); // an untouched form shows no error
        Assert.Null(form.ErrorOf(nameof(AccessKeyFormViewModel.Role)));
        Assert.False(form.CreateCommand.CanExecute(null));
        Assert.Equal(AccessKeyWords.EnterName, form.CreateTip);
        Assert.Equal(90, form.Lifetime.Days);
        Assert.Equal(DeviceAccessOption.All, form.Devices);

        form.Name = "Lobby-bot";
        Assert.Equal(AccessKeyWords.ChooseRole, form.CreateTip);
        form.Role = FormRoles[0];
        Assert.False(form.IsDevicesEnabled);
        Assert.Equal(DeviceGroupWords.AdministratorsHaveAll, form.DevicesHint);
        form.Role = FormRoles[1];
        Assert.True(form.IsDevicesEnabled);
        form.Devices = null;
        Assert.Equal(DeviceGroupWords.ChooseDevices, form.ErrorOf(nameof(AccessKeyFormViewModel.Devices)));
        Assert.False(form.CreateCommand.CanExecute(null));
        form.Role = FormRoles[0]; // an Administrator key needs no choice
        Assert.Null(form.ErrorOf(nameof(AccessKeyFormViewModel.Devices)));
        Assert.True(form.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task CreateShowsTheKeyOnceWithCopyAndTheRefusalsOfTheServerBelowTheirFields()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var groups = await api.ListDeviceGroupsAsync(CancellationToken.None);
            var lobby = groups.Groups.First();
            var clipboard = Substitute.For<IClipboardService>();
            clipboard.SetTextAsync(Arg.Any<string>()).Returns(true);
            var options = new[] { DeviceAccessOption.All }.Concat(groups.Groups.Select(g => new DeviceAccessOption(g.Id, g.Name))).ToList();
            var form = new AccessKeyFormViewModel(api, clipboard, FormRoles, options);
            var window = new AccessKeyFormWindow();
            window.Attach(form);
            window.Show();
            Pump();

            // A name the server has: its refusal directly below the Name field, the border red.
            form.Name = "health check";
            form.Role = FormRoles[2];
            await form.CreateCommand.ExecuteAsync(null);
            Pump();
            Assert.Equal("An access key with this name exists.", form.ErrorOf(nameof(AccessKeyFormViewModel.Name)));
            var nameField = window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.FormField>().Single(f => f.Label == "Name");
            Assert.Contains("An access key with this name exists.", nameField.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Assert.False(form.HasNewKey);
            Save(window, outDir, "client-access-key-name-taken.png");

            form.Name = "Lobby-bot";
            form.Role = FormRoles[1];
            form.Devices = options.Single(o => o.GroupId == lobby.Id);
            form.Lifetime = AccessKeyFormViewModel.Lifetimes[0];
            Save(window, outDir, "client-access-key-form.png");
            await form.CreateCommand.ExecuteAsync(null);
            Pump();
            Assert.StartsWith("oadm_", form.NewKey, StringComparison.Ordinal);
            Assert.True(window.FindControl<Control>("NewKeyBox")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Control>("KeyName")!.IsEffectivelyVisible); // the form gives way to the key
            Assert.False(window.FindControl<Button>("CreateKey")!.IsEffectivelyVisible);
            Assert.Equal("Close", form.CloseText);
            Assert.False(form.CreateCommand.CanExecute(null)); // one key per dialog
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains(AccessKeyWords.CopyNow, texts);
            await form.CopyKeyCommand.ExecuteAsync(null);
            await clipboard.Received(1).SetTextAsync(form.NewKey!);
            Pump();
            Save(window, outDir, "client-access-key-created.png");

            var made = (await api.ListAccessKeysAsync(CancellationToken.None)).Single(k => k.Name == "Lobby-bot");
            Assert.Equal(("Operator", false, lobby.Name), (made.RoleName, made.AllDevices, made.DeviceGroupName));
            Assert.InRange((made.Expires.ToDateTime() - made.Created.ToDateTime()).TotalDays, 29.9, 30.1);
            form.CloseCommand.Execute(null);
            Pump();
            Assert.False(window.IsVisible);
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task TheCardListsTheKeysForAdministratorsCreatesOneAndDeletesOneAfterAsking()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerConnection>.Instance);
            var groups = await api.ListDeviceGroupsAsync(CancellationToken.None);
            await api.CreateAccessKeyAsync(new CreateAccessKeyRequest
            {
                Name = "Lobby-bot", Days = 90, RoleId = FakeOadmApi.OperatorRoleId, DeviceGroupId = groups.Groups.First().Id,
            }, CancellationToken.None);
            var dialogs = Substitute.For<IDialogService>();
            dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            AccessKeyFormViewModel? shown = null;
            dialogs.ShowAccessKeyFormAsync(Arg.Any<AccessKeyFormViewModel>()).Returns(async call =>
            {
                shown = call.Arg<AccessKeyFormViewModel>();
                shown.Name = "Yard";
                shown.Role = shown.Roles.Single(r => r.Title == "Viewer");
                await shown.CreateCommand.ExecuteAsync(null);
            });
            var session = Session(UserRole.Admin);
            var card = new AiAgentsViewModel(api, dialogs, session);
            await card.LoadAsync();
            await card.TurnOnCommand.ExecuteAsync(null);
            Assert.Equal(["Health check", "Lobby-bot"], card.Keys.Keys.Select(k => k.Name));
            var lobby = card.Keys.Keys.Single(k => k.Name == "Lobby-bot");
            Assert.Equal(("Operator", groups.Groups.First().Name, AccessKeyWords.NeverUsed), (lobby.RoleName, lobby.DevicesText, lobby.LastUsed));
            Assert.Equal(DeviceGroupWords.AllDevices, card.Keys.Keys.Single(k => k.Name == "Health check").DevicesText);

            await card.Keys.CreateCommand.ExecuteAsync(null);
            Assert.NotNull(shown);
            Assert.Contains(shown.Roles, r => r.Title == "Administrator"); // every role of the server
            Assert.Equal(DeviceAccessOption.All, shown.DeviceOptions[0]);
            Assert.Equal(groups.Groups.Count + 1, shown.DeviceOptions.Count);
            Assert.Equal(["Health check", "Lobby-bot", "Yard"], card.Keys.Keys.Select(k => k.Name)); // the list after Create

            var window = new Window { Width = 1520, Height = 1600, Content = new AiPageView { DataContext = card, Margin = new Avalonia.Thickness(24) } };
            window.Show();
            Pump();
            var view = Assert.Single(window.GetVisualDescendants().OfType<AiPageView>(), c => c.IsEffectivelyVisible);
            Pump();
            Assert.True(view.FindControl<Button>("McpAccessKeys")!.IsEffectivelyVisible); // the list is a dialog now
            Assert.Null(view.FindControl<DataGrid>("KeysGrid"));
            Save(window, outDir, "client-page-ai-mcp.png");
            await card.ShowAccessKeysCommand.ExecuteAsync(null);
            await dialogs.Received(1).ShowAccessKeysAsync(card.Keys);

            var keysWindow = new AccessKeysWindow();
            keysWindow.Attach(card.Keys);
            keysWindow.Show();
            Pump();
            var grid = keysWindow.FindControl<DataGrid>("KeysGrid")!;
            Assert.Equal(["Name", "Role", "Devices", "Created", "Expires", "Last used"], grid.Columns.Take(6).Select(c => c.Header as string));
            Assert.True(keysWindow.FindControl<Control>("CreateAccessKey")!.IsEffectivelyVisible);
            Save(keysWindow, outDir, "client-access-keys-dialog.png");

            await card.Keys.DeleteCommand.ExecuteAsync(card.Keys.Keys.Single(k => k.Name == "Yard"));
            await dialogs.Received(1).ConfirmAsync("Delete access key", "Agents that use Yard can no longer connect.", "Delete"); // red: starts with Delete
            Assert.Equal(["Health check", "Lobby-bot"], card.Keys.Keys.Select(k => k.Name));
            card.Keys.CloseCommand.Execute(null); // Close of the dialog
            Assert.False(keysWindow.IsVisible);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task OtherRolesSeeNoAccessKeysAndTheAccountMenuHasNone()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetMcpAsync(Arg.Any<CancellationToken>()).Returns(new McpState { Settings = new McpSettings { Port = 5082 }, Status = McpTexts.Off, PkiAvailable = true });
        var card = new AiAgentsViewModel(api, Substitute.For<IDialogService>(), Session(UserRole.Operator));
        await card.LoadAsync();
        await api.DidNotReceive().ListAccessKeysAsync(Arg.Any<CancellationToken>()); // administrators only
        Assert.False(card.IsAdmin);

        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var app = (App)Avalonia.Application.Current!;
            var vm = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<MainWindowViewModel>(app.Services!);
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800 };
            window.Show();
            Pump();
            Assert.Null(window.FindControl<Button>("TitleUser")!.Flyout); // a click shows Your role: no account menu of keys
            window.Close();
            await Task.CompletedTask;
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task ConnectAnAgentCopiesTheChosenConfiguration()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var dialogs = Substitute.For<IDialogService>();
        dialogs.SaveFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<byte[]>()).Returns(c => c.ArgAt<string>(1));
        var clipboard = Substitute.For<IClipboardService>();
        clipboard.SetTextAsync(Arg.Any<string>()).Returns(true);
        var card = new AiAgentsViewModel(api, dialogs, Session(UserRole.Admin), clipboard);
        await card.LoadAsync();
        await card.TurnOnCommand.ExecuteAsync(null);
        var (code, project, vsCode) = (card.Connections[0], card.Connections[1], card.Connections[2]);
        Assert.Equal((false, true, true), (code.IsFile, project.IsFile, vsCode.IsFile));
        Assert.Equal(code.Text, code.Line); // the command in one line
        Assert.Equal(("For a project folder", "For .vscode/mcp.json"), (project.Line, vsCode.Line));

        await card.CopySelectedCommand.ExecuteAsync(null); // Claude Code is chosen first
        await clipboard.Received(1).SetTextAsync(code.Text);
        card.SelectedConnection = 2;
        await card.CopySelectedCommand.ExecuteAsync(null);
        await clipboard.Received(1).SetTextAsync(vsCode.Text);
        Assert.Equal("Copied VS Code.", card.Message);
        card.SelectedConnection = 1;
        await card.CopySelectedCommand.ExecuteAsync(null);
        await clipboard.Received(1).SetTextAsync(Arg.Is<string>(t => t.Contains("\"mcpServers\"", StringComparison.Ordinal)));
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
