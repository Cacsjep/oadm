extern alias fwclient;

using System.Diagnostics;
using System.Globalization;

using Avalonia.Headless;
using Avalonia.Threading;

using Oadm.Plugins.Firmware.Tests;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

using ClientStatus = fwclient::Oadm.Plugins.Firmware.FirmwareStatusInfo;
using ClientVerdict = fwclient::Oadm.Plugins.Firmware.FirmwareVerdict;
using DeviceRow = fwclient::Oadm.Plugins.Firmware.Client.FirmwareDeviceRow;
using DialogViewModel = fwclient::Oadm.Plugins.Firmware.Client.FirmwareDialogViewModel;
using DialogWindow = fwclient::Oadm.Plugins.Firmware.Client.FirmwareDialogWindow;
using ServerCompatibility = Oadm.Plugins.Firmware.FirmwareCompatibility;
using ServerOnlineFirmware = Oadm.Plugins.Firmware.OnlineFirmware;
using ServerPayload = Oadm.Plugins.Firmware.FirmwarePayload;

// Own namespace: inside Oadm.Plugins.Firmware.* the server's copies of the shared types would win.
namespace Oadm.FirmwareDialogTests;

/// <summary>The MyAxis sign-in as the client knows it, test-local.</summary>
internal sealed class FakeMyAxis(bool signedIn) : IMyAxisSignIn
{
    public bool IsSignedIn { get; private set; } = signedIn;

    public event EventHandler? Changed;

    public int SignIns { get; private set; }

    public Task<bool> SignInAsync(Avalonia.Controls.Window owner) => Task.FromResult(SignIn());

