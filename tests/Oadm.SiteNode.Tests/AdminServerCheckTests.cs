using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

/// <summary>
/// A program that took the channel's name first never gets a request: the client checks who serves the pipe (Windows) or
/// the socket's folder (Linux) before it sends anything.
/// </summary>
public sealed class AdminServerCheckTests
{
    [Fact]
    public void TheServiceSidIsThatOfWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The well-known service SID of TrustedInstaller, the same scheme as NT SERVICE\OadmSiteNode.
        Assert.Equal("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", AdminServerCheck.ServiceSid("TrustedInstaller").Value);
    }

    [Fact]
    public void OnlyTheNodesAccountsServeTheInstalledNodeAndAConsoleRunMayBeTheUser()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var service = AdminServerCheck.ServiceSid(AdminServerCheck.ServiceName);
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var other = new SecurityIdentifier("S-1-5-21-1-2-3-1002");

        Assert.True(AdminServerCheck.IsTrustedOwner(system, strict: true, user));
        Assert.True(AdminServerCheck.IsTrustedOwner(admins, strict: true, user));
        Assert.True(AdminServerCheck.IsTrustedOwner(service, strict: true, user));
        Assert.False(AdminServerCheck.IsTrustedOwner(user, strict: true, user));
        Assert.True(AdminServerCheck.IsTrustedOwner(user, strict: false, user));
        Assert.False(AdminServerCheck.IsTrustedOwner(other, strict: false, user));
        Assert.False(AdminServerCheck.IsTrustedOwner(null, strict: false, user));
    }

    [Fact]
    public async Task AnImpostorPipeGetsNothingAndTheClientSaysSo()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var name = "oadm-site-node-test-" + Guid.NewGuid().ToString("N");
        using var self = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetOwner(self.User!); // a plain user's program, also when the tests run elevated
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        await using var impostor = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var connected = impostor.WaitForConnectionAsync();
        var client = new AdminClient(new AdminEndpoint(name, "unused")) { StrictServerCheck = true };

        var response = await client.SendAsync(AdminRequest.Of(AdminCommands.PairingCode), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(AdminTexts.NotTheService, response.Error);
        await connected;
        var buffer = new byte[16];
        Assert.Equal(0, await impostor.ReadAsync(buffer)); // the client closed without a byte
    }

    [Fact]
    public void AnInstalledSocketInAFolderOthersMayWriteIsRefused()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "admin.sock");
        File.SetUnixFileMode(folder.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        Exception? refused = null;
        try
        {
            AdminServerCheck.CheckSocketFolder(path, strict: true);
        }
        catch (AdminServerNotTrustedException e)
        {
            refused = e;
        }

        Assert.NotNull(refused);
        AdminServerCheck.CheckSocketFolder(path, strict: false); // a node with its own data folder

        File.SetUnixFileMode(folder.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        AdminServerCheck.CheckSocketFolder(path, strict: true);
    }
}
