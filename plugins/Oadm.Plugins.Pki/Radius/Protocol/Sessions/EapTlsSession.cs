using System.Security.Cryptography;
using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Text;
using Oadm.Plugins.Radius.Protocol.Tls;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Oadm.Plugins.Radius.Protocol.Sessions;

/// <summary>What one step of a session answers.</summary>
internal enum EapStepKind
{
    /// <summary>Access-Challenge with <see cref="EapStep.Packet"/> (an EAP-Request).</summary>
    Challenge,
    /// <summary>Access-Accept with EAP-Success and the keys.</summary>
    Accept,
    /// <summary>Access-Reject with EAP-Failure.</summary>
    Reject,
    /// <summary>Nothing: a response that does not match the outstanding request (RFC 3748 4.1, silently discarded).</summary>
    Drop,
}

/// <summary>One step's answer.</summary>
internal readonly record struct EapStep(EapStepKind Kind, EapPacket? Packet, byte[]? Msk = null);

/// <summary>
/// One EAP-TLS conversation of one port (RFC 3748, RFC 5216, RFC 9190): Identity, EAP-TLS Start, the TLS handshake in a
/// non-blocking BouncyCastle <see cref="TlsServerProtocol"/> (no stream, no socket), fragmentation both ways with
/// acknowledgements (RFC 5216 2.1.5), the result (TLS 1.2: the server Finished; TLS 1.3: the commitment message,
/// RFC 9190 2.5) and the MSK (RFC 5216 2.3, RFC 9190 2.3). Not thread-safe: the processor holds its lock.
/// </summary>
internal sealed class EapTlsSession
{

    private readonly RadiusProtocolOptions _options;
    private readonly Func<TlsServerIdentity?> _identity;
    private readonly IClientCertificateValidator _validator;
    private readonly Action<EapSessionSnapshot> _changed;
    private readonly TimeProvider _time;

    private Phase _phase = Phase.New;
    private byte _nextIdentifier;
    private byte? _outstandingIdentifier;
    private byte? _previousIdentifier;
    private byte[]? _outstandingRequest;

    private byte[]? _incoming;
    private int _incomingLength;
    private uint _incomingTotal;
    private int _incomingFragments;
    private int _incomingExpected;

    private byte[]? _outgoing;
    private int _outgoingOffset;
    private int _outgoingIndex;
    private bool _outgoingFinal;
    private int _maxFragment;
    private int? _framedMtu;
    private bool _wireless;

    private EapTlsServer? _server;
    private TlsServerProtocol? _tls;
    private string? _pendingReason;
    private byte[]? _msk;

    private EapSessionSnapshot _snapshot;

    public EapTlsSession(Guid id, string client, byte[] state, AccessRequest first, RadiusProtocolOptions options, Func<TlsServerIdentity?> identity, IClientCertificateValidator validator, TimeProvider time, Action<EapSessionSnapshot> changed)
    {
        Id = id;
        Client = client;
        State = state;
        PortKey = PortKeyOf(client, first);
        SwitchPort = SwitchPortOf(client, first);
        _options = options;
        _identity = identity;
        _validator = validator;
        _time = time;
        _changed = changed;
        _nextIdentifier = (byte)RandomNumberGenerator.GetInt32(256);
        var now = time.GetUtcNow();
        _snapshot = new EapSessionSnapshot
        {
            Id = id,
            Client = client,
            Started = now,
            LastMessage = now,
            State = EapSessionState.WaitingForIdentity,
            Port = first.Port,
            PortType = NasPortTypes.Name(first.NasPortType),
            Mac = first.Mac,
            EapIdentity = first.UserName,
            NasIdentifier = first.NasIdentifier,
            NasAddress = (first.NasIpAddress ?? first.NasIpv6Address)?.ToString(),
        };
    }

    private enum Phase
    {
        New,
        /// <summary>Identity requested.</summary>
        AwaitIdentity,
        /// <summary>EAP-TLS Start sent or the handshake runs.</summary>
        Handshake,
        /// <summary>OADM's last flight sent; the device's empty response leads to EAP-Success (RFC 5216 2.1.3, RFC 9190 2.5).</summary>
        AwaitFinalAck,
        /// <summary>OADM's alert sent; any response leads to EAP-Failure (RFC 5216 2.1.3 "MUST wait for the peer").</summary>
        AwaitFailureAck,
        /// <summary>EAP-Success decided; the session ends with <see cref="CompleteAccept"/> once the Access-Accept is built.</summary>
        AcceptPending,
        Ended,
    }

