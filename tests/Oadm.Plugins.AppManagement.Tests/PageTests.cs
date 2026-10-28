using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Acap;
using Oadm.Plugins.AppManagement.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.AppManagement.Tests;

/// <summary>The page's view model: choose first, the summaries, the filters and the actions (no Avalonia needed).</summary>
public sealed class PageTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The page with every application shown (Hide AXIS apps has its own test).</summary>
    private static ApplicationsViewModel Page(FakePageContext ctx) => new(ctx, Clock) { HideAxisApps = false };

    [Fact]
    public async Task Nothing_is_read_before_a_device_choice()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        ctx.DeviceList.Add(new PageDevice("10.0.0.48"));
        using var vm = Page(ctx);

        vm.Activate();
        await vm.Loading;

        Assert.Equal(0, ctx.CallsOf(AppManagementMethods.Read));
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.Equal(ApplicationsViewModel.ChooseFirst, vm.EmptyText);
        Assert.True(vm.ShowEmpty);
    }

    [Fact]
    public async Task A_choice_reads_exactly_the_chosen_devices()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        var a = new PageDevice("10.0.0.48");
        var b = new PageDevice("10.0.0.49");
        ctx.DeviceList.AddRange([a, b]);
        using var vm = Page(ctx);
        vm.Activate();
        await vm.Loading;

        vm.DeviceChoice.Set(DeviceChoice.Devices([b.Id]));
        await vm.RefreshCommand.ExecuteAsync(null);
        var request = AppManagementJson.Deserialize<ReadRequest>(ctx.LastPayloadOf(AppManagementMethods.Read));
        Assert.False(request.All);
        Assert.Equal([b.Id], request.DeviceIds);

        vm.DeviceChoice.Set(DeviceChoice.AllDevices);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(AppManagementJson.Deserialize<ReadRequest>(ctx.LastPayloadOf(AppManagementMethods.Read)).All);
    }

    [Fact]
    public async Task A_remembered_choice_is_read_when_the_session_has_no_rows_yet()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        var a = new PageDevice("10.0.0.48");
        ctx.DeviceList.Add(a);
        new PageDeviceChoice(ctx).Set(DeviceChoice.Devices([a.Id])); // kept on this computer
        using var vm = Page(ctx);

        vm.Activate();
        await vm.Loading;

        Assert.Equal(1, ctx.CallsOf(AppManagementMethods.Read));
    }

    [Fact]
    public void A_license_expires_soon_within_five_days()
    {
        var today = Samples.Today;
        Assert.True(AppLicenses.ExpiresSoon(Samples.App("a", license: "Valid", expires: "2026-11-02"), today));
        Assert.False(AppLicenses.ExpiresSoon(Samples.App("a", license: "Valid", expires: "2026-11-03"), today));
        Assert.False(AppLicenses.ExpiresSoon(Samples.App("a", license: "Valid", expires: "0"), today));
        Assert.False(AppLicenses.ExpiresSoon(Samples.App("a", license: "None"), today));
    }

    [Fact]
    public void A_valid_license_with_an_end_shows_its_date_only()
    {
        using var vm = Page(new FakePageContext());
        vm.ApplyRows([Samples.Device("10.0.0.1",
            Samples.App("a", license: "Valid", expires: "2027-09-18"),
            Samples.App("b", license: "Valid", expires: "2026-10-30"),
            Samples.App("c", license: "Valid"),
            Samples.App("d", license: "None"),
            Samples.App("e", license: "Missing"))]);

        var rows = vm.DeviceRows.ToDictionary(r => r.AppKey!);
        Assert.Equal("2027-09-18", rows["a"].LicenseText);
        Assert.True(rows["a"].ShowPlainLicense);
        Assert.Equal("2026-10-30", rows["b"].LicenseText);
        Assert.True(rows["b"].IsLicenseWarning);
        Assert.Equal("Valid", rows["c"].LicenseText);
        Assert.Equal("Not required", rows["d"].LicenseText);
        Assert.Equal("Missing", rows["e"].LicenseText);
        Assert.True(rows["e"].IsLicenseError);
    }

    [Fact]
    public void Hide_axis_apps_hides_preinstalled_apps_and_apps_named_axis_and_is_on_by_default()
    {
        using var vm = new ApplicationsViewModel(new FakePageContext(), Clock);
        vm.ApplyRows([Samples.Device("10.0.0.48",
            Samples.App("bundled", "Preinstalled Tool", bundled: true),
            Samples.App("lpv", "AXIS License Plate Verifier", vendor: "Axis Communications"),
            Samples.App("aoa", "Axis Object Analytics"),
            Samples.App("taxis", "Taxis Counter"),
            Samples.App("eva", "Eva - Event Virtualizer"))]);

        Assert.True(vm.HideAxisApps);
        Assert.Equal(["eva", "taxis"], vm.DeviceRows.Select(r => r.AppKey).Order());
        vm.HideAxisApps = false;
        Assert.Equal(["aoa", "bundled", "eva", "lpv", "taxis"], vm.DeviceRows.Select(r => r.AppKey).Order());
    }

    [Fact]
    public void A_device_without_applications_and_a_failed_read_keep_one_row()
    {
        using var vm = Page(new FakePageContext());
        vm.HideAxisApps = true; // the preinstalled app is hidden
        vm.ApplyRows(
        [
            Samples.Device("10.0.0.48", Samples.App("vmd", bundled: true)),
            Samples.Failed("10.0.0.49", "Timeout after 15 s"),
            Samples.Failed("10.0.0.50", AppManagementInfo.NotChecked("Credentials required"), notChecked: true),
            Samples.Failed("10.0.0.51", AppManagementInfo.NoApplicationApi),
        ]);

        Assert.Equal(4, vm.DeviceRows.Count);
        Assert.Equal(AppRow.NoApplications, vm.DeviceRows[0].AppName);
        Assert.Equal("Timeout after 15 s", vm.DeviceRows[1].StatusText);
        Assert.True(vm.DeviceRows[1].IsStatusError);
        Assert.True(vm.DeviceRows[2].IsStatusWarning);
        Assert.Equal(AppManagementInfo.NoApplicationApi, vm.DeviceRows[3].StatusText);
    }

    [Theory]
    [InlineData(AppStatusFilter.All, new[] { "aoa@1", "lpv@1", "lpv@2", "x@3" })]
    [InlineData(AppStatusFilter.NotRunning, new[] { "lpv@2" })]
    [InlineData(AppStatusFilter.LicenseMissing, new[] { "lpv@2" })]
    [InlineData(AppStatusFilter.LicenseExpiresSoon, new[] { "lpv@1" })]
    [InlineData(AppStatusFilter.ReadFailed, new[] { "x@3" })]
    public void The_status_filter_keeps_the_matching_rows(AppStatusFilter filter, string[] expected)
    {
        using var vm = Page(new FakePageContext());
        vm.ApplyRows(
        [
            Samples.Device("10.0.0.1", Samples.App("aoa"), Samples.App("lpv", license: "Valid", expires: "2026-10-30")),
            Samples.Device("10.0.0.2", Samples.App("lpv", status: "Stopped", license: "Missing")),
            Samples.Failed("10.0.0.3", "Timeout after 15 s"),
        ]);

        vm.SelectedStatus = ApplicationsViewModel.StatusChoices.Single(c => c.Value == filter);

        Assert.Equal(expected, vm.DeviceRows.Select(r => (r.AppKey ?? "x") + "@" + r.Address.Split('.')[^1]).Order());
    }

    [Theory]
    [InlineData("Plate", 1)]
    [InlineData("acme", 1)]
    [InlineData("M3106", 0)]
    [InlineData("10.0.0.2", 2)]
    [InlineData("2.10", 1)]
    [InlineData("B8A44F000001", 2)]
    public void The_search_looks_in_application_vendor_device_model_and_version(string search, int rows)
    {
        using var vm = Page(new FakePageContext());
        vm.ApplyRows(
        [
            Samples.Device("10.0.0.1", Samples.App("lpv", "AXIS License Plate Verifier", "2.10"), Samples.App("tool", "Tool", "1.0", vendor: "Acme")),
            Samples.Device("10.0.0.2", Samples.App("aoa", "AXIS Object Analytics", "1.30"), Samples.App("aoa2", "Other", "1.31")),
        ]);

        vm.SearchText = search;

        Assert.Equal(rows, vm.DeviceRows.Count);
    }

    [Fact]
    public void The_device_grid_has_one_row_per_device_and_the_lower_grid_the_selected_devices_applications()
    {
        using var vm = Page(new FakePageContext());
        var a = Samples.Device("10.0.0.1", Samples.App("lpv", status: "Stopped"), Samples.App("aoa", license: "Missing"));
        var b = Samples.Device("10.0.0.1", Samples.App("lpv")) with { Soc = "Axis Artpec-7", Architecture = "armv7hf" }; // the same address at another site
        var c = Samples.Device("10.0.0.3");
        var d = Samples.Failed("10.0.0.4", "Timeout after 15 s");
        vm.ApplyRows([a, b, c, d]);

        Assert.Equal(4, vm.Devices.Count);
        var rows = vm.Devices.ToDictionary(r => r.DeviceId);
        Assert.Equal(("ARTPEC-7", "armv7hf", 1), (rows[b.DeviceId].Soc, rows[b.DeviceId].Architecture, rows[b.DeviceId].AppCount));
        Assert.Equal("1 license problem, 1 not running", rows[a.DeviceId].SummaryText);
        Assert.True(rows[a.DeviceId].IsError);
        Assert.Equal("All running", rows[b.DeviceId].SummaryText);
        Assert.True(rows[b.DeviceId].IsOk);
        Assert.Equal(AppRow.NoApplications, rows[c.DeviceId].SummaryText);
        Assert.Equal("Timeout after 15 s", rows[d.DeviceId].SummaryText);
        Assert.True(rows[d.DeviceId].IsError);

        // Nothing selected: the lower grid is empty and says what to do.
        Assert.Empty(vm.SelectedApps);
        Assert.Equal(ApplicationsViewModel.SelectDevicesFirst, vm.AppsEmptyText);

        vm.SetSelectedDevices([rows[a.DeviceId], rows[b.DeviceId]]);
        Assert.Equal(3, vm.SelectedApps.Count);
        Assert.Null(vm.AppsEmptyText);
        Assert.Equal([a.DeviceId, b.DeviceId], vm.TargetDevices()); // Install: the selected devices
        Assert.Empty(vm.TargetApplications()); // Start, Stop, Remove: only selected applications

        // A pushed change (a task's end reads the device again) keeps the selected devices and application rows.
        vm.SetDeviceSelection(vm.SelectedApps.Where(r => r.DeviceId == b.DeviceId));
        Assert.Equal([a.DeviceId, b.DeviceId], vm.TargetDevices()); // an application row below does not narrow Install
        vm.ApplyRows([b with { Activity = AppActivities.Stopping }]);
        Assert.Equal(2, vm.SelectedDeviceIds.Count);
        Assert.Equal(3, vm.SelectedApps.Count);
        var kept = Assert.Single(vm.SelectedRows);
        Assert.Equal((b.DeviceId, "lpv"), (kept.DeviceId, kept.AppKey));
        Assert.Same(kept, vm.SelectedApps.Single(r => r.DeviceId == b.DeviceId)); // the new row
        Assert.Single(vm.TargetApplications());
        Assert.Equal(AppActivities.Stopping, vm.Devices.Single(r => r.DeviceId == b.DeviceId).SummaryText);

        // A filter that hides a selected device drops it from the selection.
        vm.SearchText = "armv7hf";
        Assert.Equal([b.DeviceId], vm.SelectedDeviceIds);
        Assert.Equal([b.DeviceId], vm.SelectedApps.Select(r => r.DeviceId).Distinct());

        // The lower grid's own search keeps the selection of the rows it still shows.
        vm.SearchText = string.Empty;
        vm.SetSelectedDevices(vm.Devices.Where(r => r.DeviceId == a.DeviceId));
        vm.SetDeviceSelection(vm.SelectedApps.Where(r => r.AppKey == "lpv"));
        vm.AppsSearchText = "lpv";
        Assert.Equal(["lpv"], vm.SelectedApps.Select(r => r.AppKey));
        Assert.Single(vm.SelectedRows);
        vm.AppsSearchText = "nothing like it";
        Assert.Empty(vm.SelectedApps);
        Assert.Empty(vm.SelectedRows);
        Assert.Equal(ApplicationsViewModel.NothingMatches, vm.AppsEmptyText);
        vm.AppsSearchText = string.Empty;
        Assert.Equal(2, vm.SelectedApps.Count);
    }

    [Fact]
    public void A_task_on_one_application_shows_only_in_its_row()
    {
        var device = Samples.Device("10.0.0.202", Samples.App("teachcam"), Samples.App("eva")) with
        {
            Activity = AppActivities.Stopping,
            ActivityApplications = ["teachcam"],
        };
        var rows = AppRow.For(device, Samples.Today).Apps.ToDictionary(r => r.AppKey!);
        Assert.Equal(AppActivities.Stopping, rows["teachcam"].StatusText);
        Assert.True(rows["teachcam"].IsStatusAccent);
        Assert.Equal("Running", rows["eva"].StatusText);

        // No application named (Install): every row.
        Assert.All(AppRow.For(device with { ActivityApplications = [] }, Samples.Today).Apps, r => Assert.Equal(AppActivities.Stopping, r.StatusText));
    }

    [Fact]
    public void Several_selected_devices_group_the_lower_grid()
    {
        using var vm = Page(new FakePageContext());
        vm.ApplyRows([Samples.Device("10.0.0.1", Samples.App("lpv")), Samples.Device("10.0.0.2", Samples.App("lpv"))]);

        vm.SetSelectedDevices([vm.Devices[0]]);
        Assert.False(vm.GroupApps);
        vm.SetSelectedDevices(vm.Devices);
        Assert.True(vm.GroupApps);
        Assert.Equal(2, vm.SelectedApps.Select(r => r.Group).Distinct().Count());
        Assert.Equal("10.0.0.2", vm.SelectedApps[1].Group.ToString());
    }

    [Fact]
    public async Task The_row_menu_offers_settings_or_the_app_page_and_always_the_log()
    {
        var ctx = new FakePageContext();
        ctx.PluginsOn.Add(AppSettingsViewModel.WebViewId);
        var withSettings = new DeviceSettings { Address = "10.0.0.1", Settings = [new AppSettingDefinition("Mode", AppSettingKind.Text)] };
        var answers = new Dictionary<string, DeviceSettings>(StringComparer.Ordinal)
        {
            ["withsettings"] = withSettings,
            ["withpage"] = new DeviceSettings { Address = "10.0.0.1" },
            ["plain"] = new DeviceSettings { Address = "10.0.0.1" },
            ["broken"] = new DeviceSettings { Address = "10.0.0.1", Error = "Timeout after 15 s" },
        };
        ctx.Answers[AppManagementMethods.GetSettings] = payload =>
        {
            var request = AppManagementJson.Deserialize<SettingsRequest>(payload);
            return AppManagementJson.Serialize(new SettingsReply { Devices = [answers[request.Application] with { DeviceId = request.DeviceIds[0] }] });
        };
        using var vm = Page(ctx);
        vm.ApplyRows([Samples.Device("10.0.0.1",
            Samples.App("withsettings"),
            Samples.App("withpage") with { ConfigurationPage = "local/withpage/index.html" },
            Samples.App("plain"),
            Samples.App("broken"))]);
        var rows = vm.DeviceRows.ToDictionary(r => r.AppKey!);

        Assert.Equal(new AppRowMenu(true, false), await vm.MenuForAsync(rows["withsettings"], CancellationToken.None));
        Assert.Equal(new AppRowMenu(false, true), await vm.MenuForAsync(rows["withpage"], CancellationToken.None));
        Assert.Equal(new AppRowMenu(false, false), await vm.MenuForAsync(rows["plain"], CancellationToken.None)); // only Show log
        Assert.Equal(new AppRowMenu(true, false), await vm.MenuForAsync(rows["broken"], CancellationToken.None)); // not known: Settings stays

        // The answer is kept: the device is asked once per application.
        var calls = ctx.CallsOf(AppManagementMethods.GetSettings);
        await vm.MenuForAsync(rows["withsettings"], CancellationToken.None);
        Assert.Equal(calls, ctx.CallsOf(AppManagementMethods.GetSettings));

        vm.OpenAppPageCommand.Execute(rows["withpage"]);
        Assert.Equal((rows["withpage"].DeviceId, "/local/withpage/index.html"), Assert.Single(ctx.OpenedPages));

        // Without Web View the page cannot open: only Show log.
        ctx.PluginsOn.Clear();
        Assert.Equal(new AppRowMenu(false, false), await vm.MenuForAsync(rows["withpage"], CancellationToken.None));
    }

    [Fact]
    public async Task Install_and_install_licenses_open_the_pages_own_dialogs_for_their_targets()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        using var vm = Page(ctx);
        var dialogs = new RecordingDialogs();
        vm.Dialogs = dialogs;
        var a = Samples.Device("10.0.0.1", Samples.App("lpv"));
        var b = Samples.Device("10.0.0.2", Samples.App("lpv"), Samples.App("aoa"));
        var c = Samples.Device("10.0.0.3", Samples.App("aoa"));
        vm.ApplyRows([a, b, c]);

        // Install: the devices selected above, also when application rows below are selected (they never narrow it).
        vm.SetSelectedDevices(vm.Devices);
        vm.SetDeviceSelection(vm.SelectedApps.Where(r => r.AppKey == "lpv"));
        await vm.InstallCommand.ExecuteAsync(null);
        var install = Assert.Single(dialogs.Installs);
        Assert.Equal([a.DeviceId, b.DeviceId, c.DeviceId], install.Devices.Select(d => d.DeviceId));

        // Install licenses: the selected application rows themselves.
        await vm.InstallLicensesCommand.ExecuteAsync(null);
        var licenses = Assert.Single(dialogs.Licenses);
        Assert.Equal([(a.DeviceId, "lpv"), (b.DeviceId, "lpv")], licenses.Targets.Select(r => (r.DeviceId, r.AppKey!)));

        // Neither runs the Applications plugin's own dialog or starts anything by itself.
        Assert.Empty(ctx.TaskRuns);
        Assert.Equal(0, ctx.CallsOf(AppManagementMethods.Run));
        Assert.Equal(0, ctx.CallsOf(AppManagementMethods.Follow));
    }

    [Fact]
    public void A_failed_task_shows_its_message_next_to_the_fresh_state_in_its_rows()
    {
        var device = Samples.Device("10.0.0.1", Samples.App("lpv", status: "Stopped"), Samples.App("aoa")) with
        {
            TaskResults =
            [
                new AppTaskResult { Application = "lpv", Failed = true, Message = "The application did not start. Nothing was changed." },
                new AppTaskResult { Application = "aoa", Failed = false, Message = "No license key for this device." },
            ],
        };
        var (apps, _) = AppRow.For(device, Samples.Today);
        var lpv = apps.Single(r => r.AppKey == "lpv");
        Assert.Equal("Stopped. The application did not start. Nothing was changed.", lpv.StatusText);
        Assert.True(lpv.IsStatusError);
        Assert.False(lpv.IsStatusOk);
        Assert.StartsWith("0", lpv.StatusSortKey, StringComparison.Ordinal); // problems first

        var aoa = apps.Single(r => r.AppKey == "aoa");
        Assert.Equal("Running. No license key for this device.", aoa.StatusText);
        Assert.True(aoa.IsStatusWarning);
        Assert.False(aoa.IsStatusOk);

        // A task of the whole device (Install from the dialog) shows in every row, and in the row without applications.
        var install = new AppTaskResult { Failed = true, Message = "No package for armv7hf." };
        Assert.All(AppRow.For(device with { TaskResults = [install] }, Samples.Today).Apps, r => Assert.EndsWith("No package for armv7hf.", r.StatusText, StringComparison.Ordinal));
        var empty = AppRow.For(Samples.Device("10.0.0.2") with { TaskResults = [install] }, Samples.Today).Empty;
        Assert.Equal("No package for armv7hf.", empty.StatusText);
        Assert.True(empty.IsStatusError);

        // Without a result the row is as before.
        var plain = AppRow.For(Samples.Device("10.0.0.3", Samples.App("lpv")), Samples.Today).Apps.Single();
        Assert.Equal("Running", plain.StatusText);
        Assert.True(plain.IsStatusOk);
    }

    [Theory]
    [InlineData(AcapAction.Start, "Start applications", "Start AXIS License Plate Verifier on 2 devices?", "Start")]
    [InlineData(AcapAction.Stop, "Stop applications", "Stop AXIS License Plate Verifier on 2 devices?", "Stop")]
    [InlineData(AcapAction.Remove, "Remove applications", "Remove AXIS License Plate Verifier from 2 devices? The applications and their settings are deleted from the devices.", "Remove")]
    public async Task Start_stop_and_remove_are_confirmed_and_sent_per_application(AcapAction action, string title, string message, string confirm)
    {
        var ctx = new FakePageContext().WithEmptyServer();
        using var vm = Page(ctx);
        var a = Samples.Device("10.0.0.1", Samples.App("lpv", "AXIS License Plate Verifier"), Samples.App("aoa", "AXIS Object Analytics"));
        var b = Samples.Device("10.0.0.2", Samples.App("lpv", "AXIS License Plate Verifier"));
        vm.ApplyRows([a, b]);
        vm.SetDeviceSelection(vm.DeviceRows.Where(r => r.AppKey == "lpv"));
        var command = action switch { AcapAction.Start => vm.StartCommand, AcapAction.Stop => vm.StopCommand, _ => vm.RemoveCommand };

        await command.ExecuteAsync(null);

        Assert.Equal((title, message, confirm), Assert.Single(ctx.Confirmations));
        var request = AppManagementJson.Deserialize<RunRequest>(ctx.LastPayloadOf(AppManagementMethods.Run));
        Assert.Equal(action, request.Action);
        var item = Assert.Single(request.Items);
        Assert.Equal("lpv", item.Application);
        Assert.Equal(new[] { a.DeviceId, b.DeviceId }.Order(), item.DeviceIds.Order());

        // Declined: nothing is sent.
        ctx.ConfirmAnswer = false;
        vm.SetDeviceSelection(vm.DeviceRows.Where(r => r.AppKey == "lpv"));
        await command.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(AppManagementMethods.Run));
    }

    [Fact]
    public void Several_applications_are_counted_in_the_confirmation_and_rows_without_one_are_left_out()
    {
        using var vm = Page(new FakePageContext());
        vm.HideAxisApps = true; // the preinstalled app is hidden
        vm.ApplyRows(
        [
            Samples.Device("10.0.0.1", Samples.App("lpv"), Samples.App("aoa")),
            Samples.Device("10.0.0.2", Samples.App("vmd", bundled: true)),
        ]);
        vm.SetSelectedDevices(vm.Devices);
        vm.SetDeviceSelection(vm.DeviceRows);

        var items = vm.TargetApplications();
        Assert.Equal(["aoa", "lpv"], items.Select(i => i.Application).Order());
        Assert.Equal(2, vm.TargetDevices().Count); // Install: the device without applications too
    }

    [Fact]
    public void The_actions_the_role_lacks_are_hidden()
    {
        var ctx = new FakePageContext();
        ctx.Denied.Add(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Remove));
        ctx.Denied.Add(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Install));
        using var vm = Page(ctx);
        vm.ApplyRows([Samples.Device("10.0.0.1", Samples.App("lpv"))]);
        vm.SetDeviceSelection(vm.DeviceRows);

        Assert.False(vm.MayRemove);
        Assert.False(vm.MayInstall);
        Assert.True(vm.MayStartStop);
        Assert.True(vm.MayLicense);
        Assert.False(vm.RemoveCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));

        ctx.Denied.Add(PermissionKeys.Of(AppManagementInfo.PluginId, AppManagementInfo.ViewPermission));
        ctx.RaisePermissionsChanged();
        Assert.False(vm.MayStartStop);
        Assert.Equal(ApplicationsViewModel.NoViewRight, vm.EmptyText);
    }

    [Fact]
    public void Pushed_rows_replace_whole_devices_and_removed_devices_go()
    {
        using var vm = Page(new FakePageContext());
        var a = Samples.Device("10.0.0.1", Samples.App("lpv"));
        vm.HandleEvent(new PluginEvent(AppManagementMethods.RowsTopic, AppManagementJson.Serialize(new RowsEvent { Devices = [a] })));
        Assert.Single(vm.DeviceRows);

        vm.HandleEvent(new PluginEvent(AppManagementMethods.RowsTopic, AppManagementJson.Serialize(new RowsEvent { Devices = [a with { Activity = AppActivities.Stopping }] })));
        Assert.Equal(AppActivities.Stopping, Assert.Single(vm.DeviceRows).StatusText);
        Assert.True(vm.DeviceRows[0].IsStatusAccent);

        vm.HandleEvent(new PluginEvent(AppManagementMethods.RowsTopic, AppManagementJson.Serialize(new RowsEvent { Removed = [a.DeviceId] })));
        Assert.Empty(vm.DeviceRows);

        vm.HandleEvent(new PluginEvent(AppManagementMethods.StateTopic, AppManagementJson.Serialize(new ReadState { Reading = true, Done = 5, Total = 20 })));
        Assert.True(vm.IsReading);
        Assert.Equal(25, vm.ProgressValue);
    }

    /// <summary>The page's dialogs: records what the page opens, starts nothing.</summary>
    private sealed class RecordingDialogs : IApplicationDialogs
    {
        public List<InstallAppsViewModel> Installs { get; } = [];

        public List<InstallLicensesViewModel> Licenses { get; } = [];

        public Task ShowLogAsync(AppLogViewModel log) => Task.CompletedTask;

        public Task ShowSettingsAsync(AppSettingsViewModel settings) => Task.CompletedTask;

        public Task<bool> ShowInstallAsync(InstallAppsViewModel install)
        {
            Installs.Add(install);
            return Task.FromResult(false);
        }

        public Task<bool> ShowLicensesAsync(InstallLicensesViewModel licenses)
        {
            Licenses.Add(licenses);
            return Task.FromResult(false);
        }
    }
}
