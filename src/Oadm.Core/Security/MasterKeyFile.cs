using System.Globalization;
using System.Security.Cryptography;

namespace Oadm.Core.Security;

/// <summary>Where the master key in use came from.</summary>
public enum MasterKeyOrigin
{
    /// <summary>Read from an existing, well-formed master.key.</summary>
    Loaded,

    /// <summary>master.key did not exist; a new key was created.</summary>
    Created,

    /// <summary>master.key had the wrong size; it was kept as a backup and a new key was created.</summary>
    ReplacedUnreadable,
}

/// <summary>
/// The 32 byte master key file (raw bytes). Created on first start; on Unix with mode 0600,
/// on Windows it inherits the user-profile ACLs of the data folder.
/// A file that cannot be used (wrong size) or no longer matches the stored key check is never deleted:
/// it is renamed to <c>master.key.replaced-&lt;UTC time&gt;</c> before a new key is written
/// (production hardening 3: warn and continue).
/// </summary>
public static class MasterKeyFile
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static byte[] LoadOrCreate(string path) => LoadOrCreate(path, out _);

    /// <summary>Loads the key, creates it when missing, or replaces an unreadable file (kept as a backup).</summary>
    public static byte[] LoadOrCreate(string path, out MasterKeyOrigin origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == CredentialProtector.KeySize)
            {
                origin = MasterKeyOrigin.Loaded;
                return existing;
            }

            CryptographicOperations.ZeroMemory(existing);
            origin = MasterKeyOrigin.ReplacedUnreadable;
            return Replace(path);
        }

        origin = MasterKeyOrigin.Created;
        return Create(path);
    }

    /// <summary>Keeps the current file as <c>master.key.replaced-&lt;time&gt;</c> and writes a new key.</summary>
    public static byte[] Replace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path))
        {
            var backup = BackupPath(path);
            File.Move(path, backup);
        }

        return Create(path);
    }

    private static string BackupPath(string path)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = path + ".replaced-" + stamp;
        for (var n = 2; File.Exists(backup); n++)
        {
            backup = path + ".replaced-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        return backup;
    }

    private static byte[] Create(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var key = RandomNumberGenerator.GetBytes(CredentialProtector.KeySize);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerReadWrite;
            }

            using (var stream = new FileStream(path, options))
            {
                stream.Write(key);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, OwnerReadWrite);
            }

            return key;
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created it first; use theirs.
            CryptographicOperations.ZeroMemory(key);
            var theirs = File.ReadAllBytes(path);
            if (theirs.Length != CredentialProtector.KeySize)
            {
                throw new InvalidOperationException($"Master key file '{path}' is being written by another process.");
            }

            return theirs;
        }
    }
}
