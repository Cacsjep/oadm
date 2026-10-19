using System.Security.Cryptography;
using System.Text;

using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace Oadm.Sites.Binding;

/// <summary>
/// The binding exchange (docs/sites.md, Binding): SRP-6a (RFC 5054, 2048-bit group, SHA-256) with the pairing code as the
/// password, then a proof of each side over both TLS fingerprints with the session key. Nobody in between can test codes
/// offline, and a relay with its own key fails both proofs.
/// </summary>
public static class Pairing
{
    internal const string Identity = "oadm-site-node";

    internal static readonly Srp6GroupParameters Group = Srp6StandardGroups.rfc5054_2048;

    internal static byte[] Proof(byte[] key, string label, ReadOnlySpan<byte> nodeFingerprint, ReadOnlySpan<byte> serverFingerprint)
    {
        var data = new byte[label.Length + nodeFingerprint.Length + serverFingerprint.Length];
        Encoding.ASCII.GetBytes(label, data);
        nodeFingerprint.CopyTo(data.AsSpan(label.Length));
        serverFingerprint.CopyTo(data.AsSpan(label.Length + nodeFingerprint.Length));
        return HMACSHA256.HashData(key, data);
    }

    internal static byte[] SessionKey(BigInteger secret)
    {
        // S padded to the group size, so both sides hash the same bytes.
        var size = (Group.N.BitLength + 7) / 8;
        var raw = secret.ToByteArrayUnsigned();
        var padded = new byte[size];
        raw.CopyTo(padded, size - raw.Length);
        return SHA256.HashData(padded);
    }

    internal static byte[] Bytes(BigInteger value) => value.ToByteArrayUnsigned();

    internal static BigInteger Number(byte[] value) => new(1, value);

    internal static byte[] Password(string code) => Encoding.ASCII.GetBytes(code);
}

/// <summary>
/// The node's side of one pairing attempt for its current code. <see cref="Start"/> answers PairStart, <see cref="Finish"/>
/// checks Bind. One instance per attempt; a wrong proof is a wrong code.
/// </summary>
public sealed class NodePairing
{
    private readonly Srp6Server _srp = new();
    private readonly byte[] _salt;
    private readonly byte[] _b;

    public NodePairing(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        _salt = RandomNumberGenerator.GetBytes(16);
        var generator = new Srp6VerifierGenerator();
        generator.Init(Pairing.Group, new Sha256Digest());
        var verifier = generator.GenerateVerifier(_salt, Encoding.ASCII.GetBytes(Pairing.Identity), Pairing.Password(code));
        _srp.Init(Pairing.Group, verifier, new Sha256Digest(), new SecureRandom());
        _b = Pairing.Bytes(_srp.GenerateServerCredentials());
    }

    /// <summary>The reply to PairStart: the salt and B.</summary>
    public (byte[] Salt, byte[] B) Start() => (_salt, _b);

    /// <summary>
    /// Checks the server's Bind. Returns the node's proof for the reply, or null when the proof does not match (a wrong
    /// code, or a relay in between). Fingerprints are the SHA-256 of the certificates of this connection.
    /// </summary>
    public byte[]? Finish(byte[] a, byte[] serverProof, ReadOnlySpan<byte> nodeFingerprint, ReadOnlySpan<byte> serverFingerprint)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(serverProof);
        BigInteger secret;
        try
        {
            secret = _srp.CalculateSecret(Pairing.Number(a));
        }
        catch (Org.BouncyCastle.Crypto.CryptoException)
        {
            return null; // A = 0 mod N
        }
        var key = Pairing.SessionKey(secret);
        var expected = Pairing.Proof(key, "bind", nodeFingerprint, serverFingerprint);
        if (!CryptographicOperations.FixedTimeEquals(expected, serverProof))
            return null;
        return Pairing.Proof(key, "bound", nodeFingerprint, serverFingerprint);
    }
}

/// <summary>The server's side of one pairing attempt with the code the administrator typed.</summary>
public sealed class ServerPairing
{
    private readonly byte[] _key;
    private readonly byte[] _nodeFingerprint;
    private readonly byte[] _serverFingerprint;

    /// <summary>From the node's PairStart reply. Throws <see cref="ArgumentException"/> for an invalid B.</summary>
    public ServerPairing(string code, byte[] salt, byte[] b, ReadOnlySpan<byte> nodeFingerprint, ReadOnlySpan<byte> serverFingerprint)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(b);
        var srp = new Srp6Client();
        srp.Init(Pairing.Group, new Sha256Digest(), new SecureRandom());
        A = Pairing.Bytes(srp.GenerateClientCredentials(salt, Encoding.ASCII.GetBytes(Pairing.Identity), Pairing.Password(code)));
        try
        {
            _key = Pairing.SessionKey(srp.CalculateSecret(Pairing.Number(b)));
        }
        catch (Org.BouncyCastle.Crypto.CryptoException e)
        {
            throw new ArgumentException("The site node sent an invalid pairing value.", nameof(b), e);
        }
        _nodeFingerprint = nodeFingerprint.ToArray();
        _serverFingerprint = serverFingerprint.ToArray();
        Proof = Pairing.Proof(_key, "bind", _nodeFingerprint, _serverFingerprint);
    }

    /// <summary>A for Bind.</summary>
    public byte[] A { get; }

    /// <summary>The server's proof for Bind.</summary>
    public byte[] Proof { get; }

    /// <summary>True when the node's reply proves it knew the same code on this connection.</summary>
    public bool Check(byte[] nodeProof)
    {
        ArgumentNullException.ThrowIfNull(nodeProof);
        var expected = Pairing.Proof(_key, "bound", _nodeFingerprint, _serverFingerprint);
        return CryptographicOperations.FixedTimeEquals(expected, nodeProof);
    }
}
