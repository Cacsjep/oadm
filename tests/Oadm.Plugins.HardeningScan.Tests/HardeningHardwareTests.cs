using System.Runtime.CompilerServices;

using Grpc.Net.Client;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Devices;
using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Tests.Hardware;
using Oadm.Sdk.Devices;
using Oadm.Server.Hosting;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Plugins.HardeningScan.Tests;

/// <summary>
/// One Extended scan of the dev camera through the real server in-process (temp data folder, the plugin registered as a core
/// plugin, gRPC PluginService.Invoke). Read-only for the camera: param.cgi list, pwdgrp get, REST GETs, getNTPInfo, the
/// disks and applications lists and the SOAP Get of the web server settings, plus the server's usual full refresh.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class HardeningHardwareTests
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task An_extended_scan_of_the_dev_camera_finds_what_was_recorded()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "oadm-hardening-hw", Guid.NewGuid().ToString("N"));
        var app = OadmServerHost.Build([], new OadmServerHostOptions
        {
            DataDirectory = dataDirectory,
            PluginRoots = [],
            LogToConsole = false,
            ConfigureBuilder = b => b.WebHost.UseTestServer(),
            ConfigureServices = services =>
            {
                services.RemoveAll<IMdnsBrowser>();
                services.AddSingleton<IMdnsBrowser, SilentMdnsBrowser>();
            },
        });
        app.Services.GetRequiredService<PluginRegistry>().RegisterCorePlugin(new HardeningScanPlugin(), PluginOrigin.FromAssembly(typeof(HardeningScanPlugin).Assembly));
        await OadmServerHost.StartAsync(app);
        try
        {
            var server = app.GetTestServer();
            using var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler(), MaxReceiveMessageSize = 32 * 1024 * 1024 });
            var token = await Oadm.Server.Auth.InProcessAccess.CreateTokenAsync(app.Services);
            var invoker = Grpc.Core.Interceptors.ChannelExtensions.Intercept(channel, new Oadm.Contracts.Security.AuthHeaderInterceptor(() => token));
            var plugins = new Proto.PluginService.PluginServiceClient(invoker);
            Assert.Contains((await plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins, p => p.Id == HardeningScanPluginInfo.PluginId);

            var device = await app.Services.GetRequiredService<DeviceRepository>().AddAsync(new Oadm.Core.Devices.Device
            {
                Serial = "B8A44F631339",
                Address = Camera.Address,
                Status = DeviceStatus.Ok,
                Category = DeviceCategory.Camera,
                Scheme = Camera.EffectiveScheme == "http" ? DeviceScheme.Http : DeviceScheme.Https,
            }, CancellationToken.None);
            await app.Services.GetRequiredService<CredentialStore>().SetAsync(device.Id, Camera.User, Camera.Password, CancellationToken.None);
            var refreshed = await app.Services.GetRequiredService<DevicePollingService>().RefreshAsync(device.Id, CancellationToken.None);
            Console.WriteLine($"device: {refreshed?.Model} {refreshed?.FirmwareVersion} {refreshed?.Status} dhcp {refreshed?.DhcpEnabled} https {refreshed?.HttpsEnabled} cert {refreshed?.CertTrust}");

            async Task<string> Invoke(string method, object? payload) =>
                (await plugins.InvokeAsync(new Proto.InvokeRequest { PluginId = HardeningScanPluginInfo.PluginId, Method = method, PayloadJson = payload is null ? string.Empty : HardeningJson.Serialize(payload) })).PayloadJson;

            var started = DateTime.UtcNow;
            var job = HardeningJson.Deserialize<ScanJobStatus>(await Invoke(HardeningMethods.StartScan, new StartScanRequest { Level = ScanLevel.Extended, DeviceIds = [device.Id] }));
            var deadline = DateTime.UtcNow.AddSeconds(120);
            HardeningState state;
            do
            {
                await Task.Delay(250);
                state = HardeningJson.Deserialize<HardeningState>(await Invoke(HardeningMethods.GetState, null));
            }
            while (state.Job?.IsRunning != false && DateTime.UtcNow < deadline);

            Assert.Equal(job.JobId, state.Job!.JobId);
            Assert.Equal(ScanJobStates.Done, state.Job.State);
            var detail = HardeningJson.Deserialize<DetailReply>(await Invoke(HardeningMethods.GetDetail, new DetailRequest { DeviceIds = [device.Id] })).Devices.Single();
            Console.WriteLine($"scan: {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms, status {detail.Status ?? "ok"}");
            foreach (var check in detail.Checks)
            {
                Console.WriteLine($"{check.Id,-4} {check.State,-14} {check.Value} {check.Detail}");
            }

            Assert.Null(detail.Status);
            var results = detail.Checks.ToDictionary(c => c.Id);
            Assert.DoesNotContain(detail.Checks, c => c.State == CheckState.Error);
            Assert.Equal(CheckState.Info, results[HardeningCatalog.AxisOs].State);
            Assert.Equal(CheckState.Pass, results[HardeningCatalog.Accounts].State);
            Assert.Equal("Password policy: none", results[HardeningCatalog.PasswordPolicy].Value);
            Assert.Equal(CheckState.Warn, results[HardeningCatalog.IpConfiguration].State);
            Assert.Equal("SD card not encrypted", results[HardeningCatalog.StorageEncryption].Value);
            Assert.Equal(CheckState.Warn, results[HardeningCatalog.Applications].State);
            Assert.Equal("Web interface on", results[HardeningCatalog.WebInterface].Value);
            Assert.Contains("Bonjour", results[HardeningCatalog.Discovery].Value, StringComparison.Ordinal);
            Assert.Equal("SSH on", results[HardeningCatalog.Ssh].Value);
            Assert.Equal(CheckState.Warn, results[HardeningCatalog.Firewall].State);
            Assert.Equal("Recommended ciphers only", results[HardeningCatalog.Ciphers].Value);
            Assert.Equal("Remote syslog off", results[HardeningCatalog.RemoteSyslog].Value);
            Assert.Equal("SNMP off", results[HardeningCatalog.Snmp].Value);
            Assert.Equal("RTSPS off", results[HardeningCatalog.Rtsps].Value);
            Assert.Equal("Not configured", results[HardeningCatalog.OAuth].Value);
            Assert.Equal("HTTP and HTTPS", results[HardeningCatalog.HttpsOnly].Value);
            Assert.Equal("Password throttling on", results[HardeningCatalog.BruteForce].Value);
            Assert.Equal("Access log off", results[HardeningCatalog.AccessLog].Value);
            Assert.Equal("NTS off", results[HardeningCatalog.Nts].Value);
        }
        finally
        {
            await OadmServerHost.StopAsync(app);
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>No multicast from tests.</summary>
    private sealed class SilentMdnsBrowser : IMdnsBrowser
    {
        public async IAsyncEnumerable<MdnsServiceInstance> BrowseAsync(MdnsBrowseOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            yield break;
        }
    }
}
