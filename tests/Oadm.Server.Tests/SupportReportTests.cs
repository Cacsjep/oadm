using System.IO.Compression;
using System.Text;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Contracts.Logging;
using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Licensing;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;
using Oadm.Server.Backup;
using Oadm.Server.Support;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>SupportService: the support report holds the server's logs and a summary, never a secret; administrators only, audited.</summary>
public sealed class SupportReportTests
{
    private static readonly (string PrivateKeyPem, string PublicKey) Key = LicenseFile.CreateKeys();

    private const string DevicePassword = "Device-Secret-7f3c";
    private const string ListPassword = "List-Secret-91ab";
    private const string BackupPassword = "Backup-Secret-55de";
    private const string DeviceAddress = "10.98.76.54";
    private const string DeviceSerial = "B8A44FABCDEF";

    private static Task<TestServerHost> StartAsync() =>
        TestServerHost.StartAsync(configureServices: services => services.AddSingleton(sp => new ProLicense(
            sp.GetRequiredService<ServerSettingsStore>(),
            () => sp.GetRequiredService<PluginLicenses>().ServerId,
            publicKeys: [Key.PublicKey])));

    private static async Task<(string Name, Dictionary<string, string> Entries)> DownloadAsync(CallInvoker invoker)
    {
        var client = new Proto.SupportService.SupportServiceClient(invoker);
        using var call = client.CreateReport(new Proto.Empty());
        var bytes = new MemoryStream();
        string? name = null;
        long length = -1;
        await foreach (var chunk in call.ResponseStream.ReadAllAsync())
        {
            if (name is null)
            {
                (name, length) = (chunk.FileName, chunk.Length);
                continue;
            }

            chunk.Data.WriteTo(bytes);
        }

        Assert.Equal(length, bytes.Length);
        bytes.Position = 0;
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            entries[entry.FullName] = await reader.ReadToEndAsync();
        }

        return (name!, entries);
    }

    [Fact]
    public async Task TheReportHasTheLogsAndASummaryAndNeverASecret()
    {
        await using var host = await StartAsync();
        var logs = Path.Combine(host.DataDirectory, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "oadm-server-20261020.log"), "2026-10-20 09:00:00.000 +02:00 [INF] Oadm.Server: older\n");
        await File.WriteAllTextAsync(Path.Combine(logs, "oadm-server-20261021.log"), "2026-10-21 09:00:00.000 +02:00 [INF] Oadm.Server: newer\n");

        // Secrets the report must never carry: a device password, the credential list, the backup password, the license key.
        var device = await host.Get<DeviceRepository>().AddAsync(new Device { Serial = DeviceSerial, Address = DeviceAddress, Model = "P3265-V", FirmwareVersion = "12.11.77" }, CancellationToken.None);
        await host.Get<CredentialStore>().SetAsync(device.Id, "root", DevicePassword, CancellationToken.None);
        await host.Get<CredentialListStore>().AddAsync("service", ListPassword, CancellationToken.None);
        await host.Get<BackupManager>().SetScheduleAsync(true, "02:00", 7, BackupPassword, CancellationToken.None);
        var license = ProLicenseFile.Sign(Key.PrivateKeyPem, host.Get<PluginLicenses>().ServerId, "ACME GmbH", "Up to 500 devices", 500, DateOnly.FromDateTime(DateTime.Today), new DateOnly(2099, 12, 31), false);
        await host.Get<ProLicense>().ImportAsync(license, CancellationToken.None);

        var (name, entries) = await DownloadAsync(host.Invoker);

        Assert.StartsWith("oadm-support-", name, StringComparison.Ordinal);
        Assert.EndsWith(".zip", name, StringComparison.Ordinal);
        Assert.Contains("logs/oadm-server-20261020.log", entries.Keys);
        Assert.Contains("logs/oadm-server-20261021.log", entries.Keys);
        var summary = entries["summary.txt"];
        Assert.Contains("OADM version: ", summary, StringComparison.Ordinal);
        Assert.Contains("Devices: 1", summary, StringComparison.Ordinal);
        Assert.Contains("  P3265-V: 1", summary, StringComparison.Ordinal);
        Assert.Contains("  12.11.77: 1", summary, StringComparison.Ordinal);
        Assert.Contains("Plan: Up to 500 devices", summary, StringComparison.Ordinal);
        Assert.Contains("Logs in this report: " + entries.Keys.Count(k => k.StartsWith("logs/", StringComparison.Ordinal)) + " files", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(entries.Keys, k => k.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".key", StringComparison.OrdinalIgnoreCase));

        string[] secrets = [DevicePassword, ListPassword, BackupPassword, license, license[..40], DeviceAddress, DeviceSerial];
        foreach (var (entry, text) in entries)
        {
            foreach (var secret in secrets)
            {
                Assert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{entry} contains a secret");
            }
        }

        // Audited; the work file is gone after the download.
        var audit = await host.Audit.ListAsync(new Proto.ListAuditRequest());
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.SupportReportCreated && e.Target == name);
        Assert.Empty(Directory.GetFiles(host.Get<SupportReport>().Folder));
    }

    [Fact]
    public async Task OperatorsCannotCreateASupportReport()
    {
        await using var host = await StartAsync();
        var invoker = await host.InvokerForUserAsync("op", UserRole.Operator);
        var denied = await Assert.ThrowsAsync<RpcException>(() => DownloadAsync(invoker));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }

    [Fact]
    public async Task LogFilesBeyondTheLimitAreLeftOutOldestFirst()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-logarchive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var day in new[] { "20261019", "20261020", "20261021" })
            {
                await File.WriteAllTextAsync(Path.Combine(folder, $"oadm-server-{day}.log"), new string('x', 100));
            }

            // The newest file is open for writing, like the logger keeps it: still read.
            await using var open = new FileStream(Path.Combine(folder, "oadm-server-20261021.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            LogArchiveResult result;
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                result = await LogArchive.AddAsync(zip, folder, "*.log", "logs", 250, CancellationToken.None);
            }

            Assert.Equal(["oadm-server-20261021.log", "oadm-server-20261020.log"], result.Included);
            Assert.Equal(["oadm-server-20261019.log"], result.LeftOut);
            Assert.Equal(200, result.Bytes);
            buffer.Position = 0;
            using var read = new ZipArchive(buffer, ZipArchiveMode.Read);
            Assert.Equal(["logs/oadm-server-20261020.log", "logs/oadm-server-20261021.log"], read.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheFileNameCarriesTheServerNameAndTheTime()
    {
        var now = new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.FromHours(2));
        Assert.Equal("oadm-support-acs-20261022-0930.zip", SupportReport.FileName("acs", now));
        Assert.Equal("oadm-support-Server-1-20261022-0930.zip", SupportReport.FileName("Server 1", now));
        Assert.Equal("oadm-support-server-20261022-0930.zip", SupportReport.FileName("  ", now));
    }
}
