using System.Security.Cryptography;

namespace Oadm.Core.Security;

/// <summary>
/// The 32 byte master key file (raw bytes). Created on first start; on Unix with mode 0600,
/// on Windows it inherits the user-profile ACLs of the data folder.
/// An existing file with the wrong size is never overwritten (that would orphan all stored credentials).
/// </summary>
public static class MasterKeyFile
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static byte[] LoadOrCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path))
        {
            return Load(path);
        }

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
            return Load(path);
        }
    }

    private static byte[] Load(string path)
    {
        var key = File.ReadAllBytes(path);
        if (key.Length != CredentialProtector.KeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException(
                $"Master key file '{path}' is corrupt: expected {CredentialProtector.KeySize} bytes. " +
                "Restore it from backup or delete it and re-enter all device credentials.");
        }

        return key;
    }
}
