using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Provisioning.Tests.Client;

/// <summary>The profile editor against the sample server and the server's own profile check: no Avalonia.</summary>
public sealed class ProfileEditorTests
{
    private static async Task<(ProfileEditorViewModel Editor, FakePageContext Ctx)> OpenAsync(Guid? id)
    {
        var ctx = Samples.Context();
        var editor = new ProfileEditorViewModel(ctx, id);
        await editor.LoadAsync();
        await editor.CheckTask;
        return (editor, ctx);
    }

    private static NodeKindInfo Kind(NodeKind kind) => Samples.Kinds.Single(k => k.Kind == kind);

    /// <summary>The Network node's address from a pool.</summary>
    internal static void UsePool(NodeEditorViewModel node)
    {
        var ip = node.Field<ChoiceFieldViewModel>("ip");
        ip.Selected = ip.Choices.Single(c => c.Value == "pool");
    }

    [Fact]
    public async Task A_new_profile_starts_with_set_password_alone_and_the_root_password_of_the_credential_list()
    {
        var (editor, _) = await OpenAsync(null);

        Assert.Equal(ProvisioningTexts.NewProfileTitle, editor.Title);
        Assert.Equal(ProvisioningTexts.DefaultName, editor.Name);
        Assert.Equal([NodeKind.SetPassword], editor.Nodes.Select(n => n.Kind));
        Assert.True(editor.Nodes[0].IsFixed);
        Assert.Equal(Samples.Kinds.Where(k => !k.IsFixed).Select(k => k.Kind).OrderBy(NodeLook.Rank), editor.AddableKinds.Select(k => k.Kind));
        Assert.Contains("VAPIX commands", editor.AddableKinds.Select(k => k.Name));
        Assert.DoesNotContain(editor.AddableKinds, k => k.Kind is NodeKind.AddToOadm or NodeKind.WaitForApproval or NodeKind.SetIp or NodeKind.NetworkSettings);
        var password = Assert.IsType<PasswordFieldViewModel>(Assert.Single(editor.Nodes[0].Fields));
        Assert.Equal("root", Assert.Single(password.Credentials).UserName); // only the root entries
        Assert.Equal(Samples.RootCredential, password.SelectedCredential?.Id);
        Assert.False(editor.Nodes[0].CanChangeContinueOnError);
        Assert.False(editor.Nodes[0].ContinueOnError);
        Assert.Single(editor.Chain.Nodes);
        Assert.False(editor.HasErrors); // untouched
        Assert.False(editor.IsDraft);
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.SelectedNode = editor.Nodes[0];
        Assert.False(editor.RemoveNodeCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_plus_below_a_node_opens_the_grouped_node_list_and_adds_the_kind_right_after_that_node()
    {
        var (editor, _) = await OpenAsync(null);

        // The + shows below the last node, also below Set password.
        Assert.True(editor.Chain.CanAdd);
        Assert.True(editor.Chain.ShowsPlus(0));

        editor.Chain.RequestAdd(0, new Avalonia.Point(200, 120)); // the + below Set password
        Assert.True(editor.Picker.IsOpen);
        Assert.Equal(200, editor.Picker.Left);
        Assert.Equal(editor.AddableKinds.Select(k => k.Name), editor.Picker.ShownItems.Select(i => i.Label));
        Assert.All(editor.Picker.ShownItems, i => Assert.False(string.IsNullOrWhiteSpace(i.Description)));
        Assert.Equal(["AXIS OS", "Settings", "Security", "Camera"], editor.Picker.Entries.OfType<NodePickerGroup>().Select(g => g.Name));
        editor.Picker.SearchText = "date";
        var dateTime = Assert.Single(editor.Picker.ShownItems);
        editor.Picker.Choose(dateTime);

        Assert.False(editor.Picker.IsOpen);
        Assert.Equal([NodeKind.SetPassword, NodeKind.DateTime], editor.Nodes.Select(n => n.Kind));
        Assert.Same(editor.Nodes[1], editor.SelectedNode);
        Assert.Equal(1, editor.Chain.SelectedIndex);

        // The + of the last node adds at the end.
        editor.Chain.RequestAdd(1, new Avalonia.Point(200, 400));
        editor.Picker.Choose(editor.Picker.ShownItems.Single(i => i.Label == "Network"));
        Assert.Equal([NodeKind.SetPassword, NodeKind.DateTime, NodeKind.Network], editor.Nodes.Select(n => n.Kind));
        Assert.Equal(3, editor.Nodes.Select(n => n.Id).Distinct().Count());

        // A click on the canvas closes the list.
        editor.Chain.RequestAdd(1, new Avalonia.Point(200, 120));
        editor.Chain.SelectedIndex = -1;
        Assert.False(editor.Picker.IsOpen);
    }

    [Fact]
    public async Task Nodes_are_added_after_the_selection_never_before_set_password_and_move_only_below_it()
    {
        var (editor, _) = await OpenAsync(null);
        editor.SelectedNode = editor.Nodes[0];
        editor.AddNode(Kind(NodeKind.DateTime));
        editor.AddNode(Kind(NodeKind.Network));
        Assert.Equal([NodeKind.SetPassword, NodeKind.DateTime, NodeKind.Network], editor.Nodes.Select(n => n.Kind));
        Assert.Same(editor.Nodes[2], editor.SelectedNode);

        editor.SelectedNode = editor.Nodes[0];
        editor.AddNode(Kind(NodeKind.Autofocus));
        Assert.Equal([NodeKind.SetPassword, NodeKind.Autofocus, NodeKind.DateTime, NodeKind.Network], editor.Nodes.Select(n => n.Kind));

        // Network up to right below Set password, never above it.
        editor.SelectedNode = editor.Nodes[3];
        Assert.True(editor.MoveUpCommand.CanExecute(null));
        editor.MoveUp();
        editor.MoveUp();
        Assert.Equal([NodeKind.SetPassword, NodeKind.Network, NodeKind.Autofocus, NodeKind.DateTime], editor.Nodes.Select(n => n.Kind));
        Assert.False(editor.MoveUpCommand.CanExecute(null));
        Assert.Equal(1, editor.Chain.SelectedIndex);

        // Set password neither moves nor goes.
        editor.Chain.SelectedIndex = 0;
        Assert.Same(editor.Nodes[0], editor.SelectedNode);
        Assert.False(editor.MoveUpCommand.CanExecute(null));
        Assert.False(editor.MoveDownCommand.CanExecute(null));
        Assert.False(editor.RemoveNodeCommand.CanExecute(null));
        Assert.Equal(ProvisioningTexts.FixedTip, editor.FixedTip);

        editor.SelectedNode = editor.Nodes[3];
        Assert.False(editor.MoveDownCommand.CanExecute(null));
        editor.RemoveNode();
        Assert.Equal(3, editor.Nodes.Count);
        Assert.Same(editor.Nodes[2], editor.SelectedNode);
    }

    [Fact]
    public async Task Field_errors_show_below_the_field_once_edited_and_save_stores_a_draft()
    {
        var (editor, _) = await OpenAsync(null);
        var setIp = editor.AddAfter(0, Kind(NodeKind.Network))!;
        UsePool(setIp);
        var first = (TextFieldViewModel)setIp.Fields.Single(f => f.Key == "first");
        var last = (TextFieldViewModel)setIp.Fields.Single(f => f.Key == "last");

        Assert.Null(first.ErrorOf(nameof(TextFieldViewModel.Text))); // untouched: no error shown
        Assert.False(setIp.IsValid);
        Assert.True(editor.SaveCommand.CanExecute(null)); // a draft: saved, never active until fixed
        Assert.True(editor.IsDraft);
        Assert.Equal(ProvisioningTexts.DraftTip, editor.SaveTip);

        first.Text = "192.168.15.10";
        last.Text = "192.168.15";
        Assert.Equal(ProvisioningTexts.NotAnAddress, last.ErrorOf(nameof(TextFieldViewModel.Text)));
        Assert.Equal(ProvisioningTexts.NotAnAddress, setIp.CanvasError);
        Assert.Equal(ProvisioningTexts.NotAnAddress, editor.Chain.Nodes[1].Below);
        Assert.True(editor.Chain.Nodes[1].HasError);

        last.Text = "192.168.15.50";
        ((TextFieldViewModel)setIp.Fields.Single(f => f.Key == "subnetMask")).Text = "255.255.255.0";
        ((TextFieldViewModel)setIp.Fields.Single(f => f.Key == "gateway")).Text = "192.168.15.1";
        Assert.True(setIp.IsValid);
        await editor.CheckTask;
        Assert.Null(setIp.CanvasError);
        Assert.Equal("192.168.15.10 to 192.168.15.50", editor.Chain.Nodes[1].Line);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_new_network_node_starts_in_the_dhcp_servers_network_and_only_the_last_number_is_left_to_type()
    {
        var ctx = Samples.Context();
        var editor = new ProfileEditorViewModel(ctx, null, Samples.Dhcp() with { SubnetMask = "255.255.0.0" });
        await editor.LoadAsync();
        await editor.CheckTask;

        var setIp = editor.AddAfter(0, Kind(NodeKind.Network))!;
        UsePool(setIp);
        TextFieldViewModel Text(NodeEditorViewModel node, string key) => node.Field<TextFieldViewModel>(key);
        Assert.Equal("10.0.0.", Text(setIp, "first").Text); // from the range start 10.0.0.100
        Assert.Equal("10.0.0.", Text(setIp, "last").Text);
        Assert.Equal("255.255.0.0", Text(setIp, "subnetMask").Text);
        Assert.Equal(string.Empty, Text(setIp, "gateway").Text);
        Assert.Empty(setIp.Field<ListFieldViewModel>("dnsServers").Items);

        // Half filled is a draft, but no error shows below an untouched field.
        Assert.Null(Text(setIp, "first").ErrorOf(nameof(TextFieldViewModel.Text)));
        Assert.False(setIp.IsValid);
        Assert.True(editor.SaveCommand.CanExecute(null));

        // Typing the last number makes it valid.
        Text(setIp, "first").Text = "10.0.0.10";
        Text(setIp, "last").Text = "10.0.0.50";
        Text(setIp, "gateway").Text = "10.0.0.1";
        Assert.True(setIp.IsValid);

        // Other kinds keep their defaults.
        var dateTime = editor.AddAfter(1, Kind(NodeKind.DateTime))!;
        Assert.Equal(Kind(NodeKind.DateTime).DefaultSettings.GetRawText(), dateTime.ToFlowNode().Settings!.Value.GetRawText());
    }

    [Fact]
    public async Task A_new_network_node_takes_the_server_address_without_a_range_and_only_the_mask_without_a_dhcp_server()
    {
        var ctx = Samples.Context();
        var editor = new ProfileEditorViewModel(ctx, null, Samples.Dhcp() with { RangeStart = null, SubnetMask = null, ServerAddress = "192.168.15.2" });
        await editor.LoadAsync();
        var setIp = editor.AddAfter(0, Kind(NodeKind.Network))!;
        Assert.Equal("192.168.15.", setIp.Field<TextFieldViewModel>("first").Text);
        Assert.Equal("255.255.255.0", setIp.Field<TextFieldViewModel>("subnetMask").Text);

        var (unknown, _) = await OpenAsync(null); // no DHCP server
        var plain = unknown.AddAfter(0, Kind(NodeKind.Network))!;
        Assert.Equal(string.Empty, plain.Field<TextFieldViewModel>("first").Text);
        Assert.Equal(string.Empty, plain.Field<TextFieldViewModel>("last").Text);
        Assert.Equal("255.255.255.0", plain.Field<TextFieldViewModel>("subnetMask").Text);
    }

    [Fact]
    public async Task A_saved_network_node_is_never_changed_by_the_dhcp_server()
    {
        var ctx = Samples.Context();
        var editor = new ProfileEditorViewModel(ctx, Samples.ProfileId, Samples.Dhcp());
        await editor.LoadAsync();
        var setIp = editor.Nodes.Single(n => n.Kind == NodeKind.Network);
        Assert.Equal("192.168.15.10", setIp.Field<TextFieldViewModel>("first").Text);
    }

    [Fact]
    public async Task The_server_check_puts_its_error_on_the_node_and_its_warning_below_it()
    {
        var (editor, _) = await OpenAsync(null);
        var setIp = editor.AddAfter(0, Kind(NodeKind.Network))!;
        UsePool(setIp);
        foreach (var (key, value) in new[] { ("first", "10.0.0.150"), ("last", "10.0.0.160"), ("subnetMask", "255.255.255.0"), ("gateway", "10.0.0.1") })
        {
            ((TextFieldViewModel)setIp.Fields.Single(f => f.Key == key)).Text = value;
        }

        await editor.CheckTask;
        Assert.Null(setIp.CheckError);
        Assert.Equal("The pool overlaps the DHCP server's range 10.0.0.100 to 10.0.0.199.", setIp.CheckWarning);
        Assert.Equal("Oadm.StatusWarningBrush", editor.Chain.Nodes[1].BelowBrushKey);
        Assert.False(editor.Chain.Nodes[1].HasError);
        Assert.True(editor.SaveCommand.CanExecute(null)); // a warning never blocks

        // The DateTime node without a time zone: the server's error on the node, Save off with the reason.
        editor.AddNode(Kind(NodeKind.DateTime));
        await editor.CheckTask;
        var dateTime = editor.SelectedNode!;
        Assert.Equal("Choose a time zone.", dateTime.FirstError);
        Assert.True(editor.IsDraft); // Save stays on: a draft
    }

    [Fact]
    public async Task A_duplicate_name_shows_below_the_name()
    {
        var (editor, _) = await OpenAsync(null);
        editor.Name = "  ";
        Assert.Equal(ProvisioningTexts.EnterName, editor.ErrorOf(nameof(editor.Name)));
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal(ProvisioningTexts.EnterName, editor.SaveTip);
        editor.Name = "Parking";
        Assert.Null(editor.ErrorOf(nameof(editor.Name)));
    }

    [Fact]
    public async Task A_typed_password_goes_to_the_server_once_with_save_and_is_forgotten()
    {
        var (editor, ctx) = await OpenAsync(null);
        var password = (PasswordFieldViewModel)editor.Nodes[0].Fields[0];
        password.TypesPassword = true;
        password.Password = "Secret-123";
        password.Confirm = "Secret-12";
        Assert.Equal(ProvisioningTexts.PasswordsDiffer, password.ErrorOf(nameof(PasswordFieldViewModel.Confirm)));
        Assert.True(editor.IsDraft);
        password.Confirm = "Secret-123";
        await editor.CheckTask;

        // The server's check misses a credential entry while the password is typed: the typed one replaces it.
        Assert.NotNull(editor.Nodes[0].CheckError);
        Assert.Null(editor.Nodes[0].CanvasError);
        Assert.Equal("root, a new password", editor.Chain.Nodes[0].Line);
        Assert.DoesNotContain("Secret-123", ctx.LastPayloadOf(ProvisioningMethods.CheckProfile), StringComparison.Ordinal);

        await editor.SaveCommand.ExecuteAsync(null);
        var request = JsonSerializer.Deserialize<SaveProfileRequest>(ctx.LastPayloadOf(ProvisioningMethods.SaveProfile)!, Samples.Json)!;
        Assert.Equal("Secret-123", request.Password);
        Assert.Equal(1, ctx.Calls.Count(c => c.Payload?.Contains("Secret-123", StringComparison.Ordinal) == true));
        Assert.Equal(string.Empty, password.Password);
        Assert.Equal(string.Empty, password.Confirm);
        Assert.True(password.UseCredential);
        Assert.NotNull(editor.Saved);
    }

    [Fact]
    public async Task Edit_reads_the_profile_and_save_writes_the_fields_into_the_settings()
    {
        var (editor, ctx) = await OpenAsync(Samples.ProfileId);
        Assert.Equal(ProvisioningTexts.EditProfileTitle, editor.Title);
        Assert.Equal("Lobby cameras", editor.Name);
        Assert.Equal(3, editor.Nodes.Count);
        var network = editor.Nodes.Single(n => n.Kind == NodeKind.Network);
        Assert.False(network.ContinueOnError);
        Assert.True(network.Field<ChoiceFieldViewModel>("ip").Is("pool"));
        ((ListFieldViewModel)network.Fields.Single(f => f.Key == "dnsServers")).Text = "192.168.15.1, 10.0.0.17";
        network.ContinueOnError = true;

        bool? closed = null;
        editor.CloseRequested += (_, saved) => closed = saved;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        var saved = JsonSerializer.Deserialize<SaveProfileRequest>(ctx.LastPayloadOf(ProvisioningMethods.SaveProfile)!, Samples.Json)!;
        Assert.Null(saved.Password);
        var node = saved.Profile.Nodes.Single(n => n.Kind == NodeKind.Network);
        Assert.True(node.ContinueOnError);
        var settings = JsonNode.Parse(node.Settings!.Value.GetRawText())!;
        Assert.Equal("pool", (string?)settings["ip"]);
        Assert.Equal("192.168.15.10", (string?)settings["first"]);
        Assert.Equal(["192.168.15.1", "10.0.0.17"], settings["dnsServers"]!.AsArray().Select(v => (string?)v));
    }

    [Fact]
    public async Task A_refused_save_shows_the_reason_left_of_save_and_keeps_the_editor_open()
    {
        var (editor, ctx) = await OpenAsync(Samples.ProfileId);
        ctx.Answers[ProvisioningMethods.SaveProfile] = _ => throw new InvalidOperationException("The active profile must have no errors.");
        var closed = false;
        editor.CloseRequested += (_, _) => closed = true;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("The active profile must have no errors.", editor.ErrorText);
        Assert.False(closed);
    }

    private static async Task<(ProfileEditorViewModel Editor, FakePageContext Ctx, PasswordFieldViewModel Field)> OpenThreeRootsAsync(bool admin = true)
    {
        var ctx = Samples.Context();
        ctx.IsAdministrator = admin;
        ctx.Answers[ProvisioningMethods.Credentials] = _ => Samples.Serialize(Samples.ThreeRoots);
        ctx.StoredPasswords[Samples.RootCredential] = "First-111";
        ctx.StoredPasswords[Samples.SecondRoot] = "Second-222";
        var editor = new ProfileEditorViewModel(ctx, Samples.ProfileId);
        await editor.LoadAsync();
        await editor.CheckTask;
        return (editor, ctx, Assert.IsType<PasswordFieldViewModel>(Assert.Single(editor.Nodes[0].Fields)));
    }

    [Fact]
    public async Task The_credential_choice_shows_when_each_root_entry_was_added()
    {
        var (_, _, field) = await OpenThreeRootsAsync();

        Assert.Equal(3, field.Credentials.Count);
        Assert.All(field.Credentials, c => Assert.Equal("root", c.UserName));
        Assert.Equal(3, field.Credentials.Select(c => c.AddedText).Distinct().Count());
        Assert.All(field.Credentials, c => Assert.StartsWith("Added ", c.AddedText, StringComparison.Ordinal));
        Assert.Equal(Oadm.Sdk.Client.CredentialTexts.Added(new DateTimeOffset(2026, 11, 3, 16, 40, 0, TimeSpan.Zero)), field.Credentials[1].AddedText);
        Assert.StartsWith("root, added ", field.Credentials[2].ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, new CredentialChoice(Guid.NewGuid(), "root").AddedText); // a host that cannot tell
    }

    [Fact]
    public async Task The_eye_shows_the_stored_password_through_the_host_and_forgets_it_again()
    {
        var (editor, ctx, field) = await OpenThreeRootsAsync();
        var checks = ctx.CallsOf(ProvisioningMethods.CheckProfile);

        // Masked as the Credentials page until asked; nothing read before.
        Assert.True(field.CanReveal);
        Assert.True(field.ShowsStoredPassword);
        Assert.False(field.IsRevealed);
        Assert.Equal("••••••••", field.PasswordText);
        Assert.Empty(ctx.Reveals);

        await field.ToggleRevealCommand.ExecuteAsync(null);
        Assert.Equal([Samples.RootCredential], ctx.Reveals);
        Assert.True(field.IsRevealed);
        Assert.Equal("First-111", field.PasswordText);

        // The second click masks and forgets it.
        await field.ToggleRevealCommand.ExecuteAsync(null);
        Assert.False(field.IsRevealed);
        Assert.Null(field.RevealedPassword);
        Assert.Equal("••••••••", field.PasswordText);
        Assert.Single(ctx.Reveals);

        // Another choice masks it too; the next reveal asks for that entry.
        await field.ToggleRevealCommand.ExecuteAsync(null);
        field.SelectedCredential = field.Credentials[1];
        Assert.Null(field.RevealedPassword);
        await field.ToggleRevealCommand.ExecuteAsync(null);
        Assert.Equal(Samples.SecondRoot, ctx.Reveals[^1]);
        Assert.Equal("Second-222", field.PasswordText);

        // Revealing is no edit: the profile is not checked again and the saved node keeps only the id.
        await editor.CheckTask;
        Assert.Equal(checks + 1, ctx.CallsOf(ProvisioningMethods.CheckProfile)); // the other choice only
        await editor.SaveCommand.ExecuteAsync(null);
        var saved = ctx.LastPayloadOf(ProvisioningMethods.SaveProfile)!;
        Assert.DoesNotContain("Second-222", saved, StringComparison.Ordinal);
        Assert.Contains(Samples.SecondRoot.ToString(), saved, StringComparison.Ordinal);
        Assert.All(ctx.Calls, c => Assert.DoesNotContain("Second-222", c.Payload ?? string.Empty, StringComparison.Ordinal));

        // Closing the editor forgets it.
        Assert.Null(field.RevealedPassword);
    }

    [Fact]
    public async Task Cancel_and_typing_a_password_forget_the_shown_one()
    {
        var (editor, _, field) = await OpenThreeRootsAsync();
        await field.ToggleRevealCommand.ExecuteAsync(null);
        field.TypesPassword = true;
        Assert.Null(field.RevealedPassword);
        Assert.False(field.ShowsStoredPassword);

        field.UseCredential = true;
        await field.ToggleRevealCommand.ExecuteAsync(null);
        Assert.True(field.IsRevealed);
        editor.Cancel();
        Assert.Null(field.RevealedPassword);
    }

    [Fact]
    public async Task An_answer_that_arrives_after_another_choice_is_dropped()
    {
        var (_, ctx, field) = await OpenThreeRootsAsync();
        var gate = new TaskCompletionSource();
        ctx.RevealGate = gate.Task;
        var reveal = field.ToggleRevealCommand.ExecuteAsync(null);
        field.SelectedCredential = field.Credentials[1];
        gate.SetResult();
        await reveal;
        Assert.Null(field.RevealedPassword);
        Assert.Equal("••••••••", field.PasswordText);
    }

    [Fact]
    public async Task A_refused_reveal_says_why_below_the_choice()
    {
        var (_, ctx, field) = await OpenThreeRootsAsync();
        field.SelectedCredential = field.Credentials[2]; // not in the fake's store: NOT_FOUND
        await field.ToggleRevealCommand.ExecuteAsync(null);
        Assert.False(field.IsRevealed);
        Assert.True(field.HasRevealError);
        Assert.StartsWith(Oadm.Sdk.Client.CredentialTexts.ShowFailed, field.RevealError, StringComparison.Ordinal);
        Assert.Equal(Samples.ThirdRoot, ctx.Reveals[^1]);
        field.SelectedCredential = field.Credentials[0];
        Assert.Null(field.RevealError);
    }

    [Fact]
    public async Task The_eye_is_hidden_for_users_who_are_not_administrators()
    {
        var (_, ctx, field) = await OpenThreeRootsAsync(admin: false);
        Assert.False(field.CanReveal);
        Assert.False(field.ShowsStoredPassword);
        await field.ToggleRevealCommand.ExecuteAsync(null);
        Assert.Empty(ctx.Reveals);
        Assert.Null(field.RevealedPassword);
    }
}
