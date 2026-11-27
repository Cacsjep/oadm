using Oadm.Sdk.Plugins;
using Oadm.Server.Hosting;

namespace Oadm.Server.Folders;

/// <summary>One folder of a listing: its name and full path; <see cref="IsRoot"/> for a drive or top folder.</summary>
public sealed record ServerFolderEntry(string Name, string Path, bool IsRoot = false);

/// <summary>
/// The subfolders of <see cref="Path"/> (empty: the roots), at most <see cref="ServerFolderTexts.MaxEntries"/> by name with
/// <see cref="More"/> when there were more; <see cref="Problem"/> is the check of the folder itself.
/// </summary>
public sealed record ServerFolderListing(string Path, string? Parent, IReadOnlyList<ServerFolderEntry> Folders, bool More, string? Problem);

/// <summary>Why a folder call was refused; the gRPC service maps it to a status code.</summary>
public enum ServerFolderRefusal
{
    /// <summary>Not a full path, or a name no system accepts (INVALID_ARGUMENT).</summary>
    Invalid,

    /// <summary>The folder or the parent is not there (NOT_FOUND).</summary>
    NotFound,

    /// <summary>The new folder exists already (ALREADY_EXISTS).</summary>
    Exists,

    /// <summary>The server cannot open or create it, or it is a system or OADM folder (FAILED_PRECONDITION).</summary>
    Failed,
}

/// <summary>A refused folder call with the text to show (<see cref="ServerFolderTexts"/>).</summary>
public sealed class ServerFolderException(ServerFolderRefusal refusal, string message) : Exception(message)
{
    public ServerFolderRefusal Refusal { get; } = refusal;
}

