using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Identity;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Feed;
using Oadm.Sites.Transport;

namespace Oadm.SiteNode.Links;

/// <summary>
/// Mode Connect (default, docs/sites.md "Direction"): the node opens TCP + TLS to the server's site port and then serves
/// gRPC on that connection. Not bound with binding open: any server key (the pairing proves both sides); bound: only the
/// pinned key (another one is noted for "Accept" in the app); binding closed: no connection. The typed address first,
/// then the server's own addresses (SetServerAddresses); after a failure 1, 2, 4 ... 60 s. OpenConnections adds worker
/// connections to the same address, at most 8.
/// </summary>
public sealed partial class OutboundConnector(
    NodeStore store,
    BindingState binding,
    NodeIdentity identity,
    LinkRegistry links,
    ConnectionState connection,
    NodeOptions options,
    ILogger<OutboundConnector> logger) : BackgroundService
{
    public const int MaxWorkers = 8;

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly List<SiteLink> _workers = [];
    private (string Host, int Port)? _current;
    private string _key = "";
    private CancellationTokenSource? _control;

    /// <summary>Opens up to <paramref name="count"/> more worker connections (at most 8 in all); returns how many are open.</summary>
    public async Task<int> OpenWorkersAsync(int count, CancellationToken ct)
    {
        (string Host, int Port)? target;
        CancellationToken controlToken;
        int missing;
        lock (_lock)
        {
            target = _current;
            controlToken = _control?.Token ?? new CancellationToken(canceled: true);
            missing = Math.Clamp(count, 0, MaxWorkers - _workers.Count);
        }

        if (target is null || controlToken.IsCancellationRequested)
            return 0;
        await Task.WhenAll(Enumerable.Range(0, missing).Select(_ => OpenWorkerAsync(target.Value, controlToken, ct))).ConfigureAwait(false);
        lock (_lock)
            return _workers.Count;
    }

    public int Workers
    {
        get
        {
            lock (_lock)
                return _workers.Count;
        }
    }

    /// <summary>The server addresses tried, in order: the typed one, then the server's DNS name and addresses.</summary>
    public static IReadOnlyList<(string Host, int Port)> Targets(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var targets = new List<(string Host, int Port)>();
        if (ParseAddress(config.ServerAddress, NodeConfig.DefaultPort) is { } typed)
            targets.Add(typed);
        var port = config.ServerAddresses.Port is > 0 and <= 65535 ? config.ServerAddresses.Port : NodeConfig.DefaultPort;
        foreach (var address in new[] { config.ServerAddresses.DnsName }.Concat(config.ServerAddresses.Addresses))
        {
            if (ParseAddress(address, port) is { } other && !targets.Exists(t => string.Equals(t.Host, other.Host, StringComparison.OrdinalIgnoreCase) && t.Port == other.Port))
                targets.Add(other);
        }

        return targets;
    }

    /// <summary>The typed server address as host and port (<see cref="ServerAddress.Parse"/>).</summary>
    public static (string Host, int Port)? ParseAddress(string? text, int defaultPort) => ServerAddress.Parse(text, defaultPort);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void Wake() => _wake.Release();
        store.Changed += Wake;
        binding.Changed += Wake;
        binding.IdentityReset += Wake;
        try
        {
            var backoff = TimeSpan.FromSeconds(1);
            while (!stoppingToken.IsCancellationRequested)
            {
                var config = store.Snapshot();
                var targets = Targets(config);
                if (config.Mode != NodeLinkMode.Connect || binding.Binding == NodeBinding.Closed || targets.Count == 0)
                {
                    if (config.Mode == NodeLinkMode.Connect)
                        connection.Set(binding.Binding == NodeBinding.Closed ? AdminTexts.BindingClosed : "", false);
                    await WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var key = KeyOf(config);
                SslStream? tls = null;
                (string Host, int Port) used = default;
                foreach (var target in targets)
                {
                    tls = await TryConnectAsync(target, stoppingToken).ConfigureAwait(false);
                    if (tls is not null)
                    {
                        used = target;
                        break;
                    }
                }

                if (tls is null)
                {
                    connection.Set(AdminTexts.CannotReach(Name(targets[0])), false);
                    if (await WaitAsync(backoff, stoppingToken).ConfigureAwait(false) && !string.Equals(KeyOf(store.Snapshot()), key, StringComparison.Ordinal))
                        backoff = TimeSpan.FromSeconds(1); // the address changed: try at once
                    else
                        backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, options.MaxBackoff.Ticks));
                    continue;
                }

                backoff = TimeSpan.FromSeconds(1);
                connection.Set(AdminTexts.Connected(Name(used)), true);
                await ServeControlAsync(tls, used, key, stoppingToken).ConfigureAwait(false);
                connection.Set(AdminTexts.CannotReach(Name(used)), false);
                if (!stoppingToken.IsCancellationRequested)
                    await WaitAsync(backoff, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            store.Changed -= Wake;
            binding.Changed -= Wake;
            binding.IdentityReset -= Wake;
        }
    }

    // Serves the control link until it closes, or until something the link depends on changes (mode, address, binding,
    // identity): then it and every worker close and the loop connects again.
    private async Task ServeControlAsync(SslStream tls, (string Host, int Port) target, string key, CancellationToken stoppingToken)
    {
        var link = NewLink(tls, target, worker: false);
        using var control = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_lock)
        {
            _current = target;
            _control = control;
            _key = key;
        }

        var serving = links.ServeAsync(tls, link, control.Token);
        try
        {
            while (!serving.IsCompleted)
            {
                var wake = _wake.WaitAsync(control.Token);
                await Task.WhenAny(serving, wake).ConfigureAwait(false);
                if (wake.IsCompletedSuccessfully && !string.Equals(KeyOf(store.Snapshot()), key, StringComparison.Ordinal))
                    break;
                if (!wake.IsCompleted)
                {
                    await control.CancelAsync().ConfigureAwait(false);
                    await Quiet(wake).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _current = null;
                _control = null;
            }

            await control.CancelAsync().ConfigureAwait(false);
            await Quiet(serving).ConfigureAwait(false);
            List<SiteLink> workers;
            lock (_lock)
            {
                workers = [.. _workers];
                _workers.Clear();
            }

            foreach (var worker in workers)
                worker.Close();
        }
    }

    private async Task OpenWorkerAsync((string Host, int Port) target, CancellationToken control, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(control, ct);
        var tls = await TryConnectAsync(target, linked.Token).ConfigureAwait(false);
        if (tls is null)
            return;
        var link = NewLink(tls, target, worker: true);
        lock (_lock)
        {
            if (_workers.Count >= MaxWorkers || control.IsCancellationRequested)
            {
                tls.Dispose();
                return;
            }

            _workers.Add(link);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await links.ServeAsync(tls, link, control).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                lock (_lock)
                    _workers.Remove(link);
            }
        }, CancellationToken.None);
    }

    private SiteLink NewLink(SslStream tls, (string Host, int Port) target, bool worker)
    {
        var certificate = tls.RemoteCertificate is X509Certificate2 c2 ? c2 : X509CertificateLoader.LoadCertificate(tls.RemoteCertificate!.GetRawCertData());
        return new SiteLink(outbound: true, worker, Name(target), certificate, identity.Fingerprint);
    }

    private async Task<SslStream?> TryConnectAsync((string Host, int Port) target, CancellationToken ct)
    {
        var bound = binding.Bound;
        string? presented = null;
        var client = new TcpClient(AddressFamily.InterNetworkV6) { NoDelay = true };
        client.Client.DualMode = true;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(options.ConnectTimeout);
                await client.ConnectAsync(target.Host, target.Port, connect.Token).ConfigureAwait(false);
            }

            var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            var accept = SiteTls.ClientOptions(identity.Certificate, fingerprint =>
            {
                if (fingerprint is null)
                    return false;
                if (bound is null)
                    return true; // pairing proves the server
                var hex = Convert.ToHexString(fingerprint);
                if (string.Equals(hex, bound.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    return true;
                presented = hex;
                return false;
            });
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(SiteTls.HandshakeTimeout);
            try
            {
                await tls.AuthenticateAsClientAsync(accept, handshake.Token).ConfigureAwait(false);
                return tls;
            }
            catch
            {
                await tls.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception e) when (e is SocketException or IOException or System.Security.Authentication.AuthenticationException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            client.Dispose();
            var name = Name(target);
            if (presented is not null)
            {
                LogNewIdentity(logger, name, presented);
                binding.NoteNewServerIdentity(presented);
            }
            else
            {
                LogCannotConnect(logger, name, e.Message);
            }

            return null;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // What the control link depends on (not the bound server: Unbind and RotateIdentity answer on the link and keep it).
    private string KeyOf(NodeConfig config) =>
        string.Join('|', config.Mode, config.ServerAddress, binding.Binding == NodeBinding.Closed, identity.FingerprintHex);

    // True when woken (a change), false after the time.
    private async Task<bool> WaitAsync(TimeSpan time, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (time != Timeout.InfiniteTimeSpan)
            timeout.CancelAfter(time);
        try
        {
            await _wake.WaitAsync(timeout.Token).ConfigureAwait(false);
            while (_wake.CurrentCount > 0)
                await _wake.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string Name((string Host, int Port) target) =>
        target.Host is null ? "" : target.Host.Contains(':', StringComparison.Ordinal) ? $"[{target.Host}]:{target.Port}" : $"{target.Host}:{target.Port}";

    private static async Task Quiet(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cannot connect to the server at {Target}: {Reason}")]
    private static partial void LogCannotConnect(ILogger logger, string target, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server at {Target} presents a new identity {Fingerprint}; accept it in the OADM Site Node app")]
    private static partial void LogNewIdentity(ILogger logger, string target, string fingerprint);
}
