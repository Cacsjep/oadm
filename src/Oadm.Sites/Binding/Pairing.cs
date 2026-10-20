using System.Security.Cryptography;
using System.Text;

using Google.Protobuf;

using Oadm.Contracts.SiteNode;

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Security;

using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace Oadm.Sites.Binding;

/// <summary>
/// The binding exchange (docs/sites.md, Binding): J-PAKE (a balanced password-authenticated key exchange, NIST 3072-bit
/// prime order group, SHA-256, BouncyCastle) with the pairing code as the shared secret, then a proof of each side over
/// both TLS fingerprints with the session key. Neither side holds anything that lets it test codes offline: every value
/// sent is blinded by fresh random exponents, and the key of a wrong guess cannot be computed by the other side, so a
/// node or server that is not the real one gets one guess per attempt. A relay with its own key fails both proofs.
/// </summary>
/// <remarks>
/// Flow: PairStart carries the server's round 1 and returns the node's round 1 and round 2 (the node has the server's
/// round 1 by then); Bind carries the server's round 2 and its proof HMAC-SHA256(K, "bind" | node fp | server fp); the
/// node answers HMAC-SHA256(K, "bound" | node fp | server fp). K = SHA-256("oadm-site-pairing" | keying material).
/// </remarks>
public static class Pairing
{
    public const string ServerId = "oadm-server";
    public const string NodeId = "oadm-site-node";

    /// <summary>The largest number a pairing message may carry (3072 bits plus a sign byte).</summary>
    public const int MaxNumberBytes = 385;

    internal static readonly JPakePrimeOrderGroup Group = JPakePrimeOrderGroups.NIST_3072;

    internal static JPakeParticipant Participant(string id, string code) =>
        new(id, code.ToCharArray(), Group, new Sha256Digest(), new SecureRandom());

    internal static byte[] Proof(byte[] key, string label, ReadOnlySpan<byte> nodeFingerprint, ReadOnlySpan<byte> serverFingerprint)
    {
        var data = new byte[label.Length + nodeFingerprint.Length + serverFingerprint.Length];
        Encoding.ASCII.GetBytes(label, data);
        nodeFingerprint.CopyTo(data.AsSpan(label.Length));
        serverFingerprint.CopyTo(data.AsSpan(label.Length + nodeFingerprint.Length));
        return HMACSHA256.HashData(key, data);
    }

    /// <summary>K = SHA-256("oadm-site-pairing" | keying material padded to the group size).</summary>
    public static byte[] SessionKey(BigInteger keyingMaterial)
    {
        ArgumentNullException.ThrowIfNull(keyingMaterial);
        var size = (Group.P.BitLength + 7) / 8;
        var raw = keyingMaterial.ToByteArrayUnsigned();
        var label = Encoding.ASCII.GetBytes("oadm-site-pairing");
        var data = new byte[label.Length + size];
        label.CopyTo(data, 0);
        raw.CopyTo(data, label.Length + size - raw.Length);
        return SHA256.HashData(data);
    }

    public static PakeRound1 ToProto(JPakeRound1Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var message = new PakeRound1 { ParticipantId = payload.ParticipantId, Gx1 = Bytes(payload.Gx1), Gx2 = Bytes(payload.Gx2) };
        message.ProofX1.Add(payload.KnowledgeProofForX1.Select(Bytes));
        message.ProofX2.Add(payload.KnowledgeProofForX2.Select(Bytes));
        return message;
    }

    public static PakeRound2 ToProto(JPakeRound2Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var message = new PakeRound2 { ParticipantId = payload.ParticipantId, A = Bytes(payload.A) };
        message.ProofX2S.Add(payload.KnowledgeProofForX2s.Select(Bytes));
        return message;
    }

    /// <summary>The payload of a received round 1, or null when it is malformed or not from <paramref name="expectedId"/>.</summary>
    public static JPakeRound1Payload? FromProto(PakeRound1? message, string expectedId)
    {
        if (message is null || !string.Equals(message.ParticipantId, expectedId, StringComparison.Ordinal)
            || Number(message.Gx1) is not { } gx1 || Number(message.Gx2) is not { } gx2
            || Numbers(message.ProofX1) is not { } proof1 || Numbers(message.ProofX2) is not { } proof2)
        {
            return null;
        }

        return new JPakeRound1Payload(expectedId, gx1, gx2, proof1, proof2);
    }

    /// <summary>The payload of a received round 2, or null when it is malformed or not from <paramref name="expectedId"/>.</summary>
    public static JPakeRound2Payload? FromProto(PakeRound2? message, string expectedId)
    {
        if (message is null || !string.Equals(message.ParticipantId, expectedId, StringComparison.Ordinal)
            || Number(message.A) is not { } a || Numbers(message.ProofX2S) is not { } proof)
        {
            return null;
        }

        return new JPakeRound2Payload(expectedId, a, proof);
    }

    private static ByteString Bytes(BigInteger value) => ByteString.CopyFrom(value.ToByteArrayUnsigned());

    private static BigInteger? Number(ByteString bytes) =>
        bytes.Length is > 0 and <= MaxNumberBytes ? new BigInteger(1, bytes.ToByteArray()) : null;