    public bool SignIn()
    {
        SignIns++;
        IsSignedIn = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

/// <summary>The dialog with versions from axis.com, one select per device: the server's own queries answer from the recorded pages.</summary>
public sealed class FirmwareOnlineDialogTests
{
    private static Device[] Selection() =>
    [
        new("10.0.0.48", "P3265-V", "11.11.160"),
        new("10.0.0.49", "P3265-V", "11.11.160"),
        new("10.0.0.50", "P3265-V", "12.11.77"),
        new("10.0.0.51", "P3265-V", "12.11.147"),
        new("10.0.0.52", "C1410", "11.11.238"),
        new("10.0.0.53", "P12-MkIII", "9.80.1"),
    ];

    /// <summary>The dialog context with the plugin's real queries "online" and "onlineVersion" against <paramref name="site"/>.</summary>
    internal static FakeDialogContext Context(FakeAxisSite site, bool? signedIn = true)
    {
        var plugin = new Oadm.Plugins.Firmware.FirmwareTaskPlugin(new Oadm.Plugins.Firmware.FirmwareTaskTimings(), TimeProvider.System, site);
        var query = new QueryContext(new FakeAxisDevice());
        return new FakeDialogContext
        {
            MyAxis = signedIn is { } s ? new FakeMyAxis(s) : null,
            Online = (method, payload, ct) => plugin.QueryAsync(query, new FakeDevice(), method, payload, ct),
        };
    }

    private static string? Chosen(DeviceRow row) => row.OnlineVersion?.Version.Version;

    /// <summary>The server, given the payload, takes for every device of the run what the dialog showed.</summary>
    private static void ServerAgrees(DialogViewModel vm)
    {
        var payload = ServerPayload.Parse(vm.Result);
        var online = payload.AllOnline();
        var onlineImages = online.Select(o => ServerOnlineFirmware.ImageOf(o, 0)).ToList();
        var files = payload.AllFiles();
        var fileImages = files.Select(f => Oadm.Plugins.Firmware.FirmwareImageInspector.Inspect(f.FileName!, 80 * 1024 * 1024)).ToList();
        foreach (var id in vm.RunDevices)
        {
            var row = vm.Devices.Single(d => d.Device.Id == id);
            var (o, f, _) = ServerCompatibility.ChooseFor(id, row.Model, row.CurrentVersion, online, onlineImages, files, fileImages, Oadm.Plugins.Firmware.FactoryDefaultMode.None, true);
            var taken = o >= 0 ? online[o].Version : files[f].FileName;
            Assert.Equal(row.OnlineVersion?.Version.Version ?? row.ChosenFile!.Image.FileName, taken);
        }
    }

    [Fact]
    public async Task Signed_in_every_device_gets_Recommended_and_the_run_carries_little()
    {
        var devices = Selection();
        var ctx = Context(new FakeAxisSite { MakeVersionFolders = true });
        using var vm = new DialogViewModel(ctx, devices, new FakeFileSource());
        Assert.All(vm.Devices, d => Assert.Null(d.SelectedChoice));

        await vm.LoadOnlineAsync(CancellationToken.None);

        Assert.False(vm.OnlineUnreachable);
        Assert.Equal(["11.11.238", "11.11.238", "12.11.147", "12.11.147", "11.11.238", null], vm.Devices.Select(Chosen));

        // One list per model, shared by its devices: the track names as headers, every version newest first, Choose file last.
        var p = vm.Devices[0].Choices;
        Assert.Same(p, vm.Devices[3].Choices);
        Assert.Equal(["LTS 2026 · 12.11", "LTS 2024 · 11.11", "LTS 2022 · 10.12"], p.Where(c => c.IsHeader).Select(c => c.Text));
        Assert.Equal(["12.11.147", "12.11.118"], p.SkipWhile(c => c.IsHeader).Take(2).Select(c => c.Text));
        Assert.Equal("Choose file", p[^1].Text);
        Assert.Equal(["Choose file"], vm.Devices[5].Choices.Select(c => c.Text)); // P12-MkIII is not listed

        Assert.Equal("Upgrade", vm.Devices[0].VerdictText);
        Assert.Equal("11.11.160 → 11.11.238", vm.Devices[0].Message);
        Assert.Equal("Up to date", vm.Devices[3].VerdictText);
        Assert.Equal("No file", vm.Devices[5].VerdictText);
        Assert.Equal("3 of 6 devices will be updated, 2 already up to date, 1 not possible.", vm.Summary);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(ctx.Uploads); // nothing to upload: the server downloads
        var payload = ServerPayload.Parse(vm.Result);
        Assert.Null(payload.FileId);
        Assert.All(payload.AllOnline(), o => Assert.Null(o.Devices)); // by product and major: no device ids
        Assert.Equal(["11.11.238 11", "12.11.147 12"], payload.AllOnline().Select(o => o.Version + " " + o.FromMajor).Order());
        Assert.Equal([devices[0].Id, devices[1].Id, devices[2].Id], vm.RunDevices);
        Assert.Equal("Upgrade firmware", payload.TaskName()); // two versions, every device upgrades
        ServerAgrees(vm);
    }

    [Fact]
    public async Task Choosing_a_version_changes_only_that_device()
    {
        var ctx = Context(new FakeAxisSite { MakeVersionFolders = true });
        using var vm = new DialogViewModel(ctx, Selection(), new FakeFileSource());
        await vm.LoadOnlineAsync(CancellationToken.None);

        var one = vm.Devices[1];
        one.SelectedChoice = one.Choices.Single(c => c.Text == "12.11.147");

        Assert.Equal("12.11.147", Chosen(one));
        Assert.Equal("11.11.238", Chosen(vm.Devices[0])); // the other device of its model and track keeps its version
        Assert.Equal("11.11.160 → 12.11.147. Major upgrade. Make sure the device runs the latest AXIS OS 11.11 first.", one.Message);

        await vm.StartCommand.ExecuteAsync(null);

        // That device is named in the payload; the others go by product and major.
        var payload = ServerPayload.Parse(vm.Result);
        var named = Assert.Single(payload.AllOnline(), o => o.Devices is not null);
        Assert.Equal(("12.11.147", one.Device.Id), (named.Version, Assert.Single(named.Devices!)));
        ServerAgrees(vm);
    }

    /// <summary>A model with an active track (AXIS OS 13) next to its LTS tracks.</summary>
    private static FakeAxisSite ActiveSite()
    {
        var site = new FakeAxisSite(recorded: false) { MakeVersionFolders = true };
        site.Set("/ftp/pub_soft/MPQT/", FakeAxisSite.Folders("/ftp/pub_soft/MPQT/", ["Q1800-LE"]));
        site.Set("/ftp/pub_soft/PACS/", FakeAxisSite.Folders("/ftp/pub_soft/PACS/", []));
        site.Set("/ftp/pub_soft/MPQT/Q1800-LE/", FakeAxisSite.Folders("/ftp/pub_soft/MPQT/Q1800-LE/", ["13_1_20", "13_0_88", "12_11_147", "12_11_100", "11_11_238", "latest"]));
        return site;
    }

    [Fact]
    public async Task The_buttons_set_every_device()
    {
        Device[] devices = [new("10.0.1.1", "Q1800-LE", "11.11.200"), new("10.0.1.2", "Q1800-LE", "12.11.100"), new("10.0.1.3", "Q1800-LE", "13.0.88")];
        using var vm = new DialogViewModel(Context(ActiveSite()), devices, new FakeFileSource());
        Assert.False(vm.RecommendedCommand.CanExecute(null)); // nothing read yet
        await vm.LoadOnlineAsync(CancellationToken.None);

        Assert.Equal(["11.11.238", "12.11.147", "13.1.20"], vm.Devices.Select(Chosen)); // Recommended: each device's own track
        Assert.Equal("AXIS OS 13", vm.Devices[0].Choices[0].Text);

        vm.LatestLtsCommand.Execute(null);
        Assert.Equal(["12.11.147", "12.11.147", "12.11.147"], vm.Devices.Select(Chosen));
        Assert.Equal("Downgrade blocked", vm.Devices[2].VerdictText); // 13.0.88 -> 12.11.147, the decision table applies

        vm.LatestActiveCommand.Execute(null);
        Assert.Equal(["13.1.20", "13.1.20", "13.1.20"], vm.Devices.Select(Chosen));
        Assert.Equal("Not recommended", vm.Devices[0].VerdictText); // 11.11 jumps over 12.11: the upgrade path advice
        Assert.True(vm.HasBreakingChanges);

        vm.RecommendedCommand.Execute(null);
        Assert.Equal(["11.11.238", "12.11.147", "13.1.20"], vm.Devices.Select(Chosen));
    }

    [Fact]
    public async Task A_large_selection_sets_and_judges_behind_the_ring_with_the_buttons_waiting()
    {
        Device[] devices = [new("10.0.1.1", "Q1800-LE", "11.11.200"), new("10.0.1.2", "Q1800-LE", "12.11.100"), new("10.0.1.3", "Q1800-LE", "13.0.88")];
        using var vm = new DialogViewModel(Context(ActiveSite()), devices, new FakeFileSource()) { BackgroundFrom = 1, BusyPaintDelay = TimeSpan.Zero };
        await vm.LoadOnlineAsync(CancellationToken.None);
        Assert.False(vm.IsBusy);

        var run = vm.LatestLtsCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy); // from the first moment, before any device changed
        Assert.Equal("Setting the versions", vm.BusyText);
        Assert.False(vm.RecommendedCommand.CanExecute(null));
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal("Setting the versions.", vm.StartBlockedReason);
        await run;

        Assert.False(vm.IsBusy);
        Assert.Equal(["12.11.147", "12.11.147", "12.11.147"], vm.Devices.Select(Chosen));
        Assert.Equal("Downgrade blocked", vm.Devices[2].VerdictText);

        // The options judge every device again the same way.
        vm.AllowDowngrade = true;
        Assert.True(vm.IsBusy);
        Assert.Equal("Checking the devices", vm.BusyText);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => !vm.IsBusy, "the devices were judged");
        Assert.Equal("Downgrade blocked", vm.Devices[2].VerdictText); // still no factory default
        Assert.Contains("requires a factory default", vm.Devices[2].Message, StringComparison.Ordinal);

        // One device's choice judges that device only, at once.
        vm.Devices[0].SelectedChoice = vm.Devices[0].Choices.Single(c => c.Text == "13.1.20");
        Assert.False(vm.IsBusy);
        Assert.Equal("Not recommended", vm.Devices[0].VerdictText);
    }

