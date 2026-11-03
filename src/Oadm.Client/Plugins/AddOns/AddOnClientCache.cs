using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Oadm.Contracts;
using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins.AddOns;

/// <summary>
/// The client parts of add-ons in the client's data folder (docs/spec/add-ons.md rule 6):
/// <c>&lt;datafolder&gt;/plugin-cache/&lt;server&gt;/&lt;id&gt;/&lt;version&gt;/</c>, kept per server (its pinned certificate
/// fingerprint) and version, never in the server's own <c>addons/</c> folder (a server and a client of the same user share
/// the data folder). A part is written only when every file matches the SHA-256 the server sent and the whole part its
/// digest, into a new folder that is moved into place as a whole, so a version folder is always complete; a cached folder
/// is used only while it still matches the digest the server lists. A removed add-on gets a mark and its folder goes at the
/// client's next start (<see cref="DeleteRemoved"/>), like the versions an add-on no longer uses: their assemblies may be
/// loaded while the client runs.
/// </summary>
public sealed partial class AddOnClientCache
{
    /// <summary>The cache folder in the client's data folder.</summary>
    public const string FolderName = "plugin-cache";

    /// <summary>The file that marks an add-on whose files go at the next start.</summary>
    internal const string RemovedMark = ".removed";

    /// <summary>The file that names the version an add-on uses; the other versions go at the next start.</summary>
    internal const string CurrentMark = ".current";

    private readonly ILogger<AddOnClientCache> _logger;

    public AddOnClientCache(string root, ILogger<AddOnClientCache> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        _logger = logger;
    }

    /// <summary><c>&lt;datafolder&gt;/plugin-cache</c>.</summary>
    public string Root { get; }

    /// <summary>
    /// The folder of a cached version of <paramref name="server"/>, or null when it is not there, the names are not safe or
    /// its files no longer match <paramref name="digest"/> (then it is deleted). An empty digest skips the check.
    /// </summary>
    public string? Find(string server, string id, string version, string? digest = null)
    {
        if (!IsSafeName(server) || !IsSafeName(id) || !IsSafeName(version))
        {
            return null;
        }

        string folder = Path.Combine(Root, server, id, version);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(digest) && !string.Equals(DigestOf(folder), digest, StringComparison.OrdinalIgnoreCase))
        {
            LogChanged(_logger, id, version);
            DeleteQuietly(folder);
            return null;
        }