/// <summary>
/// The folders of the server's computer for the client's server folder picker and the folder checks of plugins
/// (docs/spec/security.md "Folders a plugin writes to"): names and paths only, never files. Paths must be full, without
/// "." or ".." parts; hidden, system and unreadable folders are left out. As the installed service (<paramref name="permissions"/>
/// set) a chosen folder must be changeable only by administrators / root, and a new folder is made so.
/// </summary>
/// <param name="dataDirectory">The server's data folder: it, its plugins and add-ons are OADM's own.</param>
/// <param name="programDirectory">The server's program folder (OADM's own, with everything below it).</param>
/// <param name="permissions">Who may write a folder; null outside the installed service (no such check).</param>
/// <param name="maxEntries">Most folders in one listing (<see cref="ServerFolderTexts.MaxEntries"/>; tests use fewer).</param>
public sealed partial class ServerFolderBrowser(
    string dataDirectory, string programDirectory, IFolderPermissions? permissions, ILogger<ServerFolderBrowser> logger, int maxEntries = ServerFolderTexts.MaxEntries)
    : IServerFolderCheck
{
    /// <summary>Most subfolder names read from one folder before the rest is cut off (the listing then says more).</summary>
    public const int MaxScanned = 100_000;

    private static readonly StringComparison PathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static readonly EnumerationOptions Subfolders = new()
    {
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly string _dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
    private readonly string _programDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programDirectory));

    /// <summary>
    /// The full, normalized form of <paramref name="path"/>, or null when it is not a full path on this system: empty,
    /// too long, relative, with control characters, "." or ".." parts, or a Windows device path (<c>\\?\</c>, <c>\\.\</c>).
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var text = path.Trim();
        if (text.Length > ServerFolderTexts.MaxPathLength || text.Any(char.IsControl) || !Path.IsPathFullyQualified(text))
        {
            return null;
        }

        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal)
            || text.StartsWith("//?/", StringComparison.Ordinal) || text.StartsWith("//./", StringComparison.Ordinal))
        {
            return null;
        }

        if (text.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(text);
            return IsRootPath(full) ? full : Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The drives (Windows) or the top folders (Linux, macOS: "/", the data folder's parent, the mount folders).</summary>
    public IReadOnlyList<ServerFolderEntry> Roots()
    {
        var roots = new List<ServerFolderEntry>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                    {
                        continue;
                    }

                    var letter = drive.Name.TrimEnd('\\', '/');
                    var label = drive.VolumeLabel;
                    roots.Add(new ServerFolderEntry(string.IsNullOrWhiteSpace(label) ? letter : $"{label} ({letter})", drive.RootDirectory.FullName, IsRoot: true));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A drive that does not answer is left out.
                }
            }

            return roots;
        }

        roots.Add(new ServerFolderEntry("/", "/", IsRoot: true));
        var places = new List<string?> { Path.GetDirectoryName(_dataDirectory), "/mnt", "/media", "/srv", "/Volumes" };
        foreach (var place in places)
        {
            if (place is { Length: > 1 } && Directory.Exists(place) && !roots.Exists(r => string.Equals(r.Path, place, PathComparison)))
            {
                roots.Add(new ServerFolderEntry(place, place, IsRoot: true));
            }
        }

        return roots;
    }

    /// <summary>The subfolders of <paramref name="path"/>; null or empty: <see cref="Roots"/>.</summary>
    /// <exception cref="ServerFolderException">Not a full path, not there, or the server cannot open it.</exception>
    public async Task<ServerFolderListing> ListAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ServerFolderListing(string.Empty, null, Roots(), false, ServerFolderTexts.ChooseFolder);
        }

        var folder = Normalize(path) ?? throw new ServerFolderException(ServerFolderRefusal.Invalid, ServerFolderTexts.NotFullPath);
        if (!Directory.Exists(folder))
        {
            throw new ServerFolderException(ServerFolderRefusal.NotFound, ServerFolderTexts.NotFound);
        }

        var names = new List<string>();
        var more = false;
        try
        {
            foreach (var child in new DirectoryInfo(folder).EnumerateDirectories("*", Subfolders))
            {
                ct.ThrowIfCancellationRequested();
                if (names.Count == MaxScanned)
                {
                    more = true;
                    break;
                }

                names.Add(child.Name);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            throw new ServerFolderException(ServerFolderRefusal.Failed, ServerFolderTexts.CannotOpen);
        }

        names.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase) is var c and not 0 ? c : string.CompareOrdinal(a, b));
        var folders = new List<ServerFolderEntry>(Math.Min(names.Count, maxEntries));
        var index = 0;
        for (; index < names.Count && folders.Count < maxEntries; index++)
        {
            ct.ThrowIfCancellationRequested();
            var child = Path.Combine(folder, names[index]);
            if (CanRead(child))
            {
                folders.Add(new ServerFolderEntry(names[index], child));
            }
        }

        more |= index < names.Count;
        var problem = await CheckNormalizedAsync(folder, ct).ConfigureAwait(false);
        return new ServerFolderListing(folder, IsRootPath(folder) ? null : Path.GetDirectoryName(folder), folders, more, problem);
    }

    /// <inheritdoc />
    public Task<string?> CheckAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult<string?>(ServerFolderTexts.ChooseFolder);
        }

        return Normalize(path) is { } folder ? CheckNormalizedAsync(folder, ct) : Task.FromResult<string?>(ServerFolderTexts.NotFullPath);
    }

    /// <summary>
    /// Makes the folder <paramref name="name"/> in <paramref name="parent"/> and returns it. As the installed service it is
    /// made admin-only (owner Administrators / root, no write access for others).
    /// </summary>
    /// <exception cref="ServerFolderException">The path or the name is refused, the parent is gone, it exists, or the server cannot create it.</exception>
    public async Task<ServerFolderEntry> CreateAsync(string? parent, string? name, CancellationToken ct)
    {
        var folder = Normalize(parent) ?? throw new ServerFolderException(ServerFolderRefusal.Invalid, ServerFolderTexts.NotFullPath);
        if (ServerFolderTexts.CheckName(name) is { } nameProblem)
        {
            throw new ServerFolderException(ServerFolderRefusal.Invalid, nameProblem);
        }

        if (!Directory.Exists(folder))
        {
            throw new ServerFolderException(ServerFolderRefusal.NotFound, ServerFolderTexts.NotFound);
        }

        if (OwnProblem(folder) is { } own)
        {
            throw new ServerFolderException(ServerFolderRefusal.Failed, own);
        }

        var trimmed = name!.TrimStart();
        var target = Path.Combine(folder, trimmed);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new ServerFolderException(ServerFolderRefusal.Exists, ServerFolderTexts.NameExists);
        }

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            throw new ServerFolderException(ServerFolderRefusal.Failed, ServerFolderTexts.CannotCreate);
        }

        if (permissions is not null)
        {
            try
            {
                await permissions.FixAsync(target, readableByUsers: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.Security.AccessControl.PrivilegeNotHeldException or System.ComponentModel.Win32Exception)
            {
                // The folder stays; its check says that others can change it.
                LogNotAdminOnly(logger, ex, target);
            }
        }

        return new ServerFolderEntry(trimmed, target);
    }

    private async Task<string?> CheckNormalizedAsync(string folder, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
        {
            return ServerFolderTexts.NotFound;
        }

        if (IsRootPath(folder))
        {
            return ServerFolderTexts.WholeDrive;
        }

        if (OwnProblem(folder) is { } own)
        {
            return own;
        }

        if (!CanWrite(folder))
        {
            return ServerFolderTexts.CannotWrite;
        }

        if (permissions is null)
        {
            return null;
        }

        try
        {
            return await permissions.FindOwnProblemAsync(folder, ct).ConfigureAwait(false) is null ? null : ServerFolderTexts.OthersCanChange;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Security.AccessControl.PrivilegeNotHeldException or System.ComponentModel.Win32Exception)
        {
            return ServerFolderTexts.CannotOpen;
        }
    }

    /// <summary>OADM's own folders and the system's (the folder itself, or one below such a folder), else null.</summary>
    private string? OwnProblem(string folder)
    {
        if (IsSameOrBelow(folder, _programDirectory) || IsSame(folder, _dataDirectory)
            || IsSameOrBelow(folder, Core.Plugins.PluginPaths.Installed(_dataDirectory)) || IsSameOrBelow(folder, AddOns.AddOnFolders.Root(_dataDirectory)))
        {
            return ServerFolderTexts.OadmFolder;
        }

        return SystemFoldersBelow().Any(system => IsSameOrBelow(folder, system)) || SystemFoldersItself().Any(system => IsSame(folder, system))
            ? ServerFolderTexts.SystemFolder
            : null;
    }

    /// <summary>The operating system's and the programs' folders, with everything below them.</summary>
    private static IEnumerable<string> SystemFoldersBelow()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                if (Environment.GetFolderPath(special) is { Length: > 0 } path)
                {
                    yield return path;
                }
            }

            yield break;
        }

        string[] unix = ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32", "/proc", "/run", "/sbin", "/sys", "/usr",
            "/System", "/Applications", "/Library", "/private/etc"];
        foreach (var path in unix)
        {
            yield return path;
        }
    }

    /// <summary>Folders that hold other folders (ProgramData, /home, /var): their subfolders may be chosen, they themselves not.</summary>
    private static IEnumerable<string> SystemFoldersItself()
    {
        if (OperatingSystem.IsWindows())
        {
            if (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) is { Length: > 0 } data)
            {
                yield return data;
            }

            yield break;
        }

        string[] unix = ["/home", "/var", "/var/lib", "/opt", "/mnt", "/media", "/srv", "/root", "/Users", "/Volumes", "/private", "/private/var"];
        foreach (var path in unix)
        {
            yield return path;
        }
    }

    /// <summary>A drive or the top folder (C:\, /, \server\share): it has no parent.</summary>
    private static bool IsRootPath(string path) => Path.GetDirectoryName(path) is null;

    private static bool IsSame(string path, string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(folder), PathComparison);

    private static bool IsSameOrBelow(string path, string folder)
    {
        var a = Path.TrimEndingDirectorySeparator(path);
        var b = Path.TrimEndingDirectorySeparator(folder);
        return string.Equals(a, b, PathComparison)
            || (a.Length > b.Length && a.StartsWith(b, PathComparison) && (a[b.Length] == Path.DirectorySeparatorChar || a[b.Length] == Path.AltDirectorySeparatorChar));
    }

    /// <summary>Whether the server may list <paramref name="folder"/> (a folder it cannot is left out of a listing).</summary>
    private static bool CanRead(string folder)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            _ = entries.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Whether the server can create a file in <paramref name="folder"/>: a probe file, deleted at once.</summary>
    private static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".oadm-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The new folder {Path} could not be made changeable only by administrators")]
    private static partial void LogNotAdminOnly(ILogger logger, Exception ex, string path);
}
