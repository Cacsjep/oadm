using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Core.Backup;

/// <summary>
/// What a backup says about itself without the password: enough to refuse a backup of a newer OADM before asking for it.
/// The header is not secret; it is bound to the encrypted body (each chunk's associated data holds its hash).
/// </summary>
public sealed record BackupHeader
{
    public int Format { get; init; } = BackupFile.FormatVersion;

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>OADM version that made the backup ("2.6.0").</summary>
    public string OadmVersion { get; init; } = "";

    public string ServerName { get; init; } = "";

    /// <summary>The last database migration of the backup (EF migration id).</summary>
    public string Migration { get; init; } = "";

    /// <summary>automatic, manual, before-restore.</summary>
    public string Kind { get; init; } = "";

    public int Iterations { get; init; }

    public string Salt { get; init; } = "";

    public string Nonce { get; init; } = "";

    public int ChunkSize { get; init; }

    /// <summary>HMAC of a fixed label with the key from the password: tells a wrong password from a damaged file.</summary>
    public string KeyCheck { get; init; } = "";
}

/// <summary>The password is wrong, or the file is not an OADM backup or is damaged. Nothing was changed.</summary>
public sealed class BackupFileException : Exception
{
    public BackupFileException()
    {
    }

    public BackupFileException(string message)
        : base(message)
    {
    }

    public BackupFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public bool IsWrongPassword { get; init; }
}

/// <summary>
/// The backup file: <c>"OADMBACKUP" | format (1 byte) | header length (4 bytes, big endian) | header JSON | chunks</c>. The
/// body (a ZIP) is split into chunks of <see cref="BackupHeader.ChunkSize"/> bytes, each AES-256-GCM encrypted with its own
/// nonce (the header's nonce with the chunk number in its last 8 bytes) and the associated data SHA-256(header) | chunk
/// number | last flag: chunks cannot be reordered, dropped or cut off unnoticed. The key comes from the password with
/// PBKDF2-SHA256. Every chunk but the last holds exactly ChunkSize bytes; the last fewer (possibly none).
/// </summary>
public static class BackupFile
{
    public const int FormatVersion = 1;
    public const string Extension = ".oadmbackup";
    public const int DefaultIterations = 600_000;
    public const int DefaultChunkSize = 1024 * 1024;
    public const int MinPasswordLength = 8;
    private const int TagSize = 16;
    private const int MaxHeaderBytes = 64 * 1024;
    private static readonly byte[] Magic = "OADMBACKUP"u8.ToArray();
    private static readonly byte[] KeyCheckLabel = "OADM backup key check v1"u8.ToArray();

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Why a backup password cannot be used; null when it can.</summary>
    public static string? PasswordProblem(string? password) =>
        string.IsNullOrEmpty(password) ? "Enter a password."
        : password.Length < MinPasswordLength ? $"Use at least {MinPasswordLength} characters."
        : password.Length > 256 ? "Use at most 256 characters."
        : null;

