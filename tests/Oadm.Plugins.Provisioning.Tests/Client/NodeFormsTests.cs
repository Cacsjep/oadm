using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Certificates;
using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests.Client;

/// <summary>The file picker of the editor window: the files a test chooses.</summary>
internal sealed class FakeEditorUi : IProfileEditorUi
{
    public List<string> Files { get; } = [];

    /// <summary>The file types of the last file dialog.</summary>
    public IReadOnlyList<string> LastPatterns { get; private set; } = [];

    public Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple, IReadOnlyList<string> patterns)
    {
        LastPatterns = patterns;
        return Task.FromResult<IReadOnlyList<string>>([.. Files]);
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
    public async Task Network_keeps_the_address_or_takes_a_pool_with_dns_and_has_no_host_name()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.Network);
        Assert.Equal(["ip", "first", "last", "subnetMask", "gateway", "dnsServers", "searchDomain"], node.Fields.Select(f => f.Key));
        Assert.False(node.Field<TextFieldViewModel>("first").IsShown); // Keep: no pool fields
        var dns = node.Field<ListFieldViewModel>("dnsServers");
        dns.Text = "10.0.0.1, 10.0.0.2, 10.0.0.3";
        Assert.Equal("Enter at most 2 DNS servers.", node.FirstError);
        dns.Text = "10.0.0.1";
        node.Field<TextFieldViewModel>("searchDomain").Text = "site.local";
        await NoErrorAsync(editor, node);

        var settings = await SaveAsync(editor, ctx, NodeKind.Network);
        Assert.Equal("keep", (string?)settings["ip"]);
        Assert.Equal(["10.0.0.1"], settings["dnsServers"]!.AsArray().Select(d => (string?)d));
        Assert.Equal("site.local", (string?)settings["searchDomain"]);
        Assert.False(settings.ContainsKey("hostName"));

        ProfileEditorTests.UsePool(node);
        Assert.True(node.Field<TextFieldViewModel>("first").IsShown);
        Assert.False(node.IsValid);
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
    public async Task Configuration_takes_a_configuration_file_and_what_to_apply()
    {
        var (editor, ctx, ui, node) = await AddAsync(NodeKind.Configuration);
        var file = node.Field<FileFieldViewModel>("fileId");
        Assert.True(file.IsShown);
        Assert.Equal(2, node.Fields.Count); // the file and the areas, no other source
        Assert.Equal(ProvisioningTexts.ChooseIt(ProvisioningTexts.ConfigFile), node.FirstError);

        // Only a configuration file (.cfg): the error directly below the field.
        ui.Files.Add("C:/cfg/lobby.json");
        await file.ChooseCommand.ExecuteAsync(null);
        Assert.Equal(["*.cfg"], ui.LastPatterns);
        Assert.Equal(ProvisioningTexts.ConfigFileRule, file.Error);
        Assert.Equal(ProvisioningTexts.ConfigFileRule, node.FirstError);

        ui.Files.Clear();
        ui.Files.Add("C:/cfg/lobby.cfg");
        await file.ChooseCommand.ExecuteAsync(null);
        await node.Preview!.LoadTask;
        Assert.Equal("lobby.cfg", file.FileName);
        Assert.Null(file.Error);
        await NoErrorAsync(editor, node);

        // Every area of the file at the start, like Configure devices with a file.
        var areas = node.Fields.OfType<CheckGroupFieldViewModel>().Single();
        Assert.Equal([ProvisioningTexts.Parameters, ProvisioningTexts.ActionRules, ProvisioningTexts.StreamProfiles], areas.Items.Select(i => i.Label));
        Assert.All(areas.Items, i => Assert.True(i.IsChecked));
        foreach (var item in areas.Items)
        {
            item.IsChecked = false;
        }

        Assert.Equal(ProvisioningTexts.ChooseAreas, node.FirstError);
        areas.Items[1].IsChecked = true;
        await NoErrorAsync(editor, node);
        Assert.Equal("Action rules from lobby.cfg", node.Summary);

        var settings = await SaveAsync(editor, ctx, NodeKind.Configuration);
        Assert.NotNull((string?)settings["fileId"]);
        Assert.Equal("lobby.cfg", (string?)settings["fileName"]);
        Assert.False((bool)settings["parameters"]!);
        Assert.True((bool)settings["actionRules"]!);
        Assert.False((bool)settings["streamProfiles"]!);
        Assert.Null(settings["schedules"]);
    }

    private static IReadOnlyList<string> Texts(ConfigPreviewViewModel preview) =>
        [.. preview.Rows.Select(r => r switch { PreviewHeading h => "# " + h.Text, PreviewNote n => "- " + n.Text, PreviewEntry e => e.Text, _ => "?" })];

    [Fact]
    public async Task Configuration_shows_what_the_file_holds_of_the_ticked_areas()
    {
        var (editor, ctx, ui, node) = await AddAsync(NodeKind.Configuration);
        var preview = node.Preview!;
        Assert.False(preview.IsShown); // no file yet
        Assert.Equal(0, ctx.CallsOf(ProvisioningMethods.ReadConfigFile));

        // Reading the file: the ring in place until the server answered.
        var gate = new TaskCompletionSource();
        ctx.MethodGates[ProvisioningMethods.ReadConfigFile] = gate.Task;
        ui.Files.Add("C:/cfg/parking-p3265.cfg");
        await node.Field<FileFieldViewModel>("fileId").ChooseCommand.ExecuteAsync(null);
        var areas = node.Fields.OfType<CheckGroupFieldViewModel>().Single();
        Assert.True(preview.IsShown);
        Assert.True(preview.IsLoading);
        Assert.False(preview.HasRows);
        gate.SetResult();
        await preview.LoadTask;
        Assert.False(preview.IsLoading);
        Assert.Equal(1, ctx.CallsOf(ProvisioningMethods.ReadConfigFile));
        Assert.All(areas.Items, i => Assert.True(i.IsChecked)); // the file has all three areas
        areas.Items[1].IsChecked = false; // a change of the areas reads nothing more
        areas.Items[2].IsChecked = false;

        // Parameters only: what Configure devices applies on every camera (the payload of the run), the left-out ones in one line.
        var texts = Texts(preview);
        Assert.Equal("# Parameters, 22", texts[0]);
        Assert.Equal("- 5 more stay as they are on each camera: address, names and connection settings.", texts[1]);
        Assert.Contains("ImageSource.I0.CaptureFrequency = 60Hz", texts);
        Assert.Contains("Image.I0.Appearance.Compression = 30", texts);
        Assert.Contains("Image.I0.Name = View Area 1", texts);
        Assert.DoesNotContain(texts, t => t.StartsWith("Network.", StringComparison.Ordinal) || t.StartsWith("HTTPS.", StringComparison.Ordinal) || t.StartsWith("SNMP.V2.", StringComparison.Ordinal));
        Assert.Equal(22, texts.Count(t => !t.StartsWith('#') && !t.StartsWith('-')));
        var plan = DeviceConfig.AdmConfigApply.ForEveryDevice(DeviceConfig.AdmConfigFile.Read(Samples.ConfigFile), new(true, false, false), "parking-p3265.cfg");
        Assert.Equal(plan.Payload.Parameters.Select(p => p.Name + " = " + p.Value), texts.Skip(2));

        // Ticking an area adds its heading and names (the action rules with the own schedule they use); no second read.
        areas.Items[1].IsChecked = true;
        areas.Items[2].IsChecked = true;
        texts = Texts(preview);
        Assert.Equal(["# Action rules, 1", "re", "# Schedules, 1", "10", "# Stream profiles, 3", "View Area 1_ACS_Pro_High", "View Area 1_ACS_Pro_Low", "View Area 1_ACS_Pro_Medium"],
            texts.SkipWhile(t => t != "# Action rules, 1"));
        areas.Items[0].IsChecked = false;
        Assert.Equal("# Action rules, 1", Texts(preview)[0]);
        Assert.Equal(1, ctx.CallsOf(ProvisioningMethods.ReadConfigFile));
        Assert.Null(node.FirstError);
        Assert.NotNull(editor);
    }

    [Fact]
    public async Task Configuration_hides_the_passwords_of_the_file_in_the_preview()
    {
        var (_, ctx, ui, node) = await AddAsync(NodeKind.Configuration);
        ctx.Answers[ProvisioningMethods.ReadConfigFile] = _ => JsonSerializer.Serialize(new ConfigFileReply("snmp.cfg", Cfg(new DeviceConfig.AdmConfig
        {
            Parameters = [new("SNMP.V2.Community", "s3cret-community"), new("SNMP.Enabled", "yes")],
        })), Samples.Json);
        ui.Files.Add("C:/cfg/snmp.cfg");
        await node.Field<FileFieldViewModel>("fileId").ChooseCommand.ExecuteAsync(null);
        await node.Preview!.LoadTask;
        var texts = Texts(node.Preview);
        Assert.Contains("SNMP.V2.Community = " + ConfigPreviewViewModel.Hidden, texts);
        Assert.DoesNotContain(texts, t => t.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Configuration_ticks_only_the_areas_a_newly_chosen_file_has()
    {
        var (_, ctx, ui, node) = await AddAsync(NodeKind.Configuration);
        ctx.Answers[ProvisioningMethods.ReadConfigFile] = _ => JsonSerializer.Serialize(new ConfigFileReply("50hz.cfg", Cfg(new DeviceConfig.AdmConfig
        {
            Parameters = [new("ImageSource.I0.CaptureFrequency", "50Hz")],
        })), Samples.Json);
        ui.Files.Add("C:/cfg/50hz.cfg");
        await node.Field<FileFieldViewModel>("fileId").ChooseCommand.ExecuteAsync(null);
        await node.Preview!.LoadTask;
        var items = node.Fields.OfType<CheckGroupFieldViewModel>().Single().Items;
        Assert.Equal([true, false, false], items.Select(i => i.IsChecked));
        Assert.Equal(["# Parameters, 1", "ImageSource.I0.CaptureFrequency = 50Hz"], Texts(node.Preview));
        Assert.Null(node.FirstError);
    }

    private static string Cfg(DeviceConfig.AdmConfig file) => System.Text.Encoding.UTF8.GetString(DeviceConfig.AdmConfigFile.Write(file));

    [Fact]
    public async Task Configuration_says_when_a_ticked_area_is_empty_or_the_file_cannot_be_read()
    {
        var (_, ctx, ui, node) = await AddAsync(NodeKind.Configuration);
        var file = node.Field<FileFieldViewModel>("fileId");
        ctx.Answers[ProvisioningMethods.ReadConfigFile] = _ => JsonSerializer.Serialize(new ConfigFileReply("empty.cfg",
            Cfg(new DeviceConfig.AdmConfig { StreamProfiles = [new("Low", null, "resolution=640x360")] })), Samples.Json);
        ui.Files.Add("C:/cfg/empty.cfg");
        await file.ChooseCommand.ExecuteAsync(null);
        await node.Preview!.LoadTask;
        var items = node.Fields.OfType<CheckGroupFieldViewModel>().Single().Items;
        Assert.Equal([false, false, true], items.Select(i => i.IsChecked)); // only what the file has
        Assert.Equal(["# Stream profiles, 1", "Low"], Texts(node.Preview));
        items[0].IsChecked = true; // ticked by hand: the empty areas say so
        items[1].IsChecked = true;
        Assert.Equal(["# Parameters, 0", "- " + ProvisioningTexts.NoParameters, "# Action rules, 0", "- " + ProvisioningTexts.NoActionRules, "# Stream profiles, 1", "Low"],
            Texts(node.Preview));
        Assert.Null(file.Error);

        // Nothing of the ticked areas in the file: Configure devices would refuse, the error below the file.
        node.Fields.OfType<CheckGroupFieldViewModel>().Single().Items[2].IsChecked = false;
        Assert.Equal(DeviceConfig.ConfigurePayload.NothingToApply, file.Error);
        Assert.Equal(DeviceConfig.ConfigurePayload.NothingToApply, node.FirstError);
        node.Fields.OfType<CheckGroupFieldViewModel>().Single().Items[2].IsChecked = true;
        Assert.Null(file.Error);

        // A .cfg that is no configuration file: the reader's text below the field, no rows, Save stays off.
        ctx.Answers[ProvisioningMethods.ReadConfigFile] = _ => JsonSerializer.Serialize(new ConfigFileReply("notes.cfg", "[General]\nname=lobby"), Samples.Json);
        ui.Files.Clear();
        ui.Files.Add("C:/cfg/notes.cfg");
        await file.ChooseCommand.ExecuteAsync(null);
        await node.Preview.LoadTask;
        Assert.Equal(DeviceConfig.AdmConfigFile.NotAnAdmFile, file.Error);
        Assert.Equal(DeviceConfig.AdmConfigFile.NotAnAdmFile, node.FirstError);
        Assert.False(node.Preview.HasRows);

        // A file that is no longer on the server: the server's text below the field.
        ctx.Answers[ProvisioningMethods.ReadConfigFile] = _ => throw new KeyNotFoundException("The file is no longer on the server. Choose it again.");
        ui.Files.Clear();
        ui.Files.Add("C:/cfg/other.cfg");
        await file.ChooseCommand.ExecuteAsync(null);
        await node.Preview.LoadTask;
        Assert.Equal("The file is no longer on the server. Choose it again.", file.Error);
        Assert.False(node.Preview.HasRows);
        Assert.False(node.Preview.IsLoading);
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
    public async Task Certificate_asks_for_one_of_several_https_policies_and_chooses_est()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.Certificate);
        var mode = node.Field<ChoiceFieldViewModel>("mode");
        Assert.Equal(ProvisioningTexts.CertificatePolicy, mode.Selected.Label);
        var policy = node.Field<PolicyFieldViewModel>("policyId");
        Assert.Equal(["Cameras", "Lobby, 1 year"], policy.Policies.Select(p => p.Name)); // HTTPS only, PKI's order
        Assert.Null(policy.Selected); // two policies: the user chooses
        Assert.Equal(PolicyChoice.Choose, node.FirstError);

        policy.Selected = policy.Policies[1];
        await NoErrorAsync(editor, node);
        Assert.Equal("Lobby, 1 year", node.Summary);
        var settings = await SaveAsync(editor, ctx, NodeKind.Certificate);
        Assert.Equal(Samples.Policies[1].Id.ToString(), (string?)settings["policyId"]);

        mode.Selected = mode.Choices.Single(c => c.Value == "est");
        Assert.False(policy.IsShown);
        await NoErrorAsync(editor, node);
        settings = await SaveAsync(editor, ctx, NodeKind.Certificate);
        Assert.Equal("est", (string?)settings["mode"]);
    }

    [Fact]
    public async Task Dot1x_takes_the_only_802_1x_policy()
    {
        var (editor, ctx, _, node) = await AddAsync(NodeKind.Dot1x);
        var policy = node.Field<PolicyFieldViewModel>("policyId");
        Assert.Equal("Switch ports", Assert.Single(policy.Policies).Name);
        Assert.Equal("Switch ports", policy.Selected?.Name); // the only one is chosen already
        await NoErrorAsync(editor, node);
        var settings = await SaveAsync(editor, ctx, NodeKind.Dot1x);
        Assert.Equal(Samples.Policies[2].Id.ToString(), (string?)settings["policyId"]);
    }

    [Fact]
    public void A_policy_pki_no_longer_has_or_no_policy_at_all_says_so()
    {
        var gone = new PolicyFieldViewModel("policyId", "Policy", IssuingPolicyUse.Https, Guid.NewGuid(), Samples.Policies);
        Assert.Null(gone.Selected);
        Assert.Equal(PolicyChoice.Gone, gone.Error); // shown at once

        var none = new PolicyFieldViewModel("policyId", "Policy", IssuingPolicyUse.Dot1x, null, []);
        Assert.Equal("Add an IEEE 802.1X certificate policy on the PKI page first.", none.FirstError);
    }

    [Fact]
    public async Task Nodes_without_settings_say_what_they_do()
    {
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
        var (editor, ctx, _, node) = await AddAsync(NodeKind.Network);
        ProfileEditorTests.UsePool(node);
        node.Field<TextFieldViewModel>("first").Text = "192.168";
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
