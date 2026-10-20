using System.Security.Cryptography;

using Google.Protobuf;

using Oadm.Contracts.SiteNode;
using Oadm.Sites.Binding;
using Oadm.Sites.Transport;

using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Security;

using BigInteger = Org.BouncyCastle.Math.BigInteger;

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
        var (node, server, round2, proof) = Exchange(code, code, NodeKey, ServerKey);

        var reply = node.Finish(round2, proof, NodeKey, ServerKey);

        Assert.NotNull(reply);
        Assert.True(server.Check(reply));
    }

    [Fact]
    public void AWrongCodeFailsAtTheNode()
    {
        var (node, _, round2, proof) = Exchange("ABCD1234", "ABCD1235", NodeKey, ServerKey);

        Assert.Null(node.Finish(round2, proof, NodeKey, ServerKey));
    }

    [Fact]
    public void ARelayInBetweenFailsEvenWithTheRightCode()
    {
        // The node sees the relay's key, the server sees the relay's key: the proofs cover different fingerprints.
        var code = PairingCode.Create();
        var (node, _, round2, proof) = Exchange(code, code, OtherKey, ServerKey);

        Assert.Null(node.Finish(round2, proof, NodeKey, OtherKey));
    }

    [Fact]
    public void TheNodesReplyDoesNotPassForAnotherConnection()
    {
        var code = PairingCode.Create();
        var (node, _, round2, proof) = Exchange(code, code, NodeKey, ServerKey);
        var reply = node.Finish(round2, proof, NodeKey, ServerKey)!;

        var (_, other, _, _) = Exchange(code, code, NodeKey, OtherKey);

        Assert.False(other.Check(reply));
    }

    [Fact]
    public void InvalidRoundsAreRefused()
    {
        var server = new ServerPairing("ABCD1234", NodeKey, ServerKey);
        var node = new NodePairing("ABCD1234");
        var round1 = server.Round1.Clone();
        round1.Gx2 = ByteString.CopyFrom([1]); // g^x2 = 1
        Assert.Null(node.Start(round1));
        Assert.Null(new NodePairing("ABCD1234").Start(null));
        var wrongId = server.Round1.Clone();
        wrongId.ParticipantId = Pairing.NodeId;
        Assert.Null(new NodePairing("ABCD1234").Start(wrongId));
        var tooLong = server.Round1.Clone();
        tooLong.Gx1 = ByteString.CopyFrom(new byte[Pairing.MaxNumberBytes + 1]);
        Assert.Null(new NodePairing("ABCD1234").Start(tooLong));

        var (nodeRound1, nodeRound2) = new NodePairing("ABCD1234").Start(server.Round1)!.Value;
        var forged = nodeRound2.Clone();
        forged.ProofX2S[1] = ByteString.CopyFrom([7]);
        Assert.Throws<ArgumentException>(() => server.Continue(nodeRound1, forged));
    }

    [Fact]
    public void EveryAttemptSendsFreshValues()
    {
        // Nothing sent is a function of the code and public data alone: the same code, keys and fingerprints give other
        // values every time, so a transcript tells nothing that a guess could be compared with.
        var code = PairingCode.Create();
        var first = Transcript(code);
        var second = Transcript(code);

        Assert.Equal(first.Count, second.Count);
        Assert.All(first.Zip(second), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public void AnImpostorNodeWithTheWholeTranscriptCannotTestTheCodeOffline()
    {
        // An impostor node answers the server's round 1 with its own values and a guess, receives the server's round 2 and
        // proof, and then tries every code offline. Only the guess it committed to in round 2 can match: with a wrong
        // guess, not even the right code reproduces the server's proof. (Under SRP-6a the node side holds the verifier
        // and could test all codes against the server's proof.)
        var code = "K7M2Q9XA";
        var candidates = new[] { code, "K7M2Q9XB", "00000000", "ZZZZZZZZ" };

        var wrongGuess = Impostor.Run(code, guess: "K7M2Q9XB");
        Assert.Empty(wrongGuess.CodesMatchingTheProof(candidates));

        // The same computation works when the guess was right: the check above is a real test, not a broken one.
        var rightGuess = Impostor.Run(code, guess: code);
        Assert.Equal([code], rightGuess.CodesMatchingTheProof(candidates));
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

    private static (NodePairing Node, ServerPairing Server, PakeRound2 Round2, byte[] Proof) Exchange(string nodeCode, string serverCode, byte[] nodeFp, byte[] serverFp)
    {
        var server = new ServerPairing(serverCode, nodeFp, serverFp);
        var node = new NodePairing(nodeCode);
        var (nodeRound1, nodeRound2) = node.Start(server.Round1)!.Value;
        var (round2, proof) = server.Continue(nodeRound1, nodeRound2);
        return (node, server, round2, proof);
    }

    // Every value sent on the wire in one successful exchange, in order.
    private static List<ByteString> Transcript(string code)
    {
        var server = new ServerPairing(code, NodeKey, ServerKey);
        var node = new NodePairing(code);
        var (nodeRound1, nodeRound2) = node.Start(server.Round1)!.Value;
        var (round2, proof) = server.Continue(nodeRound1, nodeRound2);
        var reply = node.Finish(round2, proof, NodeKey, ServerKey)!;
        var values = new List<ByteString>();
        foreach (var r1 in new[] { server.Round1, nodeRound1 })
        {
            values.AddRange([r1.Gx1, r1.Gx2, .. r1.ProofX1, .. r1.ProofX2]);
        }

        foreach (var r2 in new[] { nodeRound2, round2 })
        {
            values.AddRange([r2.A, .. r2.ProofX2S]);
        }

        values.Add(ByteString.CopyFrom(proof));
        values.Add(ByteString.CopyFrom(reply));
        return values;
    }

    /// <summary>A node that does not know the code, built from the J-PAKE formulas with its own exponents.</summary>
    private sealed class Impostor
    {
        private static readonly JPakePrimeOrderGroup Group = JPakePrimeOrderGroups.NIST_3072;

        private BigInteger _x2 = BigInteger.Zero;
        private BigInteger _serverGx2 = BigInteger.Zero;
        private BigInteger _serverA = BigInteger.Zero;
        private byte[] _serverProof = [];

        public static Impostor Run(string code, string guess)
        {
            var impostor = new Impostor();
            var random = new SecureRandom();
            var digest = new Sha256Digest();
            var server = new ServerPairing(code, NodeKey, ServerKey);
            var serverRound1 = Pairing.FromProto(server.Round1, Pairing.ServerId)!;

            var x1 = JPakeUtilities.GenerateX1(Group.Q, random);
            var x2 = JPakeUtilities.GenerateX2(Group.Q, random);
            var gx1 = JPakeUtilities.CalculateGx(Group.P, Group.G, x1);
            var gx2 = JPakeUtilities.CalculateGx(Group.P, Group.G, x2);
            var round1 = new JPakeRound1Payload(Pairing.NodeId, gx1, gx2,
                JPakeUtilities.CalculateZeroKnowledgeProof(Group.P, Group.Q, Group.G, gx1, x1, Pairing.NodeId, digest, random),
                JPakeUtilities.CalculateZeroKnowledgeProof(Group.P, Group.Q, Group.G, gx2, x2, Pairing.NodeId, digest, random));
            var gA = JPakeUtilities.CalculateGA(Group.P, gx1, serverRound1.Gx1, serverRound1.Gx2);
            var x2s = JPakeUtilities.CalculateX2s(Group.Q, x2, JPakeUtilities.CalculateS(Group.Q, guess.ToCharArray()));
            var a = JPakeUtilities.CalculateA(Group.P, Group.Q, gA, x2s);
            var round2 = new JPakeRound2Payload(Pairing.NodeId, a,
                JPakeUtilities.CalculateZeroKnowledgeProof(Group.P, Group.Q, gA, a, x2s, Pairing.NodeId, digest, random));

            var (serverRound2, proof) = server.Continue(Pairing.ToProto(round1), Pairing.ToProto(round2));
            impostor._x2 = x2;
            impostor._serverGx2 = serverRound1.Gx2;
            impostor._serverA = Pairing.FromProto(serverRound2, Pairing.ServerId)!.A;
            impostor._serverProof = proof;
            return impostor;
        }

        public IReadOnlyList<string> CodesMatchingTheProof(IEnumerable<string> candidates) =>
        [
            .. candidates.Where(candidate =>
            {
                var s = JPakeUtilities.CalculateS(Group.Q, candidate.ToCharArray());
                var keying = JPakeUtilities.CalculateKeyingMaterial(Group.P, Group.Q, _serverGx2, _x2, s, _serverA);
                var key = Pairing.SessionKey(keying);
                byte[] data = [.. "bind"u8, .. NodeKey, .. ServerKey];
                var expected = HMACSHA256.HashData(key, data);
                return CryptographicOperations.FixedTimeEquals(expected, _serverProof);
            }),
        ];
    }
}