    /// <summary>Starts a backup on <paramref name="output"/>: writes the header and returns the stream the ZIP body goes into.</summary>
    public static Stream Create(Stream output, BackupHeader header, string password, int iterations = DefaultIterations, int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(header);
        if (PasswordProblem(password) is { } problem)
        {
            throw new ArgumentException(problem, nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveKey(password, salt, iterations);
        var full = header with
        {
            Format = FormatVersion,
            Iterations = iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            ChunkSize = chunkSize,
            KeyCheck = Convert.ToBase64String(HMACSHA256.HashData(key, KeyCheckLabel)),
        };
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(full, Json);
        output.Write(Magic);
        output.WriteByte(FormatVersion);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, headerBytes.Length);
        output.Write(length);
        output.Write(headerBytes);
        return new EncryptingStream(output, key, nonce, SHA256.HashData(headerBytes), chunkSize);
    }

    /// <summary>Reads the header (no password needed). Throws <see cref="BackupFileException"/> for a file that is not a backup.</summary>
    public static BackupHeader ReadHeader(Stream input) => ReadHeaderCore(input).Header;

    /// <summary>Opens the body with the password: a stream of the ZIP, checked chunk by chunk.</summary>
    public static (BackupHeader Header, Stream Body) Open(Stream input, string password)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (header, hash) = ReadHeaderCore(input);
        if (header.Format != FormatVersion)
        {
            throw new BackupFileException("This backup was made by a newer OADM. Update OADM first.");
        }

        byte[] salt;
        byte[] nonce;
        try
        {
            salt = Convert.FromBase64String(header.Salt);
            nonce = Convert.FromBase64String(header.Nonce);
        }
        catch (FormatException ex)
        {
            throw new BackupFileException("The file is not an OADM backup or is damaged.", ex);
        }

        if (header.Iterations is < 1 or > 10_000_000 || header.ChunkSize is < 1024 or > 64 * 1024 * 1024 || nonce.Length != 12 || salt.Length < 8)
        {
            throw new BackupFileException("The file is not an OADM backup or is damaged.");
        }

        var key = DeriveKey(password ?? string.Empty, salt, header.Iterations);
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, KeyCheckLabel), SafeBase64(header.KeyCheck)))
        {
            CryptographicOperations.ZeroMemory(key);
            throw new BackupFileException("The password is wrong.") { IsWrongPassword = true };
        }

        return (header, new DecryptingStream(input, key, nonce, hash, header.ChunkSize));
    }

    private static byte[] SafeBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static (BackupHeader Header, byte[] Hash) ReadHeaderCore(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Span<byte> start = stackalloc byte[15];
        if (ReadFully(input, start) != start.Length || !start[..Magic.Length].SequenceEqual(Magic))
        {
            throw new BackupFileException("The file is not an OADM backup.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(start[11..]);
        if (start[10] > FormatVersion)
        {
            throw new BackupFileException("This backup was made by a newer OADM. Update OADM first.");
        }

        if (length is <= 0 or > MaxHeaderBytes)
        {
            throw new BackupFileException("The file is not an OADM backup or is damaged.");
        }

        var bytes = new byte[length];
        if (ReadFully(input, bytes) != length)
        {
            throw new BackupFileException("The backup file is incomplete.");
        }

        try
        {
            var header = JsonSerializer.Deserialize<BackupHeader>(bytes, Json) ?? throw new JsonException("empty");
            return (header, SHA256.HashData(bytes));
        }
        catch (JsonException ex)
        {
            throw new BackupFileException("The file is not an OADM backup or is damaged.", ex);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static int ReadFully(Stream input, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = input.Read(buffer[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static void ChunkNonce(byte[] baseNonce, long index, Span<byte> nonce)
    {
        baseNonce.CopyTo(nonce);
        var counter = BinaryPrimitives.ReadInt64BigEndian(nonce[4..]) ^ index;
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], counter);
    }

    private static byte[] ChunkData(byte[] headerHash, long index, bool last)
    {
        var aad = new byte[headerHash.Length + 9];
        headerHash.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(headerHash.Length), index);
        aad[^1] = last ? (byte)1 : (byte)0;
        return aad;
    }

    /// <summary>Encrypts what is written, chunk by chunk; disposing writes the last chunk (also when it is empty).</summary>
    private sealed class EncryptingStream(Stream output, byte[] key, byte[] nonce, byte[] headerHash, int chunkSize) : Stream
    {
        private readonly AesGcm _aes = new(key, TagSize);
        private readonly byte[] _buffer = new byte[chunkSize];
        private readonly byte[] _cipher = new byte[chunkSize + TagSize];
        private int _filled;
        private long _index;
        private bool _closed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            while (!buffer.IsEmpty)
            {
                // A full buffer is written only when more data follows, so the last chunk is always written by Dispose.
                if (_filled == _buffer.Length)
                {
                    WriteChunk(last: false);
                }

                var n = Math.Min(buffer.Length, _buffer.Length - _filled);
                buffer[..n].CopyTo(_buffer.AsSpan(_filled));
                _filled += n;
                buffer = buffer[n..];
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void WriteChunk(bool last)
        {
            Span<byte> chunkNonce = stackalloc byte[12];
            ChunkNonce(nonce, _index, chunkNonce);
            var cipher = _cipher.AsSpan(0, _filled + TagSize);
            _aes.Encrypt(chunkNonce, _buffer.AsSpan(0, _filled), cipher[.._filled], cipher[_filled..], ChunkData(headerHash, _index, last));
            output.Write(cipher);
            _index++;
            _filled = 0;
        }

        public override void Flush() => output.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                if (_filled == _buffer.Length)
                {
                    WriteChunk(last: false);
                }

                WriteChunk(last: true);
                output.Flush();
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(key);
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Decrypts chunk by chunk; a chunk that does not verify, or a missing last chunk, throws <see cref="BackupFileException"/>.</summary>
    private sealed class DecryptingStream(Stream input, byte[] key, byte[] nonce, byte[] headerHash, int chunkSize) : Stream
    {
        private readonly AesGcm _aes = new(key, TagSize);
        private readonly byte[] _cipher = new byte[chunkSize + TagSize];
        private readonly byte[] _plain = new byte[chunkSize];
        private int _plainLength;
        private int _plainOffset;
        private long _index;
        private bool _ended;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_plainOffset == _plainLength)
            {
                if (_ended || buffer.IsEmpty)
                {
                    return 0;
                }

                NextChunk();
            }

            var n = Math.Min(buffer.Length, _plainLength - _plainOffset);
            _plain.AsSpan(_plainOffset, n).CopyTo(buffer);
            _plainOffset += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer.AsSpan(offset, count)));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        private void NextChunk()
        {
            var read = ReadFully(input, _cipher);
            if (read < TagSize)
            {
                throw new BackupFileException("The backup file is incomplete.");
            }

            // A full chunk may still be the last one (a body of exactly n chunks ends with an empty last chunk instead).
            var last = read < _cipher.Length;
            var length = read - TagSize;
            Span<byte> chunkNonce = stackalloc byte[12];
            ChunkNonce(nonce, _index, chunkNonce);
            try
            {
                _aes.Decrypt(chunkNonce, _cipher.AsSpan(0, length), _cipher.AsSpan(length, TagSize), _plain.AsSpan(0, length), ChunkData(headerHash, _index, last));
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new BackupFileException(last ? "The backup file is damaged or incomplete." : "The backup file is damaged.", ex);
            }

            _index++;
            _plainLength = length;
            _plainOffset = 0;
            _ended = last;
            if (last && input.ReadByte() != -1)
            {
                throw new BackupFileException("The backup file is damaged (data after its end).");
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(_plain);
            }

            base.Dispose(disposing);
        }
    }
}
