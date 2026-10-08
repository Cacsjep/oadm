using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Oadm.Server.Hosting;

/// <summary>Who may write a folder, per operating system. Replaced by a fake in tests.</summary>
public interface IFolderPermissions
{
    /// <summary>
    /// Null when only administrators / root can write the folder and everything below it, else what is wrong
    /// ("BUILTIN\Users can write C:\...\plugins\x", "/var/lib/oadm/x: owner uid 1000").
    /// </summary>
    Task<string?> FindProblemAsync(string path, CancellationToken ct);

    /// <summary>
    /// Makes the folder and everything below it admin-only (owner Administrators / root, no write access for anyone
    /// else). <paramref name="readableByUsers"/> keeps read access for users (program folders). Throws when it cannot.
    /// </summary>
    Task FixAsync(string path, bool readableByUsers, CancellationToken ct);
}

public enum FolderVerdict
{
    /// <summary>Only administrators / root can write it.</summary>
    Secure = 0,

    /// <summary>Others could write it; the permissions were reset.</summary>
    Fixed = 1,

    /// <summary>Others can write it and it could not be fixed.</summary>
    Insecure = 2,
}

/// <param name="Problem">What was wrong (Fixed, Insecure).</param>
/// <param name="FixError">Why the fix failed (Insecure).</param>
public sealed record FolderCheck(string Path, FolderVerdict Verdict, string? Problem = null, string? FixError = null);

/// <summary>
/// Checks the folders the server runs code or keeps secrets from (data folder, native library extraction folder, plugin
/// folders) when it runs as the installed service (SYSTEM / root): writable only by administrators / root, else the
/// permissions are reset, else the folder is refused. Spec "Production hardening / 5. Installation and release".
/// </summary>
public sealed partial class FolderGuard(IFolderPermissions permissions, ILogger<FolderGuard> logger)
{
    /// <summary>Checks the folder and everything below it; fixes it when needed. Missing folders count as secure.</summary>
    public async Task<FolderCheck> EnsureAsync(string path, bool readableByUsers, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Directory.Exists(path))
        {
            return new FolderCheck(path, FolderVerdict.Secure);
        }

        var problem = await permissions.FindProblemAsync(path, ct).ConfigureAwait(false);
        if (problem is null)
        {
            return new FolderCheck(path, FolderVerdict.Secure);
        }

        try
        {
            await permissions.FixAsync(path, readableByUsers, ct).ConfigureAwait(false);
            var after = await permissions.FindProblemAsync(path, ct).ConfigureAwait(false);
            if (after is null)
            {
                LogFixed(logger, path, problem);
                return new FolderCheck(path, FolderVerdict.Fixed, problem);
            }

            LogInsecure(logger, path, problem, after);
            return new FolderCheck(path, FolderVerdict.Insecure, problem, after);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or PrivilegeNotHeldException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            LogInsecure(logger, path, problem, ex.Message);
            return new FolderCheck(path, FolderVerdict.Insecure, problem, ex.Message);
        }
    }

    /// <summary>The data folder (database, master key) and the extraction folder: the server refuses to start when insecure.</summary>
    public async Task EnsureServerFoldersAsync(IEnumerable<string> folders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folders);
        foreach (var folder in folders)
        {
            var check = await EnsureAsync(folder, readableByUsers: false, ct).ConfigureAwait(false);
            if (check.Verdict == FolderVerdict.Insecure)
            {
                throw new InvalidOperationException(
                    $"The folder {folder} can be changed by users other than administrators ({check.Problem}) and could not be fixed " +
                    $"({check.FixError}). The OADM server does not start: make it writable only by administrators / root.");
            }
        }
    }

    /// <summary>
    /// Checks (and fixes) every plugin folder below the roots (<c>&lt;root&gt;/&lt;plugin&gt;/plugin.json</c>, or a root that
    /// is a plugin folder itself) before the loader runs. Returns full path -> reason for the folders that must be skipped.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> CheckPluginFoldersAsync(IEnumerable<string> roots, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var refused = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var folder in PluginFolders(roots))
        {
            var check = await EnsureAsync(folder, readableByUsers: true, ct).ConfigureAwait(false);
            if (check.Verdict == FolderVerdict.Insecure)
            {
                refused[folder] = $"Skipped: the plugin folder can be changed by users other than administrators ({check.Problem}) and could not be fixed ({check.FixError}).";
            }
        }

        return refused;
    }

    /// <summary>The plugin folders the loader would load, as full paths.</summary>
    public static IEnumerable<string> PluginFolders(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            if (File.Exists(Path.Combine(root, Core.Plugins.PluginLoader.ManifestFileName)))
            {
                yield return Path.GetFullPath(root);
                continue;
            }

            foreach (var folder in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(folder, Core.Plugins.PluginLoader.ManifestFileName)))
                {
                    yield return Path.GetFullPath(folder);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Folder {Path} could be changed by users other than administrators ({Problem}); its permissions were reset")]
    private static partial void LogFixed(ILogger logger, string path, string problem);

    [LoggerMessage(Level = LogLevel.Error, Message = "Folder {Path} can be changed by users other than administrators ({Problem}) and could not be fixed: {Error}")]
    private static partial void LogInsecure(ILogger logger, string path, string problem, string error);
}

