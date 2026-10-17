using System.IO.Compression;
using System.Security.Cryptography;

namespace Oadm.Tools;

/// <summary>
/// The portable OADM server and client of a release (<c>OADM-Server-&lt;version&gt;-&lt;rid&gt;.zip</c>,
/// <c>OADM-Client-...</c>, the publish folders), downloaded once from the GitHub release and checked against its
/// SHA256SUMS.txt, or taken from a ZIP or folder the developer names (<c>--from</c>). Cached per version in
/// <c>&lt;LocalApplicationData&gt;/Oadm/dev-cache/&lt;version&gt;/&lt;server|client&gt;</c>.
/// </summary>
internal sealed class PortableOadm(string cacheRoot, HttpClient http, TextWriter output)
{
    public const string ReleaseBase = "https://github.com/Cacsjep/oadm/releases/download";

    public static string DefaultCache =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "Oadm", "dev-cache");

    /// <summary>The ZIP name of an app ("Server" or "Client") in a release.</summary>
    public static string ZipName(string app, string version, string rid) => $"OADM-{app}-{version}-{rid}.zip";

    /// <summary>
    /// The folder of the app's executable: <paramref name="from"/> (a publish folder, a folder with the exe one level
    /// down, or a ZIP), else the cached or downloaded release <paramref name="version"/>.
    /// </summary>
    public async Task<string> ResolveAsync(string app, string version, string? from, CancellationToken ct)
    {
        var exe = Cli.Exe("Oadm." + app);
        if (from is not null)
        {
            var full = Path.GetFullPath(from);
            if (Directory.Exists(full))
            {
                return FindExe(full, exe) ?? throw new ToolException($"{exe} was not found in {full}.");
            }

            if (File.Exists(full))
            {
                return Extract(full, Path.Combine(cacheRoot, "from-file", Path.GetFileNameWithoutExtension(full)), exe);
            }

            throw new ToolException($"{full} does not exist.");
        }

        var target = Path.Combine(cacheRoot, version, app.ToLowerInvariant());
        if (File.Exists(Path.Combine(target, ".complete")) && FindExe(target, exe) is { } cached)
        {
            return cached;
        }

        if (version.Contains("-dev", StringComparison.Ordinal) || version == "0.0.0")
        {
            throw new ToolException($"This oadm tool ({version}) belongs to no release. Name a portable OADM with --from <folder or ZIP>, e.g. --from artifacts/publish/{app.ToLowerInvariant()}/{Cli.Rid}.");
        }

        var name = ZipName(app, version, Cli.Rid);
        var zip = Path.Combine(cacheRoot, version, name);
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        output.WriteLine($"Downloading {name} (once for this version)...");
        await DownloadAsync($"{ReleaseBase}/v{version}/{name}", zip, ct).ConfigureAwait(false);
        var sums = await http.GetStringAsync(new Uri($"{ReleaseBase}/v{version}/SHA256SUMS.txt"), ct).ConfigureAwait(false);
        Verify(zip, name, sums);
        var folder = Extract(zip, target, exe);
        File.Delete(zip);
        return folder;
    }

    /// <summary>Throws unless the file's SHA-256 is the one SHA256SUMS.txt lists for <paramref name="name"/>.</summary>
    public static void Verify(string file, string name, string sums)
    {
        var expected = sums.Split('\n')
            .Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*', ' ') == name)?[0];
        if (expected is null)
        {
            throw new ToolException($"The release's SHA256SUMS.txt does not list {name}.");
        }

        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException($"{name} does not match the release's checksum. Nothing was installed; try again.");
        }
    }

    private async Task DownloadAsync(string url, string file, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ToolException($"{url} answered {(int)response.StatusCode}. Is this a released version? Else name a portable OADM with --from.");
        }

        var partial = file + ".part";
        await using (var target = File.Create(partial))
        {
            await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        File.Move(partial, file, overwrite: true);
    }

    /// <summary>Extracts a ZIP into <paramref name="target"/> (replacing it) and returns the folder of <paramref name="exe"/>.</summary>
    private static string Extract(string zip, string target, string exe)
    {
        var staging = target + ".extracting";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        ZipFile.ExtractToDirectory(zip, staging); // refuses entries outside the folder
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.Move(staging, target);
        var folder = FindExe(target, exe) ?? throw new ToolException($"{exe} was not found in {zip}.");
        if (!OperatingSystem.IsWindows())
        {
            var path = Path.Combine(folder, exe);
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        File.WriteAllText(Path.Combine(target, ".complete"), DateTime.UtcNow.ToString("O"));
        return folder;
    }

    /// <summary>The folder holding <paramref name="exe"/>: the folder itself or one folder below (the ZIP's top folder).</summary>
    public static string? FindExe(string folder, string exe)
    {
        if (File.Exists(Path.Combine(folder, exe)))
        {
            return folder;
        }

        return Directory.Exists(folder)
            ? Directory.GetDirectories(folder).FirstOrDefault(d => File.Exists(Path.Combine(d, exe)))
            : null;
    }
}
