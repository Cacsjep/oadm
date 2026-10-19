using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Oadm.SiteNode.Admin;

namespace Oadm.SiteNode.AdminChannel;

/// <summary>
/// The local channel of the app and the command line: a named pipe on Windows (every signed-in user may connect; an
/// administrator is SYSTEM or a caller whose token is elevated into Administrators), a Unix socket elsewhere
/// (mode 0666; an administrator is root or the service's own user, read from the peer credentials). One request per
/// connection.
/// </summary>
public sealed partial class AdminChannelHost(AdminHandler handler, NodeOptions options, ILogger<AdminChannelHost> logger) : BackgroundService
{
    /// <summary>A channel that cannot be opened is tried again after this time.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Why the channel is not open right now (null while it answers); the node runs on without it.</summary>
    public string? Problem { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.AdminEndpoint is not { } endpoint)
            return;
        var name = OperatingSystem.IsWindows() ? endpoint.PipeName : endpoint.SocketPath;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    await ServePipeAsync(endpoint.PipeName, stoppingToken).ConfigureAwait(false);
                else
                    await ServeSocketAsync(endpoint.SocketPath, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // The app and the command line cannot reach the node until the channel opens; the links keep running.
                Problem = e.Message;
                LogCannotOpen(logger, name, e.Message);
            }

            try
            {
                await Task.Delay(RetryInterval, options.Time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task ServePipeAsync(string name, CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            // A failure to create the pipe (another program holds the name) goes to the caller, which retries later.
            var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, PipeAccess());
            first = false;
            Problem = null;
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The client went away before the connection was set up: wait for the next one.
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _ = Task.Run(async () =>
            {
                await using (pipe.ConfigureAwait(false))
                    await AnswerAsync(pipe, () => IsAdministrator(pipe), ct).ConfigureAwait(false);
            }, CancellationToken.None);
        }
    }

    private async Task ServeSocketAsync(string path, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        if (File.Exists(path))
            File.Delete(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        listener.Listen(16);
        Problem = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset or SocketError.Interrupted)
                {
                    continue; // the client went away while it was accepted
                }

                _ = Task.Run(async () =>
                {
                    using var stream = new NetworkStream(socket, ownsSocket: true);
                    await AnswerAsync(stream, () => IsPeerAdministrator(socket), ct).ConfigureAwait(false);
                }, CancellationToken.None);
            }
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task AnswerAsync(Stream stream, Func<bool> isAdmin, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var request = await AdminLines.ReadAsync<AdminRequest>(stream, timeout.Token).ConfigureAwait(false);
            if (request is null)
                return;
            var admin = !AdminCommands.IsOpen(request.Command) && isAdmin();
            AdminResponse response;
            try
            {
                response = handler.Handle(request, admin);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                response = AdminResponse.Fail("The site node cannot save its settings: " + e.Message);
            }

            if (response.Ok && !AdminCommands.IsOpen(request.Command))
                LogCommand(logger, request.Command);
            await AdminLines.WriteAsync(stream, response, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static PipeSecurity PipeAccess()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var self = WindowsIdentity.GetCurrent();
        if (self.User is { } user)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    // SYSTEM (the installer) or a member of Administrators with an elevated token, read from the caller's own token. Not
    // the service's account: with LocalService any other LocalService process would have counted. A request from this
    // process itself counts too (by process id, never by account).
    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator(NamedPipeServerStream pipe)
    {
        var admin = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var caller = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                admin = caller.IsSystem || new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator);
            });
            if (!admin && NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) && processId == (uint)Environment.ProcessId)
                admin = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            admin = false;
        }

        return admin;
    }

    private static bool IsPeerAdministrator(Socket socket)
    {
        if (!OperatingSystem.IsLinux())
            return false;
        try
        {
            // struct ucred { pid_t pid; uid_t uid; gid_t gid; } of SO_PEERCRED (SOL_SOCKET = 1, SO_PEERCRED = 17).
            var credentials = new byte[12];
            socket.GetRawSocketOption(1, 17, credentials);
            var uid = BitConverter.ToUInt32(credentials, 4);
            return uid == 0 || uid == NativeMethods.geteuid();
        }
        catch (Exception e) when (e is SocketException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The local channel {Name} cannot be opened: {Reason}")]
    private static partial void LogCannotOpen(ILogger logger, string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Changed in the app or on the command line: {Command}")]
    private static partial void LogCommand(ILogger logger, string command);

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "geteuid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "One call without marshalling.")]
        internal static extern uint geteuid();

        [DllImport("kernel32", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [SuppressMessage("Interoperability", "SYSLIB1054", Justification = "One call without marshalling.")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
    }
}
