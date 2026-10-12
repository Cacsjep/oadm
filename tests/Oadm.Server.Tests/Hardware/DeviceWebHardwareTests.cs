using System.Text;

using Google.Protobuf;

using Oadm.Core.Tests.Hardware;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Hardware;

/// <summary>
/// The Web View relay against the real camera, through the real server in process (temp data folder). READ-ONLY: GET
/// of the web interface's start page, a param.cgi list, a WebSocket session token and the event WebSocket (opened and
/// closed again, no subscription).
/// </summary>
[Trait("Category", "Hardware")]
public sealed class DeviceWebHardwareTests(ITestOutputHelper output)
{
    private static DevCamera Camera => DevCameras.All.FirstOrDefault(c => c.Address == "10.0.0.202") ?? DevCameras.All[0];

    [HardwareFact]
    public async Task TheWebInterfaceOfTheRealCameraOpensLoggedInThroughTheServer()
    {
        var camera = Camera;
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);
        var (session, discovered) = await TestHelpers.ScanAsync(host, camera.Address, camera.Address);
        var found = Assert.Single(discovered);
        Assert.NotEqual(Proto.DeviceStatus.PasswordNotSet, found.Status);
        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { found.DiscoveredId },
            Credentials = { new Proto.DeviceCredentials { UserName = camera.User, Password = camera.Password } },
        });
        var deviceId = Assert.Single(reply.DeviceIds);
        var client = new Proto.DeviceWebService.DeviceWebServiceClient(host.Invoker);
        Assert.True((await client.OpenAsync(new Proto.OpenDeviceWebRequest { DeviceId = deviceId })).Ok);

        // The start page: a redirect to the web interface, made relative for the relay.
        var (start, startBody) = await GetAsync(client, deviceId, "/");
        output.WriteLine($"/ -> {start.Status} {Header(start, "Location")}");
        output.WriteLine(string.Join(" | ", start.Headers.Select(h => h.Name + ": " + h.Value)));
        output.WriteLine(Encoding.UTF8.GetString(startBody[..Math.Min(400, startBody.Length)]));
        var path = "/";
        for (var hops = 0; hops < 5 && start.Status is >= 300 and < 400; hops++)
        {
            path = Header(start, "Location")!;
            Assert.StartsWith("/", path, StringComparison.Ordinal);
            (start, _) = await GetAsync(client, deviceId, path);
            output.WriteLine($"{path} -> {start.Status}");
        }

        Assert.Equal(200, start.Status);
        Assert.Contains("text/html", Header(start, "Content-Type"), StringComparison.OrdinalIgnoreCase);

        // An API of the page with the stored login.
        var (param, body) = await GetAsync(client, deviceId, "/axis-cgi/param.cgi?action=list&group=Brand.Brand");
        output.WriteLine($"param.cgi -> {param.Status}: {Encoding.UTF8.GetString(body)}");
        Assert.Equal(200, param.Status);
        Assert.Contains("Brand.Brand=AXIS", Encoding.UTF8.GetString(body), StringComparison.Ordinal);

        // A WebSocket like the web interface opens it: session token first, then the event stream.
        var (tokenHead, tokenBody) = await GetAsync(client, deviceId, "/axis-cgi/wssession.cgi");
        Assert.Equal(200, tokenHead.Status);
        var token = Encoding.ASCII.GetString(tokenBody).Trim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = client.Exchange(cancellationToken: timeout.Token);
        await socket.RequestStream.WriteAsync(new Proto.DeviceWebFrame
        {
            Request = new Proto.DeviceWebRequestHead { DeviceId = deviceId, Method = "GET", PathAndQuery = "/vapix/ws-data-stream?sources=events&wssession=" + token, Websocket = true },
        });
        Assert.True(await socket.ResponseStream.MoveNext(timeout.Token));
        output.WriteLine($"WebSocket -> {socket.ResponseStream.Current.Response.Status}");
        Assert.Equal(101, socket.ResponseStream.Current.Response.Status);
        await socket.RequestStream.WriteAsync(new Proto.DeviceWebFrame { Close = new Proto.DeviceWebSocketClose { Status = 1000 } });
        await socket.RequestStream.CompleteAsync();
        while (await socket.ResponseStream.MoveNext(timeout.Token))
        {
            if (socket.ResponseStream.Current.KindCase == Proto.DeviceWebFrame.KindOneofCase.Close)
            {
                break;
            }
        }
    }

    [HardwareFact]
    public async Task AWebSocketWithoutSessionTokenLogsInWithTheStoredLogin()
    {
        var camera = Camera;
        await using var host = await TestServerHost.StartAsync(useRealNetwork: true);
        var (session, discovered) = await TestHelpers.ScanAsync(host, camera.Address, camera.Address);
        var found = Assert.Single(discovered);
        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { found.DiscoveredId },
            Credentials = { new Proto.DeviceCredentials { UserName = camera.User, Password = camera.Password } },
        });
        var deviceId = Assert.Single(reply.DeviceIds);
        output.WriteLine("scheme " + (await TestHelpers.GetDeviceAsync(host, deviceId)).Scheme);
        var client = new Proto.DeviceWebService.DeviceWebServiceClient(host.Invoker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = client.Exchange(cancellationToken: timeout.Token);
        await socket.RequestStream.WriteAsync(new Proto.DeviceWebFrame
        {
            Request = new Proto.DeviceWebRequestHead { DeviceId = deviceId, Method = "GET", PathAndQuery = "/vapix/ws-data-stream?sources=events", Websocket = true },
        });
        Assert.True(await socket.ResponseStream.MoveNext(timeout.Token));
        var head = socket.ResponseStream.Current.Response;
        output.WriteLine($"WebSocket without token -> {head.Status}");
        if (head.Status != 101 && await socket.ResponseStream.MoveNext(timeout.Token))
        {
            output.WriteLine(socket.ResponseStream.Current.Body.ToStringUtf8());
        }

        Assert.Equal(101, head.Status);
        await socket.RequestStream.WriteAsync(new Proto.DeviceWebFrame { Close = new Proto.DeviceWebSocketClose { Status = 1000 } });
        await socket.RequestStream.CompleteAsync();
    }

    private static string? Header(Proto.DeviceWebResponseHead head, string name) =>
        head.Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static async Task<(Proto.DeviceWebResponseHead Head, byte[] Body)> GetAsync(Proto.DeviceWebService.DeviceWebServiceClient client, string deviceId, string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = client.Exchange(cancellationToken: timeout.Token);
        await call.RequestStream.WriteAsync(new Proto.DeviceWebFrame { Request = new Proto.DeviceWebRequestHead { DeviceId = deviceId, Method = "GET", PathAndQuery = path } });
        await call.RequestStream.CompleteAsync();
        Proto.DeviceWebResponseHead? head = null;
        var body = new List<byte>();
        while (await call.ResponseStream.MoveNext(timeout.Token))
        {
            var frame = call.ResponseStream.Current;
            if (frame.KindCase == Proto.DeviceWebFrame.KindOneofCase.Response)
            {
                head = frame.Response;
            }
            else if (frame.KindCase == Proto.DeviceWebFrame.KindOneofCase.Body)
            {
                body.AddRange(frame.Body.ToByteArray());
            }
        }

        return (head!, [.. body]);
    }
}
