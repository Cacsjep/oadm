extern alias fwclient;

using System.Diagnostics;
using System.Globalization;

using Avalonia.Headless;
using Avalonia.Threading;

using Oadm.Plugins.Firmware.Tests;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

using ClientOnline = fwclient::Oadm.Plugins.Firmware.FirmwareOnline;
using ClientPayload = fwclient::Oadm.Plugins.Firmware.FirmwarePayload;
using ClientStatus = fwclient::Oadm.Plugins.Firmware.FirmwareStatusInfo;
using ClientVerdict = fwclient::Oadm.Plugins.Firmware.FirmwareVerdict;
using DialogViewModel = fwclient::Oadm.Plugins.Firmware.Client.FirmwareDialogViewModel;
using DialogWindow = fwclient::Oadm.Plugins.Firmware.Client.FirmwareDialogWindow;
using ModelRow = fwclient::Oadm.Plugins.Firmware.Client.FirmwareModelRow;

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

/// <summary>The dialog with versions from axis.com: the server's own queries answer from the recorded pages.</summary>
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
        var ctx = new FakeDialogContext
        {
            MyAxis = signedIn is { } s ? new FakeMyAxis(s) : null,
            Online = (method, payload, ct) => plugin.QueryAsync(query, new FakeDevice(), method, payload, ct),
        };
        return ctx;
    }

    private static ModelRow Row(DialogViewModel vm, string model, int major) => vm.ModelRows.Single(r => r.Model == model && r.Major == major);

    private static string? Chosen(ModelRow row) => row.OnlineVersion?.Version.Version;

    [Fact]
    public async Task One_row_per_model_and_track_and_signed_in_it_opens_with_Recommended()
    {
        var devices = Selection();
        var ctx = Context(new FakeAxisSite { MakeVersionFolders = true });
        using var vm = new DialogViewModel(ctx, devices, new FakeFileSource());

        Assert.Equal(["C1410 11", "P12-MkIII 9", "P3265-V 12", "P3265-V 11"], vm.ModelRows.Select(r => r.Model + " " + r.Major));
        Assert.Equal("2 devices", Row(vm, "P3265-V", 11).DevicesText);
        Assert.Equal("12.11.77 to 12.11.147", Row(vm, "P3265-V", 12).FirmwareNow);

        await vm.LoadOnlineAsync(CancellationToken.None);

        Assert.False(vm.OnlineUnreachable);
        Assert.Equal("11.11.238", Chosen(Row(vm, "P3265-V", 11))); // 11.11 stays 11.11
        Assert.Equal("12.11.147", Chosen(Row(vm, "P3265-V", 12)));
        Assert.Equal("11.11.238", Chosen(Row(vm, "C1410", 11)));
        Assert.Equal("Up to date", Row(vm, "C1410", 11).Status);

        // Not listed: Choose file only.
        var p12 = Row(vm, "P12-MkIII", 9);
        Assert.False(p12.IsListed);
        Assert.Equal(["Choose file"], p12.Choices.Select(c => c.Text));
        Assert.Null(p12.SelectedChoice);

        // The select: the track names as headers, every version newest first, Choose file last.
        var choices = Row(vm, "P3265-V", 11).Choices;
        Assert.Equal(["LTS 2026 · 12.11", "LTS 2024 · 11.11", "LTS 2022 · 10.12"], choices.Where(c => c.IsHeader).Select(c => c.Text));
        Assert.Equal(["12.11.147", "12.11.118"], choices.SkipWhile(c => c.IsHeader).Take(2).Select(c => c.Text));
        Assert.Equal("Choose file", choices[^1].Text);

        Assert.Equal("Upgrade", vm.Devices[0].VerdictText);
        Assert.Equal("11.11.160 → 11.11.238", vm.Devices[0].Message);
        Assert.Equal("Up to date", vm.Devices[3].VerdictText);
        Assert.Equal("3 of 6 devices will be updated, 2 already up to date, 1 not possible.", vm.Summary);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(ctx.Uploads); // nothing to upload: the server downloads
        var payload = ClientPayload.Parse(vm.Result);
        Assert.Null(payload.FileId);
        Assert.Equal([new ClientOnline("MPQT", "P3265-V", "12.11.147", 12), new ClientOnline("MPQT", "P3265-V", "11.11.238", 11)], payload.Online!.OrderByDescending(o => o.FromMajor));
        Assert.Equal([devices[0].Id, devices[1].Id, devices[2].Id], vm.RunDevices);

        // The server picks the same version for every device the run starts on.
        var server = Oadm.Plugins.Firmware.FirmwarePayload.Parse(vm.Result);
        Assert.Equal("Upgrade firmware", server.TaskName()); // two versions, every device upgrades
        foreach (var device in devices.Take(3))
        {
            var index = Oadm.Plugins.Firmware.FirmwareCompatibility.FindOnline(device.Model, device.FirmwareVersion, server.AllOnline());
            Assert.Equal(device.FirmwareVersion!.StartsWith("11.", StringComparison.Ordinal) ? "11.11.238" : "12.11.147", server.AllOnline()[index].Version);
        }
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
    public async Task The_buttons_set_every_row()
    {
        Device[] devices = [new("10.0.1.1", "Q1800-LE", "11.11.200"), new("10.0.1.2", "Q1800-LE", "12.11.100"), new("10.0.1.3", "Q1800-LE", "13.0.88")];
        using var vm = new DialogViewModel(Context(ActiveSite()), devices, new FakeFileSource());
        Assert.False(vm.RecommendedCommand.CanExecute(null)); // nothing read yet
        await vm.LoadOnlineAsync(CancellationToken.None);

        Assert.Equal(["13.1.20", "12.11.147", "11.11.238"], vm.ModelRows.Select(Chosen)); // Recommended: each row's own track
        Assert.Equal("AXIS OS 13", vm.ModelRows[0].Choices[0].Text);

        vm.LatestLtsCommand.Execute(null);
        Assert.Equal(["12.11.147", "12.11.147", "12.11.147"], vm.ModelRows.Select(Chosen));
        Assert.Equal("Downgrade blocked", vm.Devices[2].VerdictText); // 13.0.88 -> 12.11.147, the decision table applies

        vm.LatestActiveCommand.Execute(null);
        Assert.Equal(["13.1.20", "13.1.20", "13.1.20"], vm.ModelRows.Select(Chosen));
        Assert.Equal("Not recommended", vm.Devices[0].VerdictText); // 11.11 jumps over 12.11: the upgrade path advice
        Assert.True(vm.HasBreakingChanges);

        vm.RecommendedCommand.Execute(null);
        Assert.Equal(["13.1.20", "12.11.147", "11.11.238"], vm.ModelRows.Select(Chosen));
    }

    [Fact]
    public async Task Not_signed_in_every_row_starts_on_Choose_file_and_a_version_asks_to_sign_in()
    {
        var ctx = Context(new FakeAxisSite { MakeVersionFolders = true }, signedIn: false);
        using var vm = new DialogViewModel(ctx, Selection(), new FakeFileSource());
        var myAxis = (FakeMyAxis)ctx.MyAxis!;
        vm.SignInHandler = () => Task.FromResult(myAxis.SignIn());
        await vm.LoadOnlineAsync(CancellationToken.None);

        Assert.True(vm.ShowSignIn);
        Assert.All(vm.ModelRows, r => Assert.Null(r.SelectedChoice));
        Assert.True(Row(vm, "P3265-V", 11).IsListed); // the versions show for information

        var row = Row(vm, "P3265-V", 11);
        row.SelectedChoice = row.Choices.First(c => c.Text == "11.11.220");
        Assert.Equal("Sign in to MyAxis to download from axis.com.", row.Notice);
        Assert.Equal(ClientVerdict.SignInNeeded, vm.Devices[0].Check!.Verdict);
        Assert.Equal("Sign in", vm.Devices[0].VerdictText);
        Assert.False(vm.Devices[0].IsError);
        Assert.False(vm.CanStart());
        Assert.DoesNotContain("onlineVersion", ctx.Queries); // read only with a sign-in

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.True(vm.IsSignedIn);
        Assert.False(vm.ShowSignIn);
        Assert.Equal(1, myAxis.SignIns);
        Assert.Equal("11.11.220", Chosen(row)); // the choice stays
        Assert.Null(row.Notice);
        Assert.Equal("12.11.147", Chosen(Row(vm, "P3265-V", 12))); // the rows without a choice get Recommended
        Assert.Equal("11.11.160 → 11.11.220", vm.Devices[0].Message);
        Assert.True(vm.CanStart());
    }

    [Fact]
    public async Task An_older_host_without_MyAxis_offers_chosen_files_only()
    {
        using var vm = new DialogViewModel(Context(new FakeAxisSite { MakeVersionFolders = true }, signedIn: null), Selection(), new FakeFileSource());
        await vm.LoadOnlineAsync(CancellationToken.None);

        Assert.False(vm.CanSignIn);
        Assert.False(vm.ShowSignIn);
        Assert.All(vm.ModelRows, r => Assert.Null(r.SelectedChoice));
    }

    [Fact]
    public async Task No_answer_from_axis_com_keeps_Choose_file()
    {
        using var vm = new DialogViewModel(Context(new FakeAxisSite { Down = true }), Selection(), new FakeFileSource { Picked = "C:\\fw\\P3265-V_12_11_77.bin" });
        await vm.LoadOnlineAsync(CancellationToken.None);

        Assert.True(vm.OnlineUnreachable);
        Assert.All(vm.ModelRows, r => Assert.Equal(["Choose file"], r.Choices.Select(c => c.Text)));
        Assert.False(vm.RecommendedCommand.CanExecute(null));

        await vm.ChooseFileForAsync(Row(vm, "P3265-V", 11));
        Assert.True(vm.Devices[0].WillInstall);
    }

    [Fact]
    public async Task An_older_version_is_checked_when_chosen()
    {
        var site = new FakeAxisSite { MakeVersionFolders = true };
        site.Set("/ftp/pub_soft/MPQT/P3265-V/11_11_192/", FakeAxisSite.Listing("/ftp/pub_soft/MPQT/P3265-V/11_11_192/"));
        var ctx = Context(site);
        using var vm = new DialogViewModel(ctx, Selection(), new FakeFileSource());
        await vm.LoadOnlineAsync(CancellationToken.None);
        var row = Row(vm, "P3265-V", 11);

        row.SelectedChoice = row.Choices.First(c => c.Text == "11.11.205");
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => row.CheckedVersion is not null, "11.11.205 was read");
        Assert.Null(row.Error);
        Assert.True(vm.CanStart());

        row.SelectedChoice = row.Choices.First(c => c.Text == "11.11.192");
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => row.Error is not null, "11.11.192 is refused");
        Assert.Equal("AXIS OS 11.11.192 is no longer on axis.com.", row.Error);
        Assert.Equal(row.Error, vm.FileError);
        Assert.Equal(row.Error, vm.StartBlockedReason);
        Assert.Equal(2, ctx.Queries.Count(q => q == "onlineVersion"));
    }

    [Fact]
    public async Task Versions_and_chosen_files_mix_in_one_run()
    {
        Device[] devices = [new("10.0.0.48", "P3265-V", "11.11.160"), new("10.0.0.60", "Q6135-LE", "11.11.160")];
        var ctx = Context(new FakeAxisSite { MakeVersionFolders = true });
        using var vm = new DialogViewModel(ctx, devices, new FakeFileSource { Picked = "C:\\fw\\Q6135-LE_12_11_77.bin" });
        await vm.LoadOnlineAsync(CancellationToken.None);

        var q = Row(vm, "Q6135-LE", 11);
        Assert.False(q.IsListed); // its pages are not recorded: not listed
        await vm.ChooseFileForAsync(q);
        Assert.Equal("Q6135-LE_12_11_77.bin", q.SelectedChoice!.Text);
        Assert.True(vm.Devices[1].WillInstall);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(["C:\\fw\\Q6135-LE_12_11_77.bin"], ctx.Uploads); // only the chosen file
        var payload = Oadm.Plugins.Firmware.FirmwarePayload.Parse(vm.Result);
        Assert.Equal("Q6135-LE_12_11_77.bin", payload.FileName);
        Assert.Equal(new Oadm.Plugins.Firmware.FirmwareOnline("MPQT", "P3265-V", "11.11.238", 11), Assert.Single(payload.AllOnline()));
        Assert.Equal([devices[0].Id, devices[1].Id], vm.RunDevices);
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
        Assert.Equal("P3265-V_12_11_77.bin", Row(vm, "P3265-V", 11).SelectedChoice!.Text); // a chosen file stays
        Assert.Equal("11.11.238", Chosen(Row(vm, "C1410", 11)));
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_device_whose_status_shows_another_major_moves_to_that_row()
    {
        Device[] devices = [new("10.0.0.48", "P3265-V", "11.11.160"), new("10.0.0.49", "P3265-V", "12.11.77")];
        var ctx = Context(new FakeAxisSite { MakeVersionFolders = true });
        ctx.Status[devices[0].Id] = new ClientStatus { Supported = true, ActiveVersion = "12.11.118" }.ToJson();
        ctx.Status[devices[1].Id] = new ClientStatus { Supported = true, ActiveVersion = "12.11.77" }.ToJson();
        using var vm = new DialogViewModel(ctx, devices, new FakeFileSource());
        await vm.LoadOnlineAsync(CancellationToken.None);
        Assert.Equal(2, vm.ModelRows.Count);

        await vm.LoadStatusAsync(CancellationToken.None);

        var row = Assert.Single(vm.ModelRows);
        Assert.Equal((12, "2 devices", "12.11.147"), (row.Major!.Value, row.DevicesText, Chosen(row)));
        Assert.True(vm.Devices.All(d => d.WillInstall));
    }
}

