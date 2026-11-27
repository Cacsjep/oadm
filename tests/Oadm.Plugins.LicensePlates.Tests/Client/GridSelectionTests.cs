using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

using Oadm.Plugins.LicensePlates.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

/// <summary>
/// A row selected in the real grid of a tab (a click in the headless window, as the user does) reaches the view model and
/// turns its toolbar buttons on: Cameras, Plates and Log.
/// </summary>
public sealed class GridSelectionTests
{
    private static void Click(Window window, Control target)
    {
        var point = target.TranslatePoint(new Avalonia.Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Headless.Pump();
    }

    private static async Task<(LicensePlatesViewModel Page, LicensePlatesView View, Window Window)> ShowAsync()
    {
        GridSort.Install(); // as the client does at start
        var page = new LicensePlatesViewModel(Samples.Context(), Samples.Clock());
        page.Log.Decode = _ => null;
        var view = new LicensePlatesView { DataContext = page };
        var window = Headless.Host(view);
        window.Show();
        await Samples.SettleAsync(page);
        Headless.Pump();
        return (page, view, window);
    }

    [Fact]
    public async Task A_camera_clicked_in_the_grid_turns_on_Set_up_and_the_other_camera_buttons()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, view, window) = await ShowAsync();
            page.Show(page.CamerasTab);
            Headless.Pump();
            var cameras = view.FindControl<CamerasView>("CamerasPage")!;
            Assert.False(cameras.FindControl<ToolbarButton>("SetUpButton")!.IsEffectivelyEnabled);

            var row = cameras.Grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext == page.Cameras.Rows[0]);
            Click(window, row);

            // Live changes of the cameras while the row stays selected.
            page.OnEvent(LicensePlatesMethods.CamerasTopic, "[]");
            await page.Cameras.ReloadTask;
            Headless.Pump();

            Assert.Equal([page.Cameras.Rows[0]], page.Cameras.Selected);
            foreach (var name in new[] { "SetUpButton", "AllowedTagsButton", "CopySettingsButton", "UseRecommendedButton", "RemoveButton" })
            {
                Assert.True(cameras.FindControl<ToolbarButton>(name)!.IsEffectivelyEnabled, name + " stays off");
            }

            page.Deactivate();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task After_Remove_the_row_the_grid_shows_selected_is_the_view_models_selection()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, view, window) = await ShowAsync();
            page.Show(page.CamerasTab);
            Headless.Pump();
            var cameras = view.FindControl<CamerasView>("CamerasPage")!;
            Click(window, cameras.Grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext == page.Cameras.Rows[0]));

            await page.Cameras.RemoveCommand.ExecuteAsync(null);
            Headless.Pump();

            // The grid moves its selection to a remaining row: the view model has the same rows, so the buttons follow.
            Assert.Equal(cameras.Grid.SelectedItems.Cast<object>(), page.Cameras.Selected);
            Assert.Equal(cameras.Grid.SelectedItems.Count == 1, cameras.FindControl<ToolbarButton>("SetUpButton")!.IsEffectivelyEnabled);
            Click(window, cameras.Grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext == page.Cameras.Rows[0]));
            Assert.Equal([page.Cameras.Rows[0]], page.Cameras.Selected);
            Assert.True(cameras.FindControl<ToolbarButton>("SetUpButton")!.IsEffectivelyEnabled);
            page.Deactivate();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_plate_clicked_in_the_grid_turns_on_Change_tags_and_Delete()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, view, window) = await ShowAsync();
            page.Show(page.PlatesTab);
            Headless.Pump();
            var plates = view.FindControl<PlatesView>("PlatesPage")!;
            var row = plates.Grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext == page.Plates.Rows[0]);
            Click(window, row);

            Assert.Equal([page.Plates.Rows[0]], page.Plates.Selected);
            Assert.True(plates.FindControl<ToolbarButton>("ChangeTagsButton")!.IsEffectivelyEnabled);
            Assert.True(plates.FindControl<ToolbarButton>("DeleteButton")!.IsEffectivelyEnabled);

            // The list read again (a live change): new rows; the grid and the view model still agree on the plate.
            var text = page.Plates.Rows[0].Text;
            page.OnEvent(LicensePlatesMethods.PlatesTopic, "{}");
            await Samples.SettleAsync(page);
            Headless.Pump();
            Assert.Equal(plates.Grid.SelectedItems.Cast<object>(), page.Plates.Selected);
            Assert.Equal([text], page.Plates.Selected.Select(p => p.Text));
            Assert.True(plates.FindControl<ToolbarButton>("DeleteButton")!.IsEffectivelyEnabled);
            page.Deactivate();
            window.Close();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task An_unknown_plate_clicked_in_the_log_turns_on_Add_to_plates()
    {
        await Headless.Session.Dispatch(async () =>
        {
            var (page, view, window) = await ShowAsync();
            var log = view.FindControl<LogView>("LogPage")!;
            var unknown = page.Log.Rows.First(r => r.IsUnknown);
            var row = log.Grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext == unknown);
            Click(window, row);

            Assert.Equal([unknown], page.Log.Selected);
            Assert.True(log.FindControl<ToolbarButton>("AddToPlatesButton")!.IsEffectivelyEnabled);

            // The log read again (a filter): new rows; the grid and the view model agree.
            page.Log.Reload();
            await page.Log.LoadTask;
            Headless.Pump();
            Assert.Equal(log.Grid.SelectedItems.Cast<object>(), page.Log.Selected);
            page.Deactivate();
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
