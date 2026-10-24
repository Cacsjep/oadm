using System.Net;
using System.Security.Cryptography;
using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Tls;

namespace Oadm.Plugins.Radius.Protocol.Sessions;

/// <summary>What happened to a datagram.</summary>
public enum RadiusOutcome
{
    /// <summary><see cref="RadiusResult.Response"/> is the answer to send to the source.</summary>
    Answered,
    /// <summary>A retransmission: <see cref="RadiusResult.Response"/> is the cached answer, nothing was processed again.</summary>
    AnsweredFromCache,
    /// <summary>Silently dropped (RFC 2865 3); <see cref="RadiusResult.Drop"/> says why.</summary>
    Dropped,
}

/// <summary>Why a datagram was dropped (for the debug log and the switch row's counters).</summary>
public enum RadiusDropReason
{
    None,
    /// <summary>Not a RADIUS packet, or an attribute breaks the rules ("Parsing and RFC compliance").</summary>
    Malformed,
    /// <summary>A code other than Access-Request or Status-Server (accounting, answers).</summary>
    NotARequest,
    /// <summary>No Message-Authenticator or a wrong one: counted as "Wrong secret" on the switch.</summary>
    WrongSecret,
    /// <summary>A retransmission whose answer is not ready yet (RFC 5080 2.2.2).</summary>
    InProgress,
    /// <summary>An EAP response that matches no outstanding request (RFC 3748 4.1).</summary>
    StaleEapResponse,
    /// <summary>The answer would exceed 4,096 bytes (e.g. huge Proxy-State attributes).</summary>
    AnswerTooLarge,
    /// <summary>A fault of the server itself while processing (never expected; the session ends, the request is dropped).</summary>
    InternalError,
    /// <summary>
    /// An Access-Request with neither EAP-Message nor Message-Authenticator: a MAC address check (MAC authentication bypass)
    /// or a password check, not 802.1X. Counted apart from a wrong secret (RFC 3579 3.2 requires the Message-Authenticator
    /// only with EAP-Message).
    /// </summary>
    NotEap,
    /// <summary>A Status-Server while the server cannot authenticate (no server certificate): silently discarded (RFC 5997 4.2).</summary>
    NotReady,
}

/// <summary>The result of one datagram.</summary>
/// <param name="Outcome">Answered, from the cache, or dropped.</param>
/// <param name="Response">The answer to send back to the source; null when dropped.</param>
/// <param name="Drop">Why it was dropped.</param>
/// <param name="Detail">A short English detail for the debug log (never secrets, keys or payload).</param>
/// <param name="Session">The session after this step, when the request belonged to one.</param>
/// <param name="Reason">Why an Access-Reject was sent, when one was.</param>
public sealed record RadiusResult(RadiusOutcome Outcome, byte[]? Response, RadiusDropReason Drop, string? Detail, EapSessionSnapshot? Session, string? Reason = null)
{
    internal static RadiusResult Dropped(RadiusDropReason reason, string? detail, EapSessionSnapshot? session = null) => new(RadiusOutcome.Dropped, null, reason, detail, session);
}

/// <summary>
/// The RADIUS/EAP-TLS server without sockets (docs/spec/radius-server.md "Protocol"): one datagram in, at most one answer out.
/// Per datagram: parse (RFC 2865 3), only Access-Request and Status-Server, the Message-Authenticator checked before
/// anything else (RFC 3579 3.2, Blast-RADIUS), Status-Server answered (RFC 5997), the attributes read, duplicates answered
/// from the cache (RFC 5080 2.2.2), then the EAP conversation of the State attribute's session (or a new one). Answers carry
/// the Message-Authenticator first, the Response Authenticator and the request's Proxy-State attributes in order (RFC 2865
/// 5.33). Thread-safe: sessions are processed one request at a time each, different sessions in parallel; the server
/// package bounds the parallel handshakes. Sessions time out through <see cref="ExpireSessions"/> (called every second).
/// </summary>
public sealed class RadiusProcessor
{
    private readonly IClientCertificateValidator _validator;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, EapTlsSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EapTlsSession> _byPort = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _perClient = new(StringComparer.Ordinal);
    private readonly Lock _table = new();
    private volatile RadiusProtocolOptions _options;

