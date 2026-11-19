using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Provisioning.Tests.Client;

/// <summary>Renders the page and the profile editor offscreen like the client shell; OADM_SCREENSHOT_DIR writes PNGs.</summary>
public sealed class PageScreenshotTests
{
    private static async Task<(ProvisioningViewModel Vm, ProvisioningView View, Window Window)> ShowAsync(FakePageContext ctx)
    {
        var vm = new ProvisioningViewModel(ctx);
        var view = new ProvisioningView();
        var window = Headless.Host(view);
        window.Show();
        view.DataContext = vm; // after attaching: no live watch, deterministic content
        await vm.LoadAsync();
        Headless.Pump();
        return (vm, view, window);
    }

    [Fact]
    public async Task The_page_renders_the_dhcp_card_the_cameras_and_a_cameras_flow()
    {
        var suffix = Headless.Appearance;
        await await Headless.Session.Dispatch(async () =>
        {
            var (vm, view, window) = await ShowAsync(Samples.Context());
            using (vm)
            {
                Assert.True(view.FindControl<Border>("DhcpCard")!.IsEffectivelyVisible);
                Assert.True(view.FindControl<StatusChip>("PoolWarning")!.IsEffectivelyVisible);
                Assert.True(view.FindControl<Button>("StopButton")!.IsEffectivelyVisible);
                Assert.False(view.FindControl<Button>("StartButton")!.IsEffectivelyVisible);
                Assert.False(view.FindControl<Border>("FlowCard")!.IsEffectivelyVisible);
                Assert.Equal(8, view.FindControl<DataGrid>("CameraGrid")!.ItemsSource!.Cast<object>().Count());
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));
                Headless.Capture(window, $"provisioning-page-{suffix}.png");

                // A click on the failed camera: its flow right of the table, green, yellow, green, red and grey.
                var grid = view.FindControl<DataGrid>("CameraGrid")!;
                grid.SelectedItem = vm.Rows.Single(r => r.State == CameraState.Failed);
                Headless.Pump();
                var canvas = view.FindControl<NodeChainCanvas>("FlowCanvas")!;
                Assert.True(view.FindControl<Border>("FlowCard")!.IsEffectivelyVisible);
                Assert.Equal(5, canvas.Cards.Count);
                Assert.True(canvas.Cards[4].Top > canvas.Cards[3].Bottom + NodeChainCanvas.Gap, "the error below Set IP pushes Date and time down");
                Headless.Capture(window, $"provisioning-flow-{suffix}.png");
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_page_without_a_session_says_what_to_do_first()
    {
        var suffix = Headless.Appearance;
        await await Headless.Session.Dispatch(async () =>
        {
            var (vm, view, window) = await ShowAsync(Samples.Context(Samples.Status(started: false), []));
            using (vm)
            {
                Assert.True(view.FindControl<TextBlock>("EmptyLine")!.IsEffectivelyVisible);
                Assert.Equal(ProvisioningTexts.EmptyNotStarted, view.FindControl<TextBlock>("EmptyLine")!.Text);
                Assert.True(view.FindControl<Button>("StartButton")!.IsEffectivelyEnabled);
                Assert.False(view.FindControl<StatusChip>("PoolWarning")!.IsEffectivelyVisible);
                Headless.Capture(window, $"provisioning-empty-{suffix}.png");
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_profile_editor_renders_the_flow_and_the_selected_nodes_form_with_its_error()
    {
        var suffix = Headless.Appearance;
        await await Headless.Session.Dispatch(async () =>
        {
            var ctx = Samples.Context();
            var editor = new ProfileEditorViewModel(ctx, Samples.ProfileId);
            var window = new ProfileEditorWindow();
            Oadm.Client.App.ApplyCrispText(window);
            window.Attach(editor);
            window.Show();
            await editor.LoadAsync();
            await editor.CheckTask;
            editor.SelectedNode = editor.Nodes.Single(n => n.Kind == NodeKind.SetIp);
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
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_profile_editor_renders_a_full_profile_with_the_configuration_selected()
    {
        var suffix = Headless.Appearance;
        await await Headless.Session.Dispatch(async () =>
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

            Assert.Equal(11, editor.Nodes.Count);
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
        }, CancellationToken.None);
    }
}
