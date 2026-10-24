using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Sessions;
using Oadm.Plugins.Radius.Protocol.Tls;
using Oadm.Plugins.Radius.Tests.Support;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// Full EAP-TLS conversations with the BouncyCastle camera through the in-memory switch (docs/spec/radius-server.md
/// "Tests"): TLS 1.2 and 1.3, fragment sizes 500..1400 and a small Framed-MTU, the device's own fragments, every refusal
/// and the alerts of both directions with their texts, Advanced (minimum version, cipher suites).
/// </summary>
public sealed class ConversationTests
{
    public static TheoryData<int, int> FragmentSizes()
    {
        var data = new TheoryData<int, int>();
        foreach (var version in new[] { 12, 13 })
        {
            // The TLS 1.2 flight with the RSA test certificate is just below 1,400 bytes (the certificate request lists only
            // the signature schemes of RFC 9155), so 1,400 fragments only the TLS 1.3 flight.
            foreach (var size in new[] { 500, 600, 800, 1000, 1200, 1400 }.Where(s => version == 13 || s < 1400))
            {
                data.Add(version, size);
            }
        }

        return data;
    }

    private static ProtocolVersion[] Only(int version) => version == 13 ? [ProtocolVersion.TLSv13] : [ProtocolVersion.TLSv12];

