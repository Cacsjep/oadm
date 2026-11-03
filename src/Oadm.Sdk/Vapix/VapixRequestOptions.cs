namespace Oadm.Sdk.Vapix;

/// <summary>
/// Per-request options understood by <see cref="IVapixClient.SendAsync"/>. Set them on
/// <see cref="HttpRequestMessage.Options"/>.
/// </summary>
public static class VapixRequestOptions
{
    /// <summary>Name of the <see cref="Timeout"/> option.</summary>
    public const string TimeoutKey = "Oadm.RequestTimeout";

    /// <summary>
    /// Overrides the default request timeout (15 s) for one request, e.g. 20 minutes for a firmware or
    /// ACAP upload: <c>request.Options.Set(VapixRequestOptions.Timeout, TimeSpan.FromMinutes(20))</c>.
    /// The timeout covers sending the body and reading the response. Request bodies are streamed,
    /// never buffered by the client (use <see cref="StreamContent"/> for large files).
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables it.
    /// </summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> Timeout = new(TimeoutKey);

    /// <summary>Name of the <see cref="StreamResponse"/> option.</summary>
    public const string StreamResponseKey = "Oadm.StreamResponse";

    /// <summary>
    /// True returns the response as soon as its headers arrived, without buffering the body: read it with
    /// <see cref="HttpContent.ReadAsStreamAsync(CancellationToken)"/> (e.g. straight into a file) and dispose the response.
    /// The 16 MB answer limit of the buffered mode does not apply, so the caller enforces its own limit while it reads,
    /// and <see cref="Timeout"/> covers only the time until the headers: bound the body read with your own token.
    /// For large read-only downloads such as server reports: <c>request.Options.Set(VapixRequestOptions.StreamResponse, true)</c>.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> StreamResponse = new(StreamResponseKey);

    /// <summary>Name of the <see cref="LiveStream"/> option.</summary>
    public const string LiveStreamKey = "Oadm.LiveStream";

    /// <summary>
    /// True marks a streamed answer (<see cref="StreamResponse"/>) as a live stream that runs as long as the plugin reads it,
    /// such as an MJPEG stream of pictures (SDK 0.10.0). Through a site it takes none of the site's large transfer slots, which
    /// AXIS OS upgrades, application installs and exports need: a live stream would hold one for as long as it runs. A plugin
    /// that sets it limits its own streams per site. A large request body still takes a slot.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> LiveStream = new(LiveStreamKey);
}