    public Guid Id { get; }

    public string Client { get; }

    /// <summary>The State attribute value OADM sends in every Access-Challenge (RFC 2865 5.24, RFC 5080 2.1.1).</summary>
    public byte[] State { get; }

    /// <summary>Switch + port + MAC: a new session with the same key abandons this one. Null without port and MAC.</summary>
    public string? PortKey { get; }

    public bool IsEnded => _phase == Phase.Ended;

    public EapSessionSnapshot Snapshot => _snapshot;

    /// <summary>The key of a port: switch, port and MAC (several devices may share one port).</summary>
    /// <summary>The bytes held for the device's message being reassembled (tests: it grows with the fragments). Call inside the session's lock.</summary>
    internal int IncomingCapacity => _incoming?.Length ?? 0;

    /// <summary>The switch and its port (no MAC address): at most a few sessions each; null without a port.</summary>
    public string? SwitchPort { get; }

    /// <summary>The key of <see cref="SwitchPort"/>: the switch and its port; null when the request names no port.</summary>
    public static string? SwitchPortOf(string client, AccessRequest request) =>
        string.IsNullOrEmpty(request.Port) ? null : string.Join('\u001f', client, request.Port);

    public static string? PortKeyOf(string client, AccessRequest request) =>
        string.IsNullOrEmpty(request.Port) && string.IsNullOrEmpty(request.CallingStationId)
            ? null
            : string.Join('\u001f', client, request.Port, request.Mac.Normalized ?? request.CallingStationId ?? string.Empty);

    /// <summary>The first request of the session (no State): EAP-Start, an Identity response, or something the switch started.</summary>
    public EapStep Start(EapPacket? first, AccessRequest request)
    {
        Touch(request);
        if (first is null)
        {
            return AskIdentity(); // EAP-Start (RFC 3579 2.1)
        }

        // The switch sent its own request with this Identifier (its Identity request, or a method it started): OADM's first
        // request must use another one, else the switch and the device take it for a retransmission (RFC 3748 4.1).
        _nextIdentifier = unchecked((byte)(first.Identifier + 1));
        switch (first.Type)
        {
            case EapType.Identity:
                return StartTls(first);
            case EapType.Nak when !first.Data.Span.Contains(EapType.Tls):
                return Fail(first.Identifier, EapSessionTexts.NotEapTls(first.Data.ToArray()));
            default:
                // The switch started a method itself (RFC 3579 2.1, Appendix A): ask for the identity.
                return AskIdentity();
        }
    }

    /// <summary>A response within the session (with OADM's State).</summary>
    public EapStep Handle(EapPacket response, AccessRequest request)
    {
        if (_phase is Phase.Ended or Phase.AcceptPending)
        {
            return new EapStep(EapStepKind.Drop, null);
        }

        if (response.Identifier != _outstandingIdentifier)
        {
            // A repeated Identifier is a retransmission: the outstanding request again (RFC 3748 4.1); anything else is
            // silently discarded (RFC 3748 4.1, RFC 3579 2.2).
            Touch(request);
            return response.Identifier == _previousIdentifier && _outstandingRequest is not null
                ? new EapStep(EapStepKind.Challenge, EapPacket.Parse(_outstandingRequest).Packet)
                : new EapStep(EapStepKind.Drop, null);
        }

        Touch(request);
        if (_snapshot.RoundTrips > _options.MaxRoundTrips)
        {
            return Fail(response.Identifier, EapSessionTexts.TooManyRoundTrips);
        }

        _previousIdentifier = _outstandingIdentifier;
        return response.Type switch
        {
            _ when _phase == Phase.AwaitFailureAck => Fail(response.Identifier, _pendingReason ?? EapSessionTexts.UnexpectedMessage),
            EapType.Nak => Fail(response.Identifier, EapSessionTexts.NotEapTls(response.Data.ToArray())),
            EapType.Identity when _phase == Phase.AwaitIdentity => StartTls(response),
            EapType.Tls when _phase != Phase.AwaitIdentity => HandleTls(response),
            EapType.Identity or EapType.Tls => Fail(response.Identifier, EapSessionTexts.UnexpectedMessage),
            _ => Fail(response.Identifier, EapSessionTexts.OnlyEapTls),
        };
    }

