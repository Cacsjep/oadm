using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Provisioning.Tests.Client;

/// <summary>Renders the profile editor and Manage profiles offscreen like the client shell; OADM_SCREENSHOT_DIR writes PNGs.</summary>
public sealed class PageScreenshotTests
{
    [Fact]
    public async Task The_profile_editor_renders_the_flow_and_the_selected_nodes_form_with_its_error()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var editor = new ProfileEditorViewModel(ctx, Samples.ProfileId);
            var window = new ProfileEditorWindow();
            Oadm.Client.App.ApplyCrispText(window);
            window.Attach(editor);
            window.Show();
            await editor.LoadAsync();
            await editor.CheckTask;
            editor.SelectedNode = editor.Nodes.First(n => n.Kind == NodeKind.Network);
            var last = (TextFieldViewModel)editor.SelectedNode.Fields.Single(f => f.Key == "last");
            last.Text = "192.168.15";
            await editor.CheckTask;
            Headless.Pump();

            Assert.True(window.FindControl<StackPanel>("NodeForm")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<StackPanel>("ProfileForm")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled); // a draft
            Assert.Equal(1, window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("primary")));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == ProvisioningTexts.NotAnAddress);
            Headless.Capture(window, $"provisioning-editor-{suffix}.png");
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_profile_editor_renders_a_full_profile_with_the_configuration_selected()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var editor = new ProfileEditorViewModel(ctx, Samples.FullProfileId);
            var window = new ProfileEditorWindow();
            Oadm.Client.App.ApplyCrispText(window);
            window.Attach(editor);
            window.Show();
            await editor.LoadAsync();
            await editor.CheckTask;
            editor.SelectedNode = editor.Nodes.Single(n => n.Kind == NodeKind.Configuration);
            await editor.CheckTask;
            Headless.Pump();

            Assert.Equal(10, editor.Nodes.Count);
            Assert.All(editor.Nodes, n => Assert.Null(n.CanvasError));
            Assert.False(editor.IsDraft);
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled);
            Assert.Equal("parking-p3265.json", editor.SelectedNode.Field<FileFieldViewModel>("fileId").FileName);
            Assert.Contains(window.GetVisualDescendants().OfType<CheckBox>(), c => c.IsEffectivelyVisible && Equals(c.Content, ProvisioningTexts.ActionRules) && c.IsChecked == true);
            Headless.Capture(window, $"provisioning-editor-full-{suffix}.png");

            // The forms with lists and secrets: Users with a second account, the SD card with its passphrase.
            editor.SelectedNode = editor.Nodes.Single(n => n.Kind == NodeKind.Users);
            editor.SelectedNode.Field<UsersFieldViewModel>("accounts").AddAccountCommand.Execute(null);
            Headless.Pump();
            Headless.Capture(window, $"provisioning-editor-users-{suffix}.png");
            editor.SelectedNode = editor.Nodes.Single(n => n.Kind == NodeKind.SdCard);
            editor.SelectedNode.Fields.OfType<CheckGroupFieldViewModel>().Single().Items[1].IsChecked = true;
            Headless.Pump();
            Headless.Capture(window, $"provisioning-editor-sdcard-{suffix}.png");
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_profile_editor_renders_a_new_profile_the_node_list_and_the_field_help()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var editor = new ProfileEditorViewModel(ctx, null, Samples.Dhcp());
            var window = new ProfileEditorWindow();
            Oadm.Client.App.ApplyCrispText(window);
            window.Attach(editor);
            window.Show();
            await editor.LoadAsync();
            await editor.CheckTask;
            Headless.Pump();

            // A new profile: Set password alone, the + below it.
            var canvas = window.FindControl<NodeChainCanvas>("Canvas")!;
            Assert.Single(canvas.Cards);
            Assert.True(editor.Chain.ShowsPlus(0));
            var plus = canvas.PlusRect(0);
            Assert.Equal(0, canvas.PlusIndexAt(editor.Chain.View.ToScreen(plus.Center)));
            Headless.Capture(window, $"provisioning-editor-new-{suffix}.png");

            // A new Network node starts in the DHCP server's network: only the last number is left to type, no error shown yet.
            var network = editor.AddAfter(0, Samples.Kinds.Single(k => k.Kind == NodeKind.Network))!;
            ProfileEditorTests.UsePool(network);
            Headless.Pump();
            Assert.Equal("10.0.0.", network.Field<TextFieldViewModel>("first").Text);
            Assert.Null(network.Field<TextFieldViewModel>("first").ErrorOf(nameof(TextFieldViewModel.Text)));
            Headless.Capture(window, $"provisioning-editor-network-new-{suffix}.png");
            editor.SelectedNode = null;
            Headless.Pump();

            // The + opens the node list next to it; its search takes the keys.
            editor.Chain.RequestAdd(1, canvas.PickerPlace(1));
            Headless.Pump();
            var picker = window.FindControl<NodePicker>("NodePicker")!;
            Assert.True(picker.IsEffectivelyVisible);
            Assert.True(picker.Search.IsKeyboardFocusWithin);
            Assert.Equal(editor.AddableKinds.Count, editor.Picker.ShownItems.Count);
            Headless.Capture(window, $"provisioning-editor-picker-{suffix}.png");

            // Date and time from the list: the time zone choice and a ? in every field.
            editor.Picker.Choose(editor.Picker.ShownItems.Single(i => i.Label == "Date and time"));
            var node = editor.SelectedNode!;
            Assert.Equal(NodeKind.DateTime, node.Kind);
            var source = node.Field<ChoiceFieldViewModel>("ntpSource");
            source.Selected = source.Choices.Single(c => c.Value == "own");
            node.Field<TimeZoneFieldViewModel>("timeZone").Selected = TimeZoneFieldViewModel.Zones.Single(z => z.Id == "Europe/Vienna");
            await editor.CheckTask;
            Headless.Pump();
            var form = window.FindControl<StackPanel>("NodeForm")!;
            var fields = form.GetVisualDescendants().OfType<FormField>().Where(f => f.IsEffectivelyVisible).ToList();
            Assert.Equal(4, fields.Count); // Time zone, Time from, NTP servers, NTS
            Assert.All(fields, f => Assert.True(f.HelpButton.IsEffectivelyVisible));
            Assert.Equal(ProvisioningTexts.TimeZoneHelp, ToolTip.GetTip(fields[0].HelpButton));
            Assert.Contains(form.GetVisualDescendants().OfType<ComboBox>(), c => c.SelectedItem is Oadm.Plugins.DateAndTime.Model.TimeZoneEntry { Id: "Europe/Vienna" });
            Headless.Capture(window, $"provisioning-editor-datetime-{suffix}.png");
            ToolTip.SetIsOpen(fields[2].HelpButton, true);
            Headless.Pump();
            Headless.Capture(window, $"provisioning-editor-help-{suffix}.png");
            ToolTip.SetIsOpen(fields[2].HelpButton, false);

            // Network and Set password: text boxes, password boxes and a group of choices.
            editor.SelectedNode = editor.Nodes.First(n => n.Kind == NodeKind.Network);
            Headless.Pump();
            Headless.Capture(window, $"provisioning-editor-network-{suffix}.png");
            editor.SelectedNode = editor.Nodes[0];
            editor.SelectedNode.Fields.OfType<PasswordFieldViewModel>().Single().TypesPassword = true;
            Headless.Pump();
            Headless.Capture(window, $"provisioning-editor-password-{suffix}.png");
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Set_password_renders_the_eye_the_shown_password_and_the_dates_of_the_entries()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            ctx.Answers[ProvisioningMethods.Credentials] = _ => Samples.Serialize(Samples.ThreeRoots);
            ctx.StoredPasswords[Samples.RootCredential] = "Lobby-2026!";
            var editor = new ProfileEditorViewModel(ctx, Samples.ProfileId);
            var window = new ProfileEditorWindow();
            Oadm.Client.App.ApplyCrispText(window);
            window.Attach(editor);
            window.Show();
            await editor.LoadAsync();
            await editor.CheckTask;
            editor.SelectedNode = editor.Nodes[0];
            Headless.Pump();

            var field = editor.SelectedNode.Fields.OfType<PasswordFieldViewModel>().Single();
            var eye = window.GetVisualDescendants().OfType<PasswordRevealButton>().Single();
            Assert.True(eye.IsEffectivelyVisible);
            Assert.Equal(PasswordBox.ShowTip, ToolTip.GetTip(eye));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "••••••••");
            Headless.Capture(window, $"provisioning-editor-password-eye-{suffix}.png");

            eye.Command!.Execute(null);
            await Oadm.Sdk.Testing.Eventually.TrueAsync(() => field.IsRevealed, "the password shows");
            Headless.Pump();
            Assert.Equal(PasswordBox.HideTip, ToolTip.GetTip(eye));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Lobby-2026!");
            Headless.Capture(window, $"provisioning-editor-password-shown-{suffix}.png");

            var choice = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible && c.ItemsSource == field.Credentials);
            choice.IsDropDownOpen = true;
            Headless.Pump();
            var rows = choice.GetRealizedContainers().ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal(3, rows.Select(r => string.Join(" ", r.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text))).Distinct().Count()); // told apart by the date
            Headless.Capture(TopLevel.GetTopLevel(rows[0])!, $"provisioning-editor-password-list-{suffix}.png"); // the open list is a popup of its own
            choice.IsDropDownOpen = false;
            window.Close();
            Assert.Null(field.RevealedPassword); // the editor closed

            // Not an administrator: no eye, no password line.
            var userCtx = Samples.Context();
            userCtx.IsAdministrator = false;
            var userEditor = new ProfileEditorViewModel(userCtx, Samples.ProfileId);
            var userWindow = new ProfileEditorWindow();
            userWindow.Attach(userEditor);
            userWindow.Show();
            await userEditor.LoadAsync();
            await userEditor.CheckTask;
            userEditor.SelectedNode = userEditor.Nodes[0];
            Headless.Pump();
            Assert.DoesNotContain(userWindow.GetVisualDescendants().OfType<PasswordRevealButton>(), b => b.IsEffectivelyVisible);
            Assert.DoesNotContain(userWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "••••••••");
            userWindow.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Manage_profiles_renders_the_list_with_its_states()
    {
        var suffix = Headless.Appearance;
        await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context(profiles: new ProfilesReply(
            [
                new ProfileSummary(Samples.ProfileId, "Lobby cameras", 5, true, true),
                new ProfileSummary(Samples.FullProfileId, "Parking garage", 11, false, true),
                new ProfileSummary(Guid.Parse("9c9c9c9c-8888-4c2d-9e8f-0a1b2c3d4e5f"), "Warehouse (draft)", 4, false, false),
            ], Samples.ProfileId));
            var dialog = new ProfilesDialogViewModel(ctx);
            var window = new ProfilesWindow();
            Oadm.Client.App.ApplyCrispText(window);
            window.Attach(dialog);
            window.Show();
            await dialog.LoadAsync();
            dialog.Selected = dialog.Rows[1];
            Headless.Pump();

            Assert.Equal(3, dialog.Rows.Count);
            Assert.True(window.FindControl<Button>("ActivateButton")!.IsEffectivelyEnabled);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));
            Assert.Contains(window.GetVisualDescendants().OfType<StatusChip>(), c => c.IsEffectivelyVisible && c.Text == ProvisioningTexts.HasErrors);
            Headless.Capture(window, $"provisioning-profiles-{suffix}.png");
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
