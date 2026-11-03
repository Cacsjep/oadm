using System.IO.Compression;
using System.Security.Cryptography;

using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;

namespace Oadm.Server.AddOns;

/// <summary>
/// The add-on folders under <c>&lt;datafolder&gt;/addons/</c> (docs/spec/add-ons.md "Server"): <c>&lt;id&gt;/</c> per
/// add-on, <c>.download/</c> for the release files, <c>.new-*</c> and <c>.old-*</c> while one is swapped. Folders starting
/// with a dot are never add-ons and are emptied at every start.
/// </summary>
public static class AddOnFolders
{
    /// <summary>The largest add-on unpacked (models and native libraries of one platform).</summary>
    public const long MaxUnpackedBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The most files one add-on ZIP may hold.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>The largest client part sent in one answer (the client takes 32 MB).</summary>
    public const long MaxClientPartBytes = 24L * 1024 * 1024;

    /// <summary>A client part's own folder in the add-on folder: everything below it goes to the clients.</summary>
    public const string ClientFolder = Contracts.AddOnLayout.ClientFolder;

    public const string DownloadFolderName = ".download";

    /// <summary>
    /// The file "manage package addons" writes at the top of every add-on folder: the OADM release it belongs to ("4.11.0",
    /// one line). A folder put in by hand is recorded with it.
    /// </summary>
    public const string VersionFileName = "oadm-version.txt";