    private static BigInteger[]? Numbers(Google.Protobuf.Collections.RepeatedField<ByteString> list)
    {
        if (list.Count != 2)
            return null;
        var numbers = new BigInteger[2];
        for (var i = 0; i < 2; i++)
        {
            if (Number(list[i]) is not { } n)
                return null;
            numbers[i] = n;
        }

        return numbers;
    }
}

/// <summary>
/// The node's side of one pairing attempt for its current code: a fresh J-PAKE participant per PairStart. A wrong proof at
/// <see cref="Finish"/> is a wrong code. Use each instance once.
/// </summary>
public sealed class NodePairing
{
    private readonly JPakeParticipant _participant;
    private bool _started;
    private bool _finished;

    public NodePairing(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        _participant = Pairing.Participant(Pairing.NodeId, code);
    }

    /// <summary>
    /// PairStart: checks the server's round 1 and returns the node's round 1 and round 2. Null when the server's round 1
    /// is malformed or its proofs do not hold.
    /// </summary>
    public (PakeRound1 Round1, PakeRound2 Round2)? Start(PakeRound1? serverRound1)
    {
        if (_started)
            throw new InvalidOperationException("A pairing attempt starts once.");
        _started = true;
        if (Pairing.FromProto(serverRound1, Pairing.ServerId) is not { } received)
            return null;
        var round1 = _participant.CreateRound1PayloadToSend();
        try
        {
            _participant.ValidateRound1PayloadReceived(received);
        }
        catch (CryptoException)
        {
            return null;
        }

        return (Pairing.ToProto(round1), Pairing.ToProto(_participant.CreateRound2PayloadToSend()));
    }

    /// <summary>
    /// Bind: checks the server's round 2 and proof. Returns the node's proof for the reply, or null when the proof does
    /// not match (a wrong code, or a relay in between). Fingerprints are the SHA-256 of the certificates of this connection.
    /// </summary>
    public byte[]? Finish(PakeRound2? serverRound2, byte[] serverProof, ReadOnlySpan<byte> nodeFingerprint, ReadOnlySpan<byte> serverFingerprint)
    {
        ArgumentNullException.ThrowIfNull(serverProof);
        if (!_started || _finished)
            return null;
        _finished = true;
        if (Pairing.FromProto(serverRound2, Pairing.ServerId) is not { } received)
            return null;
        byte[] key;
        try
        {
            _participant.ValidateRound2PayloadReceived(received);
            key = Pairing.SessionKey(_participant.CalculateKeyingMaterial());
        }
        catch (Exception e) when (e is CryptoException or InvalidOperationException)
        {
            return null;
        }

        var expected = Pairing.Proof(key, "bind", nodeFingerprint, serverFingerprint);
        return CryptographicOperations.FixedTimeEquals(expected, serverProof) ? Pairing.Proof(key, "bound", nodeFingerprint, serverFingerprint) : null;
    }
}

/// <summary>The server's side of one pairing attempt with the code the administrator typed. Use each instance once.</summary>
public sealed class ServerPairing
{
    private readonly JPakeParticipant _participant;
    private readonly byte[] _nodeFingerprint;
    private readonly byte[] _serverFingerprint;
    private byte[]? _key;

    public ServerPairing(string code, ReadOnlySpan<byte> nodeFingerprint, ReadOnlySpan<byte> serverFingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        _participant = Pairing.Participant(Pairing.ServerId, code);
        _nodeFingerprint = nodeFingerprint.ToArray();
        _serverFingerprint = serverFingerprint.ToArray();
        Round1 = Pairing.ToProto(_participant.CreateRound1PayloadToSend());
    }

    /// <summary>The server's round 1 for PairStart.</summary>
    public PakeRound1 Round1 { get; }

    /// <summary>
    /// From the node's PairStart reply: the server's round 2 and proof for Bind. Throws <see cref="ArgumentException"/>
    /// when the node's rounds are malformed or their proofs do not hold.
    /// </summary>
    public (PakeRound2 Round2, byte[] Proof) Continue(PakeRound1? nodeRound1, PakeRound2? nodeRound2)
    {
        if (_key is not null)
            throw new InvalidOperationException("A pairing attempt continues once.");
        var round1 = Pairing.FromProto(nodeRound1, Pairing.NodeId) ?? throw new ArgumentException("The site node sent an invalid pairing value.", nameof(nodeRound1));
        var round2 = Pairing.FromProto(nodeRound2, Pairing.NodeId) ?? throw new ArgumentException("The site node sent an invalid pairing value.", nameof(nodeRound2));
        try
        {
            _participant.ValidateRound1PayloadReceived(round1);
            var mine = Pairing.ToProto(_participant.CreateRound2PayloadToSend());
            _participant.ValidateRound2PayloadReceived(round2);
            _key = Pairing.SessionKey(_participant.CalculateKeyingMaterial());
            return (mine, Pairing.Proof(_key, "bind", _nodeFingerprint, _serverFingerprint));
        }
        catch (CryptoException e)
        {
            throw new ArgumentException("The site node sent an invalid pairing value.", nameof(nodeRound2), e);
        }
    }

    /// <summary>True when the node's reply proves it knew the same code on this connection.</summary>
    public bool Check(byte[] nodeProof)
    {
        ArgumentNullException.ThrowIfNull(nodeProof);
        if (_key is null)
            return false;
        var expected = Pairing.Proof(_key, "bound", _nodeFingerprint, _serverFingerprint);
        return CryptographicOperations.FixedTimeEquals(expected, nodeProof);
    }
}
