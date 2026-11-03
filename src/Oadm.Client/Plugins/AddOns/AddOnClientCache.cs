using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins.AddOns;

/// <summary>
/// The client parts of add-ons in the client's data folder (docs/spec/add-ons.md rule 6):
/// <c>&lt;datafolder&gt;/addons/&lt;id&gt;/&lt;version&gt;/</c>, one folder per server version. A part is written only when
/// every file matches the SHA-256 the server sent, into a new folder that is moved into place as a whole, so a version
/// folder is always complete. A removed add-on gets a mark and its folder goes at the client's next start
/// (<see cref="DeleteRemoved"/>): its assemblies may be loaded while the client runs.
/// </summary>
public sealed partial class AddOnClientCache
{
    /// <summary>The file that marks an add-on whose files go at the next start.</summary>
    internal const string RemovedMark = ".removed";

    private readonly ILogger<AddOnClientCache> _logger;

    public AddOnClientCache(string root, ILogger<AddOnClientCache> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        _logger = logger;
    }

    /// <summary><c>&lt;datafolder&gt;/addons</c>.</summary>
    public string Root { get; }

    /// <summary>The folder of a cached version, or null when it is not there (or the id or version is not a safe name).</summary>
    public string? Find(string id, string version)
    {
        if (!IsSafeName(id) || !IsSafeName(version))
        {
            return null;
        }

        string folder = Path.Combine(Root, id, version);
        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>
    /// Checks every file of <paramref name="part"/> against its SHA-256 and writes them to the version folder; returns the
    /// folder. Nothing is written when a file does not match, a path leaves the folder or the part is not the one asked for.
    /// </summary>
    /// <exception cref="AddOnPartException">The part is damaged or not valid; nothing was written.</exception>
    public string Store(string expectedId, AddOnClientPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (!string.Equals(part.Id, expectedId, StringComparison.OrdinalIgnoreCase) || !IsSafeName(part.Id) || !IsSafeName(part.Version))
        {
            throw new AddOnPartException($"The server sent another client part than {expectedId}.");
        }

        if (part.Files.Count == 0)
        {
            throw new AddOnPartException($"The client part of {expectedId} has no files.");
        }

        var files = new List<(string Path, byte[] Data)>(part.Files.Count);
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
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new AddOnPartException($"The client part of {expectedId} is damaged.");
            }

            files.Add((relative, data));
        }

        string idFolder = Path.Combine(Root, part.Id);
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
                // Another sync got there first: its folder is complete.
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

    /// <summary>Marks an add-on whose files go at the next start (the server no longer has it installed).</summary>
    public void MarkRemoved(string id)
    {
        if (!IsSafeName(id))
        {
            return;
        }

        string folder = Path.Combine(Root, id);
        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(folder, RemovedMark), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(_logger, ex.Message, folder);
        }
    }

    /// <summary>Takes the mark back: the add-on was installed again before the client restarted.</summary>
    public void Unmark(string id)
    {
        if (IsSafeName(id))
        {
            string mark = Path.Combine(Root, id, RemovedMark);
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

    /// <summary>At the client's start: deletes the folders of removed add-ons and half-written downloads.</summary>
    public void DeleteRemoved()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        try
        {
            foreach (string folder in Directory.EnumerateDirectories(Root))
            {
                if (File.Exists(Path.Combine(folder, RemovedMark)))
                {
                    string id = Path.GetFileName(folder);
                    LogDeleted(_logger, id);
                    DeleteQuietly(folder);
                    continue;
                }

                foreach (string download in Directory.EnumerateDirectories(folder, ".download-*"))
                {
                    DeleteQuietly(download);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(_logger, ex.Message, Root);
        }
    }

    /// <summary>An id or version usable as one folder name: letters, digits, '.', '-', '_', '+'; never "." or "..".</summary>
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Client part of add-on {Id} V{Version} stored ({Count} files)")]
    private static partial void LogStored(ILogger logger, string id, string version, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client files of the removed add-on {Id} deleted")]
    private static partial void LogDeleted(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Add-on files could not be cleaned up ({Message}): {Path}")]
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
