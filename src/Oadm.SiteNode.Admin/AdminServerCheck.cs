using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Oadm.SiteNode.Admin;

/// <summary>The local channel is served by another program than the OADM Site Node service: nothing was sent.</summary>
public sealed class AdminServerNotTrustedException(string message) : IOException(message);

/// <summary>
/// Before the app or the command line sends anything (a change, or a read that answers with the pairing code), it checks
/// who serves the channel: another program that took the pipe name first must never get the request nor fake the answer.
/// Windows: the owner of the pipe (the creator's account, which a program cannot fake) must be SYSTEM, the service's own
/// account <c>NT SERVICE\OadmSiteNode</c> or Administrators; a node with its own data folder (a console run such as
/// <c>manage run node</c>) may also run as the current user. Linux: the socket's folder must not be writable by others
/// (systemd creates <c>/run/oadm-site-node</c> for the service).
/// </summary>
public static class AdminServerCheck
{
    /// <summary>The Windows service of the node.</summary>
    public const string ServiceName = "OadmSiteNode";

    /// <summary>The SID of <c>NT SERVICE\&lt;service&gt;</c>: S-1-5-80 and the SHA-1 of the upper-case name (UTF-16).</summary>
    [SupportedOSPlatform("windows")]
    public static SecurityIdentifier ServiceSid(string serviceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceName);
#pragma warning disable CA5350 // the SID scheme of Windows, not a security choice of ours
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
#pragma warning restore CA5350
        var parts = Enumerable.Range(0, 5).Select(i => BitConverter.ToUInt32(hash, i * 4).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new SecurityIdentifier("S-1-5-80-" + string.Join('-', parts));
    }

    /// <summary>True when <paramref name="owner"/> may serve the channel; <paramref name="strict"/> for the installed node.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsTrustedOwner(SecurityIdentifier? owner, bool strict, SecurityIdentifier? currentUser)
    {
        if (owner is null)
            return false;
        if (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner == ServiceSid(ServiceName))
            return true;
        return !strict && currentUser is not null && owner == currentUser;
    }

    /// <summary>Throws <see cref="AdminServerNotTrustedException"/> when the pipe's server is not the node.</summary>
    [SupportedOSPlatform("windows")]
    public static void CheckPipe(NamedPipeClientStream pipe, bool strict)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        SecurityIdentifier? owner;
        try
        {
            owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or IdentityNotMappedException or IOException)
        {
            owner = null;
        }

        using var self = WindowsIdentity.GetCurrent();
        if (!IsTrustedOwner(owner, strict, self.User))
            throw new AdminServerNotTrustedException(AdminTexts.NotTheService);
    }

    /// <summary>Unix: the installed node's socket folder must be writable by its owner only.</summary>
    [UnsupportedOSPlatform("windows")]
    public static void CheckSocketFolder(string socketPath, bool strict)
    {
        if (!strict || Path.GetDirectoryName(socketPath) is not { Length: > 0 } folder || !Directory.Exists(folder))
            return;
        var mode = File.GetUnixFileMode(folder);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new AdminServerNotTrustedException(AdminTexts.NotTheService);
    }
}
