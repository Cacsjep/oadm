using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.Pki.TrustStore;

/// <summary>Result of an install or remove.</summary>
/// <param name="Installed">The CA is in the machine root store afterwards.</param>
/// <param name="Error">Plain text of the failure; null on success.</param>
public sealed record TrustStoreResult(bool Installed, string? Error = null);

/// <summary>
/// The machine's trusted root store ("Install in trusted root store"): one implementation per OS, chosen at runtime
/// (<see cref="TrustStoreInstallers"/>). Firefox and Java keep their own stores and are not handled.
/// </summary>
public interface ITrustStoreInstaller
{
    /// <summary>Cheap check whether the CA is installed (store lookup, file exists, one tool call on macOS).</summary>
    Task<bool> IsInstalledAsync(X509Certificate2 ca, CancellationToken ct);

    /// <summary>Installs the CA; an installed CA is not installed again.</summary>
    Task<TrustStoreResult> InstallAsync(X509Certificate2 ca, CancellationToken ct);

    Task<TrustStoreResult> RemoveAsync(X509Certificate2 ca, CancellationToken ct);
}

/// <summary>Texts shared by the installers.</summary>
public static class TrustStoreTexts
{
    public const string PermissionDenied = "Permission denied: the server must run as administrator / root";
    public const string PermissionDeniedClient = "Permission denied: this computer's administrator must allow the installation";
    public const string NoLinuxTool = "No supported certificate tool found (update-ca-certificates or update-ca-trust)";
    public const string Cancelled = "The installation was cancelled.";
    public const string Unsupported = "Installing into the trusted root store is not supported on this system.";

    /// <summary>"&lt;tool&gt; failed: &lt;first line&gt;".</summary>
    public static string ToolFailed(string tool, ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"{tool} failed: {result.FirstLine}";
    }

    /// <summary>"oadm-&lt;first 16 hex characters of the SHA-256 fingerprint&gt;.crt".</summary>
    public static string FileName(X509Certificate2 ca)
    {
        ArgumentNullException.ThrowIfNull(ca);
        return "oadm-" + CaCertificates.Fingerprint(ca)[..16].ToLowerInvariant() + ".crt";
    }
}

/// <summary>Creates the installer of the current OS.</summary>
public static class TrustStoreInstallers
{
    /// <summary>Timeout of every tool call.</summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The server's installer: it runs elevated (administrator / root), no prompts.</summary>
    public static ITrustStoreInstaller ForServer(ILogger? logger = null) =>
        Create(HostOsInfo.Current, ProcessRunner.Instance, promptForElevation: false, logger: logger);

    /// <summary>
    /// The client's installer (<c>ClientTrustStoreInstaller</c>): asks the OS for elevation when the client is not elevated
    /// (Windows UAC via certutil, macOS administrator prompt via osascript, Linux pkexec).
    /// </summary>
    public static ITrustStoreInstaller ForClient(ILogger? logger = null) =>
        Create(HostOsInfo.Current, ProcessRunner.Instance, promptForElevation: true, logger: logger);

    /// <summary>Builds the installer of <paramref name="os"/> over the given abstractions (tests pass fakes).</summary>
    /// <param name="os">Target OS.</param>
    /// <param name="runner">Runs the OS tools.</param>
    /// <param name="promptForElevation">Client: ask the OS for elevation when not elevated.</param>
    /// <param name="windowsStore">Windows machine root store; default the real LocalMachine Root store.</param>
    /// <param name="isElevated">Whether this process is administrator / root; default <see cref="Environment.IsPrivilegedProcess"/>.</param>
    /// <param name="root">File system root for the Linux paths ("/"); tests use a temporary folder.</param>
    /// <param name="tempDirectory">Where certificate files for the tools are written; default the temp folder.</param>
    /// <param name="logger">Tool output goes here.</param>
    public static ITrustStoreInstaller Create(
        HostOs os,
        IProcessRunner runner,
        bool promptForElevation,
        IMachineRootStore? windowsStore = null,
        Func<bool>? isElevated = null,
        string root = "/",
        string? tempDirectory = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var elevated = isElevated ?? (() => Environment.IsPrivilegedProcess);
        var temp = tempDirectory ?? Path.GetTempPath();
        var log = logger ?? NullLogger.Instance;
        return os switch
        {
            HostOs.Windows => new WindowsTrustStore(windowsStore ?? new WindowsMachineRootStore(), runner, promptForElevation, elevated, temp, log),
            HostOs.Linux => new LinuxTrustStore(runner, promptForElevation, elevated, root, temp, log),
            HostOs.MacOs => new MacTrustStore(runner, promptForElevation, elevated, temp, log),
            _ => new UnsupportedTrustStore(),
        };
    }

