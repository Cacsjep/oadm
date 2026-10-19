using Google.Protobuf;

using Grpc.Core;

using Microsoft.EntityFrameworkCore;

using Oadm.Contracts.SiteNode;
using Oadm.Core.Sites;
using Oadm.Sites.Binding;

namespace Oadm.Server.Sites;

/// <summary>A change of a site the server refuses: the gRPC status and the plain text shown below the field.</summary>
public sealed class SiteRefusedException : Exception
{
    public SiteRefusedException()
    {
    }

    public SiteRefusedException(string message)
        : base(message)
    {
    }

    public SiteRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SiteRefusedException(StatusCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public StatusCode Code { get; } = StatusCode.FailedPrecondition;
}

/// <summary>Binding, renaming, the address and forgetting of sites (docs/sites.md, Binding).</summary>
public sealed partial class SiteManager
{
    /// <summary>
    /// Binds a waiting node as a new site with the pairing code (SRP-6a over this connection, <see cref="ServerPairing"/>).
    /// Refused: a bad name or code format, a name in use, over <see cref="Site.MaxSites"/>, a copy of a bound node (its
    /// id or key is known), a wrong code (audited "Wrong pairing code"), too many wrong codes, binding closed on the node.
    /// </summary>
    public async Task<Site> BindAsync(string nodeId, string name, string code, CancellationToken ct)
    {
        var siteName = CleanName(name);
        var normalized = PairingCode.Normalize(code) ?? throw new SiteRefusedException(StatusCode.InvalidArgument, SiteTexts.CodeFormat);
        WaitingNode node;
        lock (_lock)
        {
            node = _waiting.GetValueOrDefault(nodeId ?? string.Empty) ?? throw new SiteRefusedException(StatusCode.NotFound, SiteTexts.NodeGone);
            if (_sites.Values.FirstOrDefault(e => string.Equals(e.Row.NodeId, node.NodeId, StringComparison.Ordinal)
                || string.Equals(e.Row.NodeKeyFingerprint, node.Link.NodeFingerprintHex, StringComparison.OrdinalIgnoreCase)) is { } copy)
            {
                throw new SiteRefusedException(StatusCode.AlreadyExists, SiteTexts.CopyOf(copy.Row.Name));
            }

            if (_sites.Count >= Site.MaxSites)
            {
                throw new SiteRefusedException(StatusCode.ResourceExhausted, SiteTexts.TooManySites);
            }

            if (_sites.Values.Any(e => string.Equals(e.Row.Name, siteName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SiteRefusedException(StatusCode.AlreadyExists, SiteTexts.NameTaken);
            }
        }

        if (node.Link.IsClosed)
        {
            throw new SiteRefusedException(StatusCode.NotFound, SiteTexts.NodeGone);
        }

        if (node.Hello.ProtocolVersion < _options.MinProtocolVersion)
        {
            throw new SiteRefusedException(StatusCode.FailedPrecondition, SiteTexts.TooOld);
        }

        var client = node.Link.Client;
        var deadline = DateTime.UtcNow + _options.CallTimeout;
        try
        {
            var start = await client.PairStartAsync(new PairStartRequest(), deadline: deadline, cancellationToken: ct).ConfigureAwait(false);
            ServerPairing pairing;
            try
            {
                pairing = new ServerPairing(normalized, start.Salt.ToByteArray(), start.B.ToByteArray(), node.Link.NodeFingerprint, _identity.Fingerprint);
            }
            catch (ArgumentException)
            {
                throw new SiteRefusedException(StatusCode.FailedPrecondition, SiteTexts.NodeNotAnswering);
            }

            var reply = await client.BindAsync(new BindRequest
            {
                SiteName = siteName,
                ServerName = _serverName,
                A = ByteString.CopyFrom(pairing.A),
                Proof = ByteString.CopyFrom(pairing.Proof),
            }, deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: ct).ConfigureAwait(false);
            if (!pairing.Check(reply.Proof.ToByteArray()))
            {
                // The node did not prove the same code on this connection: something in between.
                await WrongCodeAsync(node).ConfigureAwait(false);
                throw new SiteRefusedException(StatusCode.InvalidArgument, SiteTexts.WrongCode);
            }
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.PermissionDenied)
        {
            await WrongCodeAsync(node).ConfigureAwait(false);
            throw new SiteRefusedException(StatusCode.InvalidArgument, SiteTexts.WrongCode);
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.ResourceExhausted or StatusCode.FailedPrecondition)
        {
            if (e.StatusCode == StatusCode.ResourceExhausted)
            {
                await WrongCodeAsync(node).ConfigureAwait(false);
            }

            throw new SiteRefusedException(e.StatusCode, e.Status.Detail);
        }
        catch (RpcException e) when (!ct.IsCancellationRequested)
        {
            throw new SiteRefusedException(StatusCode.Unavailable, SiteTexts.NodeNotAnswering + " " + e.Status.Detail);
        }

        var row = new Site
        {
            Name = siteName,
            NormalizedName = siteName.ToUpperInvariant(),
            Mode = node.Mode,
            Address = node.Address?.Host ?? node.Link.RemoteHost,
            Port = node.Address?.Port,
            NodeKeyFingerprint = node.Link.NodeFingerprintHex,
            NodeId = node.NodeId,
            BoundUtc = Time.GetUtcNow().UtcDateTime,
            LastSeenUtc = Time.GetUtcNow().UtcDateTime,
            Version = node.Hello.Version,
        };
        await using (var db = await _db.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false))
        {
            db.Sites.Add(row);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        lock (_lock)
        {
            _waiting.Remove(node.NodeId);
            _sites[row.Id] = new SiteEntry(Copy(row), _options.MaxLargeTransfers) { State = SiteState.Online, LastAnswer = Time.GetUtcNow() };
        }

        await AttachControlAsync(row.Id, node.Link, fresh: true).ConfigureAwait(false);
        await _audit.WriteAsync(SiteAudit.Bound, siteName, $"Site node {node.Name}, from {node.Link.RemoteHost}", CancellationToken.None).ConfigureAwait(false);
        LogBound(siteName, node.Link.RemoteHost);
        _ = SendServerAddressesAsync(row.Id);
        Raise([row.Id]);
        return Copy(row);
    }

    private async Task WrongCodeAsync(WaitingNode node)
    {
        LogWrongCode(node.Name, node.Link.RemoteHost);
        await _audit.WriteAsync(SiteAudit.WrongCode, node.Name, "From " + node.Link.RemoteHost, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Sends the node this server's DNS name and addresses (it tries them when the typed address no longer answers).</summary>
    private async Task SendServerAddressesAsync(Guid siteId)
    {
        SiteSession? session;
        lock (_lock)
        {
            session = _sites.GetValueOrDefault(siteId)?.Session;
        }

        if (session is null)
        {
            return;
        }

        var request = new ServerAddresses { DnsName = LocalAddresses.DnsName(), Port = _listener?.Port ?? 5081 };
        request.Addresses.Add(LocalAddresses.Local().Select(a => a.ToString()));
        try
        {
            await session.Control.Client.SetServerAddressesAsync(request, deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: session.Lifetime).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Sent again after the next bind or restart; the node keeps its typed address meanwhile.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>A new name (unique case-insensitive); the node learns it when it answers.</summary>
    public async Task<Site> RenameAsync(Guid siteId, string name, CancellationToken ct)
    {
        var siteName = CleanName(name);
        string old;
        SiteSession? session;
        lock (_lock)
        {
            var entry = _sites.GetValueOrDefault(siteId) ?? throw new SiteRefusedException(StatusCode.NotFound, "The site no longer exists.");
            if (_sites.Values.Any(e => e.Row.Id != siteId && string.Equals(e.Row.Name, siteName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SiteRefusedException(StatusCode.AlreadyExists, SiteTexts.NameTaken);
            }

            old = entry.Row.Name;
            session = entry.Session;
        }

        await using (var db = await _db.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await db.Sites.Where(s => s.Id == siteId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Name, siteName)
                .SetProperty(s => s.NormalizedName, siteName.ToUpperInvariant()), ct).ConfigureAwait(false);
        }

        Site row;
        lock (_lock)
        {
            var entry = _sites[siteId];
            entry.Row.Name = siteName;
            entry.Row.NormalizedName = siteName.ToUpperInvariant();
            row = Copy(entry.Row);
        }

        if (session is not null)
        {
            try
            {
                await session.Control.Client.RenameAsync(new RenameRequest { SiteName = siteName }, deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (RpcException)
            {
                // The node shows the old name until the next rename; OADM uses the new one.
            }
        }

        await _audit.WriteAsync(SiteAudit.Renamed, siteName, old + " to " + siteName, CancellationToken.None).ConfigureAwait(false);
        Raise([siteId]);
        return row;
    }

    /// <summary>Inbound sites: another address of the same node (its key must match).</summary>
    public async Task<Site> ChangeAddressAsync(Guid siteId, string address, int port, CancellationToken ct)
    {
        var text = port > 0 && !address.Contains(':', StringComparison.Ordinal) ? address.Trim() + ":" + port : address;
        var target = ParseNodeAddress(text) ?? throw new SiteRefusedException(StatusCode.InvalidArgument, "Enter an address such as 192.168.0.5 or node.example.com:5081.");
        string pinned;
        lock (_lock)
        {
            var entry = _sites.GetValueOrDefault(siteId) ?? throw new SiteRefusedException(StatusCode.NotFound, "The site no longer exists.");
            if (entry.Row.Mode != SiteMode.Inbound)
            {
                throw new SiteRefusedException(StatusCode.FailedPrecondition, SiteTexts.InboundOnly);
            }

            pinned = entry.Row.NodeKeyFingerprint;
        }

        NodeLink link;
        try
        {
            link = await ConnectToNodeAsync(target.Host, target.Port, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or System.Security.Authentication.AuthenticationException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new SiteRefusedException(StatusCode.Unavailable, SiteTexts.NodeNotAnswering);
        }

        var same = string.Equals(link.NodeFingerprintHex, pinned, StringComparison.OrdinalIgnoreCase);
        await link.DisposeAsync().ConfigureAwait(false);
        if (!same)
        {
            throw new SiteRefusedException(StatusCode.FailedPrecondition, SiteTexts.OtherNode);
        }

        string old;
        SiteSession? session;
        await using (var db = await _db.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            await db.Sites.Where(s => s.Id == siteId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Address, target.Host)
                .SetProperty(s => s.Port, target.Port), ct).ConfigureAwait(false);
        }

        Site row;
        lock (_lock)
        {
            var entry = _sites[siteId];
            old = entry.Row.Address + ":" + entry.Row.Port;
            entry.Row.Address = target.Host;
            entry.Row.Port = target.Port;
            session = entry.Session;
            row = Copy(entry.Row);
        }

        if (session is not null)
        {
            await session.EndAsync().ConfigureAwait(false); // reached again at the new address
        }
        else
        {
            StartConnector(siteId);
        }

        await _audit.WriteAsync(SiteAudit.AddressChanged, row.Name, $"{old} to {target.Host}:{target.Port}", CancellationToken.None).ConfigureAwait(false);
        RaiseList();
        return row;
    }

    /// <summary>
    /// Forgets the site: sends Unbind (the node opens binding again), ends the session and deletes the row. Returns whether
    /// the node answered; a node that does not answer is forgotten anyway.
    /// </summary>
    public async Task<bool> ForgetAsync(Guid siteId, CancellationToken ct)
    {
        SiteSession? session;
        CancellationTokenSource? connector;
        lock (_lock)
        {
            var entry = _sites.GetValueOrDefault(siteId) ?? throw new SiteRefusedException(StatusCode.NotFound, "The site no longer exists.");
            session = entry.Session;
            connector = entry.Connector;
        }

        var answered = false;
        if (session is { IsEnded: false })
        {
            try
            {
                await session.Control.Client.UnbindAsync(new UnbindRequest(), deadline: DateTime.UtcNow + TimeSpan.FromSeconds(5), cancellationToken: ct).ConfigureAwait(false);
                answered = true;
            }
            catch (RpcException)
            {
            }
        }

        await using (var db = await _db.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await db.Sites.Where(s => s.Id == siteId).ExecuteDeleteAsync(CancellationToken.None).ConfigureAwait(false);
        }

        lock (_lock)
        {
            _sites.Remove(siteId);
        }

        if (connector is not null)
        {
            await connector.CancelAsync().ConfigureAwait(false);
        }

        if (session is not null)
        {
            // An outbound node that unbound itself connects again and waits to be bound.
            await session.EndAsync().ConfigureAwait(false);
        }

        Raise([siteId]);
        return answered;
    }

    /// <summary>A new site identity (signed with the old key) for every connected node; others ask to accept it in their app.</summary>
    public async Task<int> RotateIdentityAsync(CancellationToken ct)
    {
        var (_, certificate, signature) = await _identity.RotateAsync(_serverName, ct).ConfigureAwait(false);
        List<SiteSession> sessions;
        lock (_lock)
        {
            sessions = [.. _sites.Values.Select(e => e.Session).OfType<SiteSession>().Where(s => !s.IsEnded)];
        }

        var told = 0;
        foreach (var session in sessions)
        {
            try
            {
                await session.Control.Client.RotateIdentityAsync(new RotateIdentityRequest
                {
                    NewCertificate = ByteString.CopyFrom(certificate),
                    Signature = ByteString.CopyFrom(signature),
                }, deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: ct).ConfigureAwait(false);
                told++;
            }
            catch (RpcException)
            {
            }
        }

        return told;
    }

    /// <summary>The bound sites (copies).</summary>
    public IReadOnlyList<Site> Sites
    {
        get
        {
            lock (_lock)
            {
                return [.. _sites.Values.Select(e => Copy(e.Row))];
            }
        }
    }

    private static string CleanName(string? name)
    {
        var value = new string([.. (name ?? string.Empty).Where(c => !char.IsControl(c))]).Trim();
        if (value.Length == 0)
        {
            throw new SiteRefusedException(StatusCode.InvalidArgument, SiteTexts.NameRequired);
        }

        return value.Length > Site.MaxNameLength ? throw new SiteRefusedException(StatusCode.InvalidArgument, SiteTexts.NameTooLong) : value;
    }

    private static Site Copy(Site row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        NormalizedName = row.NormalizedName,
        Mode = row.Mode,
        Address = row.Address,
        Port = row.Port,
        NodeKeyFingerprint = row.NodeKeyFingerprint,
        NodeId = row.NodeId,
        BoundUtc = row.BoundUtc,
        LastSeenUtc = row.LastSeenUtc,
        Version = row.Version,
        NotBound = row.NotBound,
    };
}
