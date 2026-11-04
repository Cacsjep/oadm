using System.Net.Http.Headers;
using System.Text.Json;

namespace Oadm.Contracts.Updates;

/// <summary>One file of a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, long Size, Uri Url);

/// <summary>A stable release of OADM with its files.</summary>
public sealed record ReleaseOffer(Version Version, string Tag, string Notes, IReadOnlyList<ReleaseAsset> Assets)
{
    public string VersionText => UpdateVersions.Plain(Version);

    public ReleaseAsset? Find(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>The releases of <c>Cacsjep/oadm</c> on GitHub (rule 4: stable releases only, over HTTPS).</summary>
public interface IReleaseFeed
{
    /// <summary>The latest stable release; null when GitHub cannot be reached or answers something else.</summary>
    Task<ReleaseOffer?> LatestAsync(CancellationToken ct);

    /// <summary>The stable release of <paramref name="version"/>; null when there is none or GitHub cannot be reached.</summary>
    Task<ReleaseOffer?> ByVersionAsync(Version version, CancellationToken ct);

    /// <summary>Copies a release file to <paramref name="target"/>, at most <paramref name="maxBytes"/> (more throws <see cref="UpdateRefusedException"/>).</summary>
    Task DownloadAsync(Uri url, Stream target, long maxBytes, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>
/// Reads the GitHub API (<c>api.github.com/repos/Cacsjep/oadm/releases/...</c>) and downloads release files. Drafts and
/// pre-releases are never offered; a file URL outside the repository's release downloads is refused.
/// </summary>
public sealed class GitHubReleaseFeed(HttpClient http, string userAgent, TimeProvider? time = null) : IReleaseFeed
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>A download that receives nothing for this long stops ("Could not download the update.").</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A download never takes longer than this.</summary>
    public static readonly TimeSpan DownloadLimit = TimeSpan.FromMinutes(30);

    /// <summary>The only hosts a release file may come from after GitHub's redirects (the one allow-list).</summary>
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    public const string Repository = "Cacsjep/oadm";
    public const string ApiBase = "https://api.github.com/repos/" + Repository + "/releases/";

    /// <summary>Every installer comes from here (the release's own downloads, https).</summary>
    public const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    /// <summary>How long a read of the release list may take.</summary>
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The feed with its own HTTP client (GitHub, never a device).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "Self update reads GitHub releases, not a device.")]
    public static GitHubReleaseFeed Create(string userAgent, TimeProvider? time = null) => new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, userAgent, time);

    public Task<ReleaseOffer?> LatestAsync(CancellationToken ct) => ReadAsync(new Uri(ApiBase + "latest"), ct);

    public Task<ReleaseOffer?> ByVersionAsync(Version version, CancellationToken ct) =>
        ReadAsync(new Uri(ApiBase + "tags/v" + UpdateVersions.Plain(version)), ct);

    private async Task<ReleaseOffer?> ReadAsync(Uri url, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ApiTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(request);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: limit.Token).ConfigureAwait(false);
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>A release of the API; null for a draft, a pre-release or an unknown tag.</summary>
    public static ReleaseOffer? Parse(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object
            || IsTrue(release, "draft") || IsTrue(release, "prerelease")
            || !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
            || UpdateVersions.ParseStable(tag.GetString()) is not { } version)
        {
            return null;
        }

        var assets = new List<ReleaseAsset>();
        if (release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    && asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes)
                    && asset.TryGetProperty("browser_download_url", out var link) && link.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri))
                {
                    assets.Add(new ReleaseAsset(name.GetString()!, bytes, uri));
                }
            }
        }

        var notes = release.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? string.Empty : string.Empty;
        return new ReleaseOffer(version, tag.GetString()!, notes, assets);
    }

    public async Task DownloadAsync(Uri url, Stream target, long maxBytes, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(target);
        if (!IsReleaseDownload(url))
        {
            throw new UpdateRefusedException(UpdateTexts.WrongName);
        }

        using var overall = new CancellationTokenSource(DownloadLimit, _time);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct, overall.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(request);
            HttpResponseMessage response;
            using (var idle = new CancellationTokenSource(IdleTimeout, _time))
            using (var both = CancellationTokenSource.CreateLinkedTokenSource(limited.Token, idle.Token))
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, both.Token).ConfigureAwait(false);
            }

            using (response)
            {
                if (!IsAllowedFinal(response.RequestMessage?.RequestUri ?? url))
                {
                    throw new UpdateRefusedException(UpdateTexts.DownloadFailed);
                }

                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 0 and var length && length > maxBytes)
                {
                    throw new UpdateRefusedException(UpdateTexts.TooBig);
                }

                await using var body = await response.Content.ReadAsStreamAsync(limited.Token).ConfigureAwait(false);
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    int read;
                    using (var idle = new CancellationTokenSource(IdleTimeout, _time))
                    using (var both = CancellationTokenSource.CreateLinkedTokenSource(limited.Token, idle.Token))
                    {
                        read = await body.ReadAsync(buffer, both.Token).ConfigureAwait(false);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > maxBytes)
                    {
                        throw new UpdateRefusedException(UpdateTexts.TooBig);
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), limited.Token).ConfigureAwait(false);
                    progress?.Report(total);
                }
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Nothing for a minute, or longer than the limit: stopped, nothing installed.
            throw new UpdateRefusedException(UpdateTexts.DownloadFailed, ex);
        }
    }

    /// <summary>Where a release file may end up after redirects: https on <see cref="AllowedHosts"/>.</summary>
    public static bool IsAllowedFinal(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(url.Host);
    }

    /// <summary>A file of this repository's release downloads over https.</summary>
    public static bool IsReleaseDownload(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.Scheme == Uri.UriSchemeHttps && url.AbsoluteUri.StartsWith(DownloadPrefix, StringComparison.Ordinal);
    }

    private void AddHeaders(HttpRequestMessage request) =>
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(userAgent.Split('/')[0], userAgent.Contains('/', StringComparison.Ordinal) ? userAgent.Split('/')[1] : "1"));

    private static bool IsTrue(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>An update refused or stopped with a plain reason (FAILED_PRECONDITION on the server).</summary>
public sealed class UpdateRefusedException : Exception
{
    public UpdateRefusedException()
    {
    }

    public UpdateRefusedException(string message)
        : base(message)
    {
    }

    public UpdateRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
