using System.Diagnostics;
using System.Text;

using Grpc.Net.Client;

namespace Oadm.Contracts.Updates;

/// <summary>Runs an installer and waits for it (tests use a fake: never a real installer).</summary>
public interface IInstallerRunner
{
    /// <summary>The exit code and what the installer printed.</summary>
    Task<(int ExitCode, string Output)> RunAsync(InstallerCommand command, CancellationToken ct);
}

/// <summary>Starts a program without waiting for it (the helper, the new client).</summary>
public interface IProcessLauncher
{
    void Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>The clients running on this computer (the server's helper waits for them to close, step 3).</summary>
public interface IClientProcesses
{
    int Count();
}

/// <summary>What the OADM server on an address answers with <c>AuthService.Status</c>.</summary>
/// <param name="Version">"4.8.0".</param>
/// <param name="UpdatingTo">While its update's helper runs: the version it updates to; null otherwise.</param>
public sealed record ServerAnswer(string Version, string? UpdatingTo);

/// <summary>The version the OADM server on an address answers with <c>AuthService.Status</c>; null while it does not answer.</summary>
public interface IServerVersionProbe
{
    Task<string?> VersionAsync(Uri server, CancellationToken ct);

    /// <summary>The whole answer (version and the update it runs); null while the server does not answer.</summary>
    async Task<ServerAnswer?> AnswerAsync(Uri server, CancellationToken ct) =>
        await VersionAsync(server, ct).ConfigureAwait(false) is { } version ? new ServerAnswer(version, null) : null;
}

/// <summary>The real installer run: no shell, output captured, at most 30 minutes.</summary>
public sealed class ProcessInstallerRunner : IInstallerRunner
{
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(30);

    public async Task<(int ExitCode, string Output)> RunAsync(InstallerCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var info = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in command.Environment)
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException(command.FileName + " could not be started.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);
        var output = new StringBuilder();
        var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
        var stderr = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            output.Append(await stdout.ConfigureAwait(false)).Append(await stderr.ConfigureAwait(false));
            return (process.ExitCode, output.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The limit: the installer and everything it started end, so the helper's result is final.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Ended meanwhile.
            }

            return (-1, UpdateTexts.InstallerDidNotFinish);
        }
    }
}

/// <summary>Starts a detached program (no window, nothing redirected).</summary>
public sealed class SystemProcessLauncher : IProcessLauncher
{
    public void Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException(fileName + " could not be started.");
    }
}

/// <summary>
/// The installed clients running on this computer: processes named Oadm.Client whose program lies in the installed client
/// folder (a client's own update helper, a copy in its data folder, is not one: it waits for the server).
/// </summary>
public sealed class SystemClientProcesses(string clientFolder) : IClientProcesses
{
    /// <summary>The installed client folder of this computer (<see cref="Installation"/>).</summary>
    public static string InstalledClientFolder(UpdateOs os) => os switch
    {
        UpdateOs.Windows => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolderOption.DoNotVerify), "OADM", "Client"),
        UpdateOs.MacOs => Installation.MacClientFolder,
        _ => Installation.LinuxClientFolder,
    };

    public int Count()
    {
        var count = 0;
        foreach (var process in Process.GetProcessesByName("Oadm.Client"))
        {
            using (process)
            {
                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    path = null; // not readable: counted, the wait is only 30 s
                }

                if (path is null || IsIn(path, clientFolder))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary><paramref name="executable"/> lies directly in <paramref name="folder"/>.</summary>
    public static bool IsIn(string executable, string folder)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(folder);
        static string Clean(string p) => p.Replace('\\', '/').TrimEnd('/');
        var directory = Clean(executable);
        var at = directory.LastIndexOf('/');
        return at > 0 && string.Equals(directory[..at], Clean(folder), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// <c>AuthService.Status</c> of a server (open, no token): only its version is read and nothing is sent. The server's helper
/// pins the server's own certificate (its data folder); the client's helper accepts any.
/// </summary>
/// <param name="pinnedFingerprint">The server's certificate ("3F:A2:..."): only that one is accepted. Null accepts any (the
/// client's helper, which only reads the version).</param>
public sealed class GrpcServerVersionProbe(string? pinnedFingerprint = null) : IServerVersionProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>The pinned certificate only; any without a pin.</summary>
    public bool Accepts(System.Security.Cryptography.X509Certificates.X509Certificate? certificate) =>
        pinnedFingerprint is null
        || (certificate is not null && string.Equals(Security.ServerCertificatePinning.Fingerprint(certificate), pinnedFingerprint, StringComparison.OrdinalIgnoreCase));

    public async Task<string?> VersionAsync(Uri server, CancellationToken ct) => (await AnswerAsync(server, ct).ConfigureAwait(false))?.Version;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The update helper asks the OADM server for its version, not a device.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5359:Do not disable certificate validation", Justification = "Only the open Status call; nothing is sent and only the version is read.")]
    public async Task<ServerAnswer?> AnswerAsync(Uri server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = Timeout,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, cert, _, _) => Accepts(cert) },
        };
        using var channel = GrpcChannel.ForAddress(server, new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
        var auth = new V1.AuthService.AuthServiceClient(channel);
        try
        {
            var status = await auth.StatusAsync(new V1.Empty(), deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct).ConfigureAwait(false);
            return new ServerAnswer(status.Version, string.IsNullOrEmpty(status.UpdatingTo) ? null : status.UpdatingTo);
        }
        catch (Grpc.Core.RpcException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}

/// <summary>The copy of the running program that becomes the helper (rule 3: the installer can replace the original).</summary>
public static class HelperCopy
{
    /// <summary>Copies the running executable into a new folder below <paramref name="parent"/> and returns the copy's path.</summary>
    public static string Make(string parent, string? executable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        executable ??= Environment.ProcessPath ?? throw new InvalidOperationException("The program has no path.");
        var folder = Path.Combine(parent, "helper-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(executable));
        File.Copy(executable, copy, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return copy;
    }

    /// <summary>Removes the helper folders of earlier updates (best effort: a running helper keeps its own).</summary>
    public static void CleanUp(string parent)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(parent, "helper-*"))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use: the next update removes it.
            }
        }
    }
}