    [Fact]
    public async Task Not_signed_in_every_device_starts_on_Choose_file_and_a_version_asks_to_sign_in()
    {
        var ctx = Context(new FakeAxisSite { MakeVersionFolders = true }, signedIn: false);
        using var vm = new DialogViewModel(ctx, Selection(), new FakeFileSource());
        var myAxis = (FakeMyAxis)ctx.MyAxis!;
        vm.SignInHandler = () => Task.FromResult(myAxis.SignIn());
        await vm.LoadOnlineAsync(CancellationToken.None);

        Assert.True(vm.ShowSignIn);
        Assert.All(vm.Devices, d => Assert.Null(d.SelectedChoice));
        Assert.Contains(vm.Devices[0].Choices, c => c.Text == "11.11.220"); // the versions show for information

        var device = vm.Devices[0];
        device.SelectedChoice = device.Choices.First(c => c.Text == "11.11.220");
        Assert.Equal(ClientVerdict.SignInNeeded, device.Check!.Verdict);
        Assert.Equal("Sign in", device.VerdictText);
        Assert.Equal("Sign in to MyAxis to download from axis.com.", device.Message);
        Assert.False(device.IsError);
        Assert.False(vm.CanStart());
        Assert.DoesNotContain("onlineVersion", ctx.Queries); // read only with a sign-in

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.True(vm.IsSignedIn);
        Assert.False(vm.ShowSignIn);
        Assert.Equal(1, myAxis.SignIns);
        Assert.Equal("11.11.220", Chosen(device)); // the choice stays
        Assert.Equal("12.11.147", Chosen(vm.Devices[2])); // the devices without a choice get Recommended
        Assert.Equal("11.11.160 → 11.11.220", device.Message);
        Assert.True(vm.CanStart());
    }

