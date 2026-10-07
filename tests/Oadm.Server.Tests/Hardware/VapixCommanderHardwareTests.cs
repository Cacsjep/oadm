using Oadm.Core.Tests.Hardware;
using Oadm.Plugins.VapixCommander;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Hardware;

/// <summary>
/// VAPIX Commander against the real camera, through the real server in process (temp data folder). READ-ONLY: only
/// param.cgi action=list and basicdeviceinfo getAllProperties are sent; no write command exists in the test library.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class VapixCommanderHardwareTests
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.48") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task ReadOnlyCommandsTryAndRollOutOnTheRealCamera()
    {
        var camera = Camera;
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);
        var library = VapixCommanderServerTests.WriteSampleLibrary();
        try
        {
            await VapixCommanderServerTests.RegisterAsync(host, library);

            // Add the camera (no password is ever set; the test aborts on a factory-default camera).
            var (session, discovered) = await TestHelpers.ScanAsync(host, camera.Address, camera.Address);
            var found = Assert.Single(discovered);
            Assert.NotEqual(Proto.DeviceStatus.PasswordNotSet, found.Status);
            var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest
            {
                SessionId = session,
                DiscoveredIds = { found.DiscoveredId },
                Credentials = { new Proto.DeviceCredentials { UserName = camera.User, Password = camera.Password } },
            });
            var deviceId = Guid.Parse(Assert.Single(reply.DeviceIds));
            await TestHelpers.WaitUntilAsync(
                async () => (await TestHelpers.GetDeviceAsync(host, deviceId.ToString())).Apis.Count > 0,
                "API list after the first full refresh",
                TimeSpan.FromSeconds(60));

            // Try: param.cgi list Brand
            var brand = await VapixCommanderServerTests.InvokeAsync<TryCommandReply>(host, CommanderMethods.TryRequest, new TryCommandRequest
            {
                DeviceId = deviceId,
                Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" },
            });
            Assert.Null(brand.Error);
            Assert.True(brand.Outcome!.Success, brand.Outcome.Summary);
            Assert.Equal(200, brand.Outcome.StatusCode);
            Assert.StartsWith("Product: ", brand.Outcome.Summary, StringComparison.Ordinal);
            Assert.Contains(brand.Outcome.Parameters, p => p.Name == "root.Brand.Brand" && p.Value == "AXIS");

            // Try: basicdeviceinfo (JSON API, charset=utf8 answer)
            var info = await VapixCommanderServerTests.InvokeAsync<TryCommandReply>(host, CommanderMethods.TryRequest, new TryCommandRequest
            {
                DeviceId = deviceId,
                Command = new CommandRef { Source = CommandSources.Library, Id = "common.basicdeviceinfo.read" },
            });
            Assert.True(info.Outcome!.Success, info.Outcome.Summary);
            Assert.Matches("^Model: .+ · AXIS OS: [0-9.]+$", info.Outcome.Summary);
            Assert.Contains("\"propertyList\"", info.Outcome.Body, StringComparison.Ordinal);

            // Rollout of both read commands: one task, a named step per command.
            var rollout = await VapixCommanderServerTests.InvokeAsync<RolloutReply>(host, CommanderMethods.Rollout, new RolloutRequest
            {
                DeviceIds = [deviceId],
                Commands =
                [
                    new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" } },
                    new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = "common.basicdeviceinfo.read" } },
                ],
                StopOnFirstError = true,
                Owner = "hardware-test",
            });
            Assert.Null(rollout.Error);
            var task = await TestHelpers.WaitForTaskAsync(host, Assert.Single(rollout.TaskIds).ToString(), TimeSpan.FromSeconds(60));
            Assert.Equal(Proto.TaskState.Done, task.State);
            Assert.Equal(["Check compatibility", "Read brand parameters", "Read basic device information", "Completed"], task.Steps.Select(s => s.Name));
            Assert.All(task.Steps, s => Assert.Equal(Proto.TaskStepState.Done, s.State));
            Assert.StartsWith("Product: ", task.Steps[1].Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(library, recursive: true);
        }
    }
}
