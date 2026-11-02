using System.Text.Json;

using Oadm.Plugins.Firmware.Online;

namespace Oadm.Plugins.Firmware.Tests;

/// <summary>The listing format of axis.com from recorded pages (Fixtures/Online, 2026-11-02).</summary>
public sealed class OnlineListingTests
{
    private static string Page(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Online", name));

    [Fact]
    public void A_product_folder_lists_its_version_folders_and_latest()
    {
        var entries = OnlineListing.Parse(Page("P3265-V.html"));

        Assert.All(entries, e => Assert.True(e.IsFolder));
        Assert.Equal(41, entries.Count); // 40 version folders and latest/, the parent row left out
        Assert.Contains(entries, e => e.Name == "latest" && e.Path == "/ftp/pub_soft/MPQT/P3265-V/latest/");
        Assert.Contains(entries, e => e.Name == "12_11_147");
        Assert.All(entries, e => Assert.Null(e.Size));
    }

    [Fact]
    public void A_version_folder_lists_the_bin_with_size_and_checksum()
    {
        var bin = OnlineListing.Parse(Page("P3265-V-12_11_147.html")).Single(e => e.Name.EndsWith(".bin", StringComparison.Ordinal));

        Assert.Equal("P3265-V_12_11_147.bin", bin.Name);
        Assert.Equal("/ftp/pub_soft/MPQT/P3265-V/12_11_147/P3265-V_12_11_147.bin", bin.Path);
        Assert.False(bin.IsFolder);
        Assert.Equal(236_453_888, bin.Size); // "225.5M"
        Assert.Equal("abb2d5179defc9c13be2ae68aa1f6ae747942b3ad9c509dbc1a31be8026e2888", bin.Sha256);
    }

    [Theory]
    [InlineData("225.5M", 236_453_888L)]
    [InlineData("3.1M", 3_250_586L)] // the listing counts in 1,024: a file of 3,222,833 bytes shows 3.1M
    [InlineData("7.1K", 7_270L)]
    [InlineData("9", 9L)]
    [InlineData("1.2G", 1_288_490_189L)]
    [InlineData("-", null)]
    [InlineData("", null)]
    [InlineData("big", null)]
    public void Sizes_are_read_in_units_of_1024(string text, long? bytes) => Assert.Equal(bytes, OnlineListing.ParseSize(text));

    [Theory]
    [InlineData("12_11_147", "12.11.147")]
    [InlineData("10_12_356_2", "10.12.356.2")]
    [InlineData("9_80_3_13_1", "9.80.3.13.1")]
    [InlineData("1_97_1", "1.97.1")]
    [InlineData("10_12_212_TTA", null)] // a special build
    [InlineData("latest", null)]
    [InlineData("12", null)]
    [InlineData("1_2_3_4_5_6", null)]
    public void Version_folders_have_two_to_five_numbers(string name, string? version) =>
        Assert.Equal(version, OnlineListing.VersionOfFolder(name)?.Text);

    [Fact]
    public void Ver_txt_counts_only_as_a_bare_version()
    {
        Assert.Equal("12.11.147", OnlineListing.ParseVerTxt(Page("P3265-V-ver.txt"))?.Text);
        Assert.Equal("11.11.212", OnlineListing.ParseVerTxt("11.11.212\n")?.Text);
        Assert.Null(OnlineListing.ParseVerTxt(Page("not-found.html"))); // axis.com answers a missing page with "Page not found"
        Assert.Null(OnlineListing.ParseVerTxt(""));
        Assert.Null(OnlineListing.ParseVerTxt("12.11.147 beta"));
    }

    [Fact]
    public void The_roots_list_the_products_and_odd_html_is_no_error()
    {
        var mpqt = OnlineListing.Parse(Page("mpqt-root.html"));
        Assert.Contains(mpqt, e => e.Name == "P3265-V" && e.IsFolder);
        Assert.Contains(mpqt, e => e.Name == "P1455-LE_29_mm" && e.IsFolder);
        Assert.Contains(OnlineListing.Parse(Page("pacs-root.html")), e => e.Name == "C1410_Mk_II" && e.IsFolder);
        Assert.Empty(OnlineListing.Parse(Page("not-found.html")));
        Assert.Empty(OnlineListing.Parse("<tr><td>no link</td></tr><tr>"));
        Assert.Empty(OnlineListing.Parse(null));
    }
}

/// <summary>Model match, tracks and the three selection rules (README "Firmware online").</summary>
public sealed class OnlineRulesTests
{
    [Theory]
    [InlineData("P3265-V", "P3265-V", true)]
    [InlineData("AXIS P3265-V", "p3265v", true)]
    [InlineData("C1410 Mk II", "C1410_Mk_II", true)]
    [InlineData("P1455-LE", "P1455-LE-3", false)]
    [InlineData("P1455-LE", "P1455-LE_29_mm", false)]
    [InlineData("P3265-LVE", "P3265-LVE_9_mm", false)]
    [InlineData("", "", false)]
    public void Models_match_product_folders_only_exactly(string model, string folder, bool matches) =>
        Assert.Equal(matches, OnlineFirmware.Matches(folder, model));