    [Fact]
    public async Task An_older_host_without_MyAxis_offers_chosen_files_only()
    {
        using var vm = new DialogViewModel(Context(new FakeAxisSite { MakeVersionFolders = true }, signedIn: null), Selection(), new FakeFileSource());
        await vm.LoadOnlineAsync(CancellationToken.None);

        Assert.False(vm.CanSignIn);
        Assert.False(vm.ShowSignIn);
        Assert.All(vm.Devices, d => Assert.Null(d.SelectedChoice));
    }

    [Fact]
    public async Task No_answer_from_axis_com_keeps_Choose_file()
    {
        using var vm = new DialogViewModel(Context(new FakeAxisSite { Down = true }), Selection(), new FakeFileSource { Picked = "C:\\fw\\P3265-V_12_11_77.bin" });
        await vm.LoadOnlineAsync(CancellationToken.None);

        Assert.True(vm.OnlineUnreachable);
        Assert.All(vm.Devices, d => Assert.Equal(["Choose file"], d.Choices.Select(c => c.Text)));
        Assert.False(vm.RecommendedCommand.CanExecute(null));

        await vm.ChooseFileForAsync(vm.Devices[0]);
        Assert.True(vm.Devices[0].WillInstall);
    }

    [Fact]
    public async Task A_failed_query_shows_that_axis_com_could_not_be_reached()
    {
        // The server's query fails (an error status of the host, an answer the dialog cannot read): never a silent dialog.
        foreach (var answer in new Func<string, string?, CancellationToken, Task<string?>>[]
        {
            (_, _, _) => throw new InvalidOperationException("Status(StatusCode=\"Unavailable\")"),
            (_, _, _) => Task.FromResult<string?>("{not json"),
            (_, _, _) => Task.FromResult<string?>(null),
        })
        {
            var ctx = Context(new FakeAxisSite());
            ctx.Online = answer;
            using var vm = new DialogViewModel(ctx, Selection(), new FakeFileSource());

            await vm.LoadOnlineAsync(CancellationToken.None);

            Assert.True(vm.OnlineUnreachable);
            Assert.False(vm.IsLookingUp);
            Assert.Contains("online", ctx.Queries);
        }
    }