        return folder;
    }

    /// <summary>
    /// Checks every file of <paramref name="part"/> against its SHA-256 (and the whole part against its digest) and writes
    /// them to the version folder of <paramref name="server"/>; returns the folder. Nothing is written when a file does not
    /// match, a path leaves the folder or the part is not the one asked for.
    /// </summary>
    /// <exception cref="AddOnPartException">The part is damaged or not valid; nothing was written.</exception>
    public string Store(string server, string expectedId, AddOnClientPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (!IsSafeName(server))
        {
            throw new AddOnPartException($"The server of {expectedId} has no usable name.");
        }

        if (!string.Equals(part.Id, expectedId, StringComparison.OrdinalIgnoreCase) || !IsSafeName(part.Id) || !IsSafeName(part.Version))
        {
            throw new AddOnPartException($"The server sent another client part than {expectedId}.");
        }

        if (part.Files.Count == 0)
        {
            throw new AddOnPartException($"The client part of {expectedId} has no files.");
        }

        var files = new List<(string Path, byte[] Data)>(part.Files.Count);
        var hashes = new List<(string Path, string Sha256)>(part.Files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AddOnFile file in part.Files)
        {
            string relative = SafeRelativePath(file.Path)
                ?? throw new AddOnPartException($"The client part of {expectedId} has a file outside its folder.");
            if (!seen.Add(relative))
            {
                throw new AddOnPartException($"The client part of {expectedId} has a file twice.");
            }

            byte[] data = file.Data.ToByteArray();
            string sha = Convert.ToHexStringLower(SHA256.HashData(data));
            if (!string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new AddOnPartException($"The client part of {expectedId} is damaged.");
            }

            files.Add((relative, data));
            hashes.Add((file.Path, sha));
        }

        if (part.Digest.Length > 0 && !string.Equals(AddOnLayout.Digest(hashes), part.Digest, StringComparison.OrdinalIgnoreCase))
        {
            throw new AddOnPartException($"The client part of {expectedId} is damaged.");
        }

        string idFolder = Path.Combine(Root, server, part.Id);
        string target = Path.Combine(idFolder, part.Version);
        string temp = Path.Combine(idFolder, ".download-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (relative, data) in files)
            {
                string path = Path.Combine(temp, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
            }

            if (Directory.Exists(target))
            {
                // Another client of this user got there first: its folder is complete (a copy that no longer matched
                // was deleted by Find).
                return target;
            }

            Directory.Move(temp, target);
            LogStored(_logger, part.Id, part.Version, files.Count);
            return target;
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>The add-on uses <paramref name="version"/> of this server: the other versions go at the next start.</summary>
    public void SetCurrent(string server, string id, string version)
    {
        if (IsSafeName(server) && IsSafeName(id) && IsSafeName(version))
        {
            WriteQuietly(Path.Combine(Root, server, id, CurrentMark), version);
        }
    }

    /// <summary>Marks an add-on whose files go at the next start (the server no longer has it installed).</summary>
    public void MarkRemoved(string server, string id)
    {
        if (IsSafeName(server) && IsSafeName(id) && Directory.Exists(Path.Combine(Root, server, id)))
        {
            WriteQuietly(Path.Combine(Root, server, id, RemovedMark), string.Empty);
        }
    }

    /// <summary>Takes the mark back: the add-on was installed again before the client restarted.</summary>
    public void Unmark(string server, string id)
    {
        if (IsSafeName(server) && IsSafeName(id))
        {
            string mark = Path.Combine(Root, server, id, RemovedMark);
            try
            {
                File.Delete(mark);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogCleanupFailed(_logger, ex.Message, mark);
            }
        }
    }

    /// <summary>
    /// At the client's start, before any add-on loads: deletes the folders of removed add-ons, the versions an add-on no
    /// longer uses and half-written downloads.
    /// </summary>
    public void DeleteRemoved()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        try
        {
            foreach (string server in Directory.EnumerateDirectories(Root))
            {
                foreach (string folder in Directory.EnumerateDirectories(server))
                {
                    if (File.Exists(Path.Combine(folder, RemovedMark)))
                    {
                        string id = Path.GetFileName(folder);
                        LogDeleted(_logger, id);
                        DeleteQuietly(folder);
                        continue;
                    }

                    string? current = ReadQuietly(Path.Combine(folder, CurrentMark));
                    foreach (string version in Directory.EnumerateDirectories(folder))
                    {
                        string name = Path.GetFileName(version);
                        if (name.StartsWith(".download-", StringComparison.Ordinal)
                            || (current is not null && !string.Equals(name, current, StringComparison.Ordinal)))
                        {
                            DeleteQuietly(version);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(_logger, ex.Message, Root);
        }
    }

    /// <summary>
    /// The digest of a cached version folder (<see cref="AddOnLayout.Digest"/> over its files): the files are hashed again,
    /// so a file changed on disk shows.
    /// </summary>
    public static string DigestOf(string folder)
    {
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/'), HashFile(path)));
        return AddOnLayout.Digest(files);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>A server, id or version usable as one folder name: letters, digits, '.', '-', '_', '+'; never "." or "..".</summary>
    internal static bool IsSafeName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 128 && SafeName().IsMatch(name) && !name.StartsWith('.');

    /// <summary>The "/" separated path as a relative path of this system; null when it is rooted or leaves the folder.</summary>
    internal static string? SafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 260)
        {
            return null;
        }

        string[] segments = path.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny([.. Path.GetInvalidFileNameChars(), '\\', ':']) >= 0)
            {
                return null;
            }
        }

        return Path.Combine(segments);
    }

    private void WriteQuietly(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(_logger, ex.Message, path);
        }
    }

    private static string? ReadQuietly(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } text ? text : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void DeleteQuietly(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(_logger, ex.Message, folder);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._+-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();

    [LoggerMessage(Level = LogLevel.Information, Message = "Client part of optional plugin {Id} V{Version} stored ({Count} files)")]
    private static partial void LogStored(ILogger logger, string id, string version, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored client part of optional plugin {Id} V{Version} no longer matches the server's: fetched again")]
    private static partial void LogChanged(ILogger logger, string id, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client files of the removed optional plugin {Id} deleted")]
    private static partial void LogDeleted(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optional plugin files could not be cleaned up ({Message}): {Path}")]
    private static partial void LogCleanupFailed(ILogger logger, string message, string path);
}

/// <summary>A client part that is damaged or not valid; nothing of it was written.</summary>
public sealed class AddOnPartException : Exception
{
    public AddOnPartException()
    {
    }

    public AddOnPartException(string message)
        : base(message)
    {
    }

    public AddOnPartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
