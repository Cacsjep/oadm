using Avalonia.Controls;
using Avalonia.VisualTree;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Dashboards.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>Button widgets on the page: Add button, the panel and its label rule, the 20-button limit, pressing, the right, screenshots.</summary>
public sealed partial class DashboardsPageTests
{
    private static readonly string PressRight = PermissionKeys.Of(DashboardsPluginInfo.PluginId, DashboardsPluginInfo.PressKey);

    private static (PluginHost Host, TestContext Ctx, DashboardsPageViewModel Vm, FakeTimeProvider Time) ButtonPage()
    {
        var host = new PluginHost();
        var ctx = new TestContext(host, [new TestDevice("10.0.0.48", "P3265-V")]);
        var time = new FakeTimeProvider();
        return (host, ctx, new DashboardsPageViewModel(ctx, time), time);
    }

    [Fact]
    public Task Add_button_places_a_button_whose_panel_checks_the_label_and_the_limit() => OnUi(async () =>
    {
        var (host, _, vm, _) = ButtonPage();
        using (host)
        using (vm)
        {
            Assert.True(await Headless.UntilAsync(() => vm.IsLoaded));
            Assert.False(vm.AddButtonCommand.CanExecute(null)); // only while editing
            vm.NewDashboardCommand.Execute(null);
            var tab = vm.ActiveTab!;
            Assert.True(vm.AddButtonCommand.CanExecute(null));
            vm.AddButtonCommand.Execute(null);

            var button = Assert.Single(tab.Widgets);
            Assert.Equal((WidgetKind.Button, 0, 0, 4, 2), (button.Widget.Kind, button.Rect.X, button.Rect.Y, button.Rect.Width, button.Rect.Height));
            Assert.Equal((DashboardTexts.NewButton, "Accent", (Guid?)null, ButtonSize.Small), (button.Widget.Name, button.Widget.Color, button.Widget.DeviceId, button.Widget.ButtonSize));
            Assert.Null(button.Key); // nothing to watch
            Assert.False(button.CanCopy); // no device: no Copy for other devices

            // The panel: Label, Icon, Color; nothing of an event or a device.
            var editor = tab.Editor!;
            Assert.True(editor.IsButton);
            Assert.False(editor.IsValue);
            Assert.False(editor.ShowDevice);
            Assert.False(editor.ShowNameField);
            Assert.True(editor.ShowColor);
            Assert.Equal(WidgetColors.All.Count, editor.Colors.Count); // every color is a fill

            editor.Name = " ";
            Assert.Equal([DashboardRules.LabelMissing], editor.GetErrors(nameof(WidgetEditorViewModel.Name)).Cast<string>());
            Assert.False(vm.SaveDashboardCommand.CanExecute(null));
            editor.Name = new string('x', 33);
            Assert.Equal([DashboardRules.LabelTooLong], editor.GetErrors(nameof(WidgetEditorViewModel.Name)).Cast<string>());
            editor.Name = "Open gate";
            Assert.Empty(editor.GetErrors(nameof(WidgetEditorViewModel.Name)).Cast<string>());
            editor.ButtonIcon = WidgetEditorViewModel.ButtonIconOptions.First(i => i.Key == "lockOpen");
            editor.Color = WidgetEditorViewModel.ColorOptions.First(c => c.Name == "Red");
            Assert.Equal(("Open gate", "lockOpen", "Red"), (button.Widget.Name, button.Widget.Icon, button.Widget.Color));
            Assert.Equal("Open gate", button.ButtonView!.Label);
            editor.ButtonSize = WidgetEditorViewModel.ButtonSizeOptions.First(o => o.Value == ButtonSize.Medium);
            Assert.Equal(ButtonSize.Medium, button.Widget.ButtonSize);
            Assert.Equal(0.7, button.ButtonView.Share);

            // Every label once: a new button gets a free label, a taken one shows below the Label field.
            Assert.True(tab.AddButton());
            var second = tab.Widgets[1];
            Assert.Equal(DashboardTexts.NewButton, second.Widget.Name); // "Open gate" took the first one's place
            tab.Editor!.Name = "open gate";
            Assert.Equal([DashboardRules.LabelTaken], tab.Editor.GetErrors(nameof(WidgetEditorViewModel.Name)).Cast<string>());
            Assert.Equal(DashboardTexts.NewButton, second.Widget.Name); // keeps its last good label
            Assert.True(tab.AddButton());
            Assert.Equal(DashboardTexts.NewButton + " 2", tab.Widgets[2].Widget.Name);

            // 20 buttons at most: the 21st is refused with the reason below the toolbar.
            for (var i = tab.Widgets.Count; i < DashboardsPluginInfo.MaxButtons; i++)
            {
                Assert.True(tab.AddButton());
            }

            Assert.False(tab.AddButton());
            Assert.Equal(DashboardRules.TooManyButtons, tab.Error);
            Assert.Equal(DashboardsPluginInfo.MaxButtons, tab.Widgets.Count);

            // Several buttons: their Size together ("Mixed" while they differ), and their color.
            tab.SelectAll();
            var group = tab.GroupEditor!;
            Assert.True(group.ShowButton);
            Assert.True(group.ShowColor);
            Assert.Null(group.ButtonSize); // Medium and Large: Mixed
            group.ButtonSize = WidgetEditorViewModel.ButtonSizeOptions.First(o => o.Value == ButtonSize.Small);
            Assert.All(tab.Widgets, w => Assert.Equal(ButtonSize.Small, w.Widget.ButtonSize));
            tab.Select(null);

            Assert.True(vm.SaveDashboardCommand.CanExecute(null));
            await vm.SaveDashboardCommand.ExecuteAsync(null);
            Assert.Null(tab.Error);
            Assert.False(tab.IsEditing);
            Assert.Equal(DashboardsPluginInfo.MaxButtons, host.Plugin.EventTypes.Count);
        }
    });

