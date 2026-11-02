using Oadm.Sdk.Plugins;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Firmware.Tests;

/// <summary>The task with a version from axis.com (README "Firmware online": Payload, Download firmware).</summary>
public sealed class OnlineTaskTests : IDisposable
{
    private static readonly FirmwareTaskTimings Fast = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(2),
        RestartTimeout = TimeSpan.FromSeconds(30),
        ProbeTimeout = TimeSpan.FromSeconds(30),
        CommitRetryDelay = TimeSpan.FromMilliseconds(1),
    };

    private readonly FakeAxisDownloads _downloads = new();
    private readonly FakeAxisSite _site = new();

    public void Dispose() => _downloads.Dispose();

    private FirmwareTaskPlugin Plugin() => new(Fast, TimeProvider.System, _site);

    private static string Online(string version = "12.11.147", int? fromMajor = 11, string product = "P3265-V") =>
        new FirmwarePayload { Online = [new FirmwareOnline("MPQT", product, version, fromMajor)] }.ToJson();

    private (RecordingContext Ctx, FakeAxisDevice Device) Setup(string version = "12.11.147")
    {
        var device = new FakeAxisDevice { NewVersion = version };
        var ctx = new RecordingContext(device, new FakeFiles()) { AxisDownloads = _downloads };
        return (ctx, device);
    }

    private Task Run(RecordingContext ctx, string payload) =>
        StepRun.RunAsync(ctx.Steps, () => Plugin().ExecuteAsync(ctx, new FakeDevice(), payload, CancellationToken.None));

    [Fact]
    public async Task Downloads_the_version_from_axis_com_and_uploads_that_file()
    {
        var (ctx, device) = Setup();
        var details = new List<string?>();
        _downloads.AfterProgress = _ => details.Add(StepRun.Detail(ctx.Steps, FirmwareTaskPlugin.Steps.Download));

        await Run(ctx, Online());

        Assert.Equal(
            ["Check compatibility: Done", "Read device info: Done", "Validate file: Done", "Download firmware: Done", "Read firmware status: Done", "Upload firmware: Done",
             "Install firmware: Done", "Wait for the device to come back: Done", "Verify version: Done", "Read commit state: Done", "Commit firmware: Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps));

        // The server read URL, size and SHA-256 from axis.com itself.
        var call = Assert.Single(_downloads.Calls);
        Assert.Equal(new Uri("https://www.axis.com/ftp/pub_soft/MPQT/P3265-V/12_11_147/P3265-V_12_11_147.bin"), call.Uri);
        Assert.Equal("abb2d5179defc9c13be2ae68aa1f6ae747942b3ad9c509dbc1a31be8026e2888", call.Sha256);
        Assert.Equal(236_453_888, call.Size);
        // Progress in MB of the listed size while it downloads (the fake file is 2 MB), then the result.
        Assert.Equal("0 of 225 MB", details[0]);
        Assert.Equal("2 MB from axis.com, checksum matches", StepRun.Detail(ctx.Steps, FirmwareTaskPlugin.Steps.Download));

        // The device got exactly the downloaded file, under the name from axis.com; the handle was released after the upload.
        Assert.Equal(_downloads.Content, device.UploadedBytes);
        Assert.Equal("P3265-V_12_11_147.bin", device.UploadedFileName);
        Assert.Equal(1, _downloads.Released);
        Assert.Contains("P3265-V_12_11_147.bin", StepRun.Detail(ctx.Steps, FirmwareTaskPlugin.Steps.ValidateFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_from_the_cache_says_so()
    {
        var (ctx, _) = Setup();
        _downloads.FromCache = true;

        await Run(ctx, Online());

        Assert.Equal("From the cache, checksum matches axis.com", StepRun.Detail(ctx.Steps, FirmwareTaskPlugin.Steps.Download));
    }

    [Fact]
    public async Task A_chosen_file_skips_the_download()
    {
        var device = new FakeAxisDevice();
        var files = new FakeFiles();
        var file = files.Add("P3265-V_12_11_77.bin", Fixture.Image());
        var ctx = new RecordingContext(device, files) { AxisDownloads = _downloads };

        await StepRun.RunAsync(ctx.Steps, () => Plugin().ExecuteAsync(ctx, new FakeDevice(), new FirmwarePayload { FileId = file.Id, FileName = file.Name }.ToJson(), CancellationToken.None));

        Assert.Equal("A file was chosen.", StepRun.Detail(ctx.Steps, FirmwareTaskPlugin.Steps.Download));
        Assert.Empty(_downloads.Calls);
        Assert.Empty(_site.Requests); // no axis.com page either
    }

    [Theory]
    [InlineData(true)] // signed out
    [InlineData(false)] // an older host without downloads
    public async Task Without_a_MyAxis_session_a_device_with_a_version_from_axis_com_is_refused_before_any_change(bool hostHasDownloads)
    {
        var (ctx, device) = Setup();
        _downloads.HasSession = false;
        ctx.AxisDownloads = hostHasDownloads ? _downloads : null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Online()));

        // Checked once the device's own choice is known (a device that installs a chosen file needs no sign-in).
        Assert.Equal("Sign in to MyAxis to download from axis.com. Nothing was changed.", ex.Message);
        Assert.Equal("Validate file: Failed", StepRun.Lines(ctx.Steps)[2]);
        Assert.DoesNotContain("upgrade", device.Methods);
        Assert.DoesNotContain("status", device.Methods);
        Assert.Empty(_downloads.Calls);
        Assert.Empty(_site.Requests); // axis.com is not asked either
    }

    [Theory]
    [InlineData("Your MyAxis sign-in has expired. Sign in to MyAxis again. Nothing was changed.", "Your MyAxis sign-in has expired. Sign in to MyAxis again. Nothing was changed.")]
    [InlineData("The download did not match the checksum on axis.com. Nothing was changed.", "The download did not match the checksum on axis.com. Nothing was changed.")]
    [InlineData("axis.com did not answer.", "axis.com did not answer. Nothing was changed.")]
    public async Task A_failed_download_ends_the_task_before_the_device_changes(string message, string expected)
    {
        var (ctx, device) = Setup();
        _downloads.Failure = new AxisDownloadException(message) { NeedsSignIn = message.Contains("sign-in", StringComparison.Ordinal) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Online()));

        Assert.Equal(expected, ex.Message);
        Assert.Equal("Download firmware: Failed", StepRun.Lines(ctx.Steps)[3]);
        Assert.Equal("Upload firmware: Skipped", StepRun.Lines(ctx.Steps)[5]);
        Assert.DoesNotContain("upgrade", device.Methods);
        Assert.DoesNotContain("status", device.Methods);
    }

    [Fact]
    public async Task The_handle_is_released_when_the_device_refuses_the_upload()
    {
        var (ctx, device) = Setup();
        device.UpgradeErrorCode = 421;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Online()));

        Assert.Equal(1, _downloads.Handed);
        Assert.Equal(1, _downloads.Released);
    }

    [Fact]
    public async Task A_version_no_longer_on_axis_com_is_refused_in_validate_file()
    {
        var (ctx, device) = Setup();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Online("12.11.999")));

        Assert.Equal("AXIS OS 12.11.999 for P3265-V is no longer on axis.com. Nothing was changed.", ex.Message);
        Assert.Equal("Validate file: Failed", StepRun.Lines(ctx.Steps)[2]);
        Assert.Empty(_downloads.Calls);
        Assert.DoesNotContain("upgrade", device.Methods);
    }

    [Fact]
    public async Task No_answer_from_axis_com_is_refused_in_validate_file()
    {
        var (ctx, _) = Setup();
        _site.Down = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Online()));

        Assert.Equal("axis.com could not be reached. Nothing was changed.", ex.Message);
    }

    [Fact]
    public async Task The_decision_table_applies_to_an_online_version()
    {
        // Same version: Done with warning, nothing downloaded.
        var (ctx, device) = Setup();
        device.Version = "12.11.147";
        await Run(ctx, Online(fromMajor: 12));
        Assert.Contains("Already up to date", ctx.Steps.Snapshot()[2].Detail, StringComparison.Ordinal);
        Assert.Equal("Download firmware: Skipped", StepRun.Lines(ctx.Steps)[3]);
        Assert.Empty(_downloads.Calls);

        // Older without Allow downgrade: refused before the download.
        (ctx, device) = Setup();
        device.Version = "12.11.147";
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Online("11.11.238", fromMajor: 12)));
        Assert.Contains("Downgrade 12.11.147 → 11.11.238", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_downloads.Calls);
    }

    [Fact]
    public async Task A_device_of_another_track_or_model_has_no_file()
    {
        var (ctx, device) = Setup();
        device.Version = "10.12.300"; // the row was for devices on 11

        await Run(ctx, Online(fromMajor: 11));

        Assert.Equal("Validate file: Warning", StepRun.Lines(ctx.Steps)[2]);
        Assert.Equal(FirmwareCompatibility.NoFileForModel, StepRun.Detail(ctx.Steps, FirmwareTaskPlugin.Steps.Download));
        Assert.Empty(_downloads.Calls);
    }

    [Fact]
    public async Task Mixed_payload_gives_each_device_its_version_or_file()
    {
        var files = new FakeFiles();
        var q = files.Add("Q6135-LE_12_11_77.bin", Fixture.Image(seed: 3));
        var payload = new FirmwarePayload { FileId = q.Id, FileName = q.Name, Online = [new FirmwareOnline("MPQT", "P3265-V", "12.11.147", 11)] }.ToJson();

        var p = new FakeAxisDevice { NewVersion = "12.11.147" };
        var pctx = new RecordingContext(p, files) { AxisDownloads = _downloads };
        await StepRun.RunAsync(pctx.Steps, () => Plugin().ExecuteAsync(pctx, new FakeDevice(), payload, CancellationToken.None));
        Assert.Equal(_downloads.Content, p.UploadedBytes);

        var qdev = new FakeAxisDevice { ProdNbr = "Q6135-LE", NewVersion = "12.11.77" };
        var qctx = new RecordingContext(qdev, files) { AxisDownloads = _downloads };
        await StepRun.RunAsync(qctx.Steps, () => Plugin().ExecuteAsync(qctx, new FakeDevice(model: "Q6135-LE"), payload, CancellationToken.None));
        Assert.Equal(Fixture.Image(seed: 3), qdev.UploadedBytes);
        Assert.Equal("A file was chosen.", StepRun.Detail(qctx.Steps, FirmwareTaskPlugin.Steps.Download));
        Assert.Single(_downloads.Calls);
    }

    [Fact]
    public async Task A_version_that_names_the_device_is_its_own_and_must_be_for_its_model()
    {
        var device = new FakeDevice();
        var (ctx, fake) = Setup("11.11.220");
        var payload = new FirmwarePayload
        {
            Online = [new FirmwareOnline("MPQT", "P3265-V", "12.11.147", 11), new FirmwareOnline("MPQT", "P3265-V", "11.11.220", null, [device.Id])],
        }.ToJson();
        _site.MakeVersionFolders = true;

        await StepRun.RunAsync(ctx.Steps, () => Plugin().ExecuteAsync(ctx, device, payload, CancellationToken.None));

        Assert.Equal(new Uri("https://www.axis.com/ftp/pub_soft/MPQT/P3265-V/11_11_220/P3265-V_11_11_220.bin"), Assert.Single(_downloads.Calls).Uri);
        Assert.Equal("P3265-V_11_11_220.bin", fake.UploadedFileName);

        // An entry for another product that names the device is refused before anything is read or written.
        var (other, otherDevice) = Setup();
        var wrong = new FirmwarePayload { Online = [new FirmwareOnline("MPQT", "Q6135-LE", "12.11.147", null, [device.Id])] }.ToJson();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StepRun.RunAsync(other.Steps, () => Plugin().ExecuteAsync(other, device, wrong, CancellationToken.None)));
        Assert.Equal("The file is for Q6135-LE, the device is P3265-V. Nothing was changed.", ex.Message);
        Assert.DoesNotContain("upgrade", otherDevice.Methods);
        Assert.Single(_downloads.Calls);
    }

    [Fact]
    public async Task The_upload_announces_the_real_length_of_the_download_never_the_listed_size()
    {
        // axis.com lists 225.5M (rounded); the downloaded file has its own real length (here 2 MB).
        var (ctx, device) = Setup();
        _downloads.Content = Fixture.Image(size: 2 * 1024 * 1024 + 123);

        await Run(ctx, Online());

        Assert.Equal(236_453_888, Assert.Single(_downloads.Calls).Size); // the listed size goes to the host only
        Assert.Equal(_downloads.Content, device.UploadedBytes); // all of it, nothing announced that was not sent
        Assert.StartsWith("2 MB, device accepted", StepRun.Detail(ctx.Steps, FirmwareTaskPlugin.Steps.Upload), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("changed", "the firmware file changed while it was sent")]
    [InlineData("timeout", "the device did not answer in time")]
    [InlineData("reset", "the connection to the device was lost")]
    [InlineData("other", "the connection to the device was lost")]
    public void An_interrupted_upload_says_why_in_plain_words(string kind, string expected)
    {
        Exception ex = kind switch
        {
            "changed" => new HttpRequestException("Error while copying content to a stream.", new FirmwareFileChangedException("The firmware file is shorter than announced; it changed during the upload.")),
            "timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 1200 seconds elapsing.", new TimeoutException()),
            "reset" => new HttpRequestException("Error while copying content to a stream.", new IOException("Unable to write data to the transport connection.", new System.Net.Sockets.SocketException(10054))),
            _ => new IOException("Unable to read data from the transport connection."),
        };

        Assert.Equal(expected, FirmwareTaskPlugin.UploadFailure(ex));
    }

    [Fact]
    public void The_payload_carries_versions_and_keeps_the_old_shape()
    {
        var online = FirmwarePayload.Parse(Online());
        Assert.Empty(online.AllFiles());
        Assert.Equal(new FirmwareOnline("MPQT", "P3265-V", "12.11.147", 11), Assert.Single(online.AllOnline()));
        Assert.Equal("Upgrade firmware to 12.11.147", online.TaskName());

        var old = FirmwarePayload.Parse("""{"fileId":"abc","fileName":"P3265-V_12_11_77.bin","factoryDefaultMode":"none","allowDowngrade":false}""");
        Assert.Equal("abc", Assert.Single(old.AllFiles()).FileId);
        Assert.Empty(old.AllOnline());

        Assert.Throws<ArgumentException>(() => FirmwarePayload.Parse("""{"online":[]}"""));
        Assert.Throws<ArgumentException>(() => FirmwarePayload.Parse("""{"online":[{"source":"MPQT","product":"../x","version":"12.11.147"}]}"""));
        Assert.Throws<ArgumentException>(() => FirmwarePayload.Parse("""{"online":[{"source":"FTP","product":"P3265-V","version":"12.11.147"}]}"""));
        Assert.Throws<ArgumentException>(() => FirmwarePayload.Parse("""{"online":[{"source":"MPQT","product":"P3265-V","version":"latest"}]}"""));
        Assert.Equal([FirmwarePermissions.Upgrade], new FirmwareTaskPlugin().RequiredPermissions(Online()));
    }
}