    public RadiusProcessor(RadiusProtocolOptions options, IClientCertificateValidator validator, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validator);
        _options = options;
        _validator = validator;
        _time = time ?? TimeProvider.System;
        Duplicates = new RadiusDuplicateCache(_time);
    }

    /// <summary>The settings; a new value applies to new sessions at once (switches and settings change while the server runs).</summary>
    public RadiusProtocolOptions Options
    {
        get => _options;
        set => _options = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The duplicate cache (20,000 answers or 32 MB, 30 s each, and the final answers of ended sessions).</summary>
    public RadiusDuplicateCache Duplicates { get; }

    /// <summary>
    /// Raised for every state change of a session, on the thread that processes it and while the session is locked: keep
    /// the handler short (queue the snapshot) and never call back into the processor from it.
    /// </summary>
    public event Action<EapSessionSnapshot>? SessionChanged;

    /// <summary>Open sessions now.</summary>
    public int SessionCount
    {
        get
        {
            lock (_table)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>The open sessions.</summary>
    public IReadOnlyList<EapSessionSnapshot> Sessions()
    {
        lock (_table)
        {
            return [.. _sessions.Values.Select(s => s.Snapshot)];
        }
    }

    /// <summary>
    /// Processes one datagram of <paramref name="client"/> (found by the server package by the datagram's source address)
    /// that came from <paramref name="source"/>. Never throws for any input.
    /// </summary>
    public RadiusResult Process(ReadOnlySpan<byte> datagram, RadiusClient client, IPEndPoint source)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(source);
        var parsed = RadiusPacket.Parse(datagram);
        if (parsed.Packet is not { } packet)
        {
            return RadiusResult.Dropped(RadiusDropReason.Malformed, parsed.Detail);
        }

        if (packet.Code is not (RadiusCode.AccessRequest or RadiusCode.StatusServer))
        {
            return RadiusResult.Dropped(RadiusDropReason.NotARequest, $"Code {(byte)packet.Code}");
        }

        var secret = client.Secret.Span;
        if (packet.Code == RadiusCode.AccessRequest && packet.Count(RadiusAttributeType.MessageAuthenticator) == 0
            && packet.Count(RadiusAttributeType.EapMessage) == 0)
        {
            return RadiusResult.Dropped(RadiusDropReason.NotEap, "No EAP-Message (MAC address or password check)");
        }

        if (!RadiusAuthenticators.VerifyRequest(packet, secret))
        {
            return RadiusResult.Dropped(RadiusDropReason.WrongSecret,
                packet.Count(RadiusAttributeType.MessageAuthenticator) == 0 ? "No Message-Authenticator" : "Wrong Message-Authenticator");
        }

        var proxyStates = packet.All(RadiusAttributeType.ProxyState);
        if (packet.Code == RadiusCode.StatusServer)
        {
            if (_options.ServerIdentity is null)
            {
                // Running but unable to authenticate (no certificate from the OADM CA): silently discarded, so the switch
                // fails over (RFC 5997 4.2).
                return RadiusResult.Dropped(RadiusDropReason.NotReady, "Status-Server without a server certificate");
            }

            // RFC 5997 3: Access-Accept with the Message-Authenticator; no duplicate cache, no other work.
            var status = RadiusPacketWriter.Response(RadiusCode.AccessAccept, packet.Identifier, packet.Authenticator.Span, secret, proxyStates);
            return status is null
                ? RadiusResult.Dropped(RadiusDropReason.AnswerTooLarge, "Status-Server answer too large")
                : new RadiusResult(RadiusOutcome.Answered, status, RadiusDropReason.None, "Status-Server", null);
        }

        var read = AccessRequest.Read(packet);
        if (read.Request is not { } request)
        {
            return RadiusResult.Dropped(RadiusDropReason.Malformed, read.Detail);
        }

        var duplicate = Duplicates.Begin(client.Key, source, packet.Identifier, packet.Authenticator.Span);
        switch (duplicate.State)
        {
            case DuplicateState.InProgress:
                return RadiusResult.Dropped(RadiusDropReason.InProgress, "Retransmission still in work");
            case DuplicateState.Answered:
                return new RadiusResult(RadiusOutcome.AnsweredFromCache, duplicate.Response, RadiusDropReason.None, "Retransmission", null);
        }

        RadiusResult? result = null;
        try
        {
            result = Handle(request, client);
            return result;
        }
#pragma warning disable CA1031 // one bad request must never stop the listener; fail closed (no answer)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return RadiusResult.Dropped(RadiusDropReason.InternalError, ex.GetType().Name);
        }
        finally
        {
            Duplicates.Complete(client.Key, source, packet.Identifier, packet.Authenticator.Span, result?.Response);
        }
    }

    /// <summary>Ends the sessions past their time (30 s since the last message, 60 s in all) as Timed out; returns them.</summary>
    public IReadOnlyList<EapSessionSnapshot> ExpireSessions()
    {
        var now = _time.GetUtcNow();
        List<EapTlsSession> expired;
        lock (_table)
        {
            expired = [.. _sessions.Values.Where(s => IsExpired(s.Snapshot, now))];
        }

        List<EapSessionSnapshot> ended = [];
        foreach (var session in expired)
        {
            lock (session)
            {
                if (session.IsEnded || !IsExpired(session.Snapshot, now))
                {
                    continue;
                }

                session.End(EapSessionState.TimedOut, null);
                ended.Add(session.Snapshot);
            }

            Remove(session);
        }

        return ended;
    }

    /// <summary>
    /// Ends every open session of a removed switch as Abandoned and forgets its cached answers (its next request is dropped
    /// as from an unknown switch by the server package).
    /// </summary>
    public IReadOnlyList<EapSessionSnapshot> EndSessions(string clientKey)
    {
        ArgumentNullException.ThrowIfNull(clientKey);
        List<EapTlsSession> sessions;
        lock (_table)
        {
            sessions = [.. _sessions.Values.Where(s => s.Client == clientKey)];
        }

        List<EapSessionSnapshot> ended = [];
        foreach (var session in sessions)
        {
            lock (session)
            {
                if (session.IsEnded)
                {
                    continue;
                }

                session.End(EapSessionState.Abandoned, EapSessionTexts.SwitchRemoved);
                ended.Add(session.Snapshot);
            }

            Remove(session);
        }

        Duplicates.Forget(clientKey);
        return ended;
    }

    private bool IsExpired(EapSessionSnapshot snapshot, DateTimeOffset now) =>
        now - snapshot.LastMessage >= _options.MessageTimeout || now - snapshot.Started >= _options.SessionTimeout;

    private RadiusResult Handle(AccessRequest request, RadiusClient client)
    {
        var options = _options;
        if (request.Eap is not { } eapBytes)
        {
            // Not EAP: never a password check (docs/spec "Required in each Access-Request"; RFC 3579 2.1).
            return Answer(request, client, RadiusCode.AccessReject, [], null, EapSessionTexts.OnlyEapTls);
        }

        EapPacket? eap = null;
        if (eapBytes.Length > 0)
        {
            var parsed = EapPacket.Parse(eapBytes);
            if (parsed.Packet is null || parsed.Packet.Code != EapCode.Response)
            {
                var identifier = eapBytes.Length >= 2 ? eapBytes[1] : (byte)0;
                var reason = parsed.Packet?.Code == EapCode.Request ? EapSessionTexts.RoleReversal : EapSessionTexts.MessageUnreadable;
                var session = request.State is { } state ? Find(client, state) : null;
                EapSessionSnapshot? snapshot = null;
                if (session is not null)
                {
                    lock (session)
                    {
                        session.End(EapSessionState.Failed, reason);
                        snapshot = session.Snapshot;
                    }

                    Remove(session);
                }

                // Role reversal: Access-Reject with EAP-Response/Nak, no alternative (RFC 3579 2.6.2); a fatal error: EAP-Failure (RFC 3579 2.2).
                var answer = parsed.Packet?.Code == EapCode.Request
                    ? EapPacket.Response(identifier, EapType.Nak, new byte[] { 0 })
                    : EapPacket.Failure(identifier);
                return Answer(request, client, RadiusCode.AccessReject, RadiusAttribute.EapMessages(answer.ToBytes()), snapshot, reason);
            }

            eap = parsed.Packet;
        }

        if (request.State is { } stateValue)
        {
            var session = Find(client, stateValue);
            if (session is null)
            {
                // A retransmission of the request that ended a session gets that answer again, also when the busy duplicate
                // cache dropped it meanwhile (RFC 5080 2.2.2).
                return Duplicates.FindFinal(client.Key, stateValue, request.Packet.Authenticator.Span) is { } final
                    ? new RadiusResult(RadiusOutcome.AnsweredFromCache, final, RadiusDropReason.None, "Retransmission of the final answer", null)
                    : Reject(request, client, eap, null, EapSessionTexts.SessionNotFound);
            }

            EapStep? step = null;
            RadiusResult? accepted = null;
            string? refusal = null;
            EapSessionSnapshot snapshot;
            lock (session)
            {
                if (session.IsEnded)
                {
                    refusal = EapSessionTexts.SessionNotFound;
                }
                else if (IsExpired(session.Snapshot, _time.GetUtcNow()))
                {
                    session.End(EapSessionState.TimedOut, null);
                    refusal = EapSessionTexts.SessionNotFound;
                }
                else if (eap is null)
                {
                    // EAP-Start only starts a session (RFC 3579 2.1), never continues one.
                    session.End(EapSessionState.Failed, EapSessionTexts.UnexpectedMessage);
                    refusal = EapSessionTexts.UnexpectedMessage;
                }
                else
                {
                    step = session.Handle(eap, request);
                    if (step is { Kind: EapStepKind.Accept } success)
                    {
                        // The session is Authenticated only when the Access-Accept can go out (never logged as passed without it).
                        accepted = Respond(request, client, success, session.Snapshot, session.State);
                        session.CompleteAccept(accepted.Outcome == RadiusOutcome.Answered);
                        accepted = accepted with { Session = session.Snapshot };
                    }
                }

                snapshot = session.Snapshot;
            }

            if (snapshot.IsEnded)
            {
                Remove(session);
            }

            var result = accepted
                ?? (step is { } done ? Respond(request, client, done, snapshot, session.State) : Reject(request, client, eap, snapshot, refusal!));
            if (snapshot.IsEnded && result is { Outcome: RadiusOutcome.Answered, Response: { } response })
            {
                Duplicates.RememberFinal(client.Key, session.State, request.Packet.Authenticator.Span, response);
            }

            return result;
        }

        // A new session (no State, RFC 5080 2.1.1).
        var created = Create(request, client, options);
        if (created is null)
        {
            return Reject(request, client, eap, null, EapSessionTexts.TooManySessions);
        }

        EapStep first;
        EapSessionSnapshot firstSnapshot;
        lock (created)
        {
            first = created.Start(eap, request);
            firstSnapshot = created.Snapshot;
        }

        if (firstSnapshot.IsEnded)
        {
            Remove(created);
        }

        return Respond(request, client, first, firstSnapshot, created.State);
    }

    private static RadiusResult Respond(AccessRequest request, RadiusClient client, EapStep step, EapSessionSnapshot snapshot, byte[] state)
    {
        switch (step.Kind)
        {
            case EapStepKind.Drop:
                return RadiusResult.Dropped(RadiusDropReason.StaleEapResponse, "EAP response does not match the outstanding request", snapshot);
            case EapStepKind.Challenge:
            {
                List<RadiusAttribute> attributes = [.. RadiusAttribute.EapMessages(step.Packet!.ToBytes())];
                attributes.Add(new RadiusAttribute(RadiusAttributeType.State, state));
                return Answer(request, client, RadiusCode.AccessChallenge, attributes, snapshot, null);
            }

            case EapStepKind.Accept:
            {
                // EAP-Success, the User-Name of the request (RFC 3579 3 "MUST include"), the keys (RFC 2548), the VLAN (RFC 3580 3.31).
                List<RadiusAttribute> attributes = [.. RadiusAttribute.EapMessages(step.Packet!.ToBytes())];
                if (request.Packet.Find(RadiusAttributeType.UserName) is { } userName)
                {
                    attributes.Add(userName);
                }

                attributes.AddRange(MppeKeys.Attributes(step.Msk, client.Secret.Span, request.Packet.Authenticator.Span));
                CryptographicOperations.ZeroMemory(step.Msk);
                if (snapshot.VlanId is { } vlan)
                {
                    attributes.AddRange(VlanAttributes.Attributes(vlan));
                }

                return Answer(request, client, RadiusCode.AccessAccept, attributes, snapshot, null);
            }

            default:
                return Answer(request, client, RadiusCode.AccessReject, RadiusAttribute.EapMessages(step.Packet!.ToBytes()), snapshot, snapshot.Reason);
        }
    }

    private static RadiusResult Reject(AccessRequest request, RadiusClient client, EapPacket? eap, EapSessionSnapshot? snapshot, string reason) =>
        Answer(request, client, RadiusCode.AccessReject, RadiusAttribute.EapMessages(EapPacket.Failure(eap?.Identifier ?? 0).ToBytes()), snapshot, reason);

    /// <summary>The answer with the Message-Authenticator first and the request's Proxy-State attributes last, in order (RFC 2865 5.33).</summary>
    private static RadiusResult Answer(AccessRequest request, RadiusClient client, RadiusCode code, IReadOnlyList<RadiusAttribute> attributes, EapSessionSnapshot? snapshot, string? reason)
    {
        List<RadiusAttribute> all = [.. attributes, .. request.ProxyStates];
        var packet = request.Packet;
        var bytes = RadiusPacketWriter.Response(code, packet.Identifier, packet.Authenticator.Span, client.Secret.Span, all);
        return bytes is null
            ? RadiusResult.Dropped(RadiusDropReason.AnswerTooLarge, "Answer larger than 4096 bytes", snapshot)
            : new RadiusResult(RadiusOutcome.Answered, bytes, RadiusDropReason.None, null, snapshot, code == RadiusCode.AccessReject ? reason : null);
    }

    private static string KeyOf(string client, ReadOnlySpan<byte> state) => client + "\u001f" + Convert.ToHexString(state);

    private EapTlsSession? Find(RadiusClient client, byte[] state)
    {
        lock (_table)
        {
            return _sessions.GetValueOrDefault(KeyOf(client.Key, state));
        }
    }

    private EapTlsSession? Create(AccessRequest request, RadiusClient client, RadiusProtocolOptions options)
    {
        EapTlsSession? previous = null;
        var portKey = EapTlsSession.PortKeyOf(client.Key, request);
        lock (_table)
        {
            if (portKey is not null && _byPort.TryGetValue(portKey, out var existing))
            {
                previous = existing;
            }
        }

        if (previous is not null)
        {
            // The switch started a new session for the port: the old one is abandoned.
            lock (previous)
            {
                if (!previous.IsEnded)
                {
                    previous.End(EapSessionState.Abandoned, EapSessionTexts.NewSession);
                }
            }

            Remove(previous);
        }

        lock (_table)
        {
            var perClient = _perClient.GetValueOrDefault(client.Key);
            if (_sessions.Count >= options.MaxSessions || perClient >= options.MaxSessionsPerClient)
            {
                return null;
            }

            var state = RandomNumberGenerator.GetBytes(16);
            var session = new EapTlsSession(Guid.NewGuid(), client.Key, state, request, options, () => _options.ServerIdentity, _validator, _time, s => SessionChanged?.Invoke(s));
            _sessions[KeyOf(client.Key, state)] = session;
            _perClient[client.Key] = perClient + 1;
            if (session.PortKey is { } key)
            {
                _byPort[key] = session;
            }

            return session;
        }
    }

    private void Remove(EapTlsSession session)
    {
        lock (_table)
        {
            if (!_sessions.Remove(KeyOf(session.Client, session.State)))
            {
                return;
            }

            if (session.PortKey is { } key && _byPort.TryGetValue(key, out var current) && ReferenceEquals(current, session))
            {
                _byPort.Remove(key);
            }

            var count = _perClient.GetValueOrDefault(session.Client) - 1;
            if (count <= 0)
            {
                _perClient.Remove(session.Client);
            }
            else
            {
                _perClient[session.Client] = count;
            }
        }
    }
}