    private static AxisOsVersion[] V(params string[] versions) => [.. versions.Select(v => AxisOsVersion.TryParse(v)!)];

    [Fact]
    public void A_model_that_ends_on_an_lts_has_no_active_track_and_older_majors_keep_their_last_minor()
    {
        // P3265-V on axis.com: 12 ends on 12.11, 10.10 and 10.11 next to 10.12 are dropped.
        var tracks = OnlineFirmware.BuildTracks(V("12.0.91", "12.11.77", "12.11.147", "12.10.73", "11.11.192", "11.11.238", "10.10.73", "10.11.76", "10.12.338", "10.12.363"));

        Assert.Equal(["LTS 2026 · 12.11", "LTS 2024 · 11.11", "LTS 2022 · 10.12"], tracks.Select(t => t.Name));
        Assert.All(tracks, t => Assert.Equal(OnlineTrackKind.Lts, t.Kind));
        Assert.Equal(["12.11.147", "12.11.77"], tracks[0].Versions.Select(v => v.Version)); // newest first, only the LTS minor
        Assert.Equal(["10.12.363", "10.12.338"], tracks[2].Versions.Select(v => v.Version));
    }

    [Fact]
    public void The_newest_major_is_the_active_track_with_all_its_versions()
    {
        var tracks = OnlineFirmware.BuildTracks(V("13.0.88", "13.1.20", "12.11.147", "12.6.85", "9.80.105", "9.80.3.13.1", "8.40.4", "6.50.2", "10.11.76", "1.97.1"));

        Assert.Equal(["AXIS OS 13", "LTS 2026 · 12.11", "AXIS OS 10", "PSS 9.80", "PSS 8.40", "PSS 6.50", "AXIS OS 1"], tracks.Select(t => t.Name));
        Assert.Equal(OnlineTrackKind.Active, tracks[0].Kind);
        Assert.Null(tracks[0].Minor);
        Assert.Equal(["13.1.20", "13.0.88"], tracks[0].Versions.Select(v => v.Version));
        Assert.Equal(OnlineTrackKind.Old, tracks[2].Kind); // 10.11 is no LTS
        Assert.Equal(["9.80.105", "9.80.3.13.1"], tracks[3].Versions.Select(v => v.Version));
    }

    [Fact]
    public void Lts_tracks_carry_their_year()
    {
        Assert.Equal([null, null, null, 2022, 2024, 2026], AxisOsUpgradePath.LtsTracks.Select(t => t.Year));
        Assert.Equal("LTS 2022 · 10.12", AxisOsUpgradePath.LtsName(10, 12));
        Assert.Equal("PSS 6.50", AxisOsUpgradePath.LtsName(6, 50));
        Assert.Null(AxisOsUpgradePath.LtsName(12, 6));
    }

