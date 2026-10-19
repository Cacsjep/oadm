using Oadm.Sites.Binding;
using Oadm.Sites.Transport;

namespace Oadm.Sites.Tests;

public sealed class PairingTests
{
    private static readonly byte[] NodeKey = SiteTls.Fingerprint(SiteTls.CreateIdentity("node"));
    private static readonly byte[] ServerKey = SiteTls.Fingerprint(SiteTls.CreateIdentity("server"));
    private static readonly byte[] OtherKey = SiteTls.Fingerprint(SiteTls.CreateIdentity("relay"));

    [Fact]
    public void TheRightCodeBindsAndBothSidesProveThemselves()
    {
        var code = PairingCode.Create();
        var node = new NodePairing(code);
        var (salt, b) = node.Start();

        var server = new ServerPairing(code, salt, b, NodeKey, ServerKey);
        var reply = node.Finish(server.A, server.Proof, NodeKey, ServerKey);

        Assert.NotNull(reply);
        Assert.True(server.Check(reply));
    }

    [Fact]
    public void AWrongCodeFailsAtTheNode()
    {
        var node = new NodePairing("ABCD1234");
        var (salt, b) = node.Start();

        var server = new ServerPairing("ABCD1235", salt, b, NodeKey, ServerKey);

        Assert.Null(node.Finish(server.A, server.Proof, NodeKey, ServerKey));
    }

    [Fact]
    public void ARelayInBetweenFailsEvenWithTheRightCode()
    {
        // The node sees the relay's key, the server sees the relay's key: the proofs cover different fingerprints.
        var code = PairingCode.Create();
        var node = new NodePairing(code);
        var (salt, b) = node.Start();

        var server = new ServerPairing(code, salt, b, OtherKey, ServerKey);

        Assert.Null(node.Finish(server.A, server.Proof, NodeKey, OtherKey));
    }

    [Fact]
    public void TheNodesReplyDoesNotPassForAnotherConnection()
    {
        var code = PairingCode.Create();
        var node = new NodePairing(code);
        var (salt, b) = node.Start();
        var server = new ServerPairing(code, salt, b, NodeKey, ServerKey);
        var reply = node.Finish(server.A, server.Proof, NodeKey, ServerKey)!;

        var other = new ServerPairing(code, salt, b, NodeKey, OtherKey);

        Assert.False(other.Check(reply));
    }

    [Fact]
    public void AnInvalidPublicValueIsRefused()
    {
        var node = new NodePairing("ABCD1234");
        var (salt, b) = node.Start();
        var server = new ServerPairing("ABCD1234", salt, b, NodeKey, ServerKey);

        Assert.Null(node.Finish([0], server.Proof, NodeKey, ServerKey));
        Assert.Throws<ArgumentException>(() => new ServerPairing("ABCD1234", salt, [0], NodeKey, ServerKey));
    }

    [Theory]
    [InlineData("abcd-efgh", "ABCDEFGH")]
    [InlineData("ABCD EFGH", "ABCDEFGH")]
    [InlineData("oil0-1234", "0110-1234")]
    [InlineData("ABCDEFG", null)]
    [InlineData("ABCDEFGHJ", null)]
    [InlineData("ABCD-EFGU", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TypedCodesAreNormalized(string? typed, string? expected) =>
        Assert.Equal(expected?.Replace("-", "", StringComparison.Ordinal), PairingCode.Normalize(typed));

    [Fact]
    public void NewCodesUseOnlyTheAlphabetAndShowWithADash()
    {
        for (var i = 0; i < 1000; i++)
        {
            var code = PairingCode.Create();
            Assert.Equal(code, PairingCode.Normalize(code));
            Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", PairingCode.Format(code));
        }
    }
}
