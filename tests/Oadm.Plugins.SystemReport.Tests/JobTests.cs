using System.IO.Compression;
using System.Net;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.SystemReport.Tests;

/// <summary>The job engine: bundle content and summary, failures, parallelism, status deltas, chunked read and cleanup.</summary>
public sealed class JobTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 58, 56, TimeSpan.Zero);

    private readonly TempFolder _folder = new();
    private readonly FakeVapixFactory _vapix = new();
    private readonly FakeRepository _devices = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _folder.Dispose();

    private SystemReportJobs Create(TimeSpan? timeout = null, int parallelism = SystemReportPluginInfo.Parallelism) =>
        new(_devices, new ServerReportDownloader(_vapix, timeout), _folder.Path, "1.2.3", _time, parallelism: parallelism);

    private FakeDevice AddDevice(string address, string serial, string model = "AXIS P3265-V", DeviceStatus status = DeviceStatus.Ok, DeviceCategory category = DeviceCategory.Camera)
    {
        var device = new FakeDevice { Address = address, Serial = serial, Model = model, Status = status, Category = category };
        _devices.Devices.Add(device);
        _vapix.Add(device.Id);
        return device;
    }

    private static async Task<JobStatus> RunToEndAsync(SystemReportJobs jobs, JobStatus status)
    {
        await jobs.WaitAsync(status.JobId).WaitAsync(TimeSpan.FromSeconds(30));
        return jobs.Status(status.JobId, 0);
    }

    private static async Task<byte[]> ReadAllAsync(SystemReportJobs jobs, string jobId)
    {
        using var bundle = new MemoryStream();
        ReportChunk chunk;
        var calls = 0;
        do
        {
            chunk = await jobs.ReadAsync(jobId, bundle.Length, CancellationToken.None);
            bundle.Write(Convert.FromBase64String(chunk.DataBase64));
            calls++;
        }
        while (!chunk.Eof);

        Assert.Equal(calls, Math.Max(1, (int)((chunk.Total + SystemReportPluginInfo.ChunkBytes - 1) / SystemReportPluginInfo.ChunkBytes)));
        return bundle.ToArray();
    }

    [Fact]
    public async Task One_zip_holds_every_report_and_a_summary_with_the_failures()
    {
        var camera = AddDevice("10.0.0.48", "B8A44F631339");
        var speaker = AddDevice("10.0.0.50", "ACCC8E000001", "AXIS C1310-E", category: DeviceCategory.Speaker);
        var locked = AddDevice("10.0.0.51", "ACCC8E000002", status: DeviceStatus.CredentialsRequired);
        var broken = AddDevice("fe80::1", "ACCC8E000003");
        _vapix.Cameras[broken.Id].Handler = (_, _) => Task.FromResult(FakeCamera.Text(HttpStatusCode.Unauthorized, ""));
        var removed = Guid.NewGuid();
        using var jobs = Create();

        var started = await jobs.StartAsync(new StartRequest { DeviceIds = [camera.Id, speaker.Id, locked.Id, broken.Id, removed, camera.Id] }, CancellationToken.None);
        Assert.Equal(5, started.Total);
        Assert.Equal(5, started.Devices.Count);
        Assert.Equal(["10.0.0.48", "10.0.0.50", "10.0.0.51", "fe80::1", ""], started.Devices.Select(d => d.Address).ToArray());

        var status = await RunToEndAsync(jobs, started);
        Assert.Equal(JobStates.Done, status.State);
        Assert.Equal((5, 3), (status.Finished, status.Failed));
        Assert.Equal(
            [DeviceReportStates.Done, DeviceReportStates.Done, DeviceReportStates.Failed, DeviceReportStates.Failed, DeviceReportStates.Failed],
            status.Devices.Select(d => d.State).ToArray());
        Assert.Equal("Credentials required - the device rejects the stored credentials", status.Devices[2].Error);
        Assert.Equal("Unauthorized - HTTP 401", status.Devices[3].Error);
        Assert.Equal("The device is no longer managed", status.Devices[4].Error);
        Assert.Empty(_vapix.Cameras[locked.Id].Requests);

        var bytes = await ReadAllAsync(jobs, status.JobId);
        Assert.Equal(status.Size, bytes.Length);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(
            ["10.0.0.48_B8A44F631339_P3265-V_20261008-095856.zip", "10.0.0.50_ACCC8E000001_C1310-E_20261008-095856.zip", "summary.txt"],
            zip.Entries.Select(e => e.FullName).ToArray());
        Assert.Equal("10.0.0.48_B8A44F631339_P3265-V_20261008-095856.zip", status.Devices[0].FileName);

        // Each report is stored as the device sent it.
        using (var inner = new ZipArchive(zip.Entries[0].Open()))
        {
            Assert.Equal(["serverreport_cgi.txt", "serverreport_image.jpg"], inner.Entries.Select(e => e.FullName).ToArray());
        }

        using var reader = new StreamReader(zip.GetEntry("summary.txt")!.Open(), Encoding.UTF8);
        var summary = await reader.ReadToEndAsync();
        Assert.Contains("Created: 2026-10-08 09:58:56 UTC by OADM 1.2.3", summary, StringComparison.Ordinal);
        Assert.Contains("Devices: 5, reports: 2, failed: 3", summary, StringComparison.Ordinal);
        Assert.Contains("10.0.0.48_B8A44F631339_P3265-V_20261008-095856.zip  10.0.0.48  B8A44F631339  AXIS P3265-V", summary, StringComparison.Ordinal);
        Assert.Contains("mode zip_with_image", summary, StringComparison.Ordinal);
        Assert.Contains("mode zip\r\n", summary, StringComparison.Ordinal);
        Assert.Contains("fe80::1  ACCC8E000003  AXIS P3265-V  Unauthorized - HTTP 401", summary, StringComparison.Ordinal);

        // The per-device files are gone once bundled.
        Assert.Equal(["bundle.zip"], Directory.GetFiles(Path.Combine(_folder.Path, status.JobId)).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public async Task At_most_four_devices_download_at_the_same_time()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var device = AddDevice($"10.0.1.{i + 1}", $"ACCC8E0001{i:00}");
            _vapix.Cameras[device.Id].Delay = TimeSpan.FromMilliseconds(80);
            ids.Add(device.Id);
        }

        using var jobs = Create();
        var status = await RunToEndAsync(jobs, await jobs.StartAsync(new StartRequest { DeviceIds = ids }, CancellationToken.None));

        Assert.Equal((JobStates.Done, 0), (status.State, status.Failed));
        Assert.Equal(4, jobs.MaxConcurrentDownloads);
    }

    [Fact]
    public async Task Status_lists_only_the_devices_that_changed()
    {
        var gate = new TaskCompletionSource();
        var fast = AddDevice("10.0.0.1", "ACCC8E000010");
        var slow = AddDevice("10.0.0.2", "ACCC8E000011");
        _vapix.Cameras[slow.Id].Handler = async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return FakeCamera.Zip(FakeCamera.Report(true));
        };
        using var jobs = Create();
        var started = await jobs.StartAsync(new StartRequest { DeviceIds = [fast.Id, slow.Id] }, CancellationToken.None);

        JobStatus status;
        do
        {
            await Task.Delay(10);
            status = jobs.Status(started.JobId, started.Version);
        }
        while (status.Devices.All(d => d.State != DeviceReportStates.Done));

        Assert.All(status.Devices, d => Assert.Contains(d.DeviceId, new[] { fast.Id, slow.Id }));
        Assert.Equal(JobStates.Running, status.State);
        var unchanged = jobs.Status(started.JobId, jobs.Status(started.JobId, 0).Version);
        Assert.Empty(unchanged.Devices);
        Assert.Throws<InvalidOperationException>(() => jobs.ReadAsync(started.JobId, 0, CancellationToken.None).GetAwaiter().GetResult());

        gate.SetResult();
        status = await RunToEndAsync(jobs, started);
        Assert.Equal(JobStates.Done, status.State);
    }

    [Fact]
    public async Task A_bundle_larger_than_a_chunk_is_read_in_pieces()
    {
        var a = AddDevice("10.0.0.1", "ACCC8E000020");
        var b = AddDevice("10.0.0.2", "ACCC8E000021");
        _vapix.Cameras[a.Id].Padding = 3 * 1024 * 1024;
        _vapix.Cameras[b.Id].Padding = 2 * 1024 * 1024;
        using var jobs = Create();
        var status = await RunToEndAsync(jobs, await jobs.StartAsync(new StartRequest { DeviceIds = [a.Id, b.Id] }, CancellationToken.None));

        var bytes = await ReadAllAsync(jobs, status.JobId);

        Assert.True(bytes.Length > 5 * 1024 * 1024);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(3, zip.Entries.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => jobs.ReadAsync(status.JobId, bytes.Length + 1, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_cancels_running_downloads_and_removes_the_files()
    {
        var device = AddDevice("10.0.0.1", "ACCC8E000030");
        _vapix.Cameras[device.Id].Delay = TimeSpan.FromMinutes(5);
        using var jobs = Create();
        var started = await jobs.StartAsync(new StartRequest { DeviceIds = [device.Id] }, CancellationToken.None);
        var worker = jobs.WaitAsync(started.JobId);
        var folder = Path.Combine(_folder.Path, started.JobId);
        Assert.True(Directory.Exists(folder));

        jobs.Delete(started.JobId);
        await worker.WaitAsync(TimeSpan.FromSeconds(10));

        await WaitUntilAsync(() => !Directory.Exists(folder));
        Assert.Throws<KeyNotFoundException>(() => jobs.Status(started.JobId, 0));
        Assert.Equal(0, jobs.JobCount);
    }

    [Fact]
    public async Task Jobs_are_dropped_30_minutes_after_their_last_use()
    {
        var device = AddDevice("10.0.0.1", "ACCC8E000040");
        using var jobs = Create();
        var status = await RunToEndAsync(jobs, await jobs.StartAsync(new StartRequest { DeviceIds = [device.Id] }, CancellationToken.None));
        var folder = Path.Combine(_folder.Path, status.JobId);

        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(JobStates.Done, jobs.Status(status.JobId, 0).State); // a use renews the lifetime
        _time.Advance(TimeSpan.FromMinutes(31)); // the sweeper timer runs every minute

        await WaitUntilAsync(() => jobs.JobCount == 0 && !Directory.Exists(folder));
        Assert.Throws<KeyNotFoundException>(() => jobs.Status(status.JobId, 0));
    }

    [Fact]
    public async Task Stopping_removes_every_job_and_leftovers_of_a_crash_are_removed_at_start()
    {
        Directory.CreateDirectory(Path.Combine(_folder.Path, "old-job"));
        await File.WriteAllTextAsync(Path.Combine(_folder.Path, "old-job", "0.zip"), "left over");
        var device = AddDevice("10.0.0.1", "ACCC8E000050");
        _vapix.Cameras[device.Id].Delay = TimeSpan.FromMinutes(5);

        var jobs = Create();
        Assert.False(Directory.Exists(Path.Combine(_folder.Path, "old-job")));
        await jobs.StartAsync(new StartRequest { DeviceIds = [device.Id] }, CancellationToken.None);

        jobs.Dispose();

        Assert.False(Directory.Exists(_folder.Path));
    }

    [Fact]
    public async Task A_job_needs_one_to_5000_devices()
    {
        using var jobs = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => jobs.StartAsync(new StartRequest(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => jobs.StartAsync(new StartRequest { DeviceIds = [.. Enumerable.Range(0, 5001).Select(_ => Guid.NewGuid())] }, CancellationToken.None));
        Assert.Throws<KeyNotFoundException>(() => jobs.Status("missing", 0));
    }

    [Fact]
    public async Task Five_thousand_devices_start_at_once_and_report_their_changes_compactly()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5000; i++)
        {
            var device = new FakeDevice { Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}", Serial = $"ACCC8E{i:X6}", Status = DeviceStatus.CredentialsRequired };
            _devices.Devices.Add(device);
            ids.Add(device.Id);
        }

        using var jobs = Create();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var started = await jobs.StartAsync(new StartRequest { DeviceIds = ids }, CancellationToken.None);
        var status = await RunToEndAsync(jobs, started);
        watch.Stop();

        Assert.Equal((JobStates.Done, 5000, 5000), (status.State, status.Finished, status.Failed));
        Assert.Empty(jobs.Status(started.JobId, status.Version).Devices);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"5,000 refused devices took {watch.Elapsed}");
    }

    [Fact]
    public async Task The_plugin_has_no_page_needs_the_operator_role_and_audits_the_start()
    {
        using var plugin = new SystemReportPlugin();
        ICorePlugin core = plugin;
        Assert.False(core.HasPage);
        Assert.Equal(UserRole.Operator, core.RequiredRole(SystemReportMethods.Start));
        Assert.Equal(UserRole.Operator, core.RequiredRole(SystemReportMethods.Read));
        Assert.True(core.IsAudited(SystemReportMethods.Start));
        Assert.False(core.IsAudited(SystemReportMethods.Status));
        Assert.False(core.IsAudited(SystemReportMethods.Read));
        Assert.Empty(core.TaskPlugins);
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.InvokeAsync(SystemReportMethods.Start, "{}", CancellationToken.None));
    }

    [Fact]
    public async Task The_plugin_routes_the_methods_with_json()
    {
        var device = AddDevice("10.0.0.48", "B8A44F631339");
        using var plugin = new SystemReportPlugin();
        await plugin.StartAsync(new PluginContext(_devices, _vapix, _folder.Path), CancellationToken.None);

        var started = SystemReportJson.Deserialize<JobStatus>(await plugin.InvokeAsync(SystemReportMethods.Start, SystemReportJson.Serialize(new StartRequest { DeviceIds = [device.Id] }), CancellationToken.None));
        Assert.True(Directory.Exists(Path.Combine(_folder.Path, "jobs", started.JobId)));
        await plugin.Jobs!.WaitAsync(started.JobId);
        var status = SystemReportJson.Deserialize<JobStatus>(await plugin.InvokeAsync(SystemReportMethods.Status, SystemReportJson.Serialize(new StatusRequest { JobId = started.JobId }), CancellationToken.None));
        Assert.Equal(JobStates.Done, status.State);
        var chunk = SystemReportJson.Deserialize<ReportChunk>(await plugin.InvokeAsync(SystemReportMethods.Read, SystemReportJson.Serialize(new ReadRequest { JobId = started.JobId }), CancellationToken.None));
        Assert.True(chunk.Eof);
        Assert.Equal(status.Size, Convert.FromBase64String(chunk.DataBase64).Length);
        Assert.Null(await plugin.InvokeAsync(SystemReportMethods.Delete, SystemReportJson.Serialize(new JobRequest { JobId = started.JobId }), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync("nope", null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync(SystemReportMethods.Start, "{not json", CancellationToken.None));

        await plugin.StopAsync(CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(_folder.Path, "jobs")));
    }

    [Fact]
    public void File_names_are_safe_on_every_os()
    {
        Assert.Equal("fe80--1_ACCC8E000003_P3265-V_20261008-095856.zip", ReportBundle.FileName("fe80::1", "ACCC8E000003", "AXIS P3265-V", Now));
        Assert.Equal("cam.example.com_ACCC8E000003_20261008-095856.zip", ReportBundle.FileName("cam.example.com", "ACCC8E000003", null, Now));
        Assert.Equal("10.0.0.1_X_Q1798-LE-Radar-2_20261008-095856.zip", ReportBundle.FileName("10.0.0.1", "X", "AXIS Q1798-LE Radar/2", Now));
        Assert.Equal("oadm-system-reports-2026-10-08.zip", ReportBundle.DefaultBundleName(new DateTime(2026, 10, 8, 23, 0, 0, DateTimeKind.Local)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(20);
        }
    }
}
