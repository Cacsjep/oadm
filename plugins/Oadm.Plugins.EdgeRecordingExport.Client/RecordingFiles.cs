using System.Text;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.EdgeRecordingExport.Client;

/// <summary>
/// The files of a download in the chosen folder: one folder per camera ("10.0.0.48 P3265-V B8A44F631339"), the camera's
/// file names, each file written as "name.part" and renamed once complete. A file that exists from an earlier download is
/// skipped (it is complete: only complete files carry the final name); two recordings with the same name in one download
/// (e.g. two video sources) get "_2", "_3".
/// </summary>
public sealed class RecordingFiles(string folder)
{
    /// <summary>Appended while a file is written.</summary>
    public const string PartExtension = ".part";

    private readonly HashSet<string> _written = new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    public string Folder { get; } = folder;

    /// <summary>"10.0.0.48 P3265-V B8A44F631339": address first (sorts like the device list), the MAC address keeps it unique.</summary>
    public static string CameraFolderName(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var model = device.Model?.Replace("AXIS ", string.Empty, StringComparison.Ordinal);
        return Safe(string.Join(' ', new[] { device.Address, model, device.Serial }.Where(p => !string.IsNullOrWhiteSpace(p))));
    }

    /// <summary>A name without characters that any of Windows, Linux and macOS refuse; never empty, never "." or "..".</summary>
    public static string Safe(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            text.Append(c < 32 || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? '-' : c);
        }

        var result = text.ToString().Trim().TrimEnd('.');
        return result.Length == 0 || result is "." or ".." ? "recording" : result;
    }

    /// <summary>
    /// The path to write <paramref name="fileName"/> of <paramref name="device"/> to, creating the camera's folder; null
    /// when the file is already there from an earlier download (skip it).
    /// </summary>
    public string? TargetFor(IDeviceInfo device, string fileName)
    {
        var dir = Path.Combine(Folder, CameraFolderName(device));
        Directory.CreateDirectory(dir);
        var safe = Safe(fileName);
        var path = Path.Combine(dir, safe);
        if (!_written.Contains(path))
        {
            return File.Exists(path) ? null : Claim(path);
        }

        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);
        for (var n = 2; ; n++)
        {
            var next = Path.Combine(dir, $"{stem}_{n}{extension}");
            if (!_written.Contains(next))
            {
                return File.Exists(next) ? null : Claim(next);
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="content"/> to "<paramref name="path"/>.part", then renames it to <paramref name="path"/>.
    /// Reports the bytes written so far. A failure or cancel deletes the part file.
    /// </summary>
    public static async Task<long> WriteAsync(Stream content, string path, Action<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var part = path + PartExtension;
        long total = 0;
        try
        {
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                    progress?.Invoke(total);
                }
            }

            File.Move(part, path, overwrite: false);
            return total;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private string Claim(string path)
    {
        _written.Add(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