    [Fact]
    public async Task An_older_version_is_checked_once_when_chosen()
    {
        var site = new FakeAxisSite { MakeVersionFolders = true };
        site.Set("/ftp/pub_soft/MPQT/P3265-V/11_11_192/", FakeAxisSite.Listing("/ftp/pub_soft/MPQT/P3265-V/11_11_192/"));
        var ctx = Context(site);
        using var vm = new DialogViewModel(ctx, Selection(), new FakeFileSource());
        await vm.LoadOnlineAsync(CancellationToken.None);
        var (a, b) = (vm.Devices[0], vm.Devices[1]);

        a.SelectedChoice = a.Choices.First(c => c.Text == "11.11.205");
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => ctx.Queries.Contains("onlineVersion") && vm.StartBlockedReason is null, "11.11.205 was read");
        b.SelectedChoice = b.Choices.First(c => c.Text == "11.11.205");
        Assert.Equal("Upgrade", b.VerdictText);
        Assert.Equal(1, ctx.Queries.Count(q => q == "onlineVersion")); // read once for both devices

        a.SelectedChoice = a.Choices.First(c => c.Text == "11.11.192");
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => a.Check?.Verdict == ClientVerdict.NotListed, "11.11.192 is refused");
        Assert.Equal("AXIS OS 11.11.192 is no longer on axis.com.", a.Message);
        Assert.Equal("Not on axis.com", a.VerdictText);
        Assert.False(a.WillInstall);
        Assert.True(vm.CanStart()); // the other devices still install
    }

    [Fact]
    public async Task Versions_and_chosen_files_mix_in_one_run()
    {
        Device[] devices = [new("10.0.0.48", "P3265-V", "11.11.160"), new("10.0.0.60", "Q6135-LE", "11.11.160"), new("10.0.0.61", "Q6135-LE", "11.11.160")];
        var ctx = Context(new FakeAxisSite { MakeVersionFolders = true });
        using var vm = new DialogViewModel(ctx, devices, new FakeFileSource { Picked = "C:\\fw\\Q6135-LE_12_11_77.bin" });
        await vm.LoadOnlineAsync(CancellationToken.None);

        await vm.ChooseFileForAsync(vm.Devices[1]);
        Assert.Equal("Q6135-LE_12_11_77.bin", vm.Devices[1].SelectedChoice!.Text);
        Assert.True(vm.Devices[1].WillInstall);
        Assert.Equal("No file", vm.Devices[2].VerdictText); // only that device

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(["C:\\fw\\Q6135-LE_12_11_77.bin"], ctx.Uploads); // only the chosen file
        var payload = ServerPayload.Parse(vm.Result);
        Assert.Equal("Q6135-LE_12_11_77.bin", payload.FileName);
        Assert.Null(Assert.Single(payload.AllFiles()).Devices); // every Q6135-LE of the run takes it: by product (the other one does not run)
        Assert.Equal(("11.11.238", 11), (Assert.Single(payload.AllOnline()).Version, payload.AllOnline()[0].FromMajor));
        Assert.Equal([devices[0].Id, devices[1].Id], vm.RunDevices);
        ServerAgrees(vm);
    }

    [Fact]
    public async Task While_the_lookup_runs_the_dialog_shows_it_and_waits()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = Context(new FakeAxisSite { MakeVersionFolders = true });
        var online = ctx.Online!;
        ctx.Online = async (method, payload, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return await online(method, payload, ct);
        };
        using var vm = new DialogViewModel(ctx, Selection(), new FakeFileSource());
        await vm.SelectFileAsync("P3265-V_12_11_77.bin", CancellationToken.None);

        var lookup = vm.LoadOnlineAsync(CancellationToken.None);
        Assert.True(vm.IsLookingUp);
        Assert.Equal("Reading the versions on axis.com", vm.LookupStatus);
        Assert.Equal("Reading the versions on axis.com.", vm.StartBlockedReason);
        Assert.False(vm.StartCommand.CanExecute(null));
        gate.SetResult();
        await lookup;

        Assert.False(vm.IsLookingUp);
        Assert.Equal("P3265-V_12_11_77.bin", vm.Devices[0].SelectedChoice!.Text); // a chosen file stays
        Assert.Equal("11.11.238", Chosen(vm.Devices[4]));
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_status_read_judges_against_the_current_version()
    {
        Device[] devices = [new("10.0.0.48", "P3265-V", "11.11.160")];
        var ctx = Context(new FakeAxisSite { MakeVersionFolders = true });
        ctx.Status[devices[0].Id] = new ClientStatus { Supported = true, ActiveVersion = "11.11.238" }.ToJson();
        using var vm = new DialogViewModel(ctx, devices, new FakeFileSource());
        await vm.LoadOnlineAsync(CancellationToken.None);
        Assert.Equal("Upgrade", vm.Devices[0].VerdictText);

        await vm.LoadStatusAsync(CancellationToken.None);

        Assert.Equal("Up to date", vm.Devices[0].VerdictText);
    }
}

