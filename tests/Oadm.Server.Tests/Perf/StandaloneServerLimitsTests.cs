using System.Diagnostics;

using Grpc.Core;
using Grpc.Net.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Settings;
using Oadm.Sdk.Devices;
using Oadm.Tests.Shared;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Perf;

/// <summary>
/// Memory of the real server process (Oadm.Server.dll with the development plugins) with N devices in its database, idle
/// and while clients load the device list. Opt-in with <c>OADM_LIMITS_DEVICES</c>. Contacts no device: the devices have
/// addresses of the benchmark range 198.18.0.0/15, polling and full refresh are set to once a day, NTP and DHCP are off.
/// </summary>
[Trait("Category", "Perf")]
public sealed class StandaloneServerLimitsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ServerProcessMemoryAtEverySize()
    {
        var sizes = (Environment.GetEnvironmentVariable("OADM_LIMITS_DEVICES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        foreach (var size in sizes)
        {
            var dataDir = Path.Combine(Path.GetTempPath(), "oadm-limits", Guid.NewGuid().ToString("N"));
            try
            {
                await SeedAsync(dataDir, size);
                await MeasureAsync(dataDir, size);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try
                {
                    Directory.Delete(dataDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static async Task SeedAsync(string dataDir, int size)
    {
        var paths = new OadmPaths(dataDir);
        await using var provider = new ServiceCollection().AddOadmPersistence(paths).BuildServiceProvider();
        await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);
        var apis = Enumerable.Range(0, 100).Select(i => new Oadm.Sdk.Vapix.DeviceApi($"api-{i:D3}", "1.0", $"Some API number {i}", "official")).ToList();
        await using var db = await provider.GetRequiredService<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        for (var i = 0; i < size; i++)
        {
            db.Devices.Add(new Device
            {
                Id = Guid.NewGuid(),
                Serial = $"ACCC8E{i:X6}",
                Address = $"198.{18 + i / 65536}.{i / 256 % 256}.{i % 256}",
                Scheme = DeviceScheme.Https,
                Status = DeviceStatus.Ok,
                Model = "P3265-V",
                FirmwareVersion = "12.11.77",
                ProductType = "Dome Camera",
                Category = DeviceCategory.Camera,
                Tags = i % 3 == 0 ? ["Building A"] : [],
                Apis = apis,
            });
        }

        db.Settings.Add(new Setting { Key = SettingKeys.PollingIntervalSeconds, ValueJson = "86400" });
        db.Settings.Add(new Setting { Key = SettingKeys.PollingFullRefreshMinutes, ValueJson = "1440" });
        db.Settings.Add(new Setting { Key = Oadm.Server.Plugins.PluginActivation.SettingKey, ValueJson = """{"oadm.ntp-server":false,"oadm.dhcp-server":false}""" });
        await db.SaveChangesAsync();
    }

    private async Task MeasureAsync(string dataDir, int size)
    {
        var serverDll = Path.Combine(FindRepo(), "src", "Oadm.Server", "bin", "Debug", "net10.0", "Oadm.Server.dll");
        var port = FreePort();
        var start = new ProcessStartInfo(Environment.ProcessPath!.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? Environment.ProcessPath : "dotnet")
        {
            ArgumentList = { serverDll, $"--Oadm:DataDir={dataDir}", $"--Oadm:ListenUrl=https://127.0.0.1:{port}" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // OADM_LIMITS_SERVER_HEAP (e.g. 0x40000000) caps the server's GC heap only: does it still serve the clients?
        if (Environment.GetEnvironmentVariable("OADM_LIMITS_SERVER_HEAP") is { Length: > 0 } heap)
        {
            start.Environment["DOTNET_GCHeapHardLimit"] = heap;
            output.WriteLine($"  Server heap limited to {Convert.ToInt64(heap, 16) / 1024 / 1024:N0} MB");
        }

        using var server = Process.Start(start)!;
        var tail = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Keep(string? line)
        {
            if (line is null)
            {
                return;
            }

            tail.Enqueue(line);
            while (tail.Count > 20)
            {
                tail.TryDequeue(out _);
            }
        }

        server.OutputDataReceived += (_, e) => Keep(e.Data);
        server.ErrorDataReceived += (_, e) => Keep(e.Data);
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            using var handler = new HttpClientHandler
            {
#pragma warning disable CA5359 // The test's own server with a fresh self-signed certificate on loopback.
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
#pragma warning restore CA5359
            };
            using var channel = GrpcChannel.ForAddress($"https://127.0.0.1:{port}", new GrpcChannelOptions
            {
                HttpHandler = handler,
                MaxReceiveMessageSize = 32 * 1024 * 1024,
            });
            var auth = new Proto.AuthService.AuthServiceClient(channel);
            var watch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    await auth.StatusAsync(new Proto.Empty());
                    break;
                }
                catch (RpcException) when (watch.Elapsed < TimeSpan.FromMinutes(3) && !server.HasExited)
                {
                    await Task.Delay(250);
                }
            }

            output.WriteLine($"== {size:N0} devices, server ready after {watch.Elapsed.TotalSeconds:N1} s");
            await Task.Delay(TimeSpan.FromSeconds(10));
            Report(server, "idle, 10 s after start");

            var login = await auth.CreateFirstAdminAsync(new Proto.CreateFirstAdminRequest { UserName = "admin", Password = "limits-test" });
            var headers = new Metadata { { "authorization", "Bearer " + login.Token }, { "oadm-client-machine", "limits-test" } };
            var devices = new Proto.DeviceService.DeviceServiceClient(channel);
            var tasks = new Proto.TaskService.TaskServiceClient(channel);

            async Task<int> SnapshotAsync()
            {
                using var call = devices.Watch(new Proto.WatchDevicesRequest { SnapshotEndMarker = true }, headers);
                var count = 0;
                while (await call.ResponseStream.MoveNext(CancellationToken.None))
                {
                    if (call.ResponseStream.Current.Kind == Proto.DeviceChanged.Types.Kind.SnapshotEnd)
                    {
                        return count;
                    }

                    count++;
                }

                return count;
            }

            watch.Restart();
            var received = await SnapshotAsync();
            output.WriteLine($"  One client loads the device list: {received:N0} devices in {watch.Elapsed.TotalSeconds:N1} s");
            Report(server, "after one device list");
            watch.Restart();
            await tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest { Compact = true }, headers);
            output.WriteLine($"  Task menu (compact), first call: {watch.Elapsed.TotalSeconds:N1} s");

            watch.Restart();
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SnapshotAsync()));
            output.WriteLine($"  Five clients load the device list at once: {watch.Elapsed.TotalSeconds:N1} s");
            Report(server, "after five device lists at once");
            await Task.Delay(TimeSpan.FromSeconds(30));
            Report(server, "30 s later");
        }
        catch (RpcException ex)
        {
            output.WriteLine($"  Failed: {ex.Status.StatusCode}; server exited: {server.HasExited}{(server.HasExited ? $" (code {server.ExitCode})" : string.Empty)}");
            foreach (var line in tail)
            {
                output.WriteLine("  | " + line);
            }
        }
        finally
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }
    }

    private void Report(Process server, string when)
    {
        server.Refresh();
        output.WriteLine($"  Server {when}: working set {server.WorkingSet64 / 1024 / 1024:N0} MB, private {server.PrivateMemorySize64 / 1024 / 1024:N0} MB, threads {server.Threads.Count}, CPU so far {server.TotalProcessorTime.TotalSeconds:N1} s");
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepo() => TestRepository.Root;
}
