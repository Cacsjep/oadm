namespace Oadm.Contracts.Updates;

/// <summary>What the detection of an installed program looks at (tests pass their own).</summary>
/// <param name="Os">The operating system.</param>
/// <param name="BaseDirectory">The folder of the running program (<see cref="AppContext.BaseDirectory"/>).</param>
/// <param name="ProgramFiles">The 64-bit Program Files folder (Windows).</param>
/// <param name="IsService">The server runs as the installed service (Windows service, systemd, launchd).</param>
/// <param name="FileExists">Whether a file exists.</param>
public sealed record InstallFacts(UpdateOs Os, string BaseDirectory, string ProgramFiles, bool IsService, Func<string, bool> FileExists)
{
    /// <summary>This process (<paramref name="isService"/>: the server's service detection; false for the client).</summary>
    public static InstallFacts Current(bool isService) => new(
        UpdatePlatform.Current.Os,
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolderOption.DoNotVerify),
        isService,
        File.Exists);
}

/// <summary>
/// The one place that decides whether this program runs from an installed package (docs/spec/self-update.md): only then
/// Install now and Update client are offered; a server or client run from a ZIP or with <c>dotnet run</c> keeps Open
/// download page. The folders are those of docs/spec/packaging.md "Installers".
/// </summary>
public static class Installation
{
    /// <summary>The Linux server package's file list (dpkg), with and without the architecture qualifier.</summary>
    public const string DpkgInfo = "/var/lib/dpkg/info";

    /// <summary>The LaunchDaemon of the macOS server (rule 10: without it the .pkg update leaves the server out).</summary>
    public const string MacServerDaemon = "/Library/LaunchDaemons/com.oadm.server.plist";

    public const string LinuxServerFolder = "/opt/oadm/server";
    public const string LinuxClientFolder = "/opt/oadm/client";
    public const string MacServerFolder = "/Library/Application Support/OADM/server";
    public const string MacClientFolder = "/Applications/OADM.app/Contents/MacOS";

    /// <summary>The server is the installed service of the MSI, the .deb or the .pkg.</summary>
    public static bool IsServerInstalled(InstallFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.IsService)
        {
            return false;
        }

        return facts.Os switch
        {
            UpdateOs.Windows => SameFolder(facts.Os, facts.BaseDirectory, Path.Combine(facts.ProgramFiles, "OADM", "Server")),
            UpdateOs.MacOs => SameFolder(facts.Os, facts.BaseDirectory, MacServerFolder) && facts.FileExists(MacServerDaemon),
            _ => SameFolder(facts.Os, facts.BaseDirectory, LinuxServerFolder) && HasDpkgList(facts, "oadm-server"),
        };
    }

    /// <summary>The client runs from the MSI's, the .deb's or the .pkg's folder.</summary>
    public static bool IsClientInstalled(InstallFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Os switch
        {
            UpdateOs.Windows => SameFolder(facts.Os, facts.BaseDirectory, Path.Combine(facts.ProgramFiles, "OADM", "Client")),
            UpdateOs.MacOs => SameFolder(facts.Os, facts.BaseDirectory, MacClientFolder),
            _ => SameFolder(facts.Os, facts.BaseDirectory, LinuxClientFolder) && HasDpkgList(facts, "oadm-client"),
        };
    }

    /// <summary>pkexec of polkit, the system's elevation prompt of the Linux client update.</summary>
    public static readonly IReadOnlyList<string> PkexecPaths = ["/usr/bin/pkexec", "/bin/pkexec"];

    /// <summary>
    /// Update client can run here: the client is installed, no server is installed on this computer
    /// (<see cref="HasLocalServer"/>) and, on Linux, pkexec exists (otherwise the client keeps Open download page).
    /// </summary>
    public static bool CanUpdateClient(InstallFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return IsClientInstalled(facts) && !HasLocalServer(facts) && (facts.Os != UpdateOs.Linux || PkexecPaths.Any(facts.FileExists));
    }

    /// <summary>
    /// An OADM server is installed on this computer, which the client's installer would update too (the MSI keeps
    /// "Server and client", the .pkg installs the server while its daemon exists). Update client is not offered then: the
    /// server's own Install now updates both with its safeguards. On Linux the server is a package of its own.
    /// </summary>
    public static bool HasLocalServer(InstallFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Os switch
        {
            UpdateOs.Windows => facts.FileExists(Path.Combine(facts.ProgramFiles, "OADM", "Server", "Oadm.Server.exe")),
            UpdateOs.MacOs => facts.FileExists(MacServerDaemon),
            _ => false,
        };
    }

    /// <summary>The macOS server is installed on this computer (the .pkg keeps "Server and client").</summary>
    public static bool HasMacServer(InstallFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.FileExists(MacServerDaemon);
    }

    private static bool HasDpkgList(InstallFacts facts, string package) =>
        facts.FileExists($"{DpkgInfo}/{package}.list")
        || facts.FileExists($"{DpkgInfo}/{package}:amd64.list")
        || facts.FileExists($"{DpkgInfo}/{package}:arm64.list");

    private static bool SameFolder(UpdateOs os, string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        static string Clean(string p) => p.Replace('\\', '/').TrimEnd('/');
        return string.Equals(Clean(a), Clean(b), os == UpdateOs.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }
}