/// <summary>HARD RULE "scale to thousands of devices": the selects and the buttons with the most selected devices.</summary>
public sealed class FirmwareOnlineScaleTests
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task Rows_and_the_three_buttons_for_100000_devices_are_fast()
    {
        const int models = 120;
        var names = Enumerable.Range(0, models).Select(i => string.Create(CultureInfo.InvariantCulture, $"M{3000 + i}-LE")).ToList();
        var site = new FakeAxisSite(recorded: false) { MakeVersionFolders = true };
        site.Set("/ftp/pub_soft/MPQT/", FakeAxisSite.Folders("/ftp/pub_soft/MPQT/", names));
        site.Set("/ftp/pub_soft/PACS/", FakeAxisSite.Folders("/ftp/pub_soft/PACS/", []));
        foreach (var name in names)
        {
            site.Set($"/ftp/pub_soft/MPQT/{name}/", FakeAxisSite.Folders($"/ftp/pub_soft/MPQT/{name}/", ["13_1_20", "12_11_147", "12_11_120", "11_11_238", "10_12_363"]));
        }

        string[] versions = ["11.11.200", "12.11.120", "13.0.88", "10.12.300"];
        var devices = Enumerable.Range(0, DeviceLimits.MaxDevices)
            .Select(i => new Device(string.Create(CultureInfo.InvariantCulture, $"10.{i / 65536}.{i / 256 % 256}.{i % 256}"), names[i % models], versions[i / models % versions.Length]))
            .ToArray();

        var watch = Stopwatch.StartNew();
        using var vm = new DialogViewModel(FirmwareOnlineDialogTests.Context(site), devices, new FakeFileSource()) { BusyPaintDelay = TimeSpan.Zero };
        var build = watch.ElapsedMilliseconds;
        watch.Restart();
        await vm.LoadOnlineAsync(CancellationToken.None); // ends with Recommended for every device
        var lookup = watch.ElapsedMilliseconds;
        watch.Restart();
        var busy = false;
        vm.PropertyChanged += (_, e) => busy |= e.PropertyName == nameof(DialogViewModel.IsBusy) && vm.IsBusy;
        await vm.LatestLtsCommand.ExecuteAsync(null);
        await vm.LatestActiveCommand.ExecuteAsync(null);
        await vm.RecommendedCommand.ExecuteAsync(null);
        var rules = watch.ElapsedMilliseconds;
        Assert.True(busy); // the ring showed while the buttons ran (off the UI thread)
        watch.Restart();
        vm.Devices[50_000].SelectedChoice = vm.Devices[50_000].Choices.First(c => c.Text == "12.11.147");
        var one = watch.ElapsedMilliseconds;
        var (files, online) = DialogViewModel.PayloadParts([.. vm.Devices.Where(d => d.WillInstall)]);

        Assert.All(vm.Devices, d => Assert.NotNull(d.OnlineVersion));
        Assert.Same(vm.Devices[0].Choices, vm.Devices[models].Choices); // one list per model
        Assert.Empty(files);
        Assert.True(online.Count <= models * versions.Length + 1, $"{online.Count} payload entries"); // per product and major, one named device
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"of {DeviceLimits.MaxDevices:N0} devices will be updated"), vm.Summary, StringComparison.Ordinal);
        var message = $"rows {build} ms, lookup of {models} models with Recommended {lookup} ms, three buttons {rules} ms, one device {one} ms";
        Console.WriteLine(message);
        Assert.True(build < 3_000, message); // measured 120 ms
        Assert.True(lookup < 9_000, message); // measured 870 ms
        Assert.True(rules < 17_000, message); // measured 1,700 ms
        Assert.True(one < 3_500, message); // measured 340 ms
    }
}

