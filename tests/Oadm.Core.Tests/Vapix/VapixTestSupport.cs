using System.Net;
using System.Text;

namespace Oadm.Core.Tests.Vapix;

/// <summary>Reads recorded responses from Fixtures/Vapix.</summary>
internal static class Fixtures
{
    public static string Read(string name)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Vapix", name));
    }

    /// <summary>Values of all WWW-Authenticate lines of a recorded header file.</summary>
    public static IReadOnlyList<string> WwwAuthenticate(string headerFile)
    {
        return Read(headerFile)
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("www-authenticate:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["www-authenticate:".Length..].Trim())
            .ToList();
    }

    public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/plain")
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return Text(body, status, "application/json");
    }

    /// <summary>A 401 carrying the WWW-Authenticate headers of a recorded header file.</summary>
    public static HttpResponseMessage Unauthorized(string headerFile)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) };
        foreach (var value in WwwAuthenticate(headerFile))
        {
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", value);
        }

        return response;
    }
}

/// <summary>Records requests (with buffered bodies) and answers them through a delegate.</summary>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri!, body));
        }

        var response = respond(request, body);
        response.RequestMessage = request;
        return response;
    }
}
