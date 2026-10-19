using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Oadm.Server.Tests.Support;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// A <see cref="FakeAxisDevice"/> served over real HTTP on a random loopback port, so a site node in this process can dial
/// it (the node maps 192.168.0.x to it). Anonymous requests only: the fake sees no login.
/// </summary>
internal sealed class LoopbackAxisDevice : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LoopbackAxisDevice(WebApplication app, FakeAxisDevice device, IPEndPoint endPoint)
    {
        _app = app;
        Device = device;
        EndPoint = endPoint;
    }

    public FakeAxisDevice Device { get; }

    public IPEndPoint EndPoint { get; }

    public static async Task<LoopbackAxisDevice> StartAsync(FakeAxisDevice device)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                new Uri($"http://{device.Address}{context.Request.Path}{context.Request.QueryString}"));
            HttpResponseMessage response;
            try
            {
                lock (device)
                {
                    response = device.Handle(request, body, null);
                }
            }
            catch (HttpRequestException)
            {
                context.Abort(); // the fake refuses: drop the connection
                return;
            }

            using (response)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers)
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }

                if (response.Content is { } content)
                {
                    foreach (var header in content.Headers)
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }

                    await content.CopyToAsync(context.Response.Body, context.RequestAborted);
                }
            }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new LoopbackAxisDevice(app, device, new IPEndPoint(IPAddress.Loopback, new Uri(address).Port));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
