using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Acap;
using Oadm.Plugins.AppManagement.Client;
using Oadm.Plugins.AppManagement.Reading;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.AppManagement.Tests;

/// <summary>Stage 2: Show log and Settings, read through the server part (read only); changes only as oadm.acap tasks.</summary>
public sealed class LogAndSettingsTests : IAsyncLifetime
{
    internal const string Log = """

        ----- Contents of SYSTEM_LOG for 'demo' -----


        2026-10-27T07:45:40.900+01:00 axis-b8a44f631339 [ INFO    ] demo[8617]: Started
        2026-10-27T07:45:41.100+01:00 axis-b8a44f631339 [ WARNING ] demo[8617]: Camera tampering check is slow
        2026-10-27T07:46:02.000+01:00 axis-b8a44f631339 [ ERR     ] demo[8617]: Could not reach 10.0.0.5
        """;

    private readonly FakeCoreContext _ctx = new();
    private AppManagementPlugin _plugin = null!;

    public async Task InitializeAsync()
    {
        _plugin = new AppManagementPlugin(new AppManagementOptions { Time = new FakeTimeProvider(), EventInterval = TimeSpan.FromHours(1) });
        await _plugin.StartAsync(_ctx, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    internal static string Definitions(string parameters) => $"""
        <?xml version="1.0" encoding="iso-8859-1" ?>
        <parameterDefinitions xmlns="http://www.axis.com/ParameterDefinitionsSchema" version="1.0">
          <model>AXIS P3265-V</model>
          <group name="root">
            <group name="demo">
        {parameters}
            </group>
          </group>
        </parameterDefinitions>
        """;

    internal static string Typed(string threshold = "40", string mode = "fast") => Definitions($"""
        <parameter name="Threshold" value="{threshold}" securityLevel="7746" niceName="Threshold"><type><int min="0" max="100" maxlen="3" /></type></parameter>
        <parameter name="Mode" value="{mode}" securityLevel="7746" niceName="Mode"><type><enum><entry value="fast" niceValue="Fast" /><entry value="exact" niceValue="Exact" /></enum></type></parameter>
        <parameter name="Enabled" value="yes" securityLevel="7746" niceName="Enabled"><type><bool true="yes" false="no" /></type></parameter>
        <parameter name="Server" value="10.0.0.5" securityLevel="7746" niceName="Server address"><type><string maxlen="64" /></type></parameter>
        <parameter name="Key" value="******" securityLevel="7746" niceName="Pre-shared key"><type writeonly="true"><password maxlen="16" /></type></parameter>
        <parameter name="Internal" value="1" securityLevel="7746"><type hidden="true"><string /></type></parameter>
        """);

    /// <summary>A camera that answers the log and the settings of "demo" and refuses everything else.</summary>
    private static FakeCamera Camera(string definitions) => new()
    {
        Respond = (request, _) =>
        {
            var uri = request.RequestUri?.OriginalString ?? string.Empty;
            return uri.StartsWith(AppLogReader.LogPath, StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Log) }
                : uri.Contains("listdefinitions", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(definitions) }
                : new HttpResponseMessage(HttpStatusCode.BadRequest);
        },
    };

    private FakeDevice Add(string address, string? definitions = null, DeviceStatus status = DeviceStatus.Ok) =>
        _ctx.Add(Samples.ManagedDevice(address, status), Camera(definitions ?? Typed()));

    private async Task<T> CallAsync<T>(ICallContext call, string method, object payload) =>
        AppManagementJson.Deserialize<T>(await _ctx.InvokeAsync(_plugin, call, method, AppManagementJson.Serialize(payload)));

    [Fact]
    public async Task The_log_is_read_with_one_request_newest_last()
    {
        var device = Add("10.0.0.48");
        var reply = await CallAsync<LogReply>(_ctx.CallAsAllDevices(), AppManagementMethods.GetLog, new LogRequest { DeviceId = device.Id, Application = "demo" });

        Assert.Null(reply.Error);
        Assert.Equal(3, reply.Lines.Count);
        Assert.EndsWith("Could not reach 10.0.0.5", reply.Lines[^1].Text, StringComparison.Ordinal);
        Assert.Equal(["GET " + AppLogReader.BuildUri("demo")], _ctx.Cameras[device.Id].Requests);
        Assert.Equal(0, _ctx.Cameras[device.Id].Writes);
    }

    [Fact]
    public async Task Log_and_settings_stay_in_the_callers_group_and_skip_devices_that_are_not_ok()
    {
        var mine = Add("10.0.0.48", status: DeviceStatus.CredentialsRequired);
        var other = Add("10.0.0.49");
        var call = _ctx.CallAsGroup([mine.Id]);

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => CallAsync<LogReply>(call, AppManagementMethods.GetLog, new LogRequest { DeviceId = other.Id, Application = "demo" }));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => CallAsync<SettingsReply>(call, AppManagementMethods.GetSettings, new SettingsRequest { Application = "demo", DeviceIds = [mine.Id, other.Id] }));
        var log = await CallAsync<LogReply>(call, AppManagementMethods.GetLog, new LogRequest { DeviceId = mine.Id, Application = "demo" });
        Assert.Equal("Not checked: Credentials required", log.Error);
        Assert.All(_ctx.Cameras.Values, c => Assert.Empty(c.Requests));
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync<LogReply>(_ctx.CallAsAllDevices(), AppManagementMethods.GetLog, new LogRequest { DeviceId = other.Id, Application = "../x" }));
    }

    [Fact]
    public async Task Settings_leave_hidden_ones_out_and_never_return_passwords()
    {
        var device = Add("10.0.0.48");
        var reply = await CallAsync<SettingsReply>(_ctx.CallAsAllDevices(), AppManagementMethods.GetSettings, new SettingsRequest { Application = "demo", DeviceIds = [device.Id] });

        Assert.True(reply.ValuesShown);
        var settings = Assert.Single(reply.Devices).Settings;
        Assert.DoesNotContain(settings, s => s.Name == "Internal");
        Assert.Equal("40", settings.Single(s => s.Name == "Threshold").Value);
        Assert.Null(settings.Single(s => s.Name == "Key").Value);
        Assert.Equal(["GET " + AppSettings.BuildDefinitionsUri("demo")], _ctx.Cameras[device.Id].Requests);
    }

    [Fact]
    public async Task Values_go_only_to_roles_that_may_change_settings()
    {
        var device = Add("10.0.0.48");
        var call = new DeniedCall(_ctx.CallAsAllDevices(), PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Settings));
        var reply = await CallAsync<SettingsReply>(call, AppManagementMethods.GetSettings, new SettingsRequest { Application = "demo", DeviceIds = [device.Id] });

        Assert.False(reply.ValuesShown);
        Assert.All(reply.Devices.Single().Settings, s => Assert.Null(s.Value));
    }

    [Fact]
    public async Task Change_settings_starts_one_acap_task_with_the_values_and_never_writes()
    {
        var a = Add("10.0.0.48");
        var b = Add("10.0.0.49");
        var request = new RunRequest
        {
            Action = AcapAction.Settings,
            Values = new Dictionary<string, string> { ["Threshold"] = "55" },
            Items = [new RunItem { Application = "demo", DisplayName = "Demo", DeviceIds = [a.Id, b.Id] }],
        };

        await CallAsync<RunReply>(_ctx.CallAsAllDevices(), AppManagementMethods.Run, request);

        var run = Assert.Single(_ctx.TaskRuns);
        var payload = AcapPayload.Parse(run.PayloadJson);
        Assert.Equal(AcapAction.Settings, payload.Action);
        Assert.Equal("demo", payload.Application);
        Assert.Equal("55", payload.Values!["Threshold"]);
        Assert.Equal([a.Id, b.Id], run.DeviceIds);
        Assert.All(_ctx.Cameras.Values, c => Assert.Empty(c.Requests));

        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => CallAsync<RunReply>(
            new DeniedCall(_ctx.CallAsAllDevices(), PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Settings)), AppManagementMethods.Run, request));
        Assert.Equal("Your role does not allow: Change application settings.", refused.Message);
        Assert.Single(_ctx.TaskRuns);
    }

    [Fact]
    public async Task The_log_window_shows_the_lines_and_searches_them()
    {
        var ctx = new FakePageContext();
        ctx.Answers[AppManagementMethods.GetLog] = _ => AppManagementJson.Serialize(new LogReply { Lines = AppLogReader.Parse(Log).Lines });
        using var log = new AppLogViewModel(ctx, Guid.NewGuid(), "demo", "Demo", "10.0.0.48");

        await log.RefreshAsync();
        Assert.Equal(3, log.Text.Split('\n').Length);
        Assert.False(log.ShowEmpty);
        Assert.Equal(3, log.Rows.Count);
        Assert.True(log.Rows[0].IsInfo);
        Assert.True(log.Rows[1].IsWarning);
        Assert.True(log.Rows[2].IsError);
        Assert.Equal("Could not reach 10.0.0.5", log.Rows[2].Message);
        Assert.Equal("Log of Demo on 10.0.0.48", log.Title);

        log.SearchText = "reach";
        Assert.Single(log.Text.Split('\n'));
        log.SearchText = "nothing like this";
        Assert.Equal("No line matches the search.", log.EmptyText);

        ctx.Answers[AppManagementMethods.GetLog] = _ => AppManagementJson.Serialize(new LogReply());
        log.SearchText = string.Empty;
        await log.RefreshAsync();
        Assert.Equal(AppLogReader.NoLines, log.EmptyText);
        Assert.True(log.ShowEmpty);
    }

    private static SettingsReply Reply(bool valuesShown, params string[] definitions)
    {
        var devices = definitions.Select((xml, i) => new DeviceSettings
        {
            DeviceId = Guid.NewGuid(),
            Address = "10.0.0." + i,
            Settings = [.. AppSettings.Parse(xml, "demo").Where(s => !s.Hidden).Select(s => valuesShown ? s : s with { Value = null })],
        }).ToList();
        return new SettingsReply { Devices = devices, ValuesShown = valuesShown };
    }

    [Fact]
    public void Several_devices_show_the_common_value_and_where_it_differs()
    {
        var vm = new AppSettingsViewModel(new FakePageContext(), "demo", "Demo", [], "4 devices");
        vm.Apply(Reply(true, Typed("40"), Typed("40"), Typed("40", "exact"), Typed("70")));

        Assert.Equal(["Threshold", "Mode", "Enabled", "Server", "Key"], vm.Rows.Select(r => r.Name));
        var threshold = vm.Rows.Single(r => r.Name == "Threshold");
        Assert.Equal("40", threshold.Value);
        Assert.Equal("Differs on 1 device. 0 to 100", threshold.Hint);
        Assert.Equal("fast", vm.Rows.Single(r => r.Name == "Mode").Value);
        var enabled = vm.Rows.Single(r => r.Name == "Enabled");
        Assert.True(enabled.IsYesNo);
        Assert.True(enabled.IsChecked);
        enabled.IsChecked = false;
        Assert.Equal("no", enabled.Value);
        Assert.True(enabled.IsChanged);
        enabled.IsChecked = true;
        var key = vm.Rows.Single(r => r.Name == "Key");
        Assert.True(key.IsPassword);
        Assert.Equal(string.Empty, key.Value);
        Assert.Equal(AppSettingRowViewModel.Unchanged, key.Placeholder);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal("Change a setting first.", vm.SaveBlockedReason);
    }

    [Fact]
    public async Task Save_checks_each_field_with_the_shared_rules_and_sends_only_changed_values()
    {
        var ctx = new FakePageContext().WithEmptyServer();
        var vm = new AppSettingsViewModel(ctx, "demo", "Demo", [], "10.0.0.0");
        vm.Apply(Reply(true, Typed()));
        var threshold = vm.Rows.Single(r => r.Name == "Threshold");

        threshold.Value = "abc";
        Assert.Equal("Must be a whole number.", threshold.ErrorOf(nameof(AppSettingRowViewModel.Value)));
        Assert.False(vm.SaveCommand.CanExecute(null));
        threshold.Value = "101";
        Assert.Equal("Must be 0 to 100.", threshold.ErrorOf(nameof(AppSettingRowViewModel.Value)));

        threshold.Value = "055";
        vm.Rows.Single(r => r.Name == "Key").Value = "secret";
        Assert.Null(threshold.ErrorOf(nameof(AppSettingRowViewModel.Value)));
        Assert.True(vm.SaveCommand.CanExecute(null));
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        await vm.SaveCommand.ExecuteAsync(null);

        var request = AppManagementJson.Deserialize<RunRequest>(ctx.LastPayloadOf(AppManagementMethods.Run));
        Assert.Equal(AcapAction.Settings, request.Action);
        Assert.Equal(new Dictionary<string, string> { ["Threshold"] = "55", ["Key"] = "secret" }, request.Values);
        Assert.Equal("demo", Assert.Single(request.Items).Application);
        Assert.True(closed);
    }

    [Fact]
    public void Without_the_settings_right_the_window_shows_no_values_and_no_save()
    {
        var ctx = new FakePageContext();
        ctx.Denied.Add(PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Settings));
        var vm = new AppSettingsViewModel(ctx, "demo", "Demo", [], "10.0.0.0");
        vm.Apply(Reply(false, Typed()));

        Assert.False(vm.MayChange);
        Assert.All(vm.Rows, r => Assert.True(r.IsReadOnly));
        Assert.All(vm.Rows, r => Assert.Equal(string.Empty, r.Value));
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void An_application_without_settings_points_to_its_own_page()
    {
        var vm = new AppSettingsViewModel(new FakePageContext(), "demo", "Demo", [], "10.0.0.0");
        vm.Apply(Reply(true, Definitions(string.Empty)));

        Assert.False(vm.HasRows);
        Assert.True(vm.ShowNoSettings);
        Assert.Equal(AppSettings.NoSettings, AppSettingsViewModel.NoSettingsText);
    }

    [Fact]
    public void One_device_offers_the_apps_own_page_in_Web_View()
    {
        var ctx = new FakePageContext();
        ctx.PluginsOn.Add(AppSettingsViewModel.WebViewId);
        var device = Guid.NewGuid();
        var vm = new AppSettingsViewModel(ctx, "ax_msf", "Missing Features", [device], "10.0.0.48", "/local/ax_msf/redirect.html");
        vm.Apply(Reply(true, Definitions(string.Empty)) with { Devices = [new DeviceSettings { DeviceId = device, Address = "10.0.0.48" }] });
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        Assert.True(vm.ShowNoSettings);
        Assert.True(vm.CanOpenAppPage);
        Assert.False(vm.ShowOneDeviceHint);
        vm.OpenAppPageCommand.Execute(null);
        Assert.Equal((device, "/local/ax_msf/redirect.html"), Assert.Single(ctx.OpenedPages));
        Assert.True(closed);

        // The host could not open it (the role, the device): the window says so and stays.
        ctx.OpensDevicePages = false;
        closed = false;
        vm.OpenAppPageCommand.Execute(null);
        Assert.Equal(AppSettingsViewModel.AppPageNotOpened, vm.ErrorText);
        Assert.False(closed);
    }

    [Fact]
    public void Several_devices_Web_View_off_or_no_page_offer_no_link()
    {
        var ctx = new FakePageContext();
        ctx.PluginsOn.Add(AppSettingsViewModel.WebViewId);
        var several = new AppSettingsViewModel(ctx, "ax_msf", "Missing Features", [Guid.NewGuid(), Guid.NewGuid()], "2 devices", "/local/ax_msf/redirect.html");
        Assert.False(several.CanOpenAppPage);
        Assert.True(several.ShowOneDeviceHint);
        Assert.Equal("Open it on one device.", AppSettingsViewModel.OneDeviceHint);

        var noPage = new AppSettingsViewModel(ctx, "lpv_simulator", "LPV Simulator", [Guid.NewGuid()], "10.0.0.48");
        Assert.False(noPage.CanOpenAppPage);
        Assert.False(noPage.ShowOneDeviceHint);

        var off = new AppSettingsViewModel(new FakePageContext(), "ax_msf", "Missing Features", [Guid.NewGuid()], "10.0.0.48", "/local/ax_msf/redirect.html");
        Assert.False(off.CanOpenAppPage);
        off.OpenAppPageCommand.Execute(null);
        Assert.Empty(ctx.OpenedPages);
    }

    [Fact]
    public void The_settings_window_gets_the_page_the_device_lists()
    {
        using var page = new ApplicationsViewModel(new FakePageContext(), new FakeTimeProvider());
        var app = Samples.App("ax_msf") with { ConfigurationPage = "local/ax_msf/redirect.html" };
        page.ApplyRows([Samples.Device("10.0.0.1", app), Samples.Device("10.0.0.2", Samples.App("ax_msf"))]);

        Assert.Equal("/local/ax_msf/redirect.html", page.SettingsFor(page.DeviceRows.Single(r => r.Address == "10.0.0.1"))!.ConfigurationPath);
        Assert.Null(page.SettingsFor(page.DeviceRows.Single(r => r.Address == "10.0.0.2"))!.ConfigurationPath);
    }

    [Fact]
    public void The_log_shows_the_level_as_an_icon_only_for_error_warning_and_info()
    {
        AppLogRow Row(string? level) => new(new AppLogLine("x") { Level = level });

        Assert.True(Row("ERR").HasIcon);
        Assert.True(Row("WARNING").HasIcon);
        Assert.True(Row("INFO").HasIcon);
        Assert.False(Row("DEBUG").HasIcon);
        Assert.False(Row(null).HasIcon);
        Assert.Equal("ERR", Row(" err ").Level); // the tooltip
    }

    [Fact]
    public void The_page_opens_settings_for_the_row_or_every_selected_device_with_the_application()
    {
        using var page = new ApplicationsViewModel(new FakePageContext(), new FakeTimeProvider());
        page.ApplyRows(
        [
            Samples.Device("10.0.0.1", Samples.App("demo"), Samples.App("other")),
            Samples.Device("10.0.0.2", Samples.App("demo")),
            Samples.Device("10.0.0.3", Samples.App("demo")),
        ]);

        // Not selected: the row's device only.
        Assert.Equal("10.0.0.1", page.SettingsFor(page.DeviceRows[0])!.Scope);

        // One of the selected rows: every selected device with the row's application (other applications left out).
        page.SetDeviceSelection(page.DeviceRows.Where(r => r.Address != "10.0.0.3"));
        var demo = page.DeviceRows.First(r => r.AppKey == "demo");
        Assert.Equal("2 devices", page.SettingsFor(demo)!.Scope);
        Assert.Equal("10.0.0.3", page.SettingsFor(page.DeviceRows.Single(r => r.Address == "10.0.0.3"))!.Scope);
    }
}
