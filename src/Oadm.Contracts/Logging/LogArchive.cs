using System.IO.Compression;

namespace Oadm.Contracts.Logging;

/// <summary>The log files a support report took and the ones it left out because of the size cap.</summary>
public sealed record LogArchiveResult(IReadOnlyList<string> Included, IReadOnlyList<string> LeftOut, long Bytes);

/// <summary>
/// Puts the log files of a folder into a ZIP (the support report: the server's logs on the server, the client's logs in the
/// client), newest first until <c>maxBytes</c>; older files beyond it are left out and named in the result. Files in use are
/// read shared, so the logger keeps writing. Streams each file, never a whole copy in memory.
/// </summary>
public static class LogArchive
{
    /// <summary>Most bytes of log files in one support report part (server or client).</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    public static async Task<LogArchiveResult> AddAsync(ZipArchive zip, string folder, string pattern, string entryFolder, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var included = new List<string>();
        var leftOut = new List<string>();
        long total = 0;
        if (!Directory.Exists(folder))
        {
            return new LogArchiveResult(included, leftOut, 0);
        }

        // Newest first: a day sorts after the day before, "_001" after the day's first file.
        foreach (var path in Directory.GetFiles(folder, pattern).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (total + length > maxBytes)
            {
                leftOut.Add(name);
                continue;
            }

            try
            {
                var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
                await using (source.ConfigureAwait(false))
                {
                    var entry = zip.CreateEntry(entryFolder.TrimEnd('/') + "/" + name, CompressionLevel.Optimal);
                    var target = await entry.OpenAsync(ct).ConfigureAwait(false);
                    await using (target.ConfigureAwait(false))
                    {
                        await source.CopyToAsync(target, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleted by the log retention meanwhile or not readable: left out.
                leftOut.Add(name);
                continue;
            }

            included.Add(name);
            total += length;
        }

        return new LogArchiveResult(included, leftOut, total);
    }
}
