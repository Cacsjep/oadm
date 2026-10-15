using System.Diagnostics;
using System.Text;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Plugins.DeviceConfig.Client.Configure;
using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>The Configure devices and Create configuration file dialogs, against the real server queries and a fake camera.</summary>
[Collection(HeadlessSessions.Name)]
public sealed class ConfigureDialogTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class FakeUi : IConfigureUi
    {
        public IDeviceInfo? Device { get; set; }
        public PickedFile? File { get; set; }
        public bool ConfirmAnswer { get; set; } = true;
        public List<string> Confirmations { get; } = [];
        public (string Name, byte[] Content)? Saved { get; private set; }

        public Task<IDeviceInfo?> PickDeviceAsync(IReadOnlyList<IDeviceInfo> devices, IDeviceInfo? current) => Task.FromResult(Device);

        public Task<PickedFile?> PickFileAsync() => Task.FromResult(File);

        public Task<bool> ConfirmAsync(string title, string message, string confirm)
        {
            Confirmations.Add(message);
            return Task.FromResult(ConfirmAnswer);
        }

        public Task<bool> SaveFileAsync(string suggestedName, byte[] content)
        {
            Saved = (suggestedName, content);
            return Task.FromResult(true);
        }
    }

    /// <summary>The query of the dialog, answered by the server code against one fake camera per device; counts the calls.</summary>
    private sealed class Server
    {
        // Cameras are made only for the devices the dialog reads (30,000 fake cameras would be needlessly heavy).
        private readonly Dictionary<Guid, IDeviceInfo> _devices = [];
        private readonly Dictionary<Guid, ConfigureCamera> _cameras = [];

        public List<(Guid Device, string Method)> Calls { get; } = [];

        /// <summary>Called before each query; an exception it returns is thrown (a gRPC error, a timeout).</summary>
        public Func<Guid, string, Exception?>? Fail { get; set; }

        /// <summary>Every query waits for this first (the dialog stays loading).</summary>
        public Task Gate { get; set; } = Task.CompletedTask;

        public FakeDevice Add(string address, string model = "AXIS P3265-V", string firmware = "12.11.77", DeviceStatus status = DeviceStatus.Ok)
        {
            var device = new FakeDevice
            {
                Address = address,
                Model = model,
                FirmwareVersion = firmware,
                Status = status,
                Serial = _devices.Count == 0 ? "B8A44F631339" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"B8A44F{_devices.Count:X6}"),
            };
            _devices[device.Id] = device;
            return device;
        }

        public ConfigureCamera Camera(Guid id) => _cameras.TryGetValue(id, out var camera) ? camera : _cameras[id] = new ConfigureCamera();

        public async Task<string?> QueryAsync(Guid id, string method, CancellationToken ct)
        {
            ConfigureCamera camera;
            lock (Calls)
            {
                Calls.Add((id, method));
                camera = Camera(id);
            }

            await Gate;
            if (Fail?.Invoke(id, method) is { } failure)
            {
                throw failure;
            }

            return await new ConfigureDevicesTask().QueryAsync(new QueryContext(camera), _devices[id], method, null, ct);
        }
    }

    private static ConfigureDevicesViewModel Dialog(Server server, IReadOnlyList<IDeviceInfo> devices, FakeUi ui, bool mayDangerous = true) =>
        new(devices, devices, (id, method, ct) => server.QueryAsync(id, method, ct), ui, mayDangerous);

    [Fact]
    public void Definitions_of_several_models_merge_to_what_every_one_has()
    {
        ParamDefinition Enum(string name, params string[] values) => new(name, ParamKind.Enum) { Choices = [.. values.Select(v => new ParamChoice(v))] };
        var merged = DefinitionMerge.Merge(
        [
            [Enum("Image.I0.Appearance.Resolution", "1920x1080", "1280x720", "640x360"), new("Image.I0.Appearance.Compression", ParamKind.Number) { Min = 0, Max = 100 }, new("Audio.A0.Enabled", ParamKind.Bool)],
            [Enum("Image.I0.Appearance.Resolution", "1280x720", "640x360", "320x180"), new("Image.I0.Appearance.Compression", ParamKind.Number) { Min = 10, Max = 90 }],
        ]);

        var resolution = merged.Single(m => m.Definition.Name.EndsWith("Resolution", StringComparison.Ordinal));
        Assert.Equal(["1280x720", "640x360"], resolution.Definition.Choices.Select(c => c.Value));
        Assert.True(resolution.NarrowedChoices);
        var compression = merged.Single(m => m.Definition.Name.EndsWith("Compression", StringComparison.Ordinal));
        Assert.Equal((10L, 90L, true), (compression.Definition.Min!.Value, compression.Definition.Max!.Value, compression.InAll));
        Assert.False(merged.Single(m => m.Definition.Name == "Audio.A0.Enabled").InAll);
    }

    [Fact]
    public async Task The_source_device_is_read_once_and_each_other_model_once()
    {
        var server = new Server();
        var source = server.Add("10.0.0.48");
        var sameModel = server.Add("10.0.0.49");
        var other = server.Add("10.0.0.22", "AXIS Q6135-LE", "12.6.94");
        var otherToo = server.Add("10.0.0.23", "AXIS Q6135-LE", "12.6.94");
        var dialog = Dialog(server, [source, sameModel, other, otherToo], new FakeUi());

        await dialog.InitializeAsync();

        Assert.Equal([(source.Id, ConfigureIds.ReadQuery), (other.Id, ConfigureIds.DefinitionsQuery)], server.Calls);
        Assert.NotNull(dialog.Editor);
        Assert.Equal(ConfigureFixtures.ConfigurableParameters - 6, dialog.Editor!.Parameters.Count); // several devices: no addresses and names
        Assert.Equal("Different models or AXIS OS: some settings do not apply to all devices.", dialog.Notes[0]);
        Assert.Equal("4 devices, 2 models and AXIS OS versions", dialog.DevicesText);
        Assert.Equal("Check what to apply", dialog.NextBlockedReason);
        Assert.Empty(dialog.FileLines); // a device as the source: no details, like ADM
        Assert.Equal("re", Assert.Single(dialog.Editor.Rules).Name);
        Assert.Equal("Recurring pulse", dialog.Editor.Rules[0].When);
    }

    [Fact]
    public async Task Checked_values_are_checked_below_the_input_and_the_overview_shows_what_is_applied()
    {
        var server = new Server();
        var device = server.Add("10.0.0.48");
        var target = server.Add("10.0.0.49");
        var ui = new FakeUi();
        var dialog = Dialog(server, [device, target], ui);
        await dialog.InitializeAsync();
        var editor = dialog.Editor!;

        var compression = editor.Parameters.Single(p => p.Name == "Image.I0.Appearance.Compression");
        Assert.Equal(("30", "0 to 100", false), (compression.Value, compression.Hint, compression.IsChecked));
        compression.Value = "130"; // typing checks the parameter
        Assert.True(compression.IsChecked);
        Assert.Equal("Enter 0 to 100.", compression.ErrorOf(nameof(compression.Value)));
        Assert.Equal("Image.I0.Appearance Compression: Enter 0 to 100.", dialog.NextBlockedReason);
        compression.Value = "45";
        Assert.Null(dialog.NextBlockedReason);

        var color = editor.Parameters.Single(p => p.Name == "Image.I0.Appearance.ColorEnabled");
        color.SelectedChoice = color.Choices.Single(c => c.Display == "Black and White");
        Assert.Equal(("no", true), (color.Value, color.IsChecked));
        editor.Profiles[0].IsChecked = true;
        Assert.Equal(("Parameters (2)", "Stream profiles (1)"), (editor.ParametersTabText, editor.ProfilesTabText));

        await dialog.NextCommand.ExecuteAsync(null);
        Assert.True(dialog.IsOverviewStep);
        Assert.Equal(
            ["Parameters (2)", "Image.I0.Appearance: Color enabled = no", "Image.I0.Appearance: Compression = 45", "Stream profiles (1)", "View Area 1_ACS_Pro_High: camera=1&videocodec=h264&fps=30&resolution=1920x1080&compression=30&videozstrength=20&videozgopmode=fixed&audio=0"],
            dialog.Overview.Select(o => o.Text));
        await dialog.NextCommand.ExecuteAsync(null);

        Assert.Empty(ui.Confirmations); // nothing dangerous
        var payload = ConfigurePayload.Parse(dialog.Result);
        Assert.Equal([new NameValue("Image.I0.Appearance.ColorEnabled", "no"), new NameValue("Image.I0.Appearance.Compression", "45")], payload.Parameters);
        Assert.Equal("View Area 1_ACS_Pro_High", Assert.Single(payload.StreamProfiles).Name);
        Assert.Equal("10.0.0.48 (AXIS P3265-V)", payload.Source);
        Assert.Equal(device.Id, payload.SourceDeviceId); // its own profiles stay as they are (the task skips them there)
        Assert.Contains("10.0.0.48 is the source: its own action rules and stream profiles stay as they are.", dialog.Notes);
    }

    [Fact]
    public async Task Dangerous_parameters_are_marked_asked_for_and_hidden_without_the_right()
    {
        var server = new Server();
        var device = server.Add("10.0.0.48");
        var ui = new FakeUi { ConfirmAnswer = false };
        var dialog = Dialog(server, [device], ui);
        await dialog.InitializeAsync();

        var port = dialog.Editor!.Parameters.Single(p => p.Name == "HTTPS.Port");
        Assert.Equal((true, DangerousParameters.ConnectionReason), (port.IsDangerous, port.DangerReason));
        port.Value = "8443";
        await dialog.NextCommand.ExecuteAsync(null);
        Assert.True(dialog.HasDangerousChosen);
        await dialog.NextCommand.ExecuteAsync(null);
        Assert.Contains("• HTTPS.Port", Assert.Single(ui.Confirmations), StringComparison.Ordinal);
        Assert.Null(dialog.Result); // not confirmed: nothing applied

        var limited = Dialog(server, [device], new FakeUi(), mayDangerous: false);
        await limited.InitializeAsync();
        Assert.DoesNotContain(limited.Editor!.Parameters, p => p.IsDangerous);
        Assert.True(limited.Editor.HasHiddenDangerous);
    }

    [Fact]
    public async Task An_ADM_file_checks_its_settings_and_names_what_no_device_has()
    {
        var server = new Server();
        var device = server.Add("10.0.0.48");
        var file = AdmConfigFile.Read(ConfigureFixtures.Read("adm-p3265v-12.11.77.cfg"));
        file = file with { Parameters = [.. file.Parameters, new NameValue("Network.AxisNS.Enabled", "no"), new NameValue("Image.I0.Appearance.Compression", "55")] };
        var dialog = Dialog(server, [device], new FakeUi());
        await dialog.InitializeAsync();

        await dialog.LoadFileAsync(file, "lobby.cfg");

        Assert.True(dialog.IsFileSource);
        Assert.Equal(["lobby.cfg", "Model: AXIS P3265-V", "AXIS OS: 12.11.77"], dialog.FileLines);
        var editor = dialog.Editor!;
        Assert.Equal("55", editor.Parameters.Single(p => p.Name == "Image.I0.Appearance.Compression").Value);
        Assert.Equal(5, editor.CheckedCount); // the file's 4 parameters and Compression
        Assert.True(Assert.Single(editor.Rules).IsChecked);
        Assert.Equal(3, editor.CheckedProfileCount);
        Assert.Contains("1 parameter of the file no selected device has: Network.AxisNS.Enabled", dialog.Notes);
        Assert.Equal(["com.axis.schedules.genid.id-0"], editor.CheckedSchedules.Select(s => s.Id));

        // Not an ADM file: said where the source is chosen, the dialog keeps the previous source.
        var bad = new FakeUi { File = new PickedFile("export.json", """{"data":{}}""") };
        var other = Dialog(server, [device], bad);
        await other.InitializeAsync();
        await other.UseFileCommand.ExecuteAsync(null);
        Assert.Equal(AdmConfigFile.NotAnAdmFile, other.Error);
        Assert.True(other.IsDeviceSource);
    }

    [Fact]
    public async Task Create_configuration_file_saves_an_ADM_file_with_every_schedule_for_a_rule()
    {
        var server = new Server();
        var device = server.Add("10.0.0.48");
        var ui = new FakeUi();
        var dialog = new CreateConfigFileViewModel(device, (method, ct) => server.QueryAsync(device.Id, method, ct), ui);
        await dialog.InitializeAsync();

        dialog.Editor!.Parameters.Single(p => p.Name == "Image.I0.Appearance.Compression").IsChecked = true;
        dialog.Editor.Rules[0].IsChecked = true;
        await dialog.NextCommand.ExecuteAsync(null);
        Assert.Equal("Save", dialog.NextText);
        await dialog.NextCommand.ExecuteAsync(null);

        Assert.True(dialog.Saved);
        Assert.Equal("Configuration - AXIS P3265-V 12.11.77.cfg", ui.Saved!.Value.Name);
        var saved = AdmConfigFile.Read(Encoding.UTF8.GetString(ui.Saved.Value.Content));
        Assert.Equal(("AXIS P3265-V", "12.11.77", "B8A44F631339"), (saved.Model, saved.FirmwareVersion, saved.MacAddress));
        Assert.Equal([new NameValue("Image.I0.Appearance.Compression", "30")], saved.Parameters);
        Assert.Equal(5, saved.Schedules.Count);
        Assert.Equal("re", Assert.Single(saved.Rules).Name);
        Assert.Empty(saved.StreamProfiles);
    }

    [Fact]
    public async Task With_only_the_source_selected_its_rules_and_profiles_are_not_offered()
    {
        var server = new Server();
        var device = server.Add("10.0.0.48");
        var dialog = Dialog(server, [device], new FakeUi());
        await dialog.InitializeAsync();

        Assert.Empty(dialog.Editor!.Rules);
        Assert.Empty(dialog.Editor.Profiles);
        Assert.Equal("These are the own settings of 10.0.0.48; they are already on it.", dialog.Editor.RulesEmptyText);

        // From a file, the same device gets the file's rules and profiles.
        await dialog.LoadFileAsync(AdmConfigFile.Read(ConfigureFixtures.Read("adm-p3265v-12.11.77.cfg")), "lobby.cfg");
        Assert.Single(dialog.Editor!.Rules);
        Assert.Equal(3, dialog.Editor.Profiles.Count);
    }

    [Fact]
    public async Task Addresses_and_names_are_offered_for_one_device_only()
    {
        var server = new Server();
        var first = server.Add("10.0.0.48");
        var second = server.Add("10.0.0.49");

        var one = Dialog(server, [first], new FakeUi());
        await one.InitializeAsync();
        Assert.Contains(one.Editor!.Parameters, p => p.Name == "Network.HostName");
        Assert.DoesNotContain(one.Editor.Parameters, p => p.Name == "Network.IPAddress"); // addresses only with Network settings

        var two = Dialog(server, [first, second], new FakeUi());
        await two.InitializeAsync();
        Assert.DoesNotContain(two.Editor!.Parameters, p => p.Name is "Network.IPAddress" or "Network.HostName" or "Network.UPnP.FriendlyName");
        Assert.Contains(two.Editor.Parameters, p => p.Name == "Network.DefaultRouter"); // shared values stay, with the warning

        var file = AdmConfigFile.Read(ConfigureFixtures.Read("adm-p3265v-12.11.77.cfg")) with { Parameters = [new NameValue("Network.HostName", "lobby")] };
        await two.LoadFileAsync(file, "lobby.cfg");
        Assert.Contains("The file's addresses and names are left out. " + DangerousParameters.PerDeviceNote, two.Notes);
        Assert.Equal(0, two.Editor!.CheckedCount);
    }

    [Fact]
    public async Task The_overview_step_dot_builds_the_overview_like_Next()
    {
        var server = new Server();
        var device = server.Add("10.0.0.48");
        var dialog = Dialog(server, [device], new FakeUi());
        await dialog.InitializeAsync();
        Assert.False(dialog.Steps[1].CanOpen); // nothing checked

        dialog.Editor!.Parameters.Single(p => p.Name == "Image.I0.Appearance.Compression").Value = "45";
        Assert.True(dialog.Steps[1].CanOpen);
        dialog.Steps[1].OpenCommand.Execute(null);

        Assert.True(dialog.IsOverviewStep);
        Assert.Equal(["Parameters (1)", "Image.I0.Appearance: Compression = 45"], dialog.Overview.Select(o => o.Text));
    }

    [Fact]
    public async Task Next_follows_every_value_change_of_a_checked_parameter()
    {
        var server = new Server();
        var device = server.Add("10.0.0.48");
        var dialog = Dialog(server, [device], new FakeUi());
        await dialog.InitializeAsync();
        var compression = dialog.Editor!.Parameters.Single(p => p.Name == "Image.I0.Appearance.Compression");
        compression.Value = "1";
        var raised = new List<string?>();
        dialog.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        compression.Value = "1000";
        Assert.Contains(nameof(dialog.CanNext), raised);
        Assert.False(dialog.CanNext);
        Assert.Equal("Image.I0.Appearance Compression: Enter 0 to 100.", dialog.NextBlockedReason);
        compression.Value = "100";
        Assert.True(dialog.CanNext);
    }

    [Fact]
    public async Task Server_errors_show_in_the_dialog_and_a_failed_model_is_read_again()
    {
        var server = new Server();
        var source = server.Add("10.0.0.48");
        var other = server.Add("10.0.0.22", "AXIS Q6135-LE", "12.6.94");
        var failOther = true;
        server.Fail = (id, method) => id == other.Id && failOther ? new InvalidOperationException("Timeout after 30 s") : null;
        var dialog = Dialog(server, [source, other], new FakeUi());

        await dialog.InitializeAsync(); // never throws

        Assert.NotNull(dialog.Editor);
        Assert.Contains(dialog.Notes, n => n.StartsWith("10.0.0.22 (AXIS Q6135-LE, AXIS OS 12.6.94) could not be read: Timeout after 30 s.", StringComparison.Ordinal));
        failOther = false;
        await dialog.LoadDeviceAsync(source);
        Assert.Equal(2, server.Calls.Count(c => c.Device == other.Id)); // read again, not remembered as failed
        Assert.DoesNotContain(dialog.Notes, n => n.Contains("could not be read", StringComparison.Ordinal));

        var broken = new Server();
        var lone = broken.Add("10.0.0.48");
        broken.Fail = (_, _) => new InvalidOperationException("The server is not reachable.");
        var stuck = Dialog(broken, [lone], new FakeUi());
        await stuck.InitializeAsync();
        Assert.Equal("10.0.0.48 could not be read: The server is not reachable.", stuck.Error);

        var create = new CreateConfigFileViewModel(lone, (method, ct) => broken.QueryAsync(lone.Id, method, ct), new FakeUi());
        await create.InitializeAsync();
        Assert.Equal("10.0.0.48 could not be read: The server is not reachable.", create.Error);
        Assert.False(create.IsLoading);
    }

    [Fact]
    public async Task Drop_downs_keep_their_value_and_show_their_error_and_passwords_need_one()
    {
        var server = new Server();
        var device = server.Add("10.0.0.48");
        var dialog = Dialog(server, [device], new FakeUi());
        await dialog.InitializeAsync();
        var editor = dialog.Editor!;

        var color = editor.Parameters.Single(p => p.Name == "Image.I0.Appearance.ColorEnabled");
        var chosen = color.SelectedChoice;
        color.SelectedChoice = null; // a recycled row's drop-down clearing its selection
        Assert.Equal((chosen, "yes", false), (color.SelectedChoice, color.Value, color.IsChecked));

        // A file value that is not a choice of every device: the error stands below the drop-down once checked.
        var file = AdmConfigFile.Read(ConfigureFixtures.Read("adm-p3265v-12.11.77.cfg")) with { Parameters = [new NameValue("Image.I0.Appearance.ColorEnabled", "sepia")] };
        await dialog.LoadFileAsync(file, "odd.cfg");
        var odd = dialog.Editor!.Parameters.Single(p => p.Name == "Image.I0.Appearance.ColorEnabled");
        Assert.True(odd.IsChecked);
        Assert.Equal("Choose a value from the list.", odd.ErrorOf(nameof(odd.SelectedChoice)));
        Assert.Null(odd.ErrorOf(nameof(odd.Value)));

        var password = dialog.Editor.Parameters.Single(p => p.Name == "RemoteService.ProxyPassword");
        password.IsChecked = true;
        Assert.Equal("Enter the password.", password.ErrorOf(nameof(password.Value)));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Thirty_thousand_devices_of_twenty_models_need_twenty_reads_and_the_list_stays_fast()
    {
        var server = new Server();
        var devices = Enumerable.Range(0, Oadm.Sdk.Devices.DeviceLimits.MaxDevices).Select(i => (IDeviceInfo)server.Add(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"10.1.{i / 256}.{i % 256}"),
            "AXIS P3265-V",
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"12.{i % 20}.1"))).ToList();
        var dialog = Dialog(server, devices, new FakeUi());

        var watch = Stopwatch.StartNew();
        await dialog.InitializeAsync();
        var load = watch.Elapsed;
        Assert.Equal(20, server.Calls.Count);

        watch.Restart();
        dialog.Editor!.SearchText = "compression";
        dialog.Editor.SearchText = string.Empty;
        dialog.Editor.ShowAll = true;
        var filter = watch.Elapsed;
        output.WriteLine($"30,000 devices: load {load.TotalMilliseconds:F0} ms, filter {filter.TotalMilliseconds:F0} ms");
        Assert.True(filter < TimeSpan.FromMilliseconds(500), $"filter {filter.TotalMilliseconds} ms");
        Assert.True(load < TimeSpan.FromSeconds(30), $"load {load.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task The_windows_render_settings_and_overview()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var server = new Server();
        var device = server.Add("10.0.0.48");
        var other = server.Add("10.0.0.22", "AXIS Q6135-LE", "12.6.94");
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var done = await session.Dispatch(async () =>
        {
            var dialog = Dialog(server, [device, other], new FakeUi());
            var window = new ConfigureDevicesWindow();
            window.Attach(dialog);
            window.Show();
            var gate = new TaskCompletionSource();
            server.Gate = gate.Task;
            var loading = dialog.InitializeAsync();
            Pump();
            Assert.True(dialog.IsLoading);
            Assert.Contains(window.GetVisualDescendants().OfType<LoadingOverlay>(), o => o.IsEffectivelyVisible && o.Text == "Reading 10.0.0.48");
            Save(window, outDir, "device-config-configure-loading.png");
            gate.SetResult();
            await loading;
            Pump();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<LoadingOverlay>(), o => o.IsEffectivelyVisible);
            var editor = dialog.Editor!;
            editor.SearchText = "Appearance";
            editor.Parameters.Single(p => p.Name == "Image.I0.Appearance.Compression").Value = "130";
            editor.Parameters.Single(p => p.Name == "Image.I0.Appearance.MirrorEnabled").IsChecked = true;
            Pump();
            Save(window, outDir, "device-config-configure.png");
            Assert.Equal("Image.I0.Appearance Compression: Enter 0 to 100.", dialog.NextBlockedReason);

            editor.Parameters.Single(p => p.Name == "Image.I0.Appearance.Compression").Value = "45";
            editor.Parameters.Single(p => p.Name == "HTTPS.Port").Value = "8443";
            editor.Tab = SettingsTab.ActionRules;
            editor.Rules[0].IsChecked = true;
            Pump();
            Save(window, outDir, "device-config-configure-rules.png");
            await dialog.NextCommand.ExecuteAsync(null);
            Pump();
            Save(window, outDir, "device-config-configure-overview.png");
            dialog.BackCommand.Execute(null);
            await dialog.LoadFileAsync(AdmConfigFile.Read(ConfigureFixtures.Read("adm-p3265v-12.11.77.cfg")), "lobby.cfg");
            Pump();
            Save(window, outDir, "device-config-configure-file.png");
            window.Close();

            var create = new CreateConfigFileViewModel(device, (method, ct) => server.QueryAsync(device.Id, method, ct), new FakeUi());
            var createWindow = new CreateConfigFileWindow();
            createWindow.Attach(create);
            createWindow.Show();
            await create.InitializeAsync();
            create.Editor!.Tab = SettingsTab.StreamProfiles;
            create.Editor.Profiles[1].IsChecked = true;
            Pump();
            Save(createWindow, outDir, "device-config-create-file.png");
            createWindow.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (frame)
        {
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
    }
}