    private static OnlineModel Model(string? latest, params string[] versions) =>
        new() { Model = "M", Product = "M", Source = "MPQT", Latest = latest, Tracks = OnlineFirmware.BuildTracks(V(versions)) };

    private static readonly OnlineModel WithActive = Model("13.1.20", "13.0.88", "13.1.20", "12.11.120", "12.11.147", "11.11.238", "9.80.105");
    private static readonly OnlineModel EndsOnLts = Model("11.11.238", "11.11.205", "11.11.238", "10.12.356.2", "9.80.105", "1.97.1");

    [Theory]
    [InlineData(11, "11.11.238")] // 11.11 stays 11.11
    [InlineData(13, "13.1.20")] // the active track stays active
    [InlineData(9, "9.80.105")] // 9.80 stays 9.80
    [InlineData(10, "12.11.147")] // a track no longer listed: the newest LTS
    [InlineData(null, "12.11.147")] // a version not known: the newest LTS
    public void Recommended_keeps_the_devices_on_their_track(int? major, string expected) =>
        Assert.Equal(expected, OnlineFirmware.Recommended(WithActive, major));

    [Fact]
    public void Latest_lts_and_latest_active()
    {
        Assert.Equal("12.11.147", OnlineFirmware.LatestLts(WithActive));
        Assert.Equal("13.1.20", OnlineFirmware.LatestActive(WithActive));
        Assert.Equal("11.11.238", OnlineFirmware.LatestLts(EndsOnLts));
        Assert.Equal("11.11.238", OnlineFirmware.LatestActive(EndsOnLts)); // no active track: its newest version
        Assert.Equal("1.97.1", OnlineFirmware.Recommended(EndsOnLts, 1)); // its own (old) track
    }

    [Fact]
    public void Online_entries_match_the_product_and_the_devices_major()
    {
        FirmwareOnline[] online = [new("MPQT", "P3265-V", "12.11.147", 12), new("MPQT", "P3265-V", "11.11.238", 11), new("PACS", "C1410_Mk_II", "11.11.238", null)];

        Assert.Equal(0, FirmwareCompatibility.FindOnline("P3265-V", "12.11.77", online));
        Assert.Equal(1, FirmwareCompatibility.FindOnline("AXIS P3265-V", "11.11.160", online));
        Assert.Equal(-1, FirmwareCompatibility.FindOnline("P3265-V", "10.12.300", online)); // no row for its track
        Assert.Equal(2, FirmwareCompatibility.FindOnline("C1410 Mk II", "11.11.205", online)); // the row of unknown version
        Assert.Equal(-1, FirmwareCompatibility.FindOnline("P3265-LVE", "12.11.77", online));
    }

