using Microsoft.Extensions.Time.Testing;
using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Sessions;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Plugins.Radius.Tests.Support;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// The session table and the state machine (docs/spec/radius-server.md "Session states", "Fragmentation", "Limits"): timeouts
/// with a FakeTimeProvider, retransmissions, abandoned sessions, removed switches, the limits and every EAP-TLS refusal.
/// </summary>
public sealed class SessionTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero));
    private readonly List<EapSessionSnapshot> _changes = [];

    private RadiusProcessor NewProcessor(RadiusProtocolOptions? options = null, TestValidator? validator = null)
    {
        var processor = new RadiusProcessor(options ?? new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity }, validator ?? new TestValidator(), _time);
        processor.SessionChanged += s =>
        {
            lock (_changes)
            {
                _changes.Add(s);
            }
        };
        return processor;
    }

    private static byte[] Identity(byte id = 1) => EapPacket.Response(id, EapType.Identity, "axis-accc8e123456"u8.ToArray()).ToBytes();

    /// <summary>Starts a session: Identity in, EAP-TLS Start out. Returns the switch (with the State) and the Start's identifier.</summary>
    private static (TestSwitch Switch, byte StartId) Started(RadiusProcessor processor, string? mac = "AC-CC-8E-12-34-56", string? port = "Gi1/0/5")
    {
        var radiusSwitch = new TestSwitch(processor) { CallingStationId = mac, NasPortId = port };
        var (result, answer, _) = radiusSwitch.Send(Identity());
        Assert.Equal(RadiusCode.AccessChallenge, answer!.Code);
        var start = EapPacket.Parse(TestSwitch.Join(answer)).Packet!;
        Assert.True(EapTlsMessage.Parse(start.Data.Span)!.IsStart);
        radiusSwitch.State = answer.Find(RadiusAttributeType.State)!.Value.Value.ToArray();
        Assert.Equal(EapSessionState.StartingTls, result.Session!.State);
        return (radiusSwitch, start.Identifier);
    }

    private static byte[] TlsResponse(byte id, EapTlsMessage message) => EapPacket.Response(id, EapType.Tls, message.ToBytes()).ToBytes();

    [Fact]
    public void ASessionWithoutAnswerFor30SecondsTimesOut()
    {
        var processor = NewProcessor();
        var (radiusSwitch, startId) = Started(processor);

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(processor.ExpireSessions());
        _time.Advance(TimeSpan.FromSeconds(1));
        var expired = Assert.Single(processor.ExpireSessions());

        Assert.Equal(EapSessionState.TimedOut, expired.State);
        Assert.Equal("Timed out", expired.Text);
        Assert.Equal(_time.GetUtcNow(), expired.Ended);
        Assert.Equal(0, processor.SessionCount);
        Assert.Equal(EapSessionState.TimedOut, _changes[^1].State);

        // The switch's late answer: Access-Reject "Session not found or expired".
        var (late, answer, _) = radiusSwitch.Send(TlsResponse(startId, new EapTlsMessage(EapTlsBits.None, null, new byte[] { 22, 3, 1 })));
        Assert.Equal(RadiusCode.AccessReject, answer!.Code);
        Assert.Equal("Session not found or expired", late.Reason);
    }

    [Fact]
    public void ARequestPastTheTimeoutIsRefusedEvenBeforeTheSweep()
    {
        var processor = NewProcessor();
        var (radiusSwitch, startId) = Started(processor);
        _time.Advance(TimeSpan.FromSeconds(31));

        var (late, _, _) = radiusSwitch.Send(TlsResponse(startId, new EapTlsMessage(EapTlsBits.None, null, new byte[] { 22 })));

        Assert.Equal("Session not found or expired", late.Reason);
        Assert.Equal(EapSessionState.TimedOut, late.Session!.State);
        Assert.Equal(0, processor.SessionCount);
    }

    [Fact]
    public void ASessionEndsAfter60SecondsInAllEvenWithMessages()
    {
        var processor = NewProcessor();
        var (radiusSwitch, startId) = Started(processor);
        var retransmission = Identity(); // the identity again: not outstanding, keeps the session alive (RFC 3748 4.1 discard)

        for (var i = 0; i < 2; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(25));
            var (result, _, _) = radiusSwitch.Send(retransmission);
            Assert.Equal(RadiusDropReason.StaleEapResponse, result.Drop);
            Assert.Empty(processor.ExpireSessions());
        }

        _time.Advance(TimeSpan.FromSeconds(10));
        var expired = Assert.Single(processor.ExpireSessions());
        Assert.Equal(EapSessionState.TimedOut, expired.State);
        _ = startId;
    }

    [Fact]
    public void ARefusalWithoutTheDevicesAnswerEndsFailedWithItsReason()
    {
        var processor = NewProcessor();
        var radiusSwitch = new TestSwitch(processor);
        var camera = new TestSupplicant(new SupplicantOptions { Certificate = TestPki.ForeignCameraCertificate });

        // Stop once OADM sent its alert (state Sending result with the reason): the device never answers.
        radiusSwitch.Run(camera, stopWhen: r => r.Session is { State: EapSessionState.SendingResult, Reason: not null });
        _time.Advance(TimeSpan.FromSeconds(30));
        var ended = Assert.Single(processor.ExpireSessions());

        Assert.Equal(EapSessionState.Failed, ended.State);
        Assert.Equal("Client certificate not issued by a trusted CA", ended.Reason);
        Assert.Equal(new TlsAlert(48, TlsAlertDirection.SentByOadm), ended.Alert);
    }

    [Fact]
    public void ARetransmittedResponseGetsTheOutstandingRequestAgain()
    {
        // RFC 3748 4.1: a response with the Identifier of the request before is a retransmission.
        var processor = NewProcessor();
        var radiusSwitch = new TestSwitch(processor);
        var camera = new TestSupplicant();
        var paused = radiusSwitch.Run(camera, stopWhen: r => r.Session?.State == EapSessionState.SendingServerCertificate);
        var serverFlight = TestSwitch.Join(paused.LastAnswer!);

        // The camera's client hello again (a new RADIUS packet, the old EAP Identifier).
        var hello = radiusSwitch.SentEap[^1];
        var (again, answer, _) = radiusSwitch.Send(hello);

        Assert.Equal(RadiusOutcome.Answered, again.Outcome);
        Assert.Equal(serverFlight, TestSwitch.Join(answer!));

        // The conversation still completes.
        var done = radiusSwitch.Run(camera, resume: true);
        Assert.Equal(RadiusCode.AccessAccept, done.Final);
    }

    [Fact]
    public void AResponseWithAnUnknownIdentifierIsDropped()
    {
        var processor = NewProcessor();
        var (radiusSwitch, startId) = Started(processor);

        var (result, answer, _) = radiusSwitch.Send(TlsResponse((byte)(startId + 7), new EapTlsMessage(EapTlsBits.None, null, new byte[] { 22 })));

        Assert.Null(answer);
        Assert.Equal(RadiusDropReason.StaleEapResponse, result.Drop);
        Assert.Equal(1, processor.SessionCount);
    }

    [Fact]
    public void TooManyRoundTripsEndTheSession()
    {
        var processor = NewProcessor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentityRsa, LargestFragment = 500, MaxRoundTrips = 4 });
        var camera = new TestSupplicant(new SupplicantOptions { Certificate = TestPki.CameraRsa });

        var conversation = new TestSwitch(processor).Run(camera);

        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        Assert.Equal("Too many round trips", conversation.Session!.Reason);
        Assert.Equal(60, new RadiusProtocolOptions().MaxRoundTrips);
    }

    [Fact]
    public void AnAnnouncedMessageOver64KbIsRefused()
    {
        var processor = NewProcessor();
        var (radiusSwitch, startId) = Started(processor);

        var (result, _, _) = radiusSwitch.Send(TlsResponse(startId, new EapTlsMessage(EapTlsBits.Length | EapTlsBits.More, 65537, new byte[200])));

        Assert.Equal("Message too large", result.Reason);
        Assert.Equal(EapSessionState.Failed, result.Session!.State);
    }

    [Fact]
    public void A64KbMessageIsAcceptedForReassembly()
    {
        var processor = NewProcessor();
        var (radiusSwitch, startId) = Started(processor);

        var (result, answer, _) = radiusSwitch.Send(TlsResponse(startId, new EapTlsMessage(EapTlsBits.Length | EapTlsBits.More, 65536, new byte[1000])));

        Assert.Equal(RadiusCode.AccessChallenge, answer!.Code);
        Assert.Equal(EapSessionState.ReceivingClientHello, result.Session!.State);
        Assert.Equal("Receiving client hello: fragment 1 of 66", result.Session.Text);
        var ack = EapPacket.Parse(TestSwitch.Join(answer)).Packet!;
        Assert.True(EapTlsMessage.Parse(ack.Data.Span)!.IsAcknowledgement);
        Assert.Equal((byte)(startId + 1), ack.Identifier);
    }

    public static TheoryData<string, EapTlsMessage[]> BadFragmentations() => new()
    {
        // RFC 5216 2.1.5: L MUST be set on the first fragment of a fragmented message.
        { "M without L", [new EapTlsMessage(EapTlsBits.More, null, new byte[100])] },
        // The fragments must add up to L.
        { "short", [new EapTlsMessage(EapTlsBits.Length | EapTlsBits.More, 1000, new byte[100]), new EapTlsMessage(EapTlsBits.None, null, new byte[100])] },
        { "long", [new EapTlsMessage(EapTlsBits.Length | EapTlsBits.More, 150, new byte[100]), new EapTlsMessage(EapTlsBits.None, null, new byte[100])] },
        // A later fragment repeats L with another value.
        { "other L", [new EapTlsMessage(EapTlsBits.Length | EapTlsBits.More, 1000, new byte[100]), new EapTlsMessage(EapTlsBits.Length | EapTlsBits.More, 999, new byte[100])] },
        // An unfragmented message with an L that differs from its data.
        { "L mismatch", [new EapTlsMessage(EapTlsBits.Length, 10, new byte[100])] },
        // An empty fragment in the middle.
        { "empty fragment", [new EapTlsMessage(EapTlsBits.Length | EapTlsBits.More, 1000, new byte[100]), new EapTlsMessage(EapTlsBits.More, null, ReadOnlyMemory<byte>.Empty)] },
        // L smaller than the first fragment.
        { "L below data", [new EapTlsMessage(EapTlsBits.Length | EapTlsBits.More, 50, new byte[100])] },
    };

    [Theory]
    [MemberData(nameof(BadFragmentations))]
    public void FragmentsThatBreakTheRulesEndTheSession(string name, EapTlsMessage[] messages)
    {
        var processor = NewProcessor();
        var (radiusSwitch, id) = Started(processor);
        RadiusResult? result = null;
        foreach (var message in messages)
        {
            (result, var answer, _) = radiusSwitch.Send(TlsResponse(id, message));
            if (answer!.Code != RadiusCode.AccessChallenge)
            {
                break;
            }

            id = EapPacket.Parse(TestSwitch.Join(answer)).Packet!.Identifier;
        }

        Assert.Equal("Message could not be read", result!.Reason);
        Assert.Equal(EapSessionState.Failed, result.Session!.State);
        Assert.Equal(0, processor.SessionCount);
        _ = name;
    }

    [Fact]
    public void AnAcknowledgementWhenNothingWasSentIsUnexpected()
    {
        var processor = NewProcessor();
        var (radiusSwitch, id) = Started(processor);

        var (result, _, _) = radiusSwitch.Send(TlsResponse(id, EapTlsMessage.Acknowledgement));

        Assert.Equal("Unexpected message from the device", result.Reason);
    }

    [Fact]
    public void AnIdentityInTheMiddleOfTlsIsUnexpected()
    {
        var processor = NewProcessor();
        var (radiusSwitch, id) = Started(processor);

        var (result, _, _) = radiusSwitch.Send(Identity(id));

        Assert.Equal("Unexpected message from the device", result.Reason);
    }

    [Theory]
    [InlineData(EapType.Md5Challenge)]
    [InlineData(EapType.Peap)]
    [InlineData(EapType.Notification)]
    public void OtherEapTypesAreRefused(byte type)
    {
        var processor = NewProcessor();
        var (radiusSwitch, id) = Started(processor);

        var (result, answer, _) = radiusSwitch.Send(EapPacket.Response(id, type, new byte[] { 1 }).ToBytes());

        Assert.Equal(RadiusCode.AccessReject, answer!.Code);
        Assert.Equal("Only EAP-TLS is supported", result.Reason);
        var failure = EapPacket.Parse(TestSwitch.Join(answer)).Packet!;
        Assert.Equal(EapCode.Failure, failure.Code);
        Assert.Equal(id, failure.Identifier);
    }

    [Fact]
    public void AnEapStartInsideASessionEndsIt()
    {
        var processor = NewProcessor();
        var (radiusSwitch, _) = Started(processor);

        var (result, answer, _) = radiusSwitch.Send([]);

        Assert.Equal(RadiusCode.AccessReject, answer!.Code);
        Assert.Equal(EapSessionState.Failed, result.Session!.State);
    }

    [Fact]
    public void ANakAsTheFirstMessageWithoutEapTlsFails()
    {
        var processor = NewProcessor();
        var radiusSwitch = new TestSwitch(processor);
        var (result, _, _) = radiusSwitch.Send(EapPacket.Response(3, EapType.Nak, new byte[] { EapType.Peap, EapType.MsChapV2 }).ToBytes());

        Assert.Equal("The device does not support EAP-TLS (it asked for PEAP, EAP-MSCHAPv2)", result.Reason);
    }

    [Fact]
    public void ANakOrMethodTheSwitchStartedLeadsToAnIdentityRequest()
    {
        // RFC 3579 2.1 / Appendix A: the switch started EAP-TLS itself and the device answered; OADM asks for the identity.
        var processor = NewProcessor();
        var radiusSwitch = new TestSwitch(processor);
        var (nak, nakAnswer, _) = radiusSwitch.Send(EapPacket.Response(3, EapType.Nak, new byte[] { EapType.Tls }).ToBytes());
        var (tls, tlsAnswer, _) = new TestSwitch(processor) { CallingStationId = "AC-CC-8E-00-00-01" }.Send(TlsResponse(3, new EapTlsMessage(EapTlsBits.None, null, new byte[] { 22 })));

        Assert.Equal(EapSessionState.WaitingForIdentity, nak.Session!.State);
        Assert.Equal(EapSessionState.WaitingForIdentity, tls.Session!.State);
        Assert.Equal(EapType.Identity, EapPacket.Parse(TestSwitch.Join(nakAnswer!)).Packet!.Type);
        Assert.Equal(EapType.Identity, EapPacket.Parse(TestSwitch.Join(tlsAnswer!)).Packet!.Type);
    }

    [Fact]
    public void ANewSessionForThePortAbandonsTheOldOne()
    {
        var processor = NewProcessor();
        var (old, _) = Started(processor);
        var (fresh, _) = Started(processor);

        var abandoned = _changes.Single(c => c.State == EapSessionState.Abandoned);
        Assert.Equal("Abandoned", abandoned.Text);
        Assert.Equal("The switch started a new session for the port", abandoned.Reason);
        Assert.Equal(1, processor.SessionCount);

        var (late, _, _) = old.Send(Identity(9));
        Assert.Equal("Session not found or expired", late.Reason);
        _ = fresh;
    }

    [Fact]
    public void AnotherDeviceOnTheSamePortIsItsOwnSession()
    {
        // Several MACs on one port (a camera behind a small switch, multi-auth): never abandoned by each other.
        var processor = NewProcessor();
        Started(processor, mac: "AC-CC-8E-00-00-01");
        Started(processor, mac: "AC-CC-8E-00-00-02");

        Assert.Equal(2, processor.SessionCount);
        Assert.DoesNotContain(_changes, c => c.State == EapSessionState.Abandoned);
    }

    [Fact]
    public void ARemovedSwitchEndsItsSessionsAndForgetsItsAnswers()
    {
        var processor = NewProcessor();
        var (radiusSwitch, _) = Started(processor);
        var other = new TestSwitch(processor) { Client = new RadiusClient("switch-2", TestSwitch.DefaultSecret) };
        other.Send(Identity());

        var ended = Assert.Single(processor.EndSessions("switch-1"));

        Assert.Equal(EapSessionState.Abandoned, ended.State);
        Assert.Equal("The switch was removed", ended.Reason);
        Assert.Equal(1, processor.SessionCount);
        Assert.Equal("switch-2", Assert.Single(processor.Sessions()).Client);
        _ = radiusSwitch;
    }

    [Fact]
    public void TheSessionLimitsRefuseNewSessions()
    {
        var processor = NewProcessor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity, MaxSessionsPerClient = 2, MaxSessions = 3 });
        Started(processor, mac: "AC-CC-8E-00-00-01");
        Started(processor, mac: "AC-CC-8E-00-00-02");

        // Silently dropped (the switch tries again), never an Access-Reject a flood could make the switch apply to a device.
        var (refused, _, _) = new TestSwitch(processor) { CallingStationId = "AC-CC-8E-00-00-03", NasPortId = "Gi1/0/6" }.Send(Identity());
        Assert.Equal((RadiusOutcome.Dropped, RadiusDropReason.TooManySessions, null), (refused.Outcome, refused.Drop, refused.Response));

        var otherSwitch = new TestSwitch(processor) { Client = new RadiusClient("switch-2", TestSwitch.DefaultSecret) };
        Assert.Equal(RadiusCode.AccessChallenge, RadiusPacket.Parse(otherSwitch.Send(Identity()).Result.Response!).Packet!.Code);
        var (full, _, _) = new TestSwitch(processor) { Client = new RadiusClient("switch-3", TestSwitch.DefaultSecret) }.Send(Identity());
        Assert.Equal((RadiusOutcome.Dropped, RadiusDropReason.TooManySessions), (full.Outcome, full.Drop));
        Assert.Equal(10_000, new RadiusProtocolOptions().MaxSessions);
        Assert.Equal(1_000, new RadiusProtocolOptions().MaxSessionsPerClient);
        Assert.Equal(8, new RadiusProtocolOptions().MaxSessionsPerPort);
    }

    [Fact]
    public void One_port_with_spoofed_mac_addresses_keeps_at_most_8_sessions_and_never_uses_up_its_switch()
    {
        var processor = NewProcessor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity, MaxSessionsPerClient = 10 });
        for (var i = 0; i < 20; i++)
        {
            Started(processor, mac: $"AC-CC-8E-00-01-{i:X2}");
        }

        Assert.Equal(8, processor.SessionCount); // the oldest on the port gave way
        Assert.Equal(12, _changes.Count(c => c is { State: EapSessionState.Abandoned, Reason: EapSessionTexts.TooManyOnPort }));
        Assert.Equal(8, processor.Sessions().Select(x => x.Mac).Distinct().Count()); // the newest eight

        // Another port of the same switch still gets its session.
        Started(processor, mac: "AC-CC-8E-00-02-01", port: "Gi1/0/7");
        Assert.Equal(9, processor.SessionCount);
    }

    [Fact]
    public void A_first_fragment_announcing_64_kb_holds_only_what_arrived()
    {
        var processor = NewProcessor();
        var (radiusSwitch, startId) = Started(processor);

        var (result, answer, _) = radiusSwitch.Send(TlsResponse(startId, new EapTlsMessage(EapTlsBits.Length | EapTlsBits.More, 64 * 1024, new byte[100])));

        Assert.Equal(RadiusCode.AccessChallenge, answer!.Code); // the acknowledgement of the fragment
        Assert.Equal(EapSessionState.ReceivingClientHello, result.Session!.State);
        Assert.InRange(processor.IncomingBytes(), 100, 4 * 1024);
    }

    [Fact]
    public void TheIdentityOfTheDeviceIsShownSanitized()
    {
        var processor = NewProcessor();
        var radiusSwitch = new TestSwitch(processor);
        var (result, _, _) = radiusSwitch.Send(EapPacket.Response(1, EapType.Identity, "axis-\u001b[31mevil‮"u8.ToArray()).ToBytes());

        Assert.Equal("axis-[31mevil", result.Session!.EapIdentity);
    }

    [Theory]
    [InlineData(EapSessionState.WaitingForIdentity, 0, 0, null, null, "Waiting for identity")]
    [InlineData(EapSessionState.StartingTls, 0, 0, null, null, "Starting TLS")]
    [InlineData(EapSessionState.ReceivingClientHello, 1, 2, null, null, "Receiving client hello: fragment 1 of 2")]
    [InlineData(EapSessionState.ReceivingClientHello, 0, 0, null, null, "Receiving client hello")]
    [InlineData(EapSessionState.SendingServerCertificate, 2, 4, null, null, "Sending server certificate: fragment 2 of 4")]
    [InlineData(EapSessionState.SendingServerCertificate, 0, 0, null, null, "Sending server certificate")]
    [InlineData(EapSessionState.WaitingForAcknowledgement, 2, 4, null, null, "Waiting for acknowledgement of fragment 2 of 4")]
    [InlineData(EapSessionState.ReceivingClientCertificate, 3, 5, null, null, "Receiving client certificate: fragment 3 of 5")]
    [InlineData(EapSessionState.CheckingClientCertificate, 0, 0, null, null, "Checking client certificate")]
    [InlineData(EapSessionState.WaitingForClientFinished, 0, 0, null, null, "Waiting for client finished")]
    [InlineData(EapSessionState.SendingResult, 0, 0, null, null, "Sending result")]
    [InlineData(EapSessionState.Authenticated, 0, 0, null, 20, "Authenticated with VLAN 20")]
    [InlineData(EapSessionState.Authenticated, 0, 0, null, null, "Authenticated")]
    [InlineData(EapSessionState.Failed, 0, 0, "Certificate of a device OADM does not manage (ACCC8E123456)", null, "Failed: Certificate of a device OADM does not manage (ACCC8E123456)")]
    [InlineData(EapSessionState.TimedOut, 0, 0, null, null, "Timed out")]
    [InlineData(EapSessionState.Abandoned, 0, 0, null, null, "Abandoned")]
    public void EveryStateHasItsPlainWords(EapSessionState state, int fragment, int count, string? reason, int? vlan, string text)
    {
        var snapshot = new EapSessionSnapshot
        {
            Id = Guid.NewGuid(),
            Client = "sw",
            Started = DateTimeOffset.UnixEpoch,
            LastMessage = DateTimeOffset.UnixEpoch,
            State = state,
            Fragment = fragment,
            FragmentCount = count,
            Reason = reason,
            VlanId = vlan,
        };

        Assert.Equal(text, snapshot.Text);
    }

    [Fact]
    public void TheSnapshotNeverHoldsKeyMaterial()
    {
        // Logs and the page get snapshots: no MSK, no secret, no private key among their properties.
        var names = typeof(EapSessionSnapshot).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Msk", StringComparison.OrdinalIgnoreCase) || n.Contains("Secret", StringComparison.OrdinalIgnoreCase) || n.Contains("Key", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("switch-1", new RadiusClient("switch-1", TestSwitch.DefaultSecret).ToString());
    }
}