    private static (RadiusProcessor Processor, List<EapSessionSnapshot> Changes) Processor(RadiusProtocolOptions? options = null, TestValidator? validator = null)
    {
        var processor = new RadiusProcessor(options ?? new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity }, validator ?? new TestValidator());
        var changes = new List<EapSessionSnapshot>();
        processor.SessionChanged += s =>
        {
            lock (changes)
            {
                changes.Add(s);
            }
        };
        return (processor, changes);
    }

    private static void AssertAuthenticated(Conversation conversation, TestSupplicant camera)
    {
        Assert.Equal(RadiusCode.AccessAccept, conversation.Final);
        Assert.Equal(EapCode.Success, camera.Result);
        Assert.NotNull(camera.Msk);
        Assert.Equal(camera.Msk, conversation.MskFromAccept);
    }

    [Theory]
    [MemberData(nameof(FragmentSizes))]
    public void OadmFragmentsItsFlightsToTheLargestFragmentSetting(int version, int size)
    {
        var (processor, changes) = Processor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentityRsa, LargestFragment = size });
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(version), Certificate = TestPki.CameraRsa });

        var conversation = new TestSwitch(processor).Run(camera);

        AssertAuthenticated(conversation, camera);
        Assert.True(camera.FragmentsReceived > 0, "the server flight was fragmented");

        // RFC 5216 2.1.5: L and the total on the first fragment only, M on all but the last, the fragments add up.
        var messages = camera.ServerMessages.Where(m => !m.IsStart && !m.IsAcknowledgement).ToList();
        Assert.All(messages, m => Assert.True(m.Data.Length <= size, $"{m.Data.Length} > {size}"));
        var first = messages.First(m => m.HasMore);
        Assert.True(first.HasLength);
        var flight = messages.SkipWhile(m => m != first).TakeWhile(m => m.HasMore).ToList();
        var last = messages[messages.IndexOf(first) + flight.Count];
        Assert.False(last.HasMore);
        Assert.False(last.HasLength);
        Assert.All(flight.Skip(1), m => Assert.False(m.HasLength));
        Assert.Equal((int)first.TotalLength!.Value, flight.Sum(m => m.Data.Length) + last.Data.Length);

        // "Waiting for acknowledgement of fragment 1 of n" while OADM's fragments go out.
        Assert.Contains(changes, c => c.State == EapSessionState.WaitingForAcknowledgement && c.Fragment == 1 && c.FragmentCount == flight.Count + 1);
        Assert.Contains(changes, c => c.State == EapSessionState.SendingServerCertificate && c.Fragment == flight.Count + 1 && c.FragmentCount == flight.Count + 1);
    }

    [Theory]
    [InlineData(12, 300)]
    [InlineData(13, 300)]
    [InlineData(12, 600)]
    [InlineData(13, 1200)]
    public void TheSwitchsFramedMtuMakesTheFragmentsSmaller(int version, int mtu)
    {
        // RFC 3579 2.4, RFC 3580 3.10: EAP packets of at most Framed-MTU - 4; OADM's TLS data per message = that minus 10.
        var (processor, _) = Processor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity, LargestFragment = 1400 });
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(version) });
        var radiusSwitch = new TestSwitch(processor) { FramedMtu = mtu };

        var conversation = radiusSwitch.Run(camera);

        AssertAuthenticated(conversation, camera);
        Assert.All(camera.ServerMessages, m => Assert.True(m.Data.Length <= mtu - 14, $"{m.Data.Length} > {mtu - 14}"));
        Assert.All(conversation.Results.Where(r => r.Response is not null), r =>
        {
            var eap = TestSwitch.Join(RadiusPacket.Parse(r.Response!).Packet!);
            Assert.True(eap.Length <= mtu - 4, $"EAP packet of {eap.Length} bytes for Framed-MTU {mtu}");
        });
    }

    [Fact]
    public void AWirelessPortGetsEapPacketsOfAtMost1496Bytes()
    {
        // RFC 3580 3.10: "send EAP packets no larger than 1496 octets" for NAS-Port-Type 802.11.
        var (processor, _) = Processor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentityRsa, LargestFragment = 1400 });
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(12), Certificate = TestPki.CameraRsa });
        var conversation = new TestSwitch(processor) { FramedMtu = 2304, NasPortType = 19 }.Run(camera);

        AssertAuthenticated(conversation, camera);
        Assert.All(camera.ServerMessages, m => Assert.True(m.Data.Length <= 1496 - 10));
    }

    [Theory]
    [InlineData(12, 300)]
    [InlineData(13, 300)]
    [InlineData(12, 1000)]
    [InlineData(13, 64)]
    public void TheDevicesFragmentsAreAcknowledgedAndJoined(int version, int cameraFragment)
    {
        var (processor, changes) = Processor();
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(version), Certificate = TestPki.CameraWithLongChain(3), FragmentSize = cameraFragment });

        var conversation = new TestSwitch(processor).Run(camera);

        AssertAuthenticated(conversation, camera);
        Assert.True(camera.FragmentsSent > 1);

        // "Receiving client certificate: fragment 3 of 5", the total from the length the device sent first.
        var receiving = changes.Where(c => c.State == EapSessionState.ReceivingClientCertificate).ToList();
        Assert.NotEmpty(receiving);
        Assert.Equal(Enumerable.Range(1, receiving.Count), receiving.Select(r => r.Fragment));
        Assert.All(receiving, r => Assert.Equal(receiving.Count + 1, r.FragmentCount));
        Assert.Matches(@"^Receiving client certificate: fragment 1 of \d+$", receiving[0].Text);
    }

    [Fact]
    public void AFragmentedClientHelloShowsReceivingClientHello()
    {
        var (processor, changes) = Processor();
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(13), FragmentSize = 100 });

        AssertAuthenticated(new TestSwitch(processor).Run(camera), camera);
        Assert.Contains(changes, c => c.State == EapSessionState.ReceivingClientHello && c.Fragment == 1 && c.FragmentCount > 1);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void UnfragmentedMessagesWithTheLFlagAreAccepted(int version)
    {
        // RFC 9190 2.1.9: "MUST accept unfragmented messages with and without the L bit set".
        var (processor, _) = Processor();
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(version), LengthOnUnfragmented = true });

        AssertAuthenticated(new TestSwitch(processor).Run(camera), camera);
        // OADM never sets L on unfragmented messages.
        Assert.All(camera.ServerMessages.Where(m => !m.HasMore && m.Data.Length > 0 && !camera.ServerMessages.Any(x => x.HasMore)), m => Assert.False(m.HasLength));
    }

    [Fact]
    public void AnEapStartGetsAnIdentityRequestFirst()
    {
        var (processor, changes) = Processor();
        var camera = new TestSupplicant();

        var conversation = new TestSwitch(processor) { StartWithEapStart = true }.Run(camera);

        AssertAuthenticated(conversation, camera);
        Assert.Equal(EapSessionState.WaitingForIdentity, changes[0].State);
        Assert.Equal("Waiting for identity", changes[0].Text);
        Assert.Equal("axis-accc8e123456", conversation.Session!.EapIdentity);
    }

    [Fact]
    public void EveryStateOfTheSessionIsReachedInOrder()
    {
        var (processor, changes) = Processor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentityRsa, LargestFragment = 500 });
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(12), Certificate = TestPki.CameraWithLongChain(2), FragmentSize = 400 });

        AssertAuthenticated(new TestSwitch(processor) { StartWithEapStart = true }.Run(camera), camera);

        var states = changes.Select(c => c.State).Distinct().ToList();
        Assert.Equal(
            [
                EapSessionState.WaitingForIdentity,
                EapSessionState.StartingTls,
                EapSessionState.WaitingForAcknowledgement,
                EapSessionState.SendingServerCertificate,
                EapSessionState.ReceivingClientCertificate,
                EapSessionState.CheckingClientCertificate,
                EapSessionState.SendingResult,
                EapSessionState.Authenticated,
            ],
            states);
        Assert.Equal("Checking client certificate", changes.First(c => c.State == EapSessionState.CheckingClientCertificate).Text);
        Assert.Equal("Sending result", changes.First(c => c.State == EapSessionState.SendingResult).Text);
        var done = changes[^1];
        Assert.Equal("Authenticated", done.Text);
        Assert.NotNull(done.Ended);
        Assert.Equal("AC:CC:8E:12:34:56", done.Mac.Normalized);
        Assert.Equal("GigabitEthernet1/0/5", done.Port);
        Assert.Equal("Ethernet", done.PortType);
        Assert.Equal("10.0.0.2", done.NasAddress);
        Assert.Equal(TlsVersion.Tls12, done.TlsVersion);
        Assert.StartsWith("TLS_ECDHE_RSA_WITH_", done.CipherSuite, StringComparison.Ordinal);
        Assert.Equal(3, done.ClientCertificates!.Count);
        Assert.Equal("device", done.Tag);
    }

    [Fact]
    public void TheVlanAndTheUserNameGoIntoTheAccessAccept()
    {
        var (processor, _) = Processor(validator: new TestValidator { VlanId = 20 });
        var camera = new TestSupplicant();

        var conversation = new TestSwitch(processor).Run(camera);

        AssertAuthenticated(conversation, camera);
        var accept = conversation.LastAnswer!;
        Assert.Equal("axis-accc8e123456"u8.ToArray(), accept.Find(RadiusAttributeType.UserName)!.Value.Value.ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 13 }, accept.Find(RadiusAttributeType.TunnelType)!.Value.Value.ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 6 }, accept.Find(RadiusAttributeType.TunnelMediumType)!.Value.Value.ToArray());
        Assert.Equal(new byte[] { 0, (byte)'2', (byte)'0' }, accept.Find(RadiusAttributeType.TunnelPrivateGroupId)!.Value.Value.ToArray());
        Assert.Equal("Authenticated with VLAN 20", conversation.Session!.Text);
        Assert.Equal(EapCode.Success, EapPacket.Parse(TestSwitch.Join(accept)).Packet!.Code);
    }

    [Fact]
    public void WithoutVlanTheAcceptHasNoTunnelAttributes()
    {
        var (processor, _) = Processor();
        var conversation = new TestSwitch(processor).Run(new TestSupplicant());
        Assert.Null(conversation.LastAnswer!.Find(RadiusAttributeType.TunnelType));
    }

    [Fact]
    public void AnRsaServerCertificateUsesTheRsaSuites()
    {
        var (processor, _) = Processor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentityRsa });
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(12) });

        AssertAuthenticated(new TestSwitch(processor).Run(camera), camera);
        Assert.Equal(TlsCipherSuites.Find(camera.NegotiatedCipherSuite!.Value)!.Key, ServerKeyKind.Rsa);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Tls10And11WorkOnlyWhenTheMinimumIsLowered(int version)
    {
        var protocol = version == 10 ? ProtocolVersion.TLSv10 : ProtocolVersion.TLSv11;
        int[] cbc = [CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA, CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA];
        var lowered = (TlsVersion)version;
        var (processor, _) = Processor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity, Tls = new TlsSettings(lowered, TlsCipherSuites.DefaultFor(lowered)) });
        var camera = new TestSupplicant(new SupplicantOptions { Versions = [protocol], CipherSuites = cbc });

        var conversation = new TestSwitch(processor).Run(camera);

        AssertAuthenticated(conversation, camera);
        Assert.Equal(lowered, conversation.Session!.TlsVersion);

        // With the default minimum (TLS 1.2) the same camera is refused with alert 70.
        var (strict, _) = Processor();
        var old = new TestSupplicant(new SupplicantOptions { Versions = [protocol], CipherSuites = cbc });
        var refused = new TestSwitch(strict).Run(old);
        Assert.Equal(RadiusCode.AccessReject, refused.Final);
        Assert.Equal("TLS version not supported (TLS alert 70, protocol version)", refused.Session!.Reason);
    }

    [Fact]
    public void AMinimumOfTls13RefusesATls12Device()
    {
        var (processor, _) = Processor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity, Tls = TlsSettings.Default with { MinimumVersion = TlsVersion.Tls13 } });
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(12) });

        var conversation = new TestSwitch(processor).Run(camera);

        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        Assert.Equal(EapCode.Failure, camera.Result);
        var session = conversation.Session!;
        Assert.Equal(EapSessionState.Failed, session.State);
        Assert.Equal("TLS version not supported (TLS alert 70, protocol version)", session.Reason);
        Assert.Equal(new TlsAlert(70, TlsAlertDirection.SentByOadm), session.Alert);
        Assert.Equal((short)70, camera.AlertReceived);
        Assert.Equal("TLS version not supported (TLS alert 70, protocol version)", conversation.Results[^1].Reason);
    }

    [Theory]
    [InlineData(13, CipherSuite.TLS_AES_128_GCM_SHA256)]
    [InlineData(12, CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256)]
    public void AnUncheckedCipherSuiteIsNotOffered(int version, int suite)
    {
        var unchecked_ = TlsSettings.Default.CipherSuites.Where(s => s != suite).ToHashSet();
        var (processor, _) = Processor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity, Tls = TlsSettings.Default with { CipherSuites = unchecked_ } });

        // A camera that offers only that suite is refused: no common security settings.
        var only = new TestSupplicant(new SupplicantOptions { Versions = Only(version), CipherSuites = [suite] });
        var refused = new TestSwitch(processor).Run(only);
        Assert.Equal(RadiusCode.AccessReject, refused.Final);
        Assert.Equal("No common security settings (TLS alert 40, handshake failure)", refused.Session!.Reason);

        // A camera that offers it among others gets another one.
        var many = new TestSupplicant(new SupplicantOptions { Versions = Only(version) });
        AssertAuthenticated(new TestSwitch(processor).Run(many), many);
        Assert.NotEqual(suite, many.NegotiatedCipherSuite);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void ACertificateOfAnotherCaIsRefusedWithAlert48(int version)
    {
        var (processor, _) = Processor();
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(version), Certificate = TestPki.ForeignCameraCertificate });

        var conversation = new TestSwitch(processor).Run(camera);

        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        Assert.Equal(EapCode.Failure, camera.Result);
        Assert.Equal((short)48, camera.AlertReceived);
        var session = conversation.Session!;
        Assert.Equal("Client certificate not issued by a trusted CA", session.Reason);
        Assert.Equal(new TlsAlert(48, TlsAlertDirection.SentByOadm), session.Alert);
        Assert.Equal("Client certificate not issued by a trusted CA (TLS alert 48, unknown CA)", session.Alert!.Value.Text);
        Assert.Equal("Failed: Client certificate not issued by a trusted CA", session.Text);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void TheValidatorsReasonAndAlertAreUsed(int version)
    {
        var validator = new TestValidator { Decide = _ => ClientCertificateVerdict.Refuse("Client certificate expired on 2026-09-30", 45) };
        var (processor, _) = Processor(validator: validator);
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(version) });

        var conversation = new TestSwitch(processor).Run(camera);

        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        Assert.Equal("Client certificate expired on 2026-09-30", conversation.Session!.Reason);
        Assert.Equal((short)45, camera.AlertReceived);
        var check = Assert.Single(validator.Checks);
        Assert.Equal("switch-1", check.Client);
        Assert.Equal("AC:CC:8E:12:34:56", check.Mac.Normalized);
        Assert.Equal("axis-accc8e123456", check.EapIdentity);
        Assert.Equal((TlsVersion)version, check.Version);
        Assert.Equal(TestPki.CameraCertificate.Leaf, check.Chain[0]);
    }

    [Fact]
    public void AValidatorThatThrowsRefusesTheDevice()
    {
        var (processor, _) = Processor(validator: new TestValidator { Decide = _ => throw new InvalidOperationException("broken") });
        var camera = new TestSupplicant();

        var conversation = new TestSwitch(processor).Run(camera);

        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        Assert.Equal("Internal error", conversation.Session!.Reason);
        Assert.Equal((short)80, camera.AlertReceived);
    }

    [Theory]
    [InlineData(12, (short)40)]
    [InlineData(13, (short)116)]
    public void ADeviceWithoutCertificateIsRefused(int version, short alert)
    {
        var (processor, _) = Processor();
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(version), Certificate = null });

        var conversation = new TestSwitch(processor).Run(camera);

        // The reason names the alert OADM really sent (TLS 1.2: 40, RFC 5246 7.4.6; TLS 1.3: 116, RFC 8446 4.4.2.4).
        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        Assert.Equal(version == 13 ? "No client certificate sent (TLS alert 116, certificate required)" : "No client certificate sent (TLS alert 40, handshake failure)",
            conversation.Session!.Reason);
        Assert.Equal(alert, camera.AlertReceived);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void ADeviceThatDoesNotTrustTheServerSendsAlert48(int version)
    {
        var (processor, changes) = Processor();
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(version), TrustedCa = TestPki.ForeignCaCertificate });

        var conversation = new TestSwitch(processor).Run(camera);

        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        Assert.Equal(EapCode.Failure, camera.Result);
        Assert.Equal((short)48, camera.AlertRaised);
        var session = conversation.Session!;
        Assert.Equal("The device does not trust the server certificate (TLS alert 48, unknown CA)", session.Reason);
        Assert.Equal(new TlsAlert(48, TlsAlertDirection.SentByDevice), session.Alert);
        Assert.DoesNotContain(changes, c => c.State == EapSessionState.CheckingClientCertificate);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void ACertificateWithoutItsKeyFailsTheSignatureCheck(int version)
    {
        // A copied certificate: the chain passes the validator, but the device cannot sign with its key (CertificateVerify).
        var (processor, _) = Processor();
        var copied = TestPki.CameraCertificate with { Pkcs8 = TestPki.ForeignCameraCertificate.Pkcs8 };
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(version), Certificate = copied });

        var conversation = new TestSwitch(processor).Run(camera);

        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        Assert.Equal("Signature check failed (TLS alert 51, decrypt error)", conversation.Session!.Reason);
        Assert.Null(conversation.MskFromAccept);
    }

    [Fact]
    public void ADeviceFlightInTwoMessagesShowsWaitingForClientFinished()
    {
        var (processor, changes) = Processor();
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(12), SplitFlightAfterFirstRecord = true });

        AssertAuthenticated(new TestSwitch(processor).Run(camera), camera);
        var waiting = changes.First(c => c.State == EapSessionState.WaitingForClientFinished);
        Assert.Equal("Waiting for client finished", waiting.Text);
        Assert.True(changes.IndexOf(waiting) > changes.FindIndex(c => c.State == EapSessionState.CheckingClientCertificate));
    }

    [Fact]
    public void ANakEndsTheSessionWithTheMethodTheDeviceAskedFor()
    {
        var (processor, _) = Processor();
        var camera = new TestSupplicant(new SupplicantOptions { NakWith = [EapType.Peap] });

        var conversation = new TestSwitch(processor).Run(camera);

        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        Assert.Equal(EapCode.Failure, camera.Result);
        Assert.Equal("The device does not support EAP-TLS (it asked for PEAP)", conversation.Session!.Reason);
    }

    [Fact]
    public void ManyConversationsOnOneProcessorAreIndependent()
    {
        var (processor, _) = Processor();
        for (var i = 0; i < 5; i++)
        {
            var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(i % 2 == 0 ? 13 : 12) });
            AssertAuthenticated(new TestSwitch(processor) { CallingStationId = $"AC-CC-8E-00-00-0{i}" }.Run(camera), camera);
        }

        Assert.Equal(0, processor.SessionCount);
    }

    [Theory]
    [InlineData(12, 600)]
    [InlineData(13, 600)]
    public void AFramedMtuSentOnlyInTheFirstRequestStillLimitsEveryFragment(int version, int mtu)
    {
        // RFC 3579 2.4 / RFC 3580 3.10: many switches send Framed-MTU (and NAS-Port-Type) in the first Access-Request only;
        // the smallest MTU of the session keeps limiting OADM's fragments.
        var (processor, _) = Processor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity, LargestFragment = 1400 });
        var camera = new TestSupplicant(new SupplicantOptions { Versions = Only(version) });
        var radiusSwitch = new TestSwitch(processor) { FramedMtu = mtu, PortAttributesOnlyFirst = true };

        var conversation = radiusSwitch.Run(camera);

        AssertAuthenticated(conversation, camera);
        Assert.True(camera.FragmentsReceived > 0, "the server flight was fragmented");
        Assert.All(camera.ServerMessages, m => Assert.True(m.Data.Length <= mtu - 14, $"{m.Data.Length} > {mtu - 14}"));
    }

    [Fact]
    public void WithoutACheckedTls13SuiteOnlyTls12IsOffered()
    {
        // Minimum TLS 1.2 with only TLS 1.2 suites checked: offering TLS 1.3 could only end in handshake_failure (RFC 8446
        // 4.1.1: no common suite), so a camera that offers 1.3 and 1.2 gets 1.2.
        var suites = new HashSet<int> { CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256 };
        var (processor, _) = Processor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity, Tls = new TlsSettings(TlsVersion.Tls12, suites) });
        var camera = new TestSupplicant();

        var conversation = new TestSwitch(processor).Run(camera);

        AssertAuthenticated(conversation, camera);
        Assert.Equal(ProtocolVersion.TLSv12, camera.NegotiatedVersion);
        Assert.Equal([TlsVersion.Tls12], new TlsSettings(TlsVersion.Tls12, suites).VersionsFor(ServerKeyKind.Ecdsa));
    }

    [Fact]
    public void SuitesTheServerKeyCannotUseAreNotEnough()
    {
        // The server certificate is ECDSA: checking only ECDHE_RSA suites leaves nothing to negotiate.
        var rsaOnly = new TlsSettings(TlsVersion.Tls12, new HashSet<int> { CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 });

        Assert.Null(rsaOnly.Validate());
        Assert.Equal(TlsSettings.NoUsableSuite, rsaOnly.Validate(ServerKeyKind.Ecdsa));
        Assert.Null(rsaOnly.Validate(ServerKeyKind.Rsa));
        Assert.Empty(rsaOnly.VersionsFor(ServerKeyKind.Ecdsa));
    }

    [Fact]
    public void TheCertificateRequestNamesNoSha1OrSha224UnlessOldVersionsAreAllowed()
    {
        // RFC 9155 2: SHA-1 MUST NOT be used in TLS 1.2 signatures; SHA-224 is not offered either.
        var modern = EapTlsServer.AcceptedSignatures(legacy: false);
        Assert.DoesNotContain(modern, a => a.Hash is HashAlgorithm.sha1 or HashAlgorithm.sha224 or HashAlgorithm.md5);
        Assert.Contains(SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa), modern);
        Assert.Contains(SignatureAndHashAlgorithm.rsa_pss_rsae_sha256, modern);

        var legacy = EapTlsServer.AcceptedSignatures(legacy: true);
        Assert.Contains(SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha1, SignatureAlgorithm.ecdsa), legacy);
        Assert.DoesNotContain(legacy, a => a.Hash == HashAlgorithm.sha224);
    }

    [Theory]
    [InlineData(TlsVersion.Tls12, false)]
    [InlineData(TlsVersion.Tls10, true)]
    public void ATls12CameraOfferingOnlySha1SignaturesPassesOnlyWithOldVersionsAllowed(TlsVersion minimum, bool passes)
    {
        // RFC 9155 2: OADM never signs with SHA-1 in TLS 1.2 unless Advanced allows the old versions for old devices.
        var (processor, _) = Processor(new RadiusProtocolOptions
        {
            ServerIdentity = TestPki.ServerIdentity,
            Tls = new TlsSettings(minimum, TlsCipherSuites.DefaultFor(minimum)),
        });
        var camera = new TestSupplicant(new SupplicantOptions
        {
            Versions = Only(12),
            SignatureAlgorithms = [SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha1, SignatureAlgorithm.ecdsa), SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa)],
        });
        var shaOneOnly = new TestSupplicant(new SupplicantOptions
        {
            Versions = Only(12),
            SignatureAlgorithms = [SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha1, SignatureAlgorithm.ecdsa)],
        });

        Assert.Equal(RadiusCode.AccessAccept, new TestSwitch(processor).Run(camera).Final); // SHA-256 offered too: always fine
        var conversation = new TestSwitch(processor).Run(shaOneOnly);

        Assert.Equal(passes ? RadiusCode.AccessAccept : RadiusCode.AccessReject, conversation.Final);
        if (!passes)
        {
            Assert.Equal((short)40, shaOneOnly.AlertReceived);
        }
    }
}
