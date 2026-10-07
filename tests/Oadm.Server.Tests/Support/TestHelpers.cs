using Grpc.Core;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Support;

internal static class TestHelpers
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Polls <paramref name="condition"/> until it is true or the timeout expires.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for " + what);
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Runs a range scan through gRPC and returns the session id and the discovered devices.</summary>
    public static async Task<(string SessionId, List<Proto.DiscoveredDevice> Devices)> ScanAsync(TestServerHost host, string from, string to)
    {
        var session = await host.Discovery.StartRangeScanAsync(new Proto.RangeScanRequest { From = from, To = to });
        var devices = new Dictionary<string, Proto.DiscoveredDevice>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var call = host.Discovery.WatchDiscovered(session, cancellationToken: cts.Token);
        await foreach (var message in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            if (!string.IsNullOrEmpty(message.DiscoveredId))
            {
                devices[message.DiscoveredId] = message;
            }

            if (message.ScanFinished)
            {
                break;
            }
        }

        return (session.SessionId, devices.Values.OrderBy(d => d.Address, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Reads a range scan or address probe session to the end of its stream (scan finished and
    /// every automatic login done) and returns the last message per device.
    /// </summary>
    public static async Task<Dictionary<string, Proto.DiscoveredDevice>> WatchToEndAsync(TestServerHost host, string sessionId, TimeSpan? timeout = null)
    {
        var devices = new Dictionary<string, Proto.DiscoveredDevice>();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        using var call = host.Discovery.WatchDiscovered(new Proto.DiscoverySession { SessionId = sessionId }, cancellationToken: cts.Token);
        await foreach (var message in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            if (!string.IsNullOrEmpty(message.DiscoveredId))
            {
                devices[message.DiscoveredId] = message;
            }
        }

        return devices;
    }

    /// <summary>Range scan to the end of the stream (logins included), devices by address.</summary>
    public static async Task<(string SessionId, Dictionary<string, Proto.DiscoveredDevice> Devices)> ScanWithLoginAsync(TestServerHost host, string from, string to)
    {
        var session = await host.Discovery.StartRangeScanAsync(new Proto.RangeScanRequest { From = from, To = to });
        var devices = await WatchToEndAsync(host, session.SessionId);
        return (session.SessionId, devices.Values.ToDictionary(d => d.Address));
    }

    /// <summary>Waits until the task reaches a terminal state and returns it.</summary>
    public static async Task<Proto.TaskInfo> WaitForTaskAsync(TestServerHost host, string taskId, TimeSpan? timeout = null)
    {
        Proto.TaskInfo? task = null;
        await WaitUntilAsync(
            async () =>
            {
                task = (await host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.FirstOrDefault(t => t.Id == taskId);
                return task is { State: Proto.TaskState.Done or Proto.TaskState.DoneWithWarnings or Proto.TaskState.Failed or Proto.TaskState.Cancelled };
            },
            "task " + taskId,
            timeout);
        return task!;
    }

    public static async Task<Proto.Device> GetDeviceAsync(TestServerHost host, string id) =>
        (await host.Devices.ListAsync(new Proto.Empty())).Devices.Single(d => d.Id == id);
}