    internal static string WriteTempFile(string directory, X509Certificate2 ca, bool der)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, TrustStoreTexts.FileName(ca));
        if (der)
        {
            File.WriteAllBytes(path, ca.RawData);
        }
        else
        {
            File.WriteAllText(path, CaCertificates.ToPem(ca));
        }

        return path;
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // temporary file only
        }
    }

    internal static void LogTool(ILogger logger, string tool, ProcessResult result) =>
        TrustStoreLog.ToolExited(logger, tool, result.ExitCode, result.Output);
}

internal static partial class TrustStoreLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Trust store: {Tool} exited with {ExitCode}: {Output}")]
    public static partial void ToolExited(ILogger logger, string tool, int exitCode, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trust store: writing {Target} failed")]
    public static partial void WriteFailed(ILogger logger, Exception ex, string target);
}

/// <summary>Access to the Windows LocalMachine Root store (a fake in tests: tests never touch the real store).</summary>
public interface IMachineRootStore
{
    bool Contains(string sha1Thumbprint);

    void Add(X509Certificate2 certificate);

    void Remove(string sha1Thumbprint);
}

/// <summary>The real <c>X509Store(StoreName.Root, StoreLocation.LocalMachine)</c>.</summary>
public sealed class WindowsMachineRootStore : IMachineRootStore
{
    public bool Contains(string sha1Thumbprint)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, sha1Thumbprint, validOnly: false);
        var contains = found.Count > 0;
        foreach (var certificate in found)
        {
            certificate.Dispose();
        }

        return contains;
    }

    public void Add(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    public void Remove(string sha1Thumbprint)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, sha1Thumbprint, validOnly: false))
        {
            store.Remove(certificate);
            certificate.Dispose();
        }
    }
}

/// <summary>Windows: LocalMachine Root store; the client without administrator rights runs <c>certutil -addstore Root</c> with runas.</summary>
internal sealed class WindowsTrustStore(IMachineRootStore store, IProcessRunner runner, bool prompt, Func<bool> isElevated, string temp, ILogger logger) : ITrustStoreInstaller
{
    public Task<bool> IsInstalledAsync(X509Certificate2 ca, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ca);
        try
        {
            return Task.FromResult(store.Contains(ca.Thumbprint));
        }
        catch (CryptographicException)
        {
            return Task.FromResult(false);
        }
    }

    public async Task<TrustStoreResult> InstallAsync(X509Certificate2 ca, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ca);
        if (await IsInstalledAsync(ca, ct).ConfigureAwait(false))
        {
            return new TrustStoreResult(true);
        }

        if (!prompt || isElevated())
        {
            try
            {
                using var copy = X509CertificateLoader.LoadCertificate(ca.RawData); // public part only
                store.Add(copy);
                return new TrustStoreResult(true);
            }
            catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException)
            {
                TrustStoreLog.WriteFailed(logger, ex, "the LocalMachine Root store");
                return new TrustStoreResult(false, prompt ? TrustStoreTexts.PermissionDeniedClient : TrustStoreTexts.PermissionDenied);
            }
        }

        var file = TrustStoreInstallers.WriteTempFile(temp, ca, der: true);
        try
        {
            var result = await runner.RunElevatedAsync("certutil.exe", ["-addstore", "Root", file], TrustStoreInstallers.ToolTimeout, ct).ConfigureAwait(false);
            TrustStoreInstallers.LogTool(logger, "certutil", result);
            if (result.Cancelled)
            {
                return new TrustStoreResult(false, TrustStoreTexts.Cancelled);
            }

            if (!result.Succeeded)
            {
                return new TrustStoreResult(false, TrustStoreTexts.ToolFailed("certutil", result));
            }

            return new TrustStoreResult(await IsInstalledAsync(ca, ct).ConfigureAwait(false), null);
        }
        finally
        {
            TrustStoreInstallers.TryDelete(file);
        }
    }

    public async Task<TrustStoreResult> RemoveAsync(X509Certificate2 ca, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ca);
        try
        {
            store.Remove(ca.Thumbprint);
            return new TrustStoreResult(await IsInstalledAsync(ca, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException)
        {
            return new TrustStoreResult(true, TrustStoreTexts.PermissionDenied);
        }
    }
}

/// <summary>The Linux distribution families with a system CA folder and update tool.</summary>
public enum LinuxTrustFlavor
{
    /// <summary>Debian, Ubuntu: /usr/local/share/ca-certificates + update-ca-certificates.</summary>
    Debian = 0,

