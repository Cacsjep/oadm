using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;

using Oadm.Plugins.DeviceConfig.Client;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>The export query and the export window's view model.</summary>
public sealed class ExportTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_query_returns_the_device_answer_unchanged()
    {
        var camera = new ConfigCamera();
        var reply = ExportReply.Parse(await new ExportConfigTask().QueryAsync(new QueryContext(camera), new FakeDevice(), DeviceConfigIds.ExportQuery, null, CancellationToken.None));

        Assert.Null(reply.Error);
        var file = ConfigFile.Parse(reply.Text!);
        Assert.Equal("P3265-V", file.Model);
        Assert.Equal(["GET /config/rest/$export"], camera.Requests);
    }

    [Fact]
    public async Task The_query_reports_a_turned_off_feature_and_refuses_bad_statuses_without_a_request()
    {
        var camera = new ConfigCamera { ConfigStatus = HttpStatusCode.NotFound };
        var task = new ExportConfigTask();
        Assert.Equal(DeviceConfigIds.FeatureOff, ExportReply.Parse(await task.QueryAsync(new QueryContext(camera), new FakeDevice(), DeviceConfigIds.ExportQuery, null, CancellationToken.None)).Error);

        camera.Requests.Clear();
        var reply = ExportReply.Parse(await task.QueryAsync(new QueryContext(camera), new FakeDevice { Status = DeviceStatus.CertificateChanged }, DeviceConfigIds.ExportQuery, null, CancellationToken.None));
        Assert.Equal("The device's certificate changed since it was added", reply.Error);
        Assert.Empty(camera.Requests);
        await Assert.ThrowsAsync<ArgumentException>(() => task.QueryAsync(new QueryContext(camera), new FakeDevice(), "other", null, CancellationToken.None));
    }

    [Fact]
    public async Task One_device_saves_its_json_file()
    {
        var device = new FakeDevice();
        var vm = new ExportConfigViewModel((_, _) => Task.FromResult<string?>(new ExportReply { Text = Fixtures.Export }.ToJson()), [device]);
        Assert.False(vm.IsZip);
        Assert.Equal("10.0.0.48_B8A44F631339_P3265-V_config.json", vm.SuggestedFileName);

        using var file = new MemoryStream();
        await vm.RunAsync(new ExportTarget(() => Task.FromResult<Stream>(new KeepOpenStream(file)), "C:\\x.json"));

        Assert.Equal(Fixtures.Export, Encoding.UTF8.GetString(file.ToArray()));
        Assert.Equal("Configuration saved.", vm.ResultText);
        Assert.Equal("Saved C:\\x.json", vm.ProgressText);
        Assert.Equal(("Done", true), (vm.Rows[0].StateText, vm.Rows[0].IsOk));
    }

    [Fact]
    public async Task A_failed_single_export_writes_no_file()
    {
        var opened = false;
        var vm = new ExportConfigViewModel((_, _) => Task.FromResult<string?>(new ExportReply { Error = DeviceConfigIds.FeatureOff }.ToJson()), [new FakeDevice()]);
        await vm.RunAsync(new ExportTarget(() => { opened = true; return Task.FromResult<Stream>(new MemoryStream()); }, "x.json"));

        Assert.False(opened);
        Assert.Equal("The configuration could not be exported: " + DeviceConfigIds.FeatureOff, vm.ResultText);
        Assert.True(vm.HasFailures);
        Assert.Equal(("Failed", DeviceConfigIds.FeatureOff), (vm.Rows[0].StateText, vm.Rows[0].Detail));
    }

    [Fact]
    public async Task Several_devices_go_into_one_zip_with_a_summary_of_the_failures()
    {
        var devices = new List<IDeviceInfo>
        {
            new FakeDevice { Address = "10.0.0.48", Serial = "B8A44F631339" },
            new FakeDevice { Address = "10.0.0.49", Serial = "B8A44F631340", Model = "AXIS Q6135-LE" },
            new FakeDevice { Address = "10.0.0.50", Serial = "B8A44F631341" },
        };
        var vm = new ExportConfigViewModel(
            (id, _) => id == devices[1].Id
                ? throw new HttpRequestException("Connection refused")
                : Task.FromResult<string?>(new ExportReply { Text = Fixtures.Export }.ToJson()),
            devices,
            () => new DateTime(2026, 10, 10));
        Assert.True(vm.IsZip);
        Assert.Equal("oadm-config-2026-10-10.zip", vm.SuggestedFileName);

        using var file = new MemoryStream();
        await vm.RunAsync(new ExportTarget(() => Task.FromResult<Stream>(new KeepOpenStream(file)), "site.zip"));

        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        Assert.Equal(
            ["10.0.0.48_B8A44F631339_P3265-V_config.json", "10.0.0.50_B8A44F631341_P3265-V_config.json", "summary.txt"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using var summary = new StreamReader(zip.GetEntry("summary.txt")!.Open());
        var text = summary.ReadToEnd();
        Assert.Contains("2 saved, 1 failed", text, StringComparison.Ordinal);
        Assert.Contains("10.0.0.49  B8A44F631340  AXIS Q6135-LE  Connection refused", text, StringComparison.Ordinal);
        Assert.Equal("2 configurations saved, 1 failed. summary.txt in the file lists the failures.", vm.ResultText);
        Assert.Equal("3 devices · 2 done · 1 failed", vm.Summary);
    }

    [Fact]
    public async Task Cancel_stops_the_export()
    {
        var started = new TaskCompletionSource();
        var vm = new ExportConfigViewModel(
            async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            },
            [new FakeDevice(), new FakeDevice { Serial = "B8A44F631340" }]);
        using var file = new MemoryStream();
        var run = vm.RunAsync(new ExportTarget(() => Task.FromResult<Stream>(new KeepOpenStream(file)), "site.zip"));
        await started.Task;
        vm.CancelCommand.Execute(null);
        await run;

        Assert.Equal("Cancelled. The file is incomplete; delete it.", vm.ProgressText);
        Assert.All(vm.Rows, r => Assert.Equal("Not exported", r.StateText));
        Assert.Equal("Close", vm.CancelText);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Thirty_thousand_devices_export_into_one_zip()
    {
        var devices = Enumerable.Range(0, Oadm.Sdk.Devices.DeviceLimits.MaxDevices).Select(i => (IDeviceInfo)new FakeDevice
        {
            Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
            Serial = $"B8A44F{i:X6}",
        }).ToList();
        var answer = new ExportReply { Text = Fixtures.Export }.ToJson();
        var concurrent = 0;
        var max = 0;
        var vm = new ExportConfigViewModel(
            async (_, _) =>
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref max, now);
                await Task.Yield();
                Interlocked.Decrement(ref concurrent);
                return answer;
            },
            devices);
        using var file = new MemoryStream();
        var watch = Stopwatch.StartNew();
        await vm.RunAsync(new ExportTarget(() => Task.FromResult<Stream>(new KeepOpenStream(file)), "site.zip"));
        output.WriteLine($"30,000 devices: {watch.ElapsedMilliseconds} ms, {file.Length / 1024} KB");

        Assert.Equal("30,000 configurations saved.", vm.ResultText);
        Assert.True(max <= DeviceConfigIds.ExportParallelism, $"max {max}");
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        Assert.Equal(Oadm.Sdk.Devices.DeviceLimits.MaxDevices, zip.Entries.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>The view model disposes the file stream; the test still reads it.</summary>
    internal sealed class KeepOpenStream(MemoryStream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