/// <summary>HARD RULE "scale to thousands of devices": the firmware rows of the most selected devices.</summary>
public sealed class FirmwareOnlineScaleTests
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task Rows_of_100000_devices_are_built_and_set_fast()
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
        using var vm = new DialogViewModel(FirmwareOnlineDialogTests.Context(site), devices, new FakeFileSource());
        var build = watch.ElapsedMilliseconds;
        watch.Restart();
        await vm.LoadOnlineAsync(CancellationToken.None);
        var lookup = watch.ElapsedMilliseconds;
        watch.Restart();
        vm.LatestLtsCommand.Execute(null);
        vm.RecommendedCommand.Execute(null);
        var rules = watch.ElapsedMilliseconds;

        Assert.Equal(models * versions.Length, vm.ModelRows.Count); // one row per model and track
        Assert.Equal(DeviceLimits.MaxDevices, vm.ModelRows.Sum(r => r.DeviceRows.Count));
        Assert.All(vm.ModelRows, r => Assert.NotNull(r.OnlineVersion));
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"of {DeviceLimits.MaxDevices:N0} devices will be updated"), vm.Summary, StringComparison.Ordinal);
        var message = $"rows {build} ms, lookup of {models} models {lookup} ms, two rules {rules} ms";
        Console.WriteLine(message);
        Assert.True(build < 3_000, message); // measured 240 ms
        Assert.True(lookup < 6_000, message); // measured 500 ms (fake site)
        Assert.True(rules < 6_000, message); // measured 490 ms
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
            var text = string.Join(",", vm.ModelRows.Select(r => r.SelectedChoice?.Text ?? "-"));
            window.Close();
            vm.Dispose();

            var ctx = FirmwareOnlineDialogTests.Context(new FakeAxisSite { MakeVersionFolders = true }, signedIn: false);
            var signedOut = new DialogViewModel(ctx, Shots(), new FakeFileSource());
            var second = new DialogWindow { DataContext = signedOut };
            second.Show();
            await signedOut.LoadOnlineAsync(CancellationToken.None);
            var row = signedOut.ModelRows.First(r => r.IsListed);
            row.SelectedChoice = row.Choices.First(c => !c.IsHeader);
            await PumpAsync();
            Assert.True(signedOut.ShowSignIn);
            Capture(second, outDir, "firmware-online-signed-out.png");
            second.Close();
            signedOut.Dispose();
            return text;
        }, CancellationToken.None);

        Assert.Equal("11.11.238,-,12.11.147,11.11.238", chosen);
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
