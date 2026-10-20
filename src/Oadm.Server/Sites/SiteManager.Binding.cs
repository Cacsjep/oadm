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

/// <summary>Binding, renaming and forgetting of sites (docs/sites.md, Binding).</summary>
public sealed partial class SiteManager
{
    private readonly SemaphoreSlim _bindLock = new(1, 1);

    /// <summary>Binds the one waiting node that reports <paramref name="nodeId"/> (older clients; see the overload).</summary>
    public Task<Site> BindAsync(string nodeId, string name, string code, CancellationToken ct) => BindAsync(nodeId, null, name, code, ct);

    /// <summary>
    /// Binds a waiting node as a new site with the pairing code (J-PAKE over this connection, <see cref="ServerPairing"/>).
    /// The node is chosen by its key fingerprint (<paramref name="fingerprint"/>; without it the only waiting node that
    /// reports <paramref name="nodeId"/>). Binds run one at a time, so two of them never pass the name and count checks
    /// together. Refused: a bad name or code format, a name in use, over <see cref="Site.MaxSites"/>, a copy of a bound
    /// node (its id or key is known), a wrong code (audited "Wrong pairing code"), too many wrong codes or attempts, binding
    /// closed on the node. A node of a site that accepted this server's new identity binds that site again. When the site
    /// cannot be stored after the node bound, the node is sent Unbind.
    /// </summary>
    public async Task<Site> BindAsync(string? nodeId, string? fingerprint, string name, string code, CancellationToken ct)
    {
        var siteName = CleanName(name);
        var normalized = PairingCode.Normalize(code) ?? throw new SiteRefusedException(StatusCode.InvalidArgument, SiteTexts.CodeFormat);
        await _bindLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await BindLockedAsync(nodeId, fingerprint, siteName, normalized, ct).ConfigureAwait(false);
        }
        finally
        {
            _bindLock.Release();
        }
    }

    private async Task<Site> BindLockedAsync(string? nodeId, string? fingerprint, string siteName, string normalized, CancellationToken ct)
    {
        WaitingNode node;
        Guid? rebind = null;
        lock (_lock)
        {
            node = FindWaiting(nodeId, fingerprint);
            var same = _sites.Values.FirstOrDefault(e => string.Equals(e.Row.NodeId, node.NodeId, StringComparison.Ordinal)
                || string.Equals(e.Row.NodeKeyFingerprint, node.Key, StringComparison.OrdinalIgnoreCase));
            if (same is not null)
            {
                rebind = node.RebindOf is not null && IsRebind(same, node)
                    ? same.Row.Id
                    : throw new SiteRefusedException(StatusCode.AlreadyExists, SiteTexts.CopyOf(same.Row.Name));
            }

            if (rebind is null && _sites.Count >= Site.MaxSites)
            {
                throw new SiteRefusedException(StatusCode.ResourceExhausted, SiteTexts.TooManySites);
            }

            if (_sites.Values.Any(e => e.Row.Id != rebind && string.Equals(e.Row.Name, siteName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SiteRefusedException(StatusCode.AlreadyExists, SiteTexts.NameTaken);
            }
        }

        var link = LinkForBind(node);
        if (link.Hello?.ProtocolVersion < _options.MinProtocolVersion)
        {
            throw new SiteRefusedException(StatusCode.FailedPrecondition, SiteTexts.TooOld);
        }

        await PairAsync(node, link, siteName, normalized, ct).ConfigureAwait(false);

        // The node is bound from here on: a site that cannot be stored must not leave it bound to nothing.
        Site row;
        try
        {
            row = await StoreBoundAsync(node, link, siteName, rebind).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any failure to store: the node must not stay bound to nothing.
        catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogWriteFailed(e);
            await UnbindQuietlyAsync(link).ConfigureAwait(false);
            throw new SiteRefusedException(StatusCode.Internal, SiteTexts.NotStored);
        }

        link.Trust();
        await Point("bound", CancellationToken.None).ConfigureAwait(false);
        if (!await AttachControlAsync(row.Id, link, fresh: true).ConfigureAwait(false))
        {
            // Accept site nodes turned off or the server stops: the site is bound and stored, its node connects again later.
            await link.DisposeAsync().ConfigureAwait(false);
        }

        await _audit.WriteAsync(SiteAudit.Bound, siteName, $"Site node {node.Name}, from {link.RemoteHost}", CancellationToken.None).ConfigureAwait(false);
        LogBound(siteName, link.RemoteHost);
        _ = SendServerAddressesAsync(row.Id);
        Raise([row.Id]);
        return Copy(row);
    }

    // The waiting node by its key; without one, the only waiting node with that id.
    private WaitingNode FindWaiting(string? nodeId, string? fingerprint)
    {
        if (!string.IsNullOrEmpty(fingerprint))
        {
            var byKey = _waiting.GetValueOrDefault(fingerprint);
            return byKey is not null && (string.IsNullOrEmpty(nodeId) || string.Equals(byKey.NodeId, nodeId, StringComparison.Ordinal))
                ? byKey
                : throw new SiteRefusedException(StatusCode.NotFound, SiteTexts.NodeGone);
        }

        var byId = _waiting.Values.Where(w => string.Equals(w.NodeId, nodeId, StringComparison.Ordinal)).Take(2).ToList();
        return byId.Count switch
        {
            1 => byId[0],
            0 => throw new SiteRefusedException(StatusCode.NotFound, SiteTexts.NodeGone),
            _ => throw new SiteRefusedException(StatusCode.FailedPrecondition, SiteTexts.NodeIdAmbiguous),
        };
    }

    // The waiting node's link (the connection the node opened); gone when the node went away.
    private static NodeLink LinkForBind(WaitingNode node) =>
        node.Link.IsClosed ? throw new SiteRefusedException(StatusCode.NotFound, SiteTexts.NodeGone) : node.Link;

    // PairStart and Bind: the server's round 1, the node's rounds 1 and 2, the server's round 2 and proof, the node's proof.
    private async Task PairAsync(WaitingNode node, NodeLink link, string siteName, string normalized, CancellationToken ct)
    {
        var client = link.Client;
        try
        {
            var pairing = new ServerPairing(normalized, link.NodeFingerprint, _identity.Fingerprint);
            var start = await client.PairStartAsync(new PairStartRequest { ServerRound1 = pairing.Round1 },
                deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: ct).ConfigureAwait(false);
            PakeRound2 round2;
            byte[] proof;
            try
            {
                (round2, proof) = pairing.Continue(start.NodeRound1, start.NodeRound2);
            }
            catch (ArgumentException)
            {
                throw new SiteRefusedException(StatusCode.FailedPrecondition, SiteTexts.InvalidAnswer);
            }

            // Bind runs to its end whatever the caller does: a node that bound and whose answer the server never read would
            // stay bound to a server without the site. A lost answer (no refusal) is followed by Unbind for the same reason.
            await Point("bind-call", ct).ConfigureAwait(false);
            BindReply reply;
            try
            {
                reply = await client.BindAsync(new BindRequest
                {
                    SiteName = siteName,
                    ServerName = _serverName,
                    ServerRound2 = round2,
                    Proof = ByteString.CopyFrom(proof),
                }, deadline: DateTime.UtcNow + _options.CallTimeout, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (RpcException e) when (e.StatusCode is not (StatusCode.PermissionDenied or StatusCode.ResourceExhausted or StatusCode.FailedPrecondition or StatusCode.InvalidArgument))
            {
                await UnbindQuietlyAsync(link).ConfigureAwait(false);
                throw new SiteRefusedException(StatusCode.Unavailable, SiteTexts.NodeNotAnswering + " " + (HelloRules.IsText(e.Status.Detail, 200) ? e.Status.Detail : string.Empty));
            }

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
        catch (RpcException e) when (e.StatusCode is StatusCode.ResourceExhausted or StatusCode.FailedPrecondition or StatusCode.InvalidArgument)
        {
            if (e.StatusCode == StatusCode.ResourceExhausted)
            {
                await WrongCodeAsync(node).ConfigureAwait(false);
            }

            throw new SiteRefusedException(e.StatusCode == StatusCode.InvalidArgument ? StatusCode.FailedPrecondition : e.StatusCode,
                HelloRules.IsText(e.Status.Detail, 200) ? e.Status.Detail : SiteTexts.NodeNotAnswering);
        }
        catch (RpcException e) when (!ct.IsCancellationRequested)
        {
            throw new SiteRefusedException(StatusCode.Unavailable, SiteTexts.NodeNotAnswering + " " + (HelloRules.IsText(e.Status.Detail, 200) ? e.Status.Detail : string.Empty));
        }
    }

    // The new site's row (or, binding again, the site's row with the node's new binding), in the database and in memory.
    private async Task<Site> StoreBoundAsync(WaitingNode node, NodeLink link, string siteName, Guid? rebind)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var host = link.RemoteHost;
        var version = link.Hello?.Version ?? node.Hello.Version;
        Site row;
        await using (var db = await _db.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (rebind is { } id)
            {
                row = await db.Sites.SingleAsync(s => s.Id == id, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                row = new Site { NodeKeyFingerprint = link.NodeFingerprintHex, NodeId = node.NodeId };
                db.Sites.Add(row);
            }

            row.Name = siteName;
            row.NormalizedName = siteName.ToUpperInvariant();
            row.Mode = SiteMode.Outbound;
            row.Address = host;
            row.Port = null;
            row.BoundUtc = now;
            row.LastSeenUtc = now;
            row.Version = version;
            row.NotBound = false;
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        lock (_lock)
        {
            if (_waiting.GetValueOrDefault(node.Key) is { } waiting && (waiting == node || waiting.Link == link))
            {
                _waiting.Remove(node.Key);
            }

            if (rebind is { } id && _sites.TryGetValue(id, out var entry))
            {
                entry.Row.Name = row.Name;
                entry.Row.NormalizedName = row.NormalizedName;
                entry.Row.Address = row.Address;
                entry.Row.Port = row.Port;
                entry.Row.BoundUtc = row.BoundUtc;
                entry.Row.LastSeenUtc = row.LastSeenUtc;
                entry.Row.Version = row.Version;
                entry.Row.NotBound = false;
                entry.State = SiteState.Online;
                entry.LastAnswer = Time.GetTimestamp();
            }
            else
            {
                _sites[row.Id] = new SiteEntry(Copy(row), _options.MaxLargeTransfers) { State = SiteState.Online, LastAnswer = Time.GetTimestamp() };
            }
        }

        return row;
    }

    private async Task UnbindQuietlyAsync(NodeLink link)
    {
        try
        {
            await link.Client.UnbindAsync(new UnbindRequest(), deadline: DateTime.UtcNow + TimeSpan.FromSeconds(5), cancellationToken: _stopping).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            // The node stays bound to this server without a site; Reset in its app ends that.
        }
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

    /// <summary>
    /// Forgets the site: sends Unbind (the node opens binding again), ends the session and deletes the row. Returns whether
    /// the node answered; a node that does not answer is forgotten anyway.
    /// </summary>
    public async Task<bool> ForgetAsync(Guid siteId, CancellationToken ct)
    {
        // The site leaves memory and its session is taken in one lock section: a node that connects again meanwhile finds
        // no site (AttachControlAsync), so no session can start that nobody ends.
        SiteSession? session;
        lock (_lock)
        {
            var entry = _sites.GetValueOrDefault(siteId) ?? throw new SiteRefusedException(StatusCode.NotFound, "The site no longer exists.");
            session = entry.Session;
            entry.Session = null;
            _sites.Remove(siteId);
        }

        await Point("forgetting", ct).ConfigureAwait(false);

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

        if (session is not null)
        {
            // A node that unbound itself connects again and waits to be bound.
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
