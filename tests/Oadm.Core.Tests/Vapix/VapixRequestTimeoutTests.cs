using System.Net;

using Oadm.Core.Vapix;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Vapix;

public sealed class VapixRequestTimeoutTests
{
    private static readonly Uri Base = new("http://10.0.0.48/");

    [Fact]
    public async Task TheDefaultTimeoutAppliesPerRequestAndLooksLikeAnHttpClientTimeout()
    {
        using var client = new VapixClient(Base, new SlowHandler(TimeSpan.FromSeconds(5)), timeout: TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetBasicDeviceInfoAsync(CancellationToken.None));

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal(Sdk.Devices.DeviceStatus.Unreachable, DeviceStatusClassifier.FromException(ex));
    }

    [Fact]
    public async Task APluginCanExtendTheTimeoutOfOneRequest()
    {
        var handler = new SlowHandler(TimeSpan.FromMilliseconds(300));
        using var client = new VapixClient(Base, handler, timeout: TimeSpan.FromMilliseconds(50));
        await using var body = new MemoryStream(new byte[1024]);
        using var request = new HttpRequestMessage(HttpMethod.Post, "axis-cgi/firmwaremanagement.cgi") { Content = new StreamContent(body) };
        request.Options.Set(VapixRequestOptions.Timeout, TimeSpan.FromSeconds(10));

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Oadm.RequestTimeout", VapixRequestOptions.TimeoutKey);
        Assert.IsType<StreamContent>(handler.LastContent); // streamed as given, not replaced by a buffered copy
        Assert.Equal(TimeSpan.FromMilliseconds(50), client.Timeout);
    }

    [Fact]
    public async Task AnInfiniteOverrideDisablesTheTimeout()
    {
        using var client = new VapixClient(Base, new SlowHandler(TimeSpan.FromMilliseconds(200)), timeout: TimeSpan.FromMilliseconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, "axis-cgi/x.cgi");
        request.Options.Set(VapixRequestOptions.Timeout, Timeout.InfiniteTimeSpan);

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CallerCancellationIsNotATimeout()
    {
        using var client = new VapixClient(Base, new SlowHandler(TimeSpan.FromSeconds(5)), timeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetBasicDeviceInfoAsync(cts.Token));

        Assert.IsNotType<TimeoutException>(ex.InnerException);
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        public HttpContent? LastContent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastContent = request.Content;
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
