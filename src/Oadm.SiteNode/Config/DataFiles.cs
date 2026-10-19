namespace Oadm.SiteNode.Config;

/// <summary>
/// Reads and writes the node's own files (node.json, identity.bin, local.key). A file that is absent means a first start;
/// one that exists but cannot be read (locked by a backup or virus scanner, a disk error) is read again a few times and
/// then fails the start, so a passing problem never replaces the binding or the identity. Writes go to a temporary file
/// that is flushed to the disk before it replaces the old one.
/// </summary>
public static class DataFiles
{
    public const int ReadTries = 5;

    public static readonly TimeSpan ReadPause = TimeSpan.FromMilliseconds(200);

    /// <summary>True when something has this name (a folder counts: it is not a first start either).</summary>
    public static bool Exists(string path) => Path.Exists(path);

    /// <summary>The bytes of an existing file; throws <see cref="IOException"/> when it still cannot be read after <see cref="ReadTries"/> tries.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadTries)
                {
                    throw new IOException(
                        $"{path} exists but cannot be read ({e.Message}). The site node does not start, so that its binding and identity are kept.", e);
                }

                Thread.Sleep(ReadPause);
            }
        }
    }

    /// <summary>Writes the file through a temporary file that is flushed to the disk first; <paramref name="ownerOnly"/> = mode 0600 on Linux and macOS.</summary>
    public static void WriteDurable(string path, ReadOnlySpan<byte> data, bool ownerOnly = false)
    {
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (ownerOnly && !OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