    /// <summary>RHEL, Fedora, CentOS: /etc/pki/ca-trust/source/anchors + update-ca-trust extract.</summary>
    RedHat = 1,

    /// <summary>SUSE: /etc/pki/trust/anchors + update-ca-certificates.</summary>
    Suse = 2,
}

/// <summary>Linux: a .crt file in the distribution's anchor folder plus its update tool; the client without root uses pkexec.</summary>
internal sealed class LinuxTrustStore(IProcessRunner runner, bool prompt, Func<bool> isRoot, string root, string temp, ILogger logger) : ITrustStoreInstaller
{
    private static readonly (LinuxTrustFlavor Flavor, string Folder, string Tool, string[] Args)[] Flavors =
    [
        (LinuxTrustFlavor.Suse, "etc/pki/trust/anchors", "update-ca-certificates", []),
        (LinuxTrustFlavor.RedHat, "etc/pki/ca-trust/source/anchors", "update-ca-trust", ["extract"]),
        (LinuxTrustFlavor.Debian, "usr/local/share/ca-certificates", "update-ca-certificates", []),
    ];

    public Task<bool> IsInstalledAsync(X509Certificate2 ca, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ca);
        var name = TrustStoreTexts.FileName(ca);
        return Task.FromResult(Flavors.Any(f => File.Exists(Path.Combine(root, f.Folder, name))));
    }

    public async Task<TrustStoreResult> InstallAsync(X509Certificate2 ca, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ca);
        if (await IsInstalledAsync(ca, ct).ConfigureAwait(false))
        {
            return new TrustStoreResult(true);
        }

        if (Detect() is not { } flavor)
        {
            return new TrustStoreResult(false, TrustStoreTexts.NoLinuxTool);
        }

        var target = Path.Combine(root, flavor.Folder, TrustStoreTexts.FileName(ca));
        if (!prompt || isRoot())
        {
            try
            {
                File.WriteAllText(target, CaCertificates.ToPem(ca));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                TrustStoreLog.WriteFailed(logger, ex, target);
                return new TrustStoreResult(false, prompt ? TrustStoreTexts.PermissionDeniedClient : TrustStoreTexts.PermissionDenied);
            }

            var result = await runner.RunAsync(flavor.ToolPath, flavor.Args, TrustStoreInstallers.ToolTimeout, ct).ConfigureAwait(false);
            TrustStoreInstallers.LogTool(logger, flavor.Tool, result);
            return result.Succeeded
                ? new TrustStoreResult(true)
                : new TrustStoreResult(File.Exists(target), TrustStoreTexts.ToolFailed(flavor.Tool, result));
        }

        // Client without root: one pkexec prompt copies the file and runs the update tool.
        var file = TrustStoreInstallers.WriteTempFile(temp, ca, der: false);
        var script = Script(file, target, flavor);
        var pkexec = runner.FindExecutable("pkexec");
        if (pkexec is null)
        {
            // The file stays in the temp folder so the user can run the command.
            return new TrustStoreResult(false, $"Run this command as root: sudo sh -c \"{script}\"");
        }

        try
        {
            var result = await runner.RunAsync(pkexec, ["/bin/sh", "-c", script], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            TrustStoreInstallers.LogTool(logger, "pkexec", result);
            if (result.ExitCode is 126 or 127 && !result.TimedOut)
            {
                return new TrustStoreResult(false, TrustStoreTexts.Cancelled); // dismissed or not authorized
            }

            return result.Succeeded
                ? new TrustStoreResult(true)
                : new TrustStoreResult(false, TrustStoreTexts.ToolFailed(flavor.Tool, result));
        }
        finally
        {
            TrustStoreInstallers.TryDelete(file);
        }
    }

    public async Task<TrustStoreResult> RemoveAsync(X509Certificate2 ca, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ca);
        var name = TrustStoreTexts.FileName(ca);
        foreach (var (_, folder, tool, args) in Flavors)
        {
            var path = Path.Combine(root, folder, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return new TrustStoreResult(true, TrustStoreTexts.PermissionDenied);
            }

            if (runner.FindExecutable(tool) is { } toolPath)
            {
                var result = await runner.RunAsync(toolPath, args, TrustStoreInstallers.ToolTimeout, ct).ConfigureAwait(false);
                TrustStoreInstallers.LogTool(logger, tool, result);
                if (!result.Succeeded)
                {
                    return new TrustStoreResult(false, TrustStoreTexts.ToolFailed(tool, result));
                }
            }
        }

        return new TrustStoreResult(false);
    }

    /// <summary>The first family whose anchor folder exists and whose tool is installed.</summary>
    private Detected? Detect()
    {
        foreach (var (flavor, folder, tool, args) in Flavors)
        {
            if (Directory.Exists(Path.Combine(root, folder)) && runner.FindExecutable(tool) is { } path)
            {
                return new Detected(flavor, folder, tool, path, args);
            }
        }

        return null;
    }

    private static string Script(string file, string target, Detected flavor) =>
        string.Create(CultureInfo.InvariantCulture, $"install -m 0644 '{file}' '{target}' && {flavor.ToolPath}{string.Concat(flavor.Args.Select(a => " " + a))}");

    private sealed record Detected(LinuxTrustFlavor Flavor, string Folder, string Tool, string ToolPath, string[] Args);
}

