using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// <see cref="DeviceAddressService.UpdateTlsAsync"/> and <c>ITaskExecutionContext.UpdateDeviceTlsAsync</c>: after a task
/// changed the web server certificate (or turned HTTPS off) OADM switches scheme and pin without CertificateChanged.
/// Fake devices only; the fake "presents" a real self-signed certificate to the connection's pinning.
/// </summary>
public sealed class DeviceTlsTests
{
    private const string Address = "10.0.8.5";

    private static X509Certificate2 Certificate(string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
    }

    private static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    [Fact]
    public async Task HttpsWithTheExpectedCertificatePinsItAndSwitchesTheScheme()
    {
        await using var host = await TestServerHost.StartAsync();
        var fake = host.Network.Add(Address, FakeSerials.Make(801), "pw");
        using var served = Certificate("new");
        fake.ServesHttps = true;
        fake.Certificate = served;
        var device = await DeviceServiceTests.AddDeviceAsync(host, Address, 801, status: DeviceStatus.CertificateChanged);
        await host.Get<DeviceRepository>().UpdateAsync(device.Id, d => d.CertFingerprintSha256 = "AB12", CancellationToken.None);

        await host.Get<DeviceAddressService>().UpdateTlsAsync(device.Id, "https", Fingerprint(served).ToLowerInvariant(), CancellationToken.None);

        var stored = (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!;
        Assert.Equal(DeviceScheme.Https, stored.Scheme);
        Assert.Equal(Fingerprint(served), stored.CertFingerprintSha256);
        Assert.Equal("CN=new", stored.CertSubject);
        Assert.NotNull(stored.CertNotAfterUtc);
        // No longer CertificateChanged: Unknown until the queued refresh ran, Ok when it already did (busy CI runner).
        Assert.Contains(stored.Status, new[] { DeviceStatus.Unknown, DeviceStatus.Ok });

        // The factory's client now pins the new certificate.
        var client = await host.Get<VapixClientFactory>().GetClientAsync(device.Id, CancellationToken.None);
        Assert.Equal("https", client.BaseAddress.Scheme);
        Assert.Equal(FakeSerials.Make(801), (await client.GetBasicDeviceInfoAsync(CancellationToken.None)).SerialNumber);
    }

    [Fact]
    public async Task AnotherCertificateOrSerialLeavesTheRecordUnchanged()
    {
        await using var host = await TestServerHost.StartAsync();
        var fake = host.Network.Add(Address, FakeSerials.Make(802), "pw");
        using var served = Certificate("served");
        using var expected = Certificate("expected");
        fake.ServesHttps = true;
        fake.Certificate = served;
        var device = await DeviceServiceTests.AddDeviceAsync(host, Address, 802);
        var service = host.Get<DeviceAddressService>();

        var ex = await Assert.ThrowsAsync<DeviceIdentityException>(() => service.UpdateTlsAsync(device.Id, "https", Fingerprint(expected), CancellationToken.None));
        Assert.Contains("does not present the expected certificate", ex.Message, StringComparison.Ordinal);
        var stored = (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!;
        Assert.Equal(DeviceScheme.Http, stored.Scheme);
        Assert.Null(stored.CertFingerprintSha256);

        // Another device answers at the address.
        host.Network.Add(Address, FakeSerials.Make(899), "pw");
        await Assert.ThrowsAsync<DeviceIdentityException>(() => service.UpdateTlsAsync(device.Id, "http", null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateTlsAsync(device.Id, "ftp", null, CancellationToken.None));
    }

    [Fact]
    public async Task HttpClearsTheCertificateDetails()
    {
        await using var host = await TestServerHost.StartAsync();
        host.Network.Add(Address, FakeSerials.Make(803), "pw");
        var device = await DeviceServiceTests.AddDeviceAsync(host, Address, 803);
        await host.Get<DeviceRepository>().UpdateAsync(
            device.Id,
            d =>
            {
                d.Scheme = DeviceScheme.Https;
                d.CertSubject = "CN=old";
                d.CertNotAfterUtc = DateTime.UtcNow.AddDays(30);
                d.CertTrust = CertificateTrust.SelfSigned;
            },
            CancellationToken.None);

        await host.Get<DeviceAddressService>().UpdateTlsAsync(device.Id, "http", null, CancellationToken.None);

        var stored = (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!;
        Assert.Equal(DeviceScheme.Http, stored.Scheme);
        Assert.Null(stored.CertSubject);
        Assert.Null(stored.CertNotAfterUtc);
        Assert.Equal(CertificateTrust.Unknown, stored.CertTrust);
    }

    [Fact]
    public async Task ATaskSwitchesToHttpsThroughTheSdkContext()
    {
        await using var host = await TestServerHost.StartAsync();
        var fake = host.Network.Add(Address, FakeSerials.Make(804), "pw");
        using var served = Certificate("task");
        var device = await DeviceServiceTests.AddDeviceAsync(host, Address, 804);
        var plugin = new SwitchPlugin(Fingerprint(served), () =>
        {
            fake.ServesHttps = true;
            fake.Certificate = served;
        });
        Assert.True(host.Get<PluginRegistry>().RegisterTaskPlugin(plugin, new PluginOrigin(SwitchPlugin.PluginId, "1.0.0", null)));

        var run = await host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = SwitchPlugin.PluginId, DeviceIds = { device.Id.ToString() } });
        var task = await TestHelpers.WaitForTaskAsync(host, run.TaskIds[0]);

        Assert.Equal(Proto.TaskState.Done, task.State);
        Assert.Equal("https", plugin.SchemeAfter);
        Assert.Equal(FakeSerials.Make(804), plugin.SerialAfter);
        Assert.Equal(DeviceScheme.Https, (await host.Get<DeviceRepository>().GetAsync(device.Id, CancellationToken.None))!.Scheme);
        var log = await host.Tasks.GetLogAsync(new Proto.TaskIdRequest { TaskId = run.TaskIds[0] });
        Assert.Contains(log.Entries, e => e.Message.Contains("OADM now connects over HTTPS", StringComparison.Ordinal));
    }

    /// <summary>Test plugin: "turns on" HTTPS on the fake, then lets OADM switch to it.</summary>
    private sealed class SwitchPlugin(string fingerprint, Action enableHttps) : ITaskPlugin
    {
        public const string PluginId = "test.tls";

        public string Id => PluginId;

        public string DisplayName => "Switch";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public string? SchemeAfter { get; private set; }

        public string? SerialAfter { get; private set; }

        public bool CanRun(IDeviceInfo device) => true;

        public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
        {
            enableHttps();
            await ctx.UpdateDeviceTlsAsync("https", fingerprint, ct);
            SchemeAfter = ctx.Vapix.BaseAddress.Scheme;
            SerialAfter = (await ctx.Vapix.GetBasicDeviceInfoAsync(ct)).SerialNumber;
        }
    }
}
