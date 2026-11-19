using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Model;

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

    [Fact]
    public async Task A_new_profile_starts_with_the_two_fixed_nodes_and_the_root_password_of_the_credential_list()
    {
        var (editor, _) = await OpenAsync(null);

        Assert.Equal(ProvisioningTexts.NewProfileTitle, editor.Title);
        Assert.Equal(ProvisioningTexts.DefaultName, editor.Name);
        Assert.Equal([NodeKind.SetPassword, NodeKind.AddToOadm], editor.Nodes.Select(n => n.Kind));
        Assert.All(editor.Nodes, n => Assert.True(n.IsFixed));
        Assert.Equal(Samples.Kinds.Where(k => !k.IsFixed).Select(k => k.Name), editor.AddableKinds.Select(k => k.Name));
        Assert.Contains("VAPIX commands", editor.AddableKinds.Select(k => k.Name));
        var password = Assert.IsType<PasswordFieldViewModel>(Assert.Single(editor.Nodes[0].Fields));
        Assert.Equal("root", Assert.Single(password.Credentials).UserName); // only the root entries
        Assert.Equal(Samples.RootCredential, password.SelectedCredential?.Id);
        Assert.False(editor.Nodes[0].CanChangeContinueOnError);
        Assert.False(editor.Nodes[0].ContinueOnError);
        Assert.Equal(2, editor.Chain.Nodes.Count);
        Assert.False(editor.HasErrors); // untouched
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Nodes_are_added_after_the_selection_never_before_the_fixed_ones_and_move_only_below_them()
    {
        var (editor, _) = await OpenAsync(null);
        editor.SelectedNode = editor.Nodes[0];
        editor.AddNode(Kind(NodeKind.SetIp));
        Assert.Equal([NodeKind.SetPassword, NodeKind.AddToOadm, NodeKind.SetIp], editor.Nodes.Select(n => n.Kind));
        Assert.Same(editor.Nodes[2], editor.SelectedNode);
        Assert.Equal(2, editor.Chain.SelectedIndex);

        editor.AddNode(Kind(NodeKind.DateTime));
        editor.AddNode(Kind(NodeKind.WaitForApproval));
        Assert.Equal([NodeKind.SetPassword, NodeKind.AddToOadm, NodeKind.SetIp, NodeKind.DateTime, NodeKind.WaitForApproval], editor.Nodes.Select(n => n.Kind));
        Assert.Equal(5, editor.Nodes.Select(n => n.Id).Distinct().Count());

        // Wait for approval before Set IP.
        Assert.True(editor.MoveUpCommand.CanExecute(null));
        editor.MoveUp();
        editor.MoveUp();
        Assert.Equal([NodeKind.SetPassword, NodeKind.AddToOadm, NodeKind.WaitForApproval, NodeKind.SetIp, NodeKind.DateTime], editor.Nodes.Select(n => n.Kind));
        Assert.False(editor.MoveUpCommand.CanExecute(null)); // never above Add to OADM
        Assert.Equal(2, editor.Chain.SelectedIndex);

        // The fixed nodes neither move nor go.
        editor.Chain.SelectedIndex = 1;
        Assert.Same(editor.Nodes[1], editor.SelectedNode);
        Assert.False(editor.MoveUpCommand.CanExecute(null));
        Assert.False(editor.MoveDownCommand.CanExecute(null));
        Assert.False(editor.RemoveNodeCommand.CanExecute(null));
        Assert.Equal(ProvisioningTexts.FixedTip, editor.FixedTip);

        editor.SelectedNode = editor.Nodes[4];
        Assert.False(editor.MoveDownCommand.CanExecute(null));
        editor.RemoveNode();
        Assert.Equal(4, editor.Nodes.Count);
        Assert.Same(editor.Nodes[3], editor.SelectedNode);
    }

    [Fact]
    public async Task Field_errors_show_below_the_field_once_edited_and_save_stores_a_draft()
    {
        var (editor, _) = await OpenAsync(null);
        editor.AddNode(Kind(NodeKind.SetIp));
        var setIp = editor.SelectedNode!;
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
        Assert.Equal(ProvisioningTexts.NotAnAddress, editor.Chain.Nodes[2].Below);
        Assert.True(editor.Chain.Nodes[2].HasError);

        last.Text = "192.168.15.50";
        ((TextFieldViewModel)setIp.Fields.Single(f => f.Key == "subnetMask")).Text = "255.255.255.0";
        ((TextFieldViewModel)setIp.Fields.Single(f => f.Key == "gateway")).Text = "192.168.15.1";
        Assert.True(setIp.IsValid);
        await editor.CheckTask;
        Assert.Null(setIp.CanvasError);
        Assert.Equal("192.168.15.10 to 192.168.15.50", editor.Chain.Nodes[2].Line);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_server_check_puts_its_error_on_the_node_and_its_warning_below_it()
    {
        var (editor, _) = await OpenAsync(null);
        editor.AddNode(Kind(NodeKind.SetIp));
        var setIp = editor.SelectedNode!;
        foreach (var (key, value) in new[] { ("first", "10.0.0.150"), ("last", "10.0.0.160"), ("subnetMask", "255.255.255.0"), ("gateway", "10.0.0.1") })
        {
            ((TextFieldViewModel)setIp.Fields.Single(f => f.Key == key)).Text = value;
        }

        await editor.CheckTask;
        Assert.Null(setIp.CheckError);
        Assert.Equal("The pool overlaps the DHCP server's range 10.0.0.100 to 10.0.0.199.", setIp.CheckWarning);
        Assert.Equal("Oadm.StatusWarningBrush", editor.Chain.Nodes[2].BelowBrushKey);
        Assert.False(editor.Chain.Nodes[2].HasError);
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
        Assert.Equal(5, editor.Nodes.Count);
        var setIp = editor.Nodes.Single(n => n.Kind == NodeKind.SetIp);
        Assert.False(setIp.ContinueOnError);
        ((ListFieldViewModel)setIp.Fields.Single(f => f.Key == "dnsServers")).Text = "192.168.15.1, 10.0.0.17";
        setIp.ContinueOnError = true;
        ((ListFieldViewModel)editor.Nodes[1].Fields.Single()).Text = "Lobby; Floor 2";

        bool? closed = null;
        editor.CloseRequested += (_, saved) => closed = saved;
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        var saved = JsonSerializer.Deserialize<SaveProfileRequest>(ctx.LastPayloadOf(ProvisioningMethods.SaveProfile)!, Samples.Json)!;
        Assert.Null(saved.Password);
        var node = saved.Profile.Nodes.Single(n => n.Kind == NodeKind.SetIp);
        Assert.True(node.ContinueOnError);
        var settings = JsonNode.Parse(node.Settings!.Value.GetRawText())!;
        Assert.Equal("192.168.15.10", (string?)settings["first"]);
        Assert.Equal(["192.168.15.1", "10.0.0.17"], settings["dnsServers"]!.AsArray().Select(v => (string?)v));
        var tags = JsonNode.Parse(saved.Profile.Nodes[1].Settings!.Value.GetRawText())!["tags"]!.AsArray().Select(v => (string?)v);
        Assert.Equal(["Lobby", "Floor 2"], tags);
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

    [Fact]
    public async Task Wait_for_approval_says_what_it_does_instead_of_fields()
    {
        var (editor, _) = await OpenAsync(null);
        editor.AddNode(Kind(NodeKind.WaitForApproval));
        Assert.False(editor.SelectedNode!.HasFields);
        Assert.Equal(ProvisioningTexts.WaitHint, editor.SelectedNode.Note);
        Assert.Equal(ProvisioningTexts.WaitsForContinue, editor.Chain.Nodes[2].Line);
    }
}