    [Fact]
    public Task A_press_sends_the_event_and_shows_the_answer_briefly() => OnUi(async () =>
    {
        var (host, _, vm, time) = ButtonPage();
        using (host)
        using (vm)
        {
            var saved = await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Gate house", 0,
                [new Widget { Id = "open", Kind = WidgetKind.Button, Name = "Open gate", Color = "Accent", Width = 4, Height = 2 }]), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Dashboards.Count == 1));
            vm.OpenDashboardCommand.Execute(vm.Dashboards[0]);
            var button = vm.ActiveTab!.Widgets.Single();

            button.PressCommand.Execute(null);
            Assert.True(await Headless.UntilAsync(() => button.PressNote == DashboardRules.NoRule));
            Assert.False(button.PressFailed);
            Assert.Equal(DashboardRules.NoRule, button.ButtonView!.Note);
            var fired = Assert.Single(host.Publisher.Fired);
            Assert.Equal(DashboardsPluginInfo.ButtonTypeId(saved.Id, "open"), fired.TypeId);

            // The answer goes after a moment; a rule that uses the button: "Sent".
            time.Advance(DashboardsPageViewModel.NoteTime);
            Assert.True(await Headless.UntilAsync(() => button.PressNote is null));
            Assert.Null(button.ButtonView.Note);
            host.Publisher.Rules.Add(fired.TypeId);
            button.PressCommand.Execute(null);
            Assert.True(await Headless.UntilAsync(() => button.PressNote == DashboardRules.Sent));
            Assert.True(button.PressCommand.CanExecute(null)); // no lock after a press
            button.PressCommand.Execute(null);
            Assert.True(await Headless.UntilAsync(() => host.Publisher.Fired.Count == 3));