    [Fact]
    public void An_online_version_is_judged_like_a_file_with_product_and_version()
    {
        FirmwareOnline[] online = [new("MPQT", "P3265-V", "12.11.147", 11)];
        FirmwareImageInfo[] images = [OnlineFirmware.ImageOf(online[0], 236_453_888)];
        var file = FirmwareImageInspector.Inspect("Q6135-LE_12_11_77.bin", 80 * 1024 * 1024);

        var upgrade = FirmwareCompatibility.ChooseWithOnline("P3265-V", "11.11.160", online, images, [file], FactoryDefaultMode.None, false);
        Assert.Equal((0, -1), (upgrade.Online, upgrade.File));
        Assert.Equal(FirmwareVerdict.Upgrade, upgrade.Check.Verdict);
        Assert.NotNull(upgrade.Check.PathNote); // leaves the LTS track 11.11: the upgrade path note applies unchanged

        var same = FirmwareCompatibility.ChooseWithOnline("P3265-V", "11.11.160", [new("MPQT", "P3265-V", "11.11.160", 11)], [OnlineFirmware.ImageOf(new("MPQT", "P3265-V", "11.11.160", 11), 0)], [], FactoryDefaultMode.None, false);
        Assert.Equal(FirmwareVerdict.AlreadyUpToDate, same.Check.Verdict);

        // A device of another model takes the chosen file for its product; a single file is not for every model here.
        var q = FirmwareCompatibility.ChooseWithOnline("Q6135-LE", "11.11.160", online, images, [file], FactoryDefaultMode.None, false);
        Assert.Equal((-1, 0, FirmwareVerdict.Upgrade), (q.Online, q.File, q.Check.Verdict));
        var m = FirmwareCompatibility.ChooseWithOnline("M3215-LVE", "11.11.160", online, images, [file], FactoryDefaultMode.None, false);
        Assert.Equal((-1, -1, FirmwareVerdict.NoFileForModel), (m.Online, m.File, m.Check.Verdict));

        // Older: a downgrade is refused like a file.
        var down = FirmwareCompatibility.ChooseWithOnline("P3265-V", "12.11.160", [new("MPQT", "P3265-V", "11.11.238", 12)], [OnlineFirmware.ImageOf(new("MPQT", "P3265-V", "11.11.238", 12), 0)], [], FactoryDefaultMode.None, false);
        Assert.Equal(FirmwareVerdict.DowngradeBlocked, down.Check.Verdict);
    }
}

/// <summary>The lookup of the server against the recorded pages (no network).</summary>
public sealed class OnlineLookupTests
{
    private static Task<OnlineLookupResult> Lookup(FakeAxisSite site, params string[] models) =>
        new OnlineFirmwareLookup(site).LookupAsync(models, CancellationToken.None);

