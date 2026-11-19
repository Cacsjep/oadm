using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests.Client;

/// <summary>The pickers of the editor window: the files and the camera a test chooses.</summary>
internal sealed class FakeEditorUi : IProfileEditorUi
{
    public List<string> Files { get; } = [];

    public Guid? Device { get; set; }

    public List<IReadOnlyList<IDeviceInfo>> DeviceLists { get; } = [];

    public Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple, IReadOnlyList<string> patterns) =>
        Task.FromResult<IReadOnlyList<string>>([.. Files]);

    public Task<Guid?> PickDeviceAsync(IReadOnlyList<IDeviceInfo> devices, Guid? selected)
    {
        DeviceLists.Add(devices);
        return Task.FromResult(Device);
    }
}

/// <summary>The form of every node kind: its fields, their rules (as the server checks the node) and what Save stores.</summary>
public sealed class NodeFormsTests
{
    private static async Task<(ProfileEditorViewModel Editor, FakePageContext Ctx, FakeEditorUi Ui, NodeEditorViewModel Node)> AddAsync(NodeKind kind)
    {
        var ctx = Samples.Context();
        var ui = new FakeEditorUi();
        var editor = new ProfileEditorViewModel(ctx, null) { Ui = ui };
        await editor.LoadAsync();
        await editor.CheckTask;
        if (editor.Nodes.FirstOrDefault(n => n.Kind == kind) is { } present)
        {
            editor.SelectedNode = present; // Set IP: a new profile has it
        }
        else
        {
            editor.AddNode(Samples.Kinds.Single(k => k.Kind == kind));
        }
        await editor.CheckTask;
        return (editor, ctx, ui, editor.SelectedNode!);
    }

    private static async Task<JsonObject> SaveAsync(ProfileEditorViewModel editor, FakePageContext ctx, NodeKind kind)
    {
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Null(editor.ErrorText);
        var saved = JsonSerializer.Deserialize<SaveProfileRequest>(ctx.LastPayloadOf(ProvisioningMethods.SaveProfile)!, Samples.Json)!;
        return (JsonObject)JsonNode.Parse(saved.Profile.Nodes.Single(n => n.Kind == kind).Settings!.Value.GetRawText())!;
    }

    private static async Task NoErrorAsync(ProfileEditorViewModel editor, NodeEditorViewModel node)
    {
        await editor.CheckTask;
        Assert.Null(node.FirstError);
        Assert.Null(node.CheckError); // the server's own check agrees
    }