    /// <summary>Ends the session from outside (timeout, abandoned, the switch removed, unreadable message).</summary>
    public void End(EapSessionState state, string? reason)
    {
        if (_phase == Phase.Ended)
        {
            return;
        }

        // A refusal still on its way ends as Failed with its reason, not as Timed out.
        if (state == EapSessionState.TimedOut && _pendingReason is not null)
        {
            state = EapSessionState.Failed;
            reason = _pendingReason;
        }

        Finish(state, reason);
    }

    private void Touch(AccessRequest request)
    {
        // Switches may send Framed-MTU and NAS-Port-Type only in the first request: the smallest MTU of the session counts
        // (RFC 3579 2.4, RFC 3580 3.10).
        if (request.FramedMtu is { } mtu)
        {
            _framedMtu = _framedMtu is { } known ? Math.Min(known, mtu) : mtu;
        }

        _wireless |= request.NasPortType == NasPortTypes.Wireless80211;
        _maxFragment = FragmentSize();
        Update(_snapshot with
        {
            LastMessage = _time.GetUtcNow(),
            RoundTrips = _snapshot.RoundTrips + 1,
            EapIdentity = _snapshot.EapIdentity ?? request.UserName,
        }, notify: false);
    }

    /// <summary>
    /// TLS data per EAP-TLS message: the Largest EAP packet setting (RFC 3748 3.1: 1,020 octets by default), at most
    /// Framed-MTU minus 4 octets of 802.1X header (RFC 3580 3.10, RFC 3579 2.4), an 802.11 port at most 1,496 EAP octets
    /// (RFC 3580 3.10), minus 10 octets of EAP and EAP-TLS header.
    /// </summary>
    private int FragmentSize()
    {
        var eapLimit = _options.LargestFragment;
        if (_framedMtu is { } mtu)
        {
            eapLimit = Math.Min(eapLimit, mtu - 4);
            if (_wireless)
            {
                eapLimit = Math.Min(eapLimit, 1496);
            }
        }

        return Math.Max(eapLimit - EapPacket.HeaderLength - 1 - EapTlsMessage.MaxHeaderLength, 16);
    }