    /// <summary>The release in <see cref="VersionFileName"/> of an add-on folder; null when the file is missing or empty.</summary>
    public static string? ReadVersion(string folder)
    {
        try
        {
            var path = Path.Combine(folder, VersionFileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 256)
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A downloaded add-on whose <see cref="VersionFileName"/> names another release than <paramref name="release"/>:
    /// <see cref="AddOnException"/> with <see cref="AddOnTexts.WrongPackage"/>. A folder without the file passes.
    /// </summary>
    public static void CheckVersion(string folder, string release)
    {
        if (ReadVersion(folder) is { } version && !string.Equals(version, release, StringComparison.Ordinal))
        {
            throw new AddOnException(AddOnTexts.WrongPackage);
        }
    }

    /// <summary><c>&lt;datafolder&gt;/addons</c>.</summary>
    public static string Root(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(dataDirectory, "addons");
    }

    /// <summary>
    /// Unpacks <paramref name="zipPath"/> into the new folder <paramref name="target"/>. A ZIP whose files sit in one top
    /// folder is unpacked from that folder. A broken ZIP, a name with ':' (an alternate data stream on Windows) or a file twice,
    /// a path outside the folder or more than the limits: <see cref="AddOnException"/> with <see cref="AddOnTexts.Damaged"/>,
    /// the folder deleted.
    /// </summary>
    public static async Task UnpackAsync(string zipPath, string target, CancellationToken ct)
    {
        var full = Path.GetFullPath(target);
        var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        try
        {
            Directory.CreateDirectory(full);
            var zip = await ZipFile.OpenReadAsync(zipPath, ct).ConfigureAwait(false);
            await using (zip.ConfigureAwait(false))
            {
                if (zip.Entries.Count > MaxEntries)
                {
                    throw new AddOnException(AddOnTexts.Damaged);
                }

                var top = CommonTopFolder(zip.Entries);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                var buffer = new byte[81920];
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    if (top is not null)
                    {
                        name = name[top.Length..];
                    }

                    if (name.Length == 0)
                    {
                        continue;
                    }

                    if (name.Contains(':', StringComparison.Ordinal) || !seen.Add(name.TrimEnd('/')))
                    {
                        throw new AddOnException(AddOnTexts.Damaged);
                    }

                    var path = Path.GetFullPath(Path.Combine(full, name));
                    if (!path.StartsWith(prefix, StringComparison.Ordinal) && path != full)
                    {
                        throw new AddOnException(AddOnTexts.Damaged);
                    }

                    if (name.EndsWith('/'))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var source = await entry.OpenAsync(ct).ConfigureAwait(false);
                    await using (source.ConfigureAwait(false))
                    {
                        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                        await using (file.ConfigureAwait(false))
                        {
                            int read;
                            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                            {
                                total += read;
                                if (total > MaxUnpackedBytes)
                                {
                                    throw new AddOnException(AddOnTexts.Damaged);
                                }

                                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or IOException
                                       or UnauthorizedAccessException or AddOnException or OperationCanceledException)
        {
            TryDelete(full);
            if (ex is AddOnException or OperationCanceledException)
            {
                throw;
            }

            // A name the file system refuses (ArgumentException, NotSupportedException) is a damaged ZIP, like a broken one.
            throw new AddOnException(ex is InvalidDataException or ArgumentException or NotSupportedException ? AddOnTexts.Damaged : AddOnTexts.CouldNotInstall, ex);
        }
    }

    /// <summary>The one top folder every entry sits in ("ocr/"), or null when files sit at the ZIP's root.</summary>
    private static string? CommonTopFolder(IReadOnlyCollection<ZipArchiveEntry> entries)
    {
        if (entries.Any(e => e.FullName.Replace('\\', '/') == PluginLoader.ManifestFileName))
        {
            return null;
        }

        string? top = null;
        foreach (var entry in entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var slash = name.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
            {
                return null;
            }

            var first = name[..(slash + 1)];
            if (top is null)
            {
                top = first;
            }
            else if (!string.Equals(top, first, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return top;
    }

    /// <summary>
    /// Checks the unpacked add-on's plugin.json: the expected id and a <c>minSdkVersion</c> this server meets. Otherwise
    /// <see cref="AddOnException"/> (<see cref="AddOnTexts.WrongPackage"/>, <see cref="AddOnTexts.NeedsNewerServer"/>).
    /// </summary>
    public static PluginManifest CheckManifest(string folder, string expectedId)
    {
        PluginManifest manifest;
        try
        {
            manifest = PluginLoader.ReadManifest(Path.Combine(folder, PluginLoader.ManifestFileName));
        }
        catch (PluginLoadException ex)
        {
            throw new AddOnException(AddOnTexts.WrongPackage, ex);
        }

        if (!string.Equals(manifest.Id, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AddOnException(AddOnTexts.WrongPackage);
        }

        if (!PluginLoader.IsSdkCompatible(manifest.MinSdkVersion))
        {
            throw new AddOnException(AddOnTexts.NeedsNewerServer);
        }

        return manifest;
    }

    /// <summary>
    /// Puts <paramref name="staged"/> in place of <paramref name="target"/> (a folder that is not loaded): the old one moves
    /// aside first and comes back when the move fails, then goes.
    /// </summary>
    public static void SwapIn(string staged, string target, string root)
    {
        string? old = null;
        if (Directory.Exists(target))
        {
            old = Path.Combine(root, $".old-{Path.GetFileName(target)}-{Guid.NewGuid():N}");
            Directory.Move(target, old);
        }

        try
        {
            Directory.Move(staged, target);
        }
        catch
        {
            if (old is not null)
            {
                Directory.Move(old, target);
            }

            throw;
        }

        if (old is not null)
        {
            TryDelete(old);
        }
    }

    /// <summary>
    /// The files next to plugin.json that go to the clients: the client part (<c>*.Client.dll</c>) and the managed server
    /// part it references (<c>*.Server.dll</c>: its contracts and texts), each with its symbols.
    /// </summary>
    public static readonly IReadOnlyList<string> ClientPartPatterns = ["*.Client.dll", "*.Client.pdb", "*.Server.dll", "*.Server.pdb"];

    /// <summary>
    /// The client part of an add-on folder: plugin.json, the files of <see cref="ClientPartPatterns"/> next to it and
    /// everything below <see cref="ClientFolder"/>, each with its SHA-256. Never native libraries, models or the server
    /// part's other dependencies.
    /// </summary>
    public static async Task<IReadOnlyList<AddOnClientFile>> ReadClientPartAsync(string folder, CancellationToken ct)
    {
        var paths = new List<string> { Path.Combine(folder, PluginLoader.ManifestFileName) };
        paths.AddRange(ClientPartPatterns
            .SelectMany(pattern => Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
        var client = Path.Combine(folder, ClientFolder);
        if (Directory.Exists(client))
        {
            paths.AddRange(Directory.GetFiles(client, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        }

        var files = new List<AddOnClientFile>(paths.Count);
        long total = 0;
        foreach (var path in paths)
        {
            var content = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            total += content.Length;
            if (total > MaxClientPartBytes)
            {
                throw new InvalidOperationException(AddOnTexts.ClientPartTooBig);
            }

            var relative = Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
            files.Add(new AddOnClientFile(relative, content, Convert.ToHexStringLower(SHA256.HashData(content))));
        }

        return files;
    }

    /// <summary>Deletes a folder; false when it (or a file in it) cannot go now.</summary>
    public static bool TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes the leftovers of an install that stopped (<c>.download</c>, <c>.new-*</c>, <c>.old-*</c>).</summary>
    public static void CleanTemporary(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var folder in Directory.GetDirectories(root))
        {
            if (Path.GetFileName(folder).StartsWith('.'))
            {
                TryDelete(folder);
            }
        }
    }
}
