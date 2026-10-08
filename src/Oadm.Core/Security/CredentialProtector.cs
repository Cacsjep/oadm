using System.Security.Cryptography;
using System.Text;

namespace Oadm.Core.Security;

/// <summary>
/// AES-256-GCM encryption of device passwords with the server master key.
/// Blob layout (stored as-is in a BLOB column): <c>nonce (12 bytes) | ciphertext (n bytes) | tag (16 bytes)</c>,
/// where n is the UTF-8 length of the plaintext. A fresh random nonce is used for every call.
/// Optional associated data (CredentialStore uses the device id) binds a blob to its record,
/// so a ciphertext copied to another device row fails to decrypt.
/// </summary>
public sealed class CredentialProtector : IDisposable
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>Label of the key check value (<see cref="ComputeKeyCheck"/>).</summary>
    private static readonly byte[] KeyCheckLabel = Encoding.UTF8.GetBytes("OADM master key check v1");

    private byte[] _key;
    private bool _disposed;

    public CredentialProtector(ReadOnlySpan<byte> key, MasterKeyOrigin origin = MasterKeyOrigin.Loaded)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Master key must be {KeySize} bytes.", nameof(key));
        }

        _key = key.ToArray();
        Origin = origin;
    }

    /// <summary>Where the key came from (a new key means stored credentials may be unreadable).</summary>
    public MasterKeyOrigin Origin { get; }

    /// <summary>Path of the key file, when loaded from one.</summary>
    public string? KeyFilePath { get; private init; }

    /// <summary>Loads the master key from <paramref name="masterKeyPath"/>, creating it on first start.</summary>
    public static CredentialProtector FromKeyFile(string masterKeyPath)
    {
        var key = MasterKeyFile.LoadOrCreate(masterKeyPath, out var origin);
        try
        {
            return new CredentialProtector(key, origin) { KeyFilePath = masterKeyPath };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>HMAC-SHA256 of a fixed label with the key (base64): stored as <c>Security.KeyCheck</c> to detect another key.</summary>
    public string ComputeKeyCheck()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Convert.ToBase64String(HMACSHA256.HashData(Volatile.Read(ref _key), KeyCheckLabel));
    }

    /// <summary>Switches to a new key (startup, after the key file was replaced). Blobs of the old key become unreadable.</summary>
    internal void ReplaceKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Master key must be {KeySize} bytes.", nameof(key));
        }

        var old = Interlocked.Exchange(ref _key, key.ToArray());
        CryptographicOperations.ZeroMemory(old);
    }

    public byte[] Protect(string plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var blob = new byte[NonceSize + plainBytes.Length + TagSize];
            var nonce = blob.AsSpan(0, NonceSize);
            var cipher = blob.AsSpan(NonceSize, plainBytes.Length);
            var tag = blob.AsSpan(NonceSize + plainBytes.Length, TagSize);

            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(Volatile.Read(ref _key), TagSize);
            aes.Encrypt(nonce, plainBytes, cipher, tag, associatedData);
            return blob;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <summary>Decrypts a blob produced by <see cref="Protect"/>.</summary>
    /// <exception cref="CryptographicException">Blob is malformed, was tampered with, or was encrypted with another key or associated data.</exception>
    public string Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> associatedData = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (blob.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Encrypted credential is too short.");
        }

        var cipherLength = blob.Length - NonceSize - TagSize;
        var nonce = blob[..NonceSize];
        var cipher = blob.Slice(NonceSize, cipherLength);
        var tag = blob[(NonceSize + cipherLength)..];

        var plainBytes = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(Volatile.Read(ref _key), TagSize);
            aes.Decrypt(nonce, cipher, tag, plainBytes, associatedData);
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }

    public override string ToString() => nameof(CredentialProtector);
}