            // Another user removed the button meanwhile: the server's reason shows below it as an error.
            await host.Plugin.SaveAsync(new SaveDashboardRequest(saved.Id, "Gate house", 0, []), CancellationToken.None);
            button.PressCommand.Execute(null);
            Assert.True(await Headless.UntilAsync(() => button.PressFailed));
            Assert.Equal(DashboardRules.ButtonGone, button.PressNote);
            Assert.Equal(3, host.Publisher.Fired.Count);
        }
    });

    [Fact]
    public Task A_button_does_nothing_while_editing_and_is_disabled_without_the_right() => OnUi(async () =>
    {
        var (host, ctx, vm, _) = ButtonPage();
        using (host)
        using (vm)
        {
            var saved = await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Gate house", 0,
                [new Widget { Id = "open", Kind = WidgetKind.Button, Name = "Open gate", Color = "Accent", Width = 4, Height = 2 }]), CancellationToken.None);
            Assert.True(await Headless.UntilAsync(() => vm.Dashboards.Count == 1));
            vm.EditDashboardCommand.Execute(vm.Dashboards[0]);
            var button = vm.ActiveTab!.Widgets.Single();
            Assert.True(vm.ActiveTab.IsEditing);
            Assert.False(button.ButtonView!.Button.IsHitTestVisible); // the edit frame takes the press
            await button.PressCommand.ExecuteAsync(null); // returns at once while editing
            Assert.Empty(host.Publisher.Fired);
            vm.CancelEditCommand.Execute(null);

            ctx.Denied.Add(PressRight);
            ctx.RaisePermissionsChanged();
            Assert.False(vm.MayPress);
            Assert.False(button.ButtonView.Button.IsEnabled);
            Assert.True(ToolTip.GetShowOnDisabled(button.ButtonView.Button));
            Assert.Equal(DashboardRules.PressNotAllowed, ToolTip.GetTip(button.ButtonView.Button));
            await button.PressCommand.ExecuteAsync(null);
            Assert.Empty(host.Publisher.Fired);

            ctx.Denied.Remove(PressRight);
            ctx.RaisePermissionsChanged();
            Assert.True(button.ButtonView.Button.IsEnabled);
            Assert.Null(ToolTip.GetTip(button.ButtonView.Button));
            Assert.Equal(saved.Id, vm.ActiveTab.Id);
        }
    });

    [Fact]
    public async Task The_page_shows_buttons_in_view_and_edit_mode()
    {
        var done = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var host = new PluginHost();
            var io = new TestDevice("10.0.0.120", "A9210") { Category = Oadm.Sdk.Devices.DeviceCategory.IoModule };
            var gate = new TestDevice("10.0.0.121", "A1601") { Category = Oadm.Sdk.Devices.DeviceCategory.DoorController };
            var ctx = new TestContext(host, [io, gate]);
            using (host)
            {
                host.Feed.Publish(Types.Gate, gate.Id, PublishedEventKind.On, ("port", "1"));
                host.Feed.Publish(Types.Temperature, io.Id, PublishedEventKind.Fired, ("temperature", "21.5"));
                var saved = await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Gate house", 0,
                [
                    new Widget { Id = "g", Kind = WidgetKind.OnOff, TypeId = Types.Gate, Field = DashboardsPluginInfo.ActiveField, DeviceId = gate.Id, Name = "Gate", OnText = "Open", OffText = "Closed", OnIcon = "lockOpen", OnColor = "Amber", OffIcon = "lock", X = 0, Y = 0, Width = 6, Height = 6 },
                    new Widget { Id = "t", Kind = WidgetKind.Number, TypeId = Types.Temperature, Field = "temperature", DeviceId = io.Id, Name = "Gate house", Unit = "°C", X = 6, Y = 0, Width = 6, Height = 6 },
                    // The three sizes side by side in tiles of the same size; a light fill takes dark text.
                    new Widget { Id = "open", Kind = WidgetKind.Button, Name = "Open gate", Icon = "lockOpen", Color = "Accent", X = 0, Y = 6, Width = 4, Height = 3, ButtonSize = ButtonSize.Large },
                    new Widget { Id = "close", Kind = WidgetKind.Button, Name = "Close gate", Icon = "lock", Color = "Neutral", X = 4, Y = 6, Width = 4, Height = 3, ButtonSize = ButtonSize.Medium },
                    new Widget { Id = "light", Kind = WidgetKind.Button, Name = "Yard light", Icon = "lightbulb", Color = "Amber", X = 8, Y = 6, Width = 4, Height = 3, ButtonSize = ButtonSize.Small },
                    new Widget { Id = "alarm", Kind = WidgetKind.Button, Name = "Sound the siren", Color = "Red", X = 0, Y = 9, Width = 8, Height = 3, ButtonSize = ButtonSize.Large },
                    new Widget { Id = "info", Kind = WidgetKind.Button, Name = "Call reception", Icon = "bell", Color = "Text", X = 8, Y = 9, Width = 4, Height = 3, ButtonSize = ButtonSize.Large },
                ]), CancellationToken.None);
                host.Publisher.Rules.Add(DashboardsPluginInfo.ButtonTypeId(saved.Id, "open"));

                var view = (DashboardsPageView)new DashboardsPage().CreateView(ctx);
                var page = (DashboardsPageViewModel)view.DataContext!;
                var window = Headless.Host(view);
                window.Show();
                Assert.True(await Headless.UntilAsync(() => page.Dashboards.Count == 1));
                page.OpenDashboardCommand.Execute(page.Dashboards[0]);
                Assert.True(await Headless.UntilAsync(() => page.ActiveTab!.Widgets.Where(w => w.Widget.IsValue).All(w => w.Value is not null)));
                var buttons = view.GetVisualDescendants().OfType<ButtonWidgetView>().ToList();
                Assert.Equal(5, buttons.Count);
                Assert.Equal([1.0, 0.7, 0.45], buttons.Take(3).Select(b => b.Share)); // Large, Medium, Small
                Assert.True(buttons.Single(b => b.Label == "Yard light").OnLight); // amber: dark text
                Assert.True(buttons.Single(b => b.Label == "Call reception").OnLight); // the text color of the dark theme: dark text
                Assert.False(buttons.Single(b => b.Label == "Open gate").OnLight); // accent: white text
                Assert.All(buttons, b => Assert.True(b.Button.IsEnabled));
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<DeviceIcon>(), _ => true); // buttons never show a device

                var open = page.ActiveTab!.Widgets.Single(w => w.Id == "open");
                open.PressCommand.Execute(null);
                Assert.True(await Headless.UntilAsync(() => open.ButtonView!.Note == DashboardRules.Sent));
                Headless.Pump();
                Headless.Capture(window, "plugin-dashboards-buttons.png");

                // No rule uses "Close gate": the answer lies over the bottom of the button, which keeps its size.
                var close = page.ActiveTab!.Widgets.Single(w => w.Id == "close");
                var before = close.ButtonView!.Button.Bounds;
                close.PressCommand.Execute(null);
                Assert.True(await Headless.UntilAsync(() => close.ButtonView.Note == DashboardRules.NoRule));
                Headless.Pump();
                Assert.Equal(before, close.ButtonView.Button.Bounds);
                Headless.Capture(window, "plugin-dashboards-buttons-norule.png");

                page.EditDashboardCommand.Execute(page.Dashboards[0]);
                page.ActiveTab.Select(open);
                Headless.Pump();
                Assert.True(page.ActiveTab.Editor!.IsButton);
                Assert.Equal(7, view.GetVisualDescendants().OfType<TileFrame>().Count());
                Headless.Capture(window, "plugin-dashboards-buttons-edit.png");
                window.Close();
            }

            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public void A_light_fill_takes_dark_text_and_a_dark_fill_white_text()
    {
        Assert.False(ButtonWidgetView.IsLightFill(Avalonia.Media.Color.Parse("#6C5CE7"))); // accent
        Assert.False(ButtonWidgetView.IsLightFill(Avalonia.Media.Color.Parse("#000000")));
        Assert.False(ButtonWidgetView.IsLightFill(Avalonia.Media.Color.Parse("#9A9A9A"))); // grey: 0.32
        Assert.True(ButtonWidgetView.IsLightFill(Avalonia.Media.Color.Parse("#E8B84A"))); // amber of the dark theme: 0.53
        Assert.True(ButtonWidgetView.IsLightFill(Avalonia.Media.Color.Parse("#F2F2F2"))); // the text color of the dark theme
        Assert.True(ButtonWidgetView.IsLightFill(Avalonia.Media.Color.Parse("#FFFFFF")));
        Assert.False(ButtonWidgetView.IsLightFill(Avalonia.Media.Color.Parse("#1A1A1A"))); // the text color of the light theme
        Assert.True(ButtonWidgetView.IsLightFill(Avalonia.Media.Color.Parse("#BCBCBC"))); // 0.5: dark from here on
        Assert.False(ButtonWidgetView.IsLightFill(Avalonia.Media.Color.Parse("#BBBBBB")));
    }
}