    private EapStep AskIdentity()
    {
        _phase = Phase.AwaitIdentity;
        Update(_snapshot with { State = EapSessionState.WaitingForIdentity, Fragment = 0, FragmentCount = 0 });
        return Challenge(EapType.Identity, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>Identity received: EAP-TLS Start (RFC 5216 2.1.1).</summary>
    private EapStep StartTls(EapPacket identity)
    {
        var text = RadiusText.Decode(identity.Data.Span);
        _phase = Phase.Handshake;
        Update(_snapshot with { State = EapSessionState.StartingTls, EapIdentity = text.Length > 0 ? text : _snapshot.EapIdentity });
        return Challenge(EapType.Tls, EapTlsMessage.StartMessage.ToBytes());
    }

    private EapStep HandleTls(EapPacket response)
    {
        var message = EapTlsMessage.Parse(response.Data.Span);
        if (message is null)
        {
            return Fail(response.Identifier, EapSessionTexts.MessageUnreadable);
        }

        switch (_phase)
        {
            case Phase.AwaitFailureAck:
                return Fail(response.Identifier, _pendingReason ?? EapSessionTexts.UnexpectedMessage);
            case Phase.AwaitFinalAck when _outgoing is null:
                if (message.IsAcknowledgement)
                {
                    return Succeed(response.Identifier);
                }

                // The device refused OADM's Finished or commitment with an alert (RFC 5216 2.1.3, RFC 9190 Figure 5).
                return Fail(response.Identifier, DeviceAlertReason(message.Data) ?? EapSessionTexts.UnexpectedMessage);
        }

        if (_outgoing is not null)
        {
            // A fragment of OADM is outstanding: only an acknowledgement may come (RFC 5216 2.1.5).
            return message.IsAcknowledgement ? NextFragment() : Fail(response.Identifier, EapSessionTexts.UnexpectedMessage);
        }

        if (message.Data.IsEmpty && !message.HasMore)
        {
            return Fail(response.Identifier, EapSessionTexts.UnexpectedMessage);
        }

        var complete = Reassemble(message, out var error);
        if (error is not null)
        {
            return Fail(response.Identifier, error);
        }

        if (complete is null)
        {
            // A fragment of the device: acknowledge it with an empty EAP-TLS request (RFC 5216 2.1.5).
            var receiving = _server?.ClientCertificateSeen == true || _tls is not null ? EapSessionState.ReceivingClientCertificate : EapSessionState.ReceivingClientHello;
            Update(_snapshot with { State = receiving, Fragment = _incomingFragments, FragmentCount = Math.Max(_incomingExpected, _incomingFragments) });
            return Challenge(EapType.Tls, EapTlsMessage.Acknowledgement.ToBytes());
        }

        return OfferToTls(response.Identifier, complete);
    }

    /// <summary>
    /// Joins the device's fragments (RFC 5216 2.1.5, RFC 9190 2.1.9): L on the first fragment of a fragmented message (its
    /// length at most 64 KB), M on all but the last, the fragments add up to L. An unfragmented message may carry L. A later
    /// fragment may repeat L only with the same value. Null while fragments are missing.
    /// </summary>
    private byte[]? Reassemble(EapTlsMessage message, out string? error)
    {
        error = null;
        var data = message.Data.Span;
        if (_incoming is null)
        {
            if (message.HasMore && !message.HasLength)
            {
                error = EapSessionTexts.MessageUnreadable;
                return null;
            }

            if (message.TotalLength is { } total)
            {
                if (total > _options.MaxMessageLength)
                {
                    error = EapSessionTexts.MessageTooLarge;
                    return null;
                }

                if (total < data.Length || (!message.HasMore && total != data.Length) || (message.HasMore && data.IsEmpty))
                {
                    error = EapSessionTexts.MessageUnreadable;
                    return null;
                }
            }

            if (!message.HasMore)
            {
                return data.ToArray();
            }

            // Grows as fragments arrive (never the whole announced length up front: a first fragment alone cannot make the
            // server hold 64 KB per session).
            _incoming = new byte[(int)Math.Min(message.TotalLength!.Value, (uint)Math.Max(data.Length * 2, 1024))];
            _incomingLength = 0;
            _incomingTotal = message.TotalLength.Value;
            _incomingFragments = 0;
            _incomingExpected = (int)((_incomingTotal + (uint)data.Length - 1) / (uint)data.Length);
        }
        else if ((message.TotalLength is { } repeated && repeated != _incomingTotal) || data.IsEmpty)
        {
            error = EapSessionTexts.MessageUnreadable;
            return null;
        }

        if (_incomingLength + data.Length > _incomingTotal)
        {
            error = EapSessionTexts.MessageUnreadable;
            return null;
        }

        if (_incomingLength + data.Length > _incoming.Length)
        {
            Array.Resize(ref _incoming, (int)Math.Min(_incomingTotal, Math.Max(_incoming.Length * 2L, _incomingLength + data.Length)));
        }

        data.CopyTo(_incoming.AsSpan(_incomingLength));
        _incomingLength += data.Length;
        _incomingFragments++;
        if (message.HasMore)
        {
            return null;
        }

        var joined = _incoming;
        _incoming = null;
        if (_incomingLength != _incomingTotal)
        {
            error = EapSessionTexts.MessageUnreadable;
            return null;
        }

        return joined.Length == _incomingLength ? joined : joined.AsSpan(0, _incomingLength).ToArray();
    }

    private EapStep OfferToTls(byte responseIdentifier, byte[] data)
    {
        if (_tls is null)
        {
            var identity = _identity(); // the current one: a renewed certificate applies to every handshake that starts after it
            if (identity is null)
            {
                return Fail(responseIdentifier, EapSessionTexts.NoServerCertificate);
            }

            _server = new EapTlsServer(new BcTlsCrypto(new SecureRandom()), _options.Tls, identity, Check)
            {
                CheckStarting = () => Update(_snapshot with { State = EapSessionState.CheckingClientCertificate, Fragment = 0, FragmentCount = 0 }),
            };
            _tls = new TlsServerProtocol();
            _tls.Accept(_server);
        }

        try
        {
            _tls.OfferInput(data);
        }
        catch (TlsFatalAlertReceived)
        {
            // The device refused OADM: EAP-Failure at once (RFC 5216 2.1.3 "MUST reply with an EAP-Failure").
            return Fail(responseIdentifier, DeviceReason());
        }
#pragma warning disable CA1031 // any TLS failure fails the session, never the server
        catch (Exception)
#pragma warning restore CA1031
        {
            // OADM refused the device: send the alert, wait for the response, then EAP-Failure (RFC 5216 2.1.3, RFC 9190 Figure 4, 6).
            var reason = OadmReason();
            var alert = Drain();
            if (alert.Length == 0)
            {
                return Fail(responseIdentifier, reason);
            }

            _pendingReason = reason;
            _phase = Phase.AwaitFailureAck;
            Update(_snapshot with { Reason = reason, Alert = _server!.Alert });
            return SendFlight(alert, final: true);
        }

        if (_server!.HandshakeComplete)
        {
            byte[] msk;
            try
            {
                if (_server.NegotiatedVersion == TlsVersion.Tls13)
                {
                    // The protected success indication: application data 0x00 after the handshake (RFC 9190 2.5).
                    _tls.WriteApplicationData([0], 0, 1);
                }

                msk = _server.Msk ?? throw new InvalidOperationException("No keys");
                _server.Msk = null;
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                return Fail(responseIdentifier, TlsAlertTexts.Text(80, TlsAlertDirection.SentByOadm));
            }

            _msk = msk;
            _phase = Phase.AwaitFinalAck;
            UpdateTlsDetails();
            return SendFlight(Drain(), final: true);
        }

        var output = Drain();
        UpdateTlsDetails();
        if (output.Length == 0)
        {
            // TLS needs more of the device's flight: ask for it with an empty request.
            Update(_snapshot with { State = _server.ClientCertificateSeen ? EapSessionState.WaitingForClientFinished : EapSessionState.ReceivingClientHello, Fragment = 0, FragmentCount = 0 });
            return Challenge(EapType.Tls, EapTlsMessage.Acknowledgement.ToBytes());
        }

        return SendFlight(output, final: false);
    }

    private ClientCertificateVerdict Check(IReadOnlyList<byte[]> chain, TlsVersion version)
    {
        Update(_snapshot with { ClientCertificates = chain, TlsVersion = version }, notify: false);
        return _validator.Validate(new ClientCertificateCheck(Client, Id, chain, version, _snapshot.EapIdentity, _snapshot.Mac));
    }

    private void UpdateTlsDetails()
    {
        if (_server?.Context?.SecurityParameters is not { } parameters)
        {
            return;
        }

        var suite = TlsCipherSuites.Find(parameters.CipherSuite)?.Name;
        Update(_snapshot with { TlsVersion = _server.NegotiatedVersion ?? _snapshot.TlsVersion, CipherSuite = suite ?? _snapshot.CipherSuite }, notify: false);
    }

    private byte[] Drain()
    {
        var available = _tls?.GetAvailableOutputBytes() ?? 0;
        if (available <= 0)
        {
            return [];
        }

        var output = new byte[available];
        _tls!.ReadOutput(output, 0, available);
        return output;
    }

    /// <summary>Sends a TLS flight, in fragments when it is larger than the fragment size (RFC 5216 2.1.5).</summary>
    private EapStep SendFlight(byte[] flight, bool final)
    {
        _outgoingFinal = final;
        if (flight.Length <= _maxFragment)
        {
            // Unfragmented messages never carry L (RFC 9190 2.1.9).
            Update(_snapshot with { State = final ? EapSessionState.SendingResult : EapSessionState.SendingServerCertificate, Fragment = 0, FragmentCount = 0 });
            return Challenge(EapType.Tls, new EapTlsMessage(EapTlsBits.None, null, flight).ToBytes());
        }

        _outgoing = flight;
        _outgoingOffset = 0;
        _outgoingIndex = 0;
        return NextFragment();
    }

    private EapStep NextFragment()
    {
        var flight = _outgoing!;
        var remaining = flight.Length - _outgoingOffset;
        var size = Math.Min(_maxFragment, remaining);
        var first = _outgoingOffset == 0;
        var more = remaining > size;
        var flags = (first ? EapTlsBits.Length : EapTlsBits.None) | (more ? EapTlsBits.More : EapTlsBits.None);
        var message = new EapTlsMessage(flags, first ? (uint)flight.Length : null, flight.AsMemory(_outgoingOffset, size));
        _outgoingOffset += size;
        _outgoingIndex++;
        var count = _outgoingIndex + ((flight.Length - _outgoingOffset + _maxFragment - 1) / _maxFragment);
        if (!more)
        {
            _outgoing = null;
        }

        var state = more ? EapSessionState.WaitingForAcknowledgement : _outgoingFinal ? EapSessionState.SendingResult : EapSessionState.SendingServerCertificate;
        Update(_snapshot with { State = state, Fragment = _outgoingIndex, FragmentCount = count });
        return Challenge(EapType.Tls, message.ToBytes());
    }

    private EapStep Challenge(byte type, ReadOnlyMemory<byte> data)
    {
        var identifier = _nextIdentifier++;
        var request = EapPacket.Request(identifier, type, data);
        _outstandingIdentifier = identifier;
        _outstandingRequest = request.ToBytes();
        return new EapStep(EapStepKind.Challenge, request);
    }

    /// <summary>
    /// The device acknowledged OADM's last flight: EAP-Success, but only with an accepting verdict of the validator and the
    /// keys (fail closed). The session ends with <see cref="CompleteAccept"/> once the processor knows the Access-Accept can
    /// be sent, so it is never logged Authenticated without one.
    /// </summary>
    private EapStep Succeed(byte responseIdentifier)
    {
        if (_server?.Verdict is not { Accepted: true } verdict || _msk is null)
        {
            return Fail(responseIdentifier, TlsAlertTexts.Text(80, TlsAlertDirection.SentByOadm));
        }

        var msk = _msk;
        _msk = null;
        Update(_snapshot with { VlanId = verdict.VlanId, Warning = verdict.Warning, Tag = verdict.Tag }, notify: false);
        _phase = Phase.AcceptPending;
        return new EapStep(EapStepKind.Accept, EapPacket.Success(responseIdentifier), msk);
    }

    /// <summary>
    /// Ends a session whose EAP-Success was decided: Authenticated when the Access-Accept goes out, else Failed "Answer too
    /// large" (the switch gets nothing and its retransmission finds no session).
    /// </summary>
    public void CompleteAccept(bool sent)
    {
        if (_phase != Phase.AcceptPending)
        {
            return;
        }

        if (sent)
        {
            Finish(EapSessionState.Authenticated, null);
        }
        else
        {
            Update(_snapshot with { VlanId = null }, notify: false);
            Finish(EapSessionState.Failed, EapSessionTexts.AnswerTooLarge);
        }
    }

    private EapStep Fail(byte responseIdentifier, string reason)
    {
        Update(_snapshot with { Tag = _server?.Verdict?.Tag ?? _snapshot.Tag, Alert = _server?.Alert ?? _snapshot.Alert }, notify: false);
        Finish(EapSessionState.Failed, reason);
        return new EapStep(EapStepKind.Reject, EapPacket.Failure(responseIdentifier));
    }

    private void Finish(EapSessionState state, string? reason)
    {
        _phase = Phase.Ended;
        _incoming = null;
        _outgoing = null;
        _outstandingRequest = null;
        if (_msk is not null)
        {
            CryptographicOperations.ZeroMemory(_msk);
            _msk = null;
        }

        _tls = null;
        Update(_snapshot with { State = state, Reason = reason, Ended = _time.GetUtcNow(), Fragment = 0, FragmentCount = 0 });
        _server = null;
    }

    /// <summary>Why the device refused OADM: its alert in plain words.</summary>
    private string DeviceReason() =>
        _server?.Alert is { Direction: TlsAlertDirection.SentByDevice } alert ? alert.Text : EapSessionTexts.UnexpectedMessage;

    /// <summary>Why OADM refused the device: the validator's reason, else OADM's alert in plain words.</summary>
    private string OadmReason()
    {
        if (_server?.Verdict is { Accepted: false, Reason: { } reason })
        {
            return reason;
        }

        return _server?.Alert is { } alert ? alert.Text : EapSessionTexts.MessageUnreadable;
    }

    /// <summary>The device's alert in a response after OADM's last flight, or null.</summary>
    private string? DeviceAlertReason(ReadOnlyMemory<byte> data)
    {
        if (_tls is null || data.IsEmpty)
        {
            return null;
        }

        try
        {
            _tls.OfferInput(data.ToArray());
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return _server?.Alert is { Direction: TlsAlertDirection.SentByDevice } alert ? alert.Text : null;
        }

        return _server?.Alert is { Direction: TlsAlertDirection.SentByDevice } received ? received.Text : null;
    }

    private void Update(EapSessionSnapshot snapshot, bool notify = true)
    {
        _snapshot = snapshot;
        if (notify)
        {
            _changed(snapshot);
        }
    }
}
