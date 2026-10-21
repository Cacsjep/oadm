using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.NtpServer.Protocol;
using Oadm.Sdk.Sites;

namespace Oadm.Plugins.NtpServer.Serving;

/// <summary>
/// "Serve at sites" for one site: the datagrams of the site node's UDP relay are answered here from the same time source
/// as the server's own network. The relay's round trip is part of the delay the device measures, like any network hop.
/// Each site has its own rate limiter, so the limits are kept per (site, client address) and overlapping addresses of two
/// sites never share a bucket.
/// </summary>
public sealed partial class SiteServer : IAsyncDisposable
{
    /// <summary>Offsets beyond this are shown as unknown (clients that randomize the transmit timestamp).</summary>
    private static readonly double MaxShownOffsetMilliseconds = TimeSpan.FromDays(1).TotalMilliseconds;

    private readonly Func<TimeSourceState> _source;
    private readonly NtpRateLimiter _limiter;
    private readonly RequestLog _log;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private long _answered;

    public SiteServer(Guid siteId, string name, IUdpRelay relay, Func<TimeSourceState> source, RateLimitOptions limits, RequestLog log, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(log);
        SiteId = siteId;
        Name = name;
        Relay = relay;
        _source = source;
        _time = time ?? TimeProvider.System;
        _limiter = new NtpRateLimiter(limits, _time);
        _log = log;
        _logger = logger ?? NullLogger.Instance;
    }

    public Guid SiteId { get; }

    /// <summary>The site's name (follows a rename).</summary>
    public string Name { get; set; }

    public IUdpRelay Relay { get; }

    /// <summary>Requests answered through this site.</summary>
    public long AnsweredCount => Interlocked.Read(ref _answered);

    /// <summary>The site's state for the page.</summary>
    public SiteServing Serving => new(SiteId, Relay.StateText, Relay.State == RelayState.Running);

    public void Start() => _loop = Task.Run(() => ServeAsync(_stop.Token));

    private async Task ServeAsync(CancellationToken ct)
    {
        var answer = new byte[NtpPacket.Length];
        while (!ct.IsCancellationRequested)
        {
            Datagram datagram;
            try
            {
                datagram = await Relay.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or System.Threading.Channels.ChannelClosedException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                await HandleAsync(datagram, answer, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One bad datagram never stops serving the site.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(Name, ex.Message);
            }
        }
    }

    private async ValueTask HandleAsync(Datagram datagram, byte[] answer, DateTime receivedUtc, CancellationToken ct)
    {
        if (!NtpResponder.IsValidRequest(datagram.Data.Span, out var parsed))
        {
            return; // silently, like the server's own network
        }

        var client = ClientAddress.FromIPAddress(datagram.Remote.Address);
        var rate = _limiter.Check(client);
        switch (rate.Decision)
        {
            case RateDecision.GlobalDrop:
                return;
            case RateDecision.Drop:
            case RateDecision.DropWithKiss:
                if (rate.Log)
                {
                    _log.Add(receivedUtc, client, double.NaN, RequestResult.RateLimited, this);
                }

                if (rate.Decision == RateDecision.DropWithKiss)
                {
                    NtpResponder.BuildKissOfDeath(parsed, receivedUtc, "RATE", answer);
                    await Relay.SendAsync(answer, datagram.Remote, ct).ConfigureAwait(false);
                }

                return;
        }

        if (!NtpResponder.TryBuildResponse(datagram.Data.Span, receivedUtc, _source(), answer, out _))
        {
            return;
        }

        NtpPacket.WriteTransmitTimestamp(answer, NtpTimestamp.FromDateTime(_time.GetUtcNow().UtcDateTime));
        await Relay.SendAsync(answer, datagram.Remote, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _answered);
        var offset = (NtpTimestamp.ToDateTime(parsed.TransmitTimestamp, receivedUtc) - receivedUtc).TotalMilliseconds;
        _log.Add(receivedUtc, client, Math.Abs(offset) > MaxShownOffsetMilliseconds ? double.NaN : offset, RequestResult.Answered, this);
    }

    /// <summary>The server no longer serves at this site: the relay is released, so the node gives the port back.</summary>
    public ValueTask ReleaseAsync() => CloseAsync(release: true);

    public ValueTask DisposeAsync() => CloseAsync(release: false);

    private async ValueTask CloseAsync(bool release)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (release)
        {
            await Relay.ReleaseAsync().ConfigureAwait(false);
        }
        else
        {
            await Relay.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "NTP request through site {Site} not answered: {Error}")]
    private partial void LogFailed(string site, string error);
}
