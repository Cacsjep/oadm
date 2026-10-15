using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;

using Oadm.Plugins.DeviceConfig.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>The import dialog's view model: one file, a ZIP, confirmation, upload and payload.</summary>
public sealed class ImportDialogTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("oadm-config-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    internal sealed class NoPicker : IConfigFileSource
    {
        public Task<string?> PickAsync() => Task.FromResult<string?>(null);
    }

    internal static string FileFor(string model, string? recipients = null)
    {
        var export = (JsonObject)JsonNode.Parse(Fixtures.Export)!;
        export["metadata"]!["prodNbr"] = model;
        if (recipients is not null)
        {
            export["data"]!["action-rules.v2"]!["recipients"] = new JsonArray(new JsonObject { ["id"] = recipients });
        }

        return export.ToJsonString();
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Zip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_folder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }

        return path;
    }

    internal static ImportConfigViewModel Dialog(IReadOnlyList<IDeviceInfo> devices, List<string>? uploads = null, bool confirm = true, List<string>? questions = null) =>
        new(devices, new NoPicker(),
            (path, progress, _) =>
            {
                uploads?.Add(path);
                progress.Report(1);
                return Task.FromResult(new UploadedFile("up1", Path.GetFileName(path), 1, "00"));
            },
            (_, message, _) =>
            {
                questions?.Add(message);
                return Task.FromResult(confirm);
            });

    [Fact]
    public async Task One_file_goes_to_the_devices_of_its_model()
    {
        IDeviceInfo[] devices =
        [
            new FakeDevice { Address = "10.0.0.48" },
            new FakeDevice { Address = "10.0.0.49", Serial = "B8A44F631340" },
            new FakeDevice { Address = "10.0.0.50", Serial = "B8A44F631341", Model = "AXIS Q6135-LE" },
        ];
        var uploads = new List<string>();
        var questions = new List<string>();
        var vm = Dialog(devices, uploads, questions: questions);
        Assert.Equal("Choose a configuration file", vm.ImportBlockedReason);

        var path = Write("lobby.json", FileFor("P3265-V", recipients: "r1"));
        await vm.LoadAsync(path);

        Assert.False(vm.IsZip);
        Assert.Equal("From AXIS P3265-V, AXIS OS 12.11.77", vm.FileDetails);
        Assert.Equal(8, vm.Sections.Count);
        Assert.StartsWith("Network and system settings (", vm.Sections[3].Text, StringComparison.Ordinal);
        Assert.Equal(
            [ImportConfigViewModel.IdentityNote, ImportConfigViewModel.PasswordNote, "1 of 3 devices are other models and are left out."],
            vm.Notes);
        Assert.Equal(2, vm.TargetCount);
        Assert.Null(vm.ImportBlockedReason);

        // Unchecking every area blocks Import.
        foreach (var section in vm.Sections)
        {
            section.IsChecked = false;
        }

        Assert.Equal("Choose at least one settings area", vm.ImportBlockedReason);
        Assert.False(vm.ImportCommand.CanExecute(null));
        vm.Sections[5].IsChecked = true;

        string? closed = null;
        vm.CloseRequested += (_, payload) => closed = payload;
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["Import the configuration into 2 devices? The settings in the file replace the current ones."], questions);
        Assert.Equal([path], uploads);
        var payload = ImportPayload.Parse(closed);
        Assert.Equal(("up1", "lobby.json", false), (payload.FileId, payload.FileName, payload.IsZip));
        Assert.Equal(["time.v2"], payload.Sections);
        Assert.Equal(new[] { devices[0].Id, devices[1].Id }.Order(), payload.Files.Keys.Order());
        Assert.All(payload.Files.Values, v => Assert.Equal(string.Empty, v));
        Assert.Equal("Other model: the file is for AXIS P3265-V", payload.LeftOut[devices[2].Id]);
    }

    [Fact]
    public async Task Connection_settings_are_off_by_default_and_warned_about()
    {
        var questions = new List<string>();
        var vm = Dialog([new FakeDevice()], questions: questions);
        await vm.LoadAsync(Write("lobby.json", FileFor("P3265-V")));

        var connection = vm.Sections[^1];
        Assert.Equal(("Connection settings (10 values)", false), (connection.Text, connection.IsChecked));
        Assert.All(vm.Sections.SkipLast(1), s => Assert.True(s.IsChecked));
        Assert.True(vm.HasConnectionSettings);

        // Left out: the plain question, no connection area in the payload.
        string? closed = null;
        vm.CloseRequested += (_, payload) => closed = payload;
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Equal("Import the configuration into 1 device? The settings in the file replace the current ones.", questions[^1]);
        Assert.DoesNotContain(ConfigSections.Connection, ImportPayload.Parse(closed).Sections);

        // Chosen: the question warns, and the area goes last.
        var chosen = Dialog([new FakeDevice()], questions: questions);
        await chosen.LoadAsync(Write("lobby2.json", FileFor("P3265-V")));
        chosen.Sections[^1].IsChecked = true;
        chosen.CloseRequested += (_, payload) => closed = payload;
        await chosen.ImportCommand.ExecuteAsync(null);
        Assert.Equal("Import the configuration into 1 device? The settings in the file replace the current ones. The devices may become unreachable.", questions[^1]);
        Assert.Equal(ConfigSections.Connection, ImportPayload.Parse(closed).Sections[^1]);

        // A ZIP lists them last too, unchecked.
        var zip = Dialog([new FakeDevice()]);
        await zip.LoadAsync(Zip("site.zip", ("10.0.0.48_B8A44F631339_P3265-V_config.json", FileFor("P3265-V"))));
        Assert.Equal((ConfigSections.Connection, false), (zip.Sections[^1].Key, zip.Sections[^1].IsChecked));
    }

    [Fact]
    public async Task No_upload_without_confirmation()
    {
        var uploads = new List<string>();
        var vm = Dialog([new FakeDevice()], uploads, confirm: false);
        await vm.LoadAsync(Write("lobby.json", FileFor("P3265-V")));
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Empty(uploads);
        Assert.Null(vm.Result);
    }

    [Fact]
    public async Task A_file_of_another_model_or_a_broken_file_blocks_import()
    {
        var vm = Dialog([new FakeDevice()]);
        await vm.LoadAsync(Write("other.json", FileFor("Q6135-LE")));
        Assert.Equal("No selected device is of the file's model", vm.ImportBlockedReason);
        Assert.Contains("None of the selected devices is an AXIS Q6135-LE.", vm.Notes);

        await vm.LoadAsync(Write("broken.json", "{"));
        Assert.Equal(DeviceConfigIds.NotAConfigFile, vm.FileError);
        Assert.Equal(DeviceConfigIds.NotAConfigFile, vm.ImportBlockedReason);
        Assert.Empty(vm.Sections);

        await vm.LoadAsync(Write("broken.zip", "not a zip"));
        Assert.Equal("Not a ZIP file", vm.FileError);
    }

    [Fact]
    public async Task A_zip_gives_each_device_its_own_file_by_mac_address()
    {
        IDeviceInfo[] devices =
        [
            new FakeDevice { Address = "10.0.0.48", Serial = "B8A44F631339" },
            new FakeDevice { Address = "10.0.0.49", Serial = "B8A44F631340" },
            new FakeDevice { Address = "10.0.0.50", Serial = "B8A44F631341" },
            new FakeDevice { Address = "10.0.0.51", Serial = "B8A44F631342" },
            new FakeDevice { Address = "10.0.0.52", Serial = "B8A44F631343" },
        ];
        var path = Zip("site.zip",
            ("10.0.0.48_B8A44F631339_P3265-V_config.json", FileFor("P3265-V")),
            ("b8:a4:4f:63:13:40.json", FileFor("Q6135-LE")),
            ("B8A44F631341-a.json", FileFor("P3265-V")),
            ("B8A44F631341-b.json", FileFor("P3265-V")),
            ("axis-b8a44f631342.json", "not json"),
            ("10.0.0.99_ACCC8E000000_P3265-V_config.json", FileFor("P3265-V")),
            ("summary.txt", "ignored"));
        var vm = Dialog(devices);
        await vm.LoadAsync(path);

        Assert.True(vm.IsZip);
        Assert.Equal("6 configuration files", vm.FileDetails);
        Assert.Equal(
        [
            "Ready",
            "Other model: the file is for AXIS Q6135-LE",
            DeviceConfigIds.TwoFiles,
            DeviceConfigIds.NotAConfigFile,
            DeviceConfigIds.NoFile,
        ], vm.Rows.Select(r => r.Status));
        Assert.Equal("10.0.0.48_B8A44F631339_P3265-V_config.json", vm.Rows[0].FileText);
        Assert.Equal("1 ready · 4 left out · 1 file has no selected device", vm.DevicesSummary);
        Assert.Equal(8, vm.Sections.Count);
        Assert.All(vm.Sections, s => Assert.Null(s.Detail));

        string? closed = null;
        vm.CloseRequested += (_, payload) => closed = payload;
        await vm.ImportCommand.ExecuteAsync(null);
        var payload = ImportPayload.Parse(closed);
        Assert.True(payload.IsZip);
        Assert.Equal("10.0.0.48_B8A44F631339_P3265-V_config.json", payload.Files[devices[0].Id]);
        Assert.Single(payload.Files);
        Assert.Equal(DeviceConfigIds.NoFile, payload.LeftOut[devices[4].Id]);
    }

    [Fact]
    public async Task A_zip_without_a_file_for_any_selected_device_blocks_import()
    {
        var vm = Dialog([new FakeDevice()]);
        await vm.LoadAsync(Zip("other.zip", ("10.0.0.99_ACCC8E000000_P3265-V_config.json", FileFor("P3265-V"))));
        Assert.Equal("No selected device has a file in the ZIP", vm.ImportBlockedReason);

        await vm.LoadAsync(Zip("empty.zip", ("readme.txt", "x")));
        Assert.Equal("The ZIP has no configuration files", vm.FileError);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Five_thousand_devices_are_matched_in_one_pass()
    {
        // A ZIP holds at most DeviceConfigIds.MaxZipEntries (5,000) configuration files, so this stays at 5,000 devices.
        var devices = Enumerable.Range(0, DeviceConfigIds.MaxZipEntries).Select(i => (IDeviceInfo)new FakeDevice
        {
            Address = $"10.0.{i / 256}.{i % 256}",
            Serial = $"B8A44F{i:X6}",
        }).ToList();
        var content = FileFor("P3265-V");
        var path = Zip("big.zip", [.. devices.Select(d => (ConfigFileNames.ForDevice(d.Address, d.Serial, d.Model), content))]);
        var vm = Dialog(devices);
        var watch = Stopwatch.StartNew();
        await vm.LoadAsync(path);
        output.WriteLine($"5,000 files read and matched: {watch.ElapsedMilliseconds} ms");

        Assert.Equal(DeviceConfigIds.MaxZipEntries, vm.TargetCount);
        Assert.Equal("5,000 ready", vm.DevicesSummary);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
    }
}