    [Fact]
    public async Task Reads_the_tracks_of_the_selected_models_only()
    {
        var site = new FakeAxisSite();
        var result = await Lookup(site, "P3265-V", "AXIS P3265-V", "p12-mkiii");

        Assert.False(result.Unreachable);
        Assert.Equal(2, result.Models.Count); // one per model
        var p = result.Models[0];
        Assert.Equal(("P3265-V", "MPQT", "12.11.147"), (p.Product, p.Source, p.Latest));
        Assert.Equal(["LTS 2026 · 12.11", "LTS 2024 · 11.11", "LTS 2022 · 10.12"], p.Tracks.Select(t => t.Name));
        Assert.Equal(["12.11.147", "12.11.118", "12.11.77", "12.11.72", "12.11.64"], p.Tracks[0].Versions.Select(v => v.Version));
        // The newest folder of each track is read for its size and SHA-256; older ones when chosen.
        Assert.Equal(236_453_888, p.Tracks[0].Versions[0].Size);
        Assert.Equal("abb2d5179defc9c13be2ae68aa1f6ae747942b3ad9c509dbc1a31be8026e2888", p.Tracks[0].Versions[0].Sha256);
        Assert.NotNull(p.Tracks[1].Versions[0].Sha256);
        Assert.NotNull(p.Tracks[2].Versions[0].Sha256);
        Assert.Null(p.Tracks[0].Versions[1].Size);

        // P12-MkIII is not listed: Choose file only.
        Assert.False(result.Models[1].IsListed);
        Assert.Null(result.Models[1].Product);

        // Both roots, then the model's folder, latest/, ver.txt and the newest folder of each of its three tracks.
        string[] expected =
        [
            "/ftp/pub_soft/MPQT/", "/ftp/pub_soft/PACS/", "/ftp/pub_soft/MPQT/P3265-V/", "/ftp/pub_soft/MPQT/P3265-V/latest/", "/ftp/pub_soft/MPQT/P3265-V/latest/ver.txt",
            "/ftp/pub_soft/MPQT/P3265-V/12_11_147/", "/ftp/pub_soft/MPQT/P3265-V/11_11_238/", "/ftp/pub_soft/MPQT/P3265-V/10_12_363/",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), site.Requests.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_version_only_in_latest_counts_with_the_latest_file_and_special_builds_are_skipped()
    {
        var result = await Lookup(new FakeAxisSite(), "P1455-LE");

        var model = Assert.Single(result.Models);
        Assert.Equal("P1455-LE", model.Product); // never P1455-LE-3 or P1455-LE_29_mm
        var track = Assert.Single(model.Tracks);
        Assert.Equal("LTS 2024 · 11.11", track.Name);
        Assert.Equal("11.11.212", Assert.Single(track.Versions).Version); // 10_12_212_TTA is a special build
        Assert.NotNull(track.Versions[0].Sha256);
        Assert.Equal("11.11.212", model.Latest);
    }

    [Fact]
    public async Task A_product_of_pacs_is_found_there()
    {
        var site = new FakeAxisSite { MakeVersionFolders = true };
        var result = await Lookup(site, "C1410");

        var model = Assert.Single(result.Models);
        Assert.Equal(("C1410", "PACS"), (model.Product, model.Source));
        Assert.Equal(["LTS 2024 · 11.11", "LTS 2022 · 10.12", "PSS 9.80", "AXIS OS 1"], model.Tracks.Select(t => t.Name));
        Assert.Equal("11.11.238", model.Latest);
        Assert.Equal("10.12.356.2", model.Tracks[1].Newest);
        Assert.Equal("11.11.238", OnlineFirmware.LatestActive(model)); // ends on an LTS: no active track
    }

    [Fact]
    public async Task Versions_without_a_bin_a_checksum_or_1_MB_are_left_out()
    {
        var site = new FakeAxisSite { MakeVersionFolders = true };
        site.Set("/ftp/pub_soft/MPQT/P3265-V/12_11_147/", FakeAxisSite.Listing("/ftp/pub_soft/MPQT/P3265-V/12_11_147/", ("P3265-V_12_11_147.bin", "225.5M", null)));
        site.Set("/ftp/pub_soft/MPQT/P3265-V/12_11_118/", FakeAxisSite.Listing("/ftp/pub_soft/MPQT/P3265-V/12_11_118/", ("P3265-V_12_11_118.bin", "900K", FakeAxisSite.Sha("x"))));
        site.Set("/ftp/pub_soft/MPQT/P3265-V/11_11_238/", FakeAxisSite.Listing("/ftp/pub_soft/MPQT/P3265-V/11_11_238/", ("notes.html", "1.0M", FakeAxisSite.Sha("y"))));

        var model = (await Lookup(site, "P3265-V")).Models[0];

        Assert.Equal("12.11.77", model.Tracks[0].Newest); // 12.11.147 has no checksum, 12.11.118 is too small
        Assert.Equal("11.11.220", model.Tracks[1].Newest); // 11.11.238 has no .bin
        Assert.Equal("12.11.77", model.Latest); // ver.txt names a version that was left out
    }

    [Fact]
    public async Task No_answer_from_axis_com()
    {
        Assert.True((await Lookup(new FakeAxisSite { Down = true }, "P3265-V")).Unreachable);

        var site = new FakeAxisSite();
        site.Failing.Add("/ftp/pub_soft/MPQT/P3265-V/latest/");
        var model = Assert.Single((await Lookup(site, "P3265-V")).Models);
        Assert.True(model.Failed);
        Assert.False(model.IsListed);
    }

    [Fact]
    public async Task At_most_8_models_at_a_time()
    {
        var names = Enumerable.Range(1, 20).Select(i => "M" + (1000 + i)).ToList();
        var site = new FakeAxisSite(recorded: false) { ProductGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        site.Set("/ftp/pub_soft/MPQT/", FakeAxisSite.Folders("/ftp/pub_soft/MPQT/", names));
        site.Set("/ftp/pub_soft/PACS/", FakeAxisSite.Folders("/ftp/pub_soft/PACS/", []));

        var lookup = Lookup(site, [.. names]);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => site.InFlight == OnlineFirmwareLookup.MaxParallelModels, "8 models are read");
        Assert.Equal(OnlineFirmwareLookup.MaxParallelModels, site.MaxInFlight);
        site.ProductGate.SetResult();
        var result = await lookup;

        Assert.Equal(20, result.Models.Count);
        Assert.Equal(OnlineFirmwareLookup.MaxParallelModels, site.MaxInFlight);
        await Assert.ThrowsAsync<ArgumentException>(() => Lookup(site, [.. Enumerable.Range(0, 51).Select(i => "X" + i)]));
    }

    [Fact]
    public async Task One_version_gives_its_file()
    {
        var lookup = new OnlineFirmwareLookup(new FakeAxisSite());

        var file = await lookup.FindVersionAsync("MPQT", "P3265-V", "12.11.147", CancellationToken.None);
        Assert.NotNull(file);
        Assert.Equal("https://www.axis.com/ftp/pub_soft/MPQT/P3265-V/12_11_147/P3265-V_12_11_147.bin", file.Url);
        Assert.Equal(("P3265-V_12_11_147.bin", 236_453_888L), (file.FileName, file.Size));

        // Only in latest/: the latest file when ver.txt names the version.
        var latest = await lookup.FindVersionAsync("MPQT", "P1455-LE", "11.11.212", CancellationToken.None);
        Assert.Equal("https://www.axis.com/ftp/pub_soft/MPQT/P1455-LE/latest/P1455-LE.bin", latest!.Url);

        Assert.Null(await lookup.FindVersionAsync("MPQT", "P3265-V", "12.11.999", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => lookup.FindVersionAsync("MPQT", "../P3265-V", "12.11.147", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => lookup.FindVersionAsync("OTHER", "P3265-V", "12.11.147", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => lookup.FindVersionAsync("MPQT", "P3265-V", "12.11.147/../x", CancellationToken.None));
    }

    [Fact]
    public async Task The_queries_answer_json_and_never_touch_the_device()
    {
        var plugin = new FirmwareTaskPlugin(new FirmwareTaskTimings(), TimeProvider.System, new FakeAxisSite());
        var device = new FakeAxisDevice();
        var ctx = new QueryContext(device);

        var json = await plugin.QueryAsync(ctx, new FakeDevice(), FirmwareTaskPlugin.OnlineQuery, """{"models":["P3265-V"]}""", CancellationToken.None);
        var result = JsonSerializer.Deserialize<OnlineLookupResult>(json!, FirmwarePayload.JsonOptions)!;
        Assert.Equal("12.11.147", result.Models[0].Latest);

        var file = OnlineFileInfo.FromJson(await plugin.QueryAsync(ctx, new FakeDevice(), FirmwareTaskPlugin.OnlineVersionQuery, """{"source":"MPQT","product":"P3265-V","version":"11.11.238"}""", CancellationToken.None));
        Assert.Equal("P3265-V_11_11_238.bin", file!.FileName);
        Assert.Null(await plugin.QueryAsync(ctx, new FakeDevice(), FirmwareTaskPlugin.OnlineVersionQuery, """{"source":"MPQT","product":"P3265-V","version":"11.11.1"}""", CancellationToken.None));
        Assert.Empty(device.Methods);

        await Assert.ThrowsAsync<ArgumentException>(() => plugin.QueryAsync(ctx, new FakeDevice(), FirmwareTaskPlugin.OnlineQuery, "{", CancellationToken.None));
        var down = new FirmwareTaskPlugin(new FirmwareTaskTimings(), TimeProvider.System, new FakeAxisSite { Down = true });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => down.QueryAsync(ctx, new FakeDevice(), FirmwareTaskPlugin.OnlineVersionQuery, """{"source":"MPQT","product":"P3265-V","version":"11.11.238"}""", CancellationToken.None));
        Assert.Equal("axis.com could not be reached.", ex.Message);
        Assert.True(OnlineLookupResult.FromJson(await down.QueryAsync(ctx, new FakeDevice(), FirmwareTaskPlugin.OnlineQuery, """{"models":["P3265-V"]}""", CancellationToken.None))!.Unreachable);
    }
}