/// <summary>
/// Windows: the folder and every entry below it must be owned by SYSTEM, Administrators or TrustedInstaller and no allow
/// entry may give anyone else write, delete or permission rights (CREATOR OWNER is fine: only those who may create can
/// own). Fix: owner Administrators, protected ACL SYSTEM + Administrators full control (+ Users read and execute for
/// program folders), entries below inherit it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFolderPermissions : IFolderPermissions
{
    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x40000000 /* GENERIC_WRITE */ | (FileSystemRights)0x10000000 /* GENERIC_ALL */;

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static bool IsTrusted(SecurityIdentifier sid) =>
        sid == System || sid == Administrators || sid == TrustedInstaller;

    /// <summary>Whether an allow entry of <paramref name="rights"/> for <paramref name="sid"/> lets a non-admin change things.</summary>
    public static bool GrantsWriteToOthers(SecurityIdentifier sid, FileSystemRights rights) =>
        (rights & WriteRights) != 0 && !IsTrusted(sid) && sid != CreatorOwner;

    public Task<string?> FindProblemAsync(string path, CancellationToken ct)
    {
        var root = new DirectoryInfo(path);
        var problem = Problem(root.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), path);
        if (problem is null)
        {
            foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                FileSystemSecurity security = entry is DirectoryInfo d
                    ? d.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                    : ((FileInfo)entry).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                if (Problem(security, entry.FullName) is { } p)
                {
                    problem = p;
                    break;
                }
            }
        }

        return Task.FromResult(problem);
    }

    public Task FixAsync(string path, bool readableByUsers, CancellationToken ct)
    {
        var root = new DirectoryInfo(path);
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (readableByUsers)
        {
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        root.SetAccessControl(security);

        // Everything below: owner Administrators, no explicit entries, inherit from the folder.
        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo dir)
            {
                var s = new DirectorySecurity();
                s.SetOwner(Administrators);
                s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                dir.SetAccessControl(s);
            }
            else
            {
                var s = new FileSecurity();
                s.SetOwner(Administrators);
                s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                ((FileInfo)entry).SetAccessControl(s);
            }
        }

        return Task.CompletedTask;
    }

    private static string? Problem(FileSystemSecurity security, string path)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !IsTrusted(owner))
        {
            return $"{path} is owned by {Name(owner)}";
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid
                && GrantsWriteToOthers(sid, rule.FileSystemRights))
            {
                return $"{Name(sid)} can write {path}";
            }
        }

        return null;
    }

    private static string Name(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}

/// <summary>
/// Linux and macOS: the folder and everything below it must be owned by root and not writable by group or others
/// (symbolic links are not checked). Uses <c>find</c>, <c>chown</c> and <c>chmod</c>, which every system has; the server
/// runs as root, so the fix (owner root, group/other write removed) always may change the files.
/// </summary>
public sealed class UnixFolderPermissions(ICommandRunner runner) : IFolderPermissions
{
    public static IReadOnlyList<string> FindArguments(string path) =>
        [path, "!", "-type", "l", "(", "!", "-user", "0", "-o", "-perm", "-0020", "-o", "-perm", "-0002", ")", "-print"];

    public async Task<string?> FindProblemAsync(string path, CancellationToken ct)
    {
        var result = await runner.RunAsync("find", FindArguments(path), ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return $"cannot check {path}: {result.Output}";
        }

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length switch
        {
            0 => null,
            1 => $"{lines[0]} is not owned by root or writable by group or others",
            _ => $"{lines[0]} and {lines.Length - 1} more are not owned by root or writable by group or others",
        };
    }

    public async Task FixAsync(string path, bool readableByUsers, CancellationToken ct)
    {
        foreach (var (tool, args) in new (string, string[])[] { ("chown", ["-R", "0:0", path]), ("chmod", ["-R", "go-w", path]) })
        {
            var result = await runner.RunAsync(tool, args, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"{tool} failed: {result.Output}");
            }
        }
    }
}
