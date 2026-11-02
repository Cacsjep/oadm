using System.Net;
using System.Text;

namespace Oadm.Plugins.Firmware.Online;

/// <summary>A page of www.axis.com: its text, or not there (404, or a redirect: axis.com answers missing pages with 307).</summary>
public sealed record AxisPage(bool Found, string Text)
{
    public static readonly AxisPage Missing = new(false, string.Empty);
}

/// <summary>axis.com did not answer (network, timeout, a server error); the message is for the log.</summary>
public sealed class AxisSiteException : Exception
{
    public AxisSiteException()
    {
    }

    public AxisSiteException(string message)
        : base(message)
    {
    }

    public AxisSiteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reads the public listing pages of www.axis.com/ftp/pub_soft (no sign-in, no cookies). Replaced by a fake in tests.</summary>
public interface IAxisSite
{
    /// <summary>The page at <paramref name="path"/> ("/ftp/pub_soft/MPQT/"); throws <see cref="AxisSiteException"/> when axis.com does not answer.</summary>
    Task<AxisPage> GetAsync(string path, CancellationToken ct);
}

/// <summary>
/// The real <see cref="IAxisSite"/>: HTTPS to www.axis.com only, paths below <c>/ftp/pub_soft/</c> only, no cookies, no
/// redirects (a redirect means the page is not there), 15 s per page, at most 8 MB per page. Never a device and never a
/// .bin: the downloads go through the host (<c>IAxisDownloads</c>) with the user's MyAxis sign-in.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The public AXIS OS listings on www.axis.com, never a device.")]
public sealed class AxisSite : IAxisSite
{
    /// <summary>Largest page read (the MPQT root listing is about 210 KB).</summary>
    public const int MaxPageBytes = 8 * 1024 * 1024;

    private static readonly Uri Base = new(OnlineFirmware.Site);

    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient(
        new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        },
        disposeHandler: true)
    {
        BaseAddress = Base,
        Timeout = TimeSpan.FromSeconds(15),
    });

    public static AxisSite Instance { get; } = new();

    public async Task<AxisPage> GetAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith("/ftp/pub_soft/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal) || path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only the listings below /ftp/pub_soft/ are read.", nameof(path));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, path));
            using var response = await SharedClient.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 404 or 410 || status is >= 300 and < 400)
            {
                return AxisPage.Missing;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AxisSiteException($"www.axis.com answered {status} for {path}.");
            }

            if (response.Content.Headers.ContentLength > MaxPageBytes)
            {
                throw new AxisSiteException($"The page {path} is larger than expected.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxPageBytes)
                {
                    throw new AxisSiteException($"The page {path} is larger than expected.");
                }

                buffer.Write(chunk, 0, read);
            }

            return new AxisPage(true, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
        catch (HttpRequestException ex)
        {
            throw new AxisSiteException($"www.axis.com could not be reached ({ex.Message}).", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AxisSiteException("www.axis.com did not answer in time.", ex);
        }
        catch (IOException ex)
        {
            throw new AxisSiteException($"www.axis.com could not be read ({ex.Message}).", ex);
        }
    }
}