/// <summary>The dialog with versions from axis.com, offscreen with the host theme. Set OADM_SCREENSHOT_DIR to write PNGs.</summary>
public sealed class FirmwareOnlineHeadlessTests
{
    [Fact]
    public async Task Dialog_renders_signed_in_with_Recommended_and_signed_out()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var session = HeadlessSession.Shared;

        var chosen = await session.Dispatch(async () =>
        {
            var vm = new DialogViewModel(FirmwareOnlineDialogTests.Context(new FakeAxisSite { MakeVersionFolders = true }), Shots(), new FakeFileSource());
            var window = new DialogWindow { DataContext = vm };
            window.Show();
            await vm.LoadOnlineAsync(CancellationToken.None);
            await PumpAsync();
            Capture(window, outDir, "firmware-online-recommended.png");
            var text = string.Join(",", vm.Devices.Select(d => d.SelectedChoice?.Text ?? "-"));
            window.Close();
            vm.Dispose();

            var ctx = FirmwareOnlineDialogTests.Context(new FakeAxisSite { MakeVersionFolders = true }, signedIn: false);
            var signedOut = new DialogViewModel(ctx, Shots(), new FakeFileSource());
            var second = new DialogWindow { DataContext = signedOut };
            second.Show();
            await signedOut.LoadOnlineAsync(CancellationToken.None);
            var device = signedOut.Devices[4];
            device.SelectedChoice = device.Choices.First(c => !c.IsHeader);
            await PumpAsync();
            Assert.True(signedOut.ShowSignIn);
            Capture(second, outDir, "firmware-online-signed-out.png");
            second.Close();
            signedOut.Dispose();
            return text;
        }, CancellationToken.None);

        Assert.Equal("11.11.238,11.11.238,12.11.147,12.11.147,11.11.238,-", chosen);
    }

    private static Device[] Shots() =>
    [
        new("10.0.0.48", "P3265-V", "11.11.160"),
        new("10.0.0.49", "P3265-V", "11.11.160"),
        new("10.0.0.50", "P3265-V", "12.11.77"),
        new("10.0.0.51", "P3265-V", "12.11.147"),
        new("10.0.0.52", "C1410", "11.11.212"),
        new("10.0.0.53", "P12-MkIII", "9.80.1"),
    ];

    private static async Task PumpAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }

    private static void Capture(Avalonia.Controls.Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
