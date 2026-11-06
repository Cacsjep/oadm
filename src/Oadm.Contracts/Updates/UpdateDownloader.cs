using System.Security.Cryptography;

namespace Oadm.Contracts.Updates;

/// <summary>The free space of a folder's drive (tests pass their own).</summary>
public interface IDiskSpace
{
    /// <summary>Free bytes for this user on the drive of <paramref name="folder"/>; null when unknown.</summary>
    long? AvailableBytes(string folder);
}

/// <summary>The drive of the folder (Windows: its root; elsewhere the file system the folder lies on).</summary>
public sealed class SystemDiskSpace : IDiskSpace
{
    public long? AvailableBytes(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var full = Path.GetFullPath(folder);
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : full);
            return drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The steps of an update the dialog lists.</summary>
public enum UpdateStep
{
    Download,
    Check,
    Install,
}

/// <summary>
/// Downloads the installer of a release and checks it (rules 4 and 13): the exact asset name of the platform, at most
/// <see cref="MaxInstallerBytes"/>, free space of three times its size before anything is downloaded, the SHA-256 of the
/// release's <c>SHA256SUMS.txt</c>. Anything else is deleted and refused with <see cref="UpdateRefusedException"/>.
/// </summary>
public sealed class UpdateDownloader(IReleaseFeed feed, IDiskSpace disk)
{
    /// <summary>400 MB (rule 4).</summary>
    public const long MaxInstallerBytes = 400L * 1024 * 1024;

    /// <summary>The checksums file of every release.</summary>
    public const string ChecksumsName = "SHA256SUMS.txt";

    /// <summary>The checksums file is small; anything larger is not it.</summary>
    public const long MaxChecksumsBytes = 1024 * 1024;

    /// <summary>The data folder needs this many times the installer's size (download, database copy, installer cache).</summary>
    public const int SpaceFactor = 3;

    /// <summary>
    /// The installer <paramref name="name"/> of <paramref name="release"/> into <paramref name="folder"/>, checked. Reports
    /// the step and the download's percent. Returns the installer's path.
    /// </summary>
    /// <param name="extraBytes">More room the update needs in the folder (the server: its database, copied there first).</param>
    public async Task<string> DownloadAsync(ReleaseOffer release, string name, string folder, Action<UpdateStep, int>? progress, CancellationToken ct, long extraBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var asset = release.Find(name) ?? throw new UpdateRefusedException(UpdateTexts.NoInstaller);
        if (!string.Equals(Path.GetFileName(asset.Url.AbsolutePath), name, StringComparison.Ordinal) || !GitHubReleaseFeed.IsReleaseDownload(asset.Url))
        {
            throw new UpdateRefusedException(UpdateTexts.WrongName);
        }

        if (asset.Size > MaxInstallerBytes)
        {
            throw new UpdateRefusedException(UpdateTexts.TooBig);
        }

        Directory.CreateDirectory(folder);
        if (disk.AvailableBytes(folder) is { } free && free < (SpaceFactor * Math.Max(asset.Size, 1)) + Math.Max(extraBytes, 0))
        {
            throw new UpdateRefusedException(UpdateTexts.NotEnoughSpace);
        }

        progress?.Invoke(UpdateStep.Download, 0);
        var expected = await ExpectedHashAsync(release, name, ct).ConfigureAwait(false);
        var target = Path.Combine(folder, name);
        var part = target + ".part";
        File.Delete(part);
        try
        {
            byte[] hash;
            var file = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                var size = Math.Max(asset.Size, 1);
                var reported = 0; // 0 % went out above: each whole percent goes out once
                void Report(long bytes)
                {
                    var percent = (int)Math.Clamp(bytes * 100 / size, 0, 100);
                    if (percent != reported)
                    {
                        reported = percent;
                        progress?.Invoke(UpdateStep.Download, percent);
                    }
                }

                try
                {
                    await feed.DownloadAsync(asset.Url, file, Math.Min(MaxInstallerBytes, asset.Size > 0 ? asset.Size : MaxInstallerBytes), new SyncProgress<long>(Report), ct).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    throw new UpdateRefusedException(UpdateTexts.DownloadFailed);
                }
                catch (IOException)
                {
                    throw new UpdateRefusedException(UpdateTexts.DownloadFailed);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new UpdateRefusedException(UpdateTexts.DownloadFailed);
                }

                progress?.Invoke(UpdateStep.Check, 0);
                if (file.Length != asset.Size)
                {
                    throw new UpdateRefusedException(UpdateTexts.WrongChecksum);
                }

                file.Position = 0;
                hash = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
            }

            if (!CryptographicOperations.FixedTimeEquals(hash, expected))
            {
                throw new UpdateRefusedException(UpdateTexts.WrongChecksum);
            }

            File.Move(part, target, overwrite: true);
            return target;
        }
        catch
        {
            File.Delete(part);
            throw;
        }
    }

    private async Task<byte[]> ExpectedHashAsync(ReleaseOffer release, string name, CancellationToken ct)
    {
        var sums = release.Find(ChecksumsName) ?? throw new UpdateRefusedException(UpdateTexts.NoChecksum);
        if (sums.Size > MaxChecksumsBytes)
        {
            throw new UpdateRefusedException(UpdateTexts.NoChecksum);
        }

        using var buffer = new MemoryStream();
        try
        {
            await feed.DownloadAsync(sums.Url, buffer, MaxChecksumsBytes, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new UpdateRefusedException(UpdateTexts.DownloadFailed);
        }

        return FindHash(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), name) ?? throw new UpdateRefusedException(UpdateTexts.NoChecksum);
    }

    /// <summary>The hash of <paramref name="name"/> in a <c>sha256sum</c> listing ("&lt;hex&gt;  name" or "&lt;hex&gt; *name").</summary>
    public static byte[]? FindHash(string sums, string name)
    {
        ArgumentNullException.ThrowIfNull(sums);
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space != 64)
            {
                continue;
            }

            var file = line[(space + 1)..].TrimStart(' ').TrimStart('*');
            if (!string.Equals(file, name, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                return Convert.FromHexString(line[..64]);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }
}