    [Fact]
    public async Task Every_kind_has_its_own_form_with_plain_labels()
    {
        foreach (var kind in Samples.Kinds.Where(k => !k.IsFixed))
        {
            var (_, _, _, node) = await AddAsync(kind.Kind);
            Assert.NotEqual(ProvisioningTexts.UnknownNode, node.Note);
            Assert.True(node.HasFields || node.Note is not null, $"{kind.Name} has fields or says what it does");
            Assert.All(node.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Label)));
        }
    }

    [Fact]
    public async Task Upgrade_online_takes_the_target_and_an_optional_version()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.UpgradeOnline);
        Assert.Equal(ProvisioningTexts.MyAxisHint, node.Note);
        var target = node.Field<ChoiceFieldViewModel>("target");
        Assert.Equal(ProvisioningTexts.OwnTrack, target.Selected.Label);
        target.Selected = target.Choices.Single(c => c.Value == "latestLts");
        var older = node.Field<TextFieldViewModel>("onlyIfOlderThan");
        older.Text = "12.x";
        Assert.Equal("Enter a version like 12.6.95.", older.ErrorOf(nameof(TextFieldViewModel.Text)));
        older.Text = "12.6.95";
        await NoErrorAsync(editor, node);
        Assert.Equal("Newest LTS, if older than 12.6.95", node.Summary);

        var settings = await SaveAsync(editor, ctx, NodeKind.UpgradeOnline);
        Assert.Equal("latestLts", (string?)settings["target"]);
        Assert.Equal("12.6.95", (string?)settings["onlyIfOlderThan"]);
    }

    [Fact]
    public async Task Upgrade_from_files_uploads_the_chosen_files_one_per_model()
    {
        var (editor, ctx, ui, node) = await AddAsync(NodeKind.UpgradeFiles);
        var files = node.Field<FilesFieldViewModel>("files");
        Assert.Equal(ProvisioningTexts.ChooseOsFiles, node.FirstError);

        ui.Files.AddRange(["C:/os/P3265-LVE_12_6_95.bin", "C:/os/M3086-V_12_6_95.bin"]);
        await files.ChooseCommand.ExecuteAsync(null);
        Assert.Equal(2, files.Count);
        Assert.Equal(2, ctx.Uploads.Count);
        Assert.Equal(2, ctx.CallsOf(ProvisioningMethods.StoreFile));
        Assert.Equal("2 files", files.Summary);
        await NoErrorAsync(editor, node);

        ui.Files.Clear();
        ui.Files.Add("C:/os/P3265-LVE_12_6_96.bin");
        await files.ChooseCommand.ExecuteAsync(null);
        Assert.StartsWith("Two files are for", node.FirstError, StringComparison.Ordinal);
        files.RemoveFileCommand.Execute(files.Files[^1]);
        Assert.Null(node.FirstError);

        var settings = await SaveAsync(editor, ctx, NodeKind.UpgradeFiles);
        Assert.Equal(["P3265-LVE_12_6_95.bin", "M3086-V_12_6_95.bin"], settings["files"]!.AsArray().Select(f => (string?)f!["name"]));
    }

    [Fact]
    public async Task Network_settings_need_the_dns_servers_and_have_no_host_name()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.NetworkSettings);
        Assert.Equal(["dnsServers", "searchDomain"], node.Fields.Select(f => f.Key));
        var dns = node.Field<ListFieldViewModel>("dnsServers");
        dns.Text = "10.0.0.1";
        dns.Text = string.Empty;
        Assert.Equal("Enter the DNS servers.", dns.ErrorOf(nameof(ListFieldViewModel.Text)));
        dns.Text = "10.0.0.1, 10.0.0.2, 10.0.0.3";
        Assert.Equal("Enter at most 2 DNS servers.", node.FirstError);
        dns.Text = "10.0.0.1";
        node.Field<TextFieldViewModel>("searchDomain").Text = "site.local";
        await NoErrorAsync(editor, node);

        var settings = await SaveAsync(editor, ctx, NodeKind.NetworkSettings);
        Assert.Equal(["10.0.0.1"], settings["dnsServers"]!.AsArray().Select(d => (string?)d));
        Assert.Equal("site.local", (string?)settings["searchDomain"]);
        Assert.False(settings.ContainsKey("hostName"));
    }

    [Fact]
    public async Task Date_and_time_chooses_dhcp_its_own_servers_or_oadms_ntp_server()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.DateTime);
        var zone = node.Field<TimeZoneFieldViewModel>("timeZone");
        Assert.Equal(ProvisioningTexts.ChooseTimeZone, zone.FirstError);
        Assert.Contains(TimeZoneFieldViewModel.Zones, z => z.Id == "Europe/Vienna");
        zone.Selected = TimeZoneFieldViewModel.Zones.Single(z => z.Id == "Europe/Vienna");
        Assert.Null(zone.FirstError);
        var source = node.Field<ChoiceFieldViewModel>("ntpSource");
        var servers = node.Field<ListFieldViewModel>("ntpServers");
        Assert.False(servers.IsShown);
        source.Selected = source.Choices.Single(c => c.Value == "own");
        Assert.True(servers.IsShown);
        servers.Text = "pool ntp";
        Assert.Equal("pool ntp is not a host name or IP address.", servers.ErrorOf(nameof(ListFieldViewModel.Text)));
        servers.Text = "10.0.0.17";
        source.Selected = source.Choices.Single(c => c.Value == "oadm");
        Assert.False(servers.IsShown);
        await NoErrorAsync(editor, node);
        Assert.Equal("Europe/Vienna, OADM's NTP server", node.Summary);

        var settings = await SaveAsync(editor, ctx, NodeKind.DateTime);
        Assert.True((bool)settings["oadmNtp"]!);
        Assert.Empty(settings["ntpServers"]!.AsArray());
        Assert.False((bool)settings["nts"]!);
        Assert.Null(settings["ntpSource"]);
    }

    [Fact]
    public async Task Configuration_takes_a_file_or_a_template_camera_and_what_to_apply()
    {
        var (editor, ctx, ui, node) = await AddAsync(NodeKind.Configuration);
        var file = node.Field<FileFieldViewModel>("fileId");
        var camera = node.Field<CameraFieldViewModel>("templateSerial");
        Assert.True(file.IsShown);
        Assert.False(camera.IsShown);
        Assert.Equal(ProvisioningTexts.ChooseIt(ProvisioningTexts.ConfigFile), node.FirstError);

        ui.Files.Add("C:/cfg/lobby.json");
        await file.ChooseCommand.ExecuteAsync(null);
        Assert.Equal("lobby.json", file.FileName);
        await NoErrorAsync(editor, node);

        var areas = node.Fields.OfType<CheckGroupFieldViewModel>().Single();
        areas.Items[0].IsChecked = false;
        Assert.Equal(ProvisioningTexts.ChooseAreas, node.FirstError);
        areas.Items[1].IsChecked = true;

        // The template camera, with the device choice over the cameras OADM manages.
        var template = new FakeDevice { Serial = "ACCC8E000048", Address = "10.0.0.48", Model = "AXIS P3265-LVE" };
        ctx.DeviceList.Add(template);
        var source = node.Field<ChoiceFieldViewModel>("source");
        source.Selected = source.Choices.Single(c => c.Value == "templateCamera");
        Assert.False(file.IsShown);
        Assert.Equal(ProvisioningTexts.ChooseTemplate, node.FirstError);
        ui.Device = template.Id;
        await camera.ChooseCommand.ExecuteAsync(null);
        Assert.Same(template, Assert.Single(Assert.Single(ui.DeviceLists)));
        Assert.Equal("AXIS P3265-LVE, 10.0.0.48", camera.ButtonText);
        await NoErrorAsync(editor, node);

        var settings = await SaveAsync(editor, ctx, NodeKind.Configuration);
        Assert.Equal("templateCamera", (string?)settings["source"]);
        Assert.Equal("ACCC8E000048", (string?)settings["templateSerial"]);
        Assert.False((bool)settings["parameters"]!);
        Assert.True((bool)settings["actionRules"]!);
    }

    [Fact]
    public async Task Users_accounts_check_their_names_and_send_each_password_once_with_save()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.Users);
        var users = node.Field<UsersFieldViewModel>("accounts");
        Assert.Equal(ProvisioningTexts.AddAccountFirst, node.FirstError);
        users.AddAccountCommand.Execute(null);
        users.AddAccountCommand.Execute(null);
        var (first, second) = (users.Rows[0], users.Rows[1]);
        first.Name = "root";
        Assert.Equal(ProvisioningTexts.RootIsSetPassword, first.ErrorOf(nameof(UserRowViewModel.Name)));
        first.Name = "operator1";
        second.Name = "Operator1";
        Assert.Equal("The account Operator1 is there twice.", second.ErrorOf(nameof(UserRowViewModel.Name)));
        second.Name = "viewer1";
        first.Role = UsersFieldViewModel.Roles.Single(r => r.Value == "operator");
        first.Password.Text = "Pass-123";
        first.Password.Confirm = "Pass-123";
        second.Password.Text = "View-456";
        second.Password.Confirm = "View-456";
        Assert.Null(node.FirstError);
        await editor.CheckTask;
        Assert.Null(node.CanvasError); // the server misses the secrets until Save sends them

        var settings = await SaveAsync(editor, ctx, NodeKind.Users);
        Assert.Equal(2, ctx.CallsOf(ProvisioningMethods.StoreSecret));
        Assert.Equal(1, ctx.Calls.Count(c => c.Payload?.Contains("Pass-123", StringComparison.Ordinal) == true));
        Assert.DoesNotContain("Pass-123", ctx.LastPayloadOf(ProvisioningMethods.SaveProfile), StringComparison.Ordinal);
        var accounts = settings["accounts"]!.AsArray();
        Assert.Equal(["operator1", "viewer1"], accounts.Select(a => (string?)a!["name"]));
        Assert.Equal("operator", (string?)accounts[0]!["role"]);
        Assert.All(accounts, a => Assert.NotNull((string?)a!["secretId"]));
        Assert.Equal(string.Empty, first.Password.Text);
        Assert.Equal(ProvisioningTexts.StoredSecret, first.Password.StoredPlaceholder);
    }

    [Fact]
    public async Task Certificate_keeps_its_policy_and_chooses_est()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.Certificate);
        var mode = node.Field<ChoiceFieldViewModel>("mode");
        Assert.Equal(ProvisioningTexts.CertificatePolicy, mode.Selected.Label);
        mode.Selected = mode.Choices.Single(c => c.Value == "est");
        await NoErrorAsync(editor, node);
        var settings = await SaveAsync(editor, ctx, NodeKind.Certificate);
        Assert.Equal("est", (string?)settings["mode"]);
    }

    [Fact]
    public async Task Nodes_without_settings_say_what_they_do()
    {
        var (_, _, _, dot1x) = await AddAsync(NodeKind.Dot1x);
        Assert.Equal(ProvisioningTexts.Dot1xHint, dot1x.Note);
        Assert.False(dot1x.HasFields);
        var (_, _, _, focus) = await AddAsync(NodeKind.Autofocus);
        Assert.Equal(ProvisioningTexts.AutofocusHint, focus.Note);
        Assert.False(focus.HasFields);
    }

    [Fact]
    public async Task Sd_card_asks_for_the_passphrase_only_when_it_encrypts()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.SdCard);
        var passphrase = node.Field<SecretFieldViewModel>(SecretFieldViewModel.SecretKey);
        var group = node.Fields.OfType<CheckGroupFieldViewModel>().Single();
        Assert.False(passphrase.IsShown);
        group.Items[0].IsChecked = false;
        Assert.Equal(ProvisioningTexts.ChooseSdCard, node.FirstError);
        group.Items[1].IsChecked = true;
        Assert.True(passphrase.IsShown);
        Assert.Equal(ProvisioningTexts.TypeIt(ProvisioningTexts.Passphrase), node.FirstError);
        passphrase.Text = "Card-pass-1";
        passphrase.Confirm = "Card-pass-1";
        var days = node.Field<TextFieldViewModel>("maxAgeDays");
        days.Text = "0";
        Assert.Equal("Enter 1 to 7000 days.", days.ErrorOf(nameof(TextFieldViewModel.Text)));
        days.Text = "30";
        Assert.Null(node.FirstError);

        var settings = await SaveAsync(editor, ctx, NodeKind.SdCard);
        Assert.True((bool)settings["encrypt"]!);
        Assert.Equal(30, (int)settings["maxAgeDays"]!);
        Assert.NotNull((string?)settings["secretId"]);
        Assert.Equal(1, ctx.CallsOf(ProvisioningMethods.StoreSecret));
    }

    [Fact]
    public async Task Vapix_commands_come_from_the_library_and_check_their_values()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.VapixCommands);
        var commands = node.Field<VapixCommandsFieldViewModel>("commands");
        Assert.NotEmpty(commands.Commands);
        Assert.Equal(ProvisioningTexts.AddCommandFirst, node.FirstError);
        commands.AddCommandCommand.Execute(null);
        var step = commands.Rows[0];
        Assert.Equal(ProvisioningTexts.ChooseCommand, step.Error);

        // A whole number that is not one: the Commander's own check, below the value.
        step.Command = commands.Commands.First(c => c.Fields.Any(f => f.Type == VapixCommander.FieldTypes.Integer));
        var value = step.Values.First(v => v.Field.Type == VapixCommander.FieldTypes.Integer);
        value.Text = "abc";
        Assert.Equal("Must be a whole number.", value.ErrorOf(nameof(CommandValueViewModel.Text)));
        Assert.NotNull(node.FirstError);

        // A command without fields runs as it is; the server's check agrees.
        step.Command = commands.Commands.First(c => c.Fields.Count == 0);
        await NoErrorAsync(editor, node);
        var settings = await SaveAsync(editor, ctx, NodeKind.VapixCommands);
        Assert.Equal(step.Command!.Id, (string?)settings["commands"]![0]!["commandId"]);
    }

    [Fact]
    public async Task A_profile_with_node_errors_is_saved_as_a_draft()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.SetIp);
        Assert.NotNull(node.CanvasError);
        Assert.True(editor.IsDraft);
        Assert.True(editor.SaveCommand.CanExecute(null));
        Assert.Equal(ProvisioningTexts.DraftTip, editor.SaveTip);
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(ProvisioningMethods.SaveProfile));
        Assert.NotNull(editor.Saved);
    }

    [Fact]
    public async Task Every_field_of_every_node_has_a_short_help_that_does_not_repeat_its_label()
    {
        var ctx = Samples.Context();
        var editor = new ProfileEditorViewModel(ctx, null) { Ui = new FakeEditorUi() };
        await editor.LoadAsync();
        foreach (var kind in editor.AddableKinds.ToList())
        {
            editor.AddNode(kind);
        }

        // The rows of Set password, Users and VAPIX commands have their help in the window (fixed labels).
        var fields = editor.Nodes.SelectMany(n => n.Fields)
            .Where(f => f is not PasswordFieldViewModel and not UsersFieldViewModel and not VapixCommandsFieldViewModel)
            .ToList();
        Assert.NotEmpty(fields);
        Assert.All(fields, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Help), $"{f.Key} has no help");
            Assert.NotEqual(f.Label, f.Help, StringComparer.OrdinalIgnoreCase);
        });

        var helps = typeof(ProvisioningTexts).GetFields().Where(f => f.Name.EndsWith("Help", StringComparison.Ordinal)).Select(f => (string)f.GetValue(null)!).ToList();
        Assert.True(helps.Count >= 30);
        Assert.All(helps, h => Assert.InRange(h.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 2, 9));
        Assert.All(helps, h => Assert.False(h.EndsWith('.'), h));
    }
}
