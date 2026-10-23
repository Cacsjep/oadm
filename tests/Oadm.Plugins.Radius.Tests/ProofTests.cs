using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Sessions;
using Oadm.Plugins.Radius.Tests.Support;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// The proof of docs/spec/radius-server.md "TLS library": a full EAP-TLS exchange with a BouncyCastle TLS client as the
/// camera, through an in-memory switch, BouncyCastle's non-blocking TlsServerProtocol on OADM's side, TLS 1.2 and 1.3,
/// and the MSK equal on both sides (the switch decrypts it from MS-MPPE-Recv-Key and Send-Key).
/// </summary>
public sealed class ProofTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void AFullEapTlsExchangeAuthenticatesTheCameraAndBothSidesHaveTheSameMsk(int version)
    {
        var processor = new RadiusProcessor(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity }, new TestValidator());
        var camera = new TestSupplicant(new SupplicantOptions
        {
            Versions = version == 13 ? [ProtocolVersion.TLSv13] : [ProtocolVersion.TLSv12],
        });

        var conversation = new TestSwitch(processor).Run(camera);

        Assert.Equal(RadiusCode.AccessAccept, conversation.Final);
        Assert.Equal(Oadm.Plugins.Radius.Protocol.Eap.EapCode.Success, camera.Result);
        Assert.NotNull(camera.Msk);
        Assert.Equal(64, camera.Msk!.Length);
        Assert.Equal(camera.Msk, conversation.MskFromAccept);
        Assert.Equal(version == 13, camera.CommitmentReceived);
        Assert.Equal(EapSessionState.Authenticated, conversation.Session!.State);
        Assert.Equal(version == 13 ? Protocol.Tls.TlsVersion.Tls13 : Protocol.Tls.TlsVersion.Tls12, conversation.Session.TlsVersion);
        Assert.Equal(0, processor.SessionCount);
    }
}
