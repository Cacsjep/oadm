using System.Diagnostics;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander;

/// <summary>Everything about one sent command: the interpretation plus the raw exchange for the Try view.</summary>
public sealed class CommandOutcome
{
    public required bool Success { get; init; }

    /// <summary>Extracted result after success, or the error text (device text or transport text).</summary>
    public required string Summary { get; init; }

    public IReadOnlyList<NamedValue> Values { get; init; } = [];

    public IReadOnlyList<NamedValue> Parameters { get; init; } = [];

    /// <summary>Null when no HTTP answer arrived (timeout, connection, TLS).</summary>
    public int? StatusCode { get; init; }

    public string? ReasonPhrase { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<NamedValue> ResponseHeaders { get; init; } = [];

    public string? ContentType { get; init; }

    /// <summary>Response body, pretty-printed and cut to <see cref="CommandExecutor.MaxBodyChars"/>.</summary>
    public string? Body { get; init; }

    public bool BodyTruncated { get; init; }

    /// <summary>"GET /axis-cgi/param.cgi?..." with password values masked.</summary>
    public string? RequestLine { get; init; }

    public string? RequestBody { get; init; }
}

/// <summary>Sends a rendered command with the device's pre-authenticated VAPIX client and interprets the answer.</summary>
public static class CommandExecutor
{
    public const int MaxBodyChars = 256 * 1024;

    /// <summary>
    /// Sends <paramref name="request"/>. Transport failures become an unsuccessful outcome with a readable text;
    /// only cancellation through <paramref name="ct"/> is thrown.
    /// </summary>
    public static async Task<CommandOutcome> ExecuteAsync(IVapixClient vapix, CommandDefinition command, RenderedRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(request);
        var watch = Stopwatch.StartNew();
        using var http = request.ToHttpRequest();
        HttpResponseMessage response;
        try
        {
            response = await vapix.SendAsync(http, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Every transport failure is a result for the user, never a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new CommandOutcome
            {
                Success = false,
                Summary = request.Mask(TransportErrors.Describe(ex, request.Timeout)),
                Duration = watch.Elapsed,
                RequestLine = request.Describe(),
                RequestBody = request.DescribeBody(),
            };
        }

        using (response)
        {
            // Decoded as UTF-8 here: Axis devices send "charset=utf8", which ReadAsStringAsync rejects.
            var bytes = response.Content is null ? [] : await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            watch.Stop();
            var contentType = response.Content?.Headers.ContentType?.ToString();
            var spec = request.Response ?? command.Response;
            Interpretation interpretation;
            string pretty;
            if (ResponseInterpreter.IsBinary(contentType, bytes))
            {
                var described = ResponseInterpreter.DescribeBinary(contentType, bytes.LongLength);
                interpretation = response.IsSuccessStatusCode
                    ? new Interpretation(true, described, [], [])
                    : Interpretation.Fail(ResponseInterpreter.StatusText((int)response.StatusCode, response.ReasonPhrase));
                pretty = "(" + described + ")";
            }
            else
            {
                var body = System.Text.Encoding.UTF8.GetString(bytes);
                if (body.Length > 0 && body[0] == '\uFEFF')
                {
                    body = body[1..];
                }

                interpretation = ResponseInterpreter.Interpret(spec, (int)response.StatusCode, response.ReasonPhrase, contentType, body);
                pretty = ResponseInterpreter.Pretty(body, contentType);
            }

            var headers = response.Headers.Concat(response.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .Select(h => new NamedValue(h.Key, string.Join(", ", h.Value)))
                .ToList();
            return new CommandOutcome
            {
                Success = interpretation.Success,
                Summary = request.Mask(interpretation.Summary),
                Values = interpretation.Values,
                Parameters = interpretation.Parameters,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                Duration = watch.Elapsed,
                ResponseHeaders = headers,
                ContentType = contentType,
                Body = pretty.Length > MaxBodyChars ? pretty[..MaxBodyChars] : pretty,
                BodyTruncated = pretty.Length > MaxBodyChars,
                RequestLine = request.Describe(),
                RequestBody = request.DescribeBody(),
            };
        }
    }
}