/// <summary>macOS: the System keychain with <c>security</c>; the client without root asks through osascript.</summary>
internal sealed class MacTrustStore(IProcessRunner runner, bool prompt, Func<bool> isRoot, string temp, ILogger logger) : ITrustStoreInstaller
{
    public const string Security = "/usr/bin/security";
    public const string Keychain = "/Library/Keychains/System.keychain";

    public async Task<bool> IsInstalledAsync(X509Certificate2 ca, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ca);
        var result = await runner.RunAsync(Security, ["find-certificate", "-Z", "-a", Keychain], TrustStoreInstallers.ToolTimeout, ct).ConfigureAwait(false);
        return result.Succeeded && result.Output.Contains(CaCertificates.Fingerprint(ca), StringComparison.OrdinalIgnoreCase);
    }

    public async Task<TrustStoreResult> InstallAsync(X509Certificate2 ca, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ca);
        if (await IsInstalledAsync(ca, ct).ConfigureAwait(false))
        {
            return new TrustStoreResult(true);
        }

        var file = TrustStoreInstallers.WriteTempFile(temp, ca, der: false);
        try
        {
            string[] add = ["add-trusted-cert", "-d", "-r", "trustRoot", "-k", Keychain, file];
            ProcessResult result;
            if (!prompt || isRoot())
            {
                result = await runner.RunAsync(Security, add, TrustStoreInstallers.ToolTimeout, ct).ConfigureAwait(false);
            }
            else
            {
                var command = Security + " " + string.Join(' ', add.Select(a => "'" + a + "'"));
                var script = $"do shell script \"{command}\" with administrator privileges";
                result = await runner.RunAsync("/usr/bin/osascript", ["-e", script], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                if (!result.Succeeded && result.Output.Contains("-128", StringComparison.Ordinal))
                {
                    return new TrustStoreResult(false, TrustStoreTexts.Cancelled); // "User canceled. (-128)"
                }
            }

            TrustStoreInstallers.LogTool(logger, "security", result);
            if (result.Succeeded)
            {
                return new TrustStoreResult(true);
            }

            return new TrustStoreResult(false, IsPermission(result) ? TrustStoreTexts.PermissionDenied : TrustStoreTexts.ToolFailed("security", result));
        }
        finally
        {
            TrustStoreInstallers.TryDelete(file);
        }
    }

    public async Task<TrustStoreResult> RemoveAsync(X509Certificate2 ca, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ca);
        var result = await runner.RunAsync(Security, ["delete-certificate", "-Z", ca.Thumbprint, Keychain], TrustStoreInstallers.ToolTimeout, ct).ConfigureAwait(false);
        TrustStoreInstallers.LogTool(logger, "security", result);
        if (result.Succeeded)
        {
            return new TrustStoreResult(false);
        }

        return new TrustStoreResult(true, IsPermission(result) ? TrustStoreTexts.PermissionDenied : TrustStoreTexts.ToolFailed("security", result));
    }

    private static bool IsPermission(ProcessResult result) =>
        result.Output.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || result.Output.Contains("permission", StringComparison.OrdinalIgnoreCase)
        || result.Output.Contains("not permitted", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Other systems: nothing can be installed.</summary>
internal sealed class UnsupportedTrustStore : ITrustStoreInstaller
{
    public Task<bool> IsInstalledAsync(X509Certificate2 ca, CancellationToken ct) => Task.FromResult(false);

    public Task<TrustStoreResult> InstallAsync(X509Certificate2 ca, CancellationToken ct) => Task.FromResult(new TrustStoreResult(false, TrustStoreTexts.Unsupported));

    public Task<TrustStoreResult> RemoveAsync(X509Certificate2 ca, CancellationToken ct) => Task.FromResult(new TrustStoreResult(false, TrustStoreTexts.Unsupported));
}
