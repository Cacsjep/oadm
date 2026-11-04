using System.Globalization;
using System.IO.Compression;

namespace Oadm.Plugins.Acap;

/// <summary>
/// The picked files of one install dialog as the .eap paths the dialog goes on with. A ZIP is unpacked on the client into a
/// private temp folder of the dialog (the .eap files in it, also in subfolders; everything else is ignored); the ZIP itself
/// never reaches the server or a device. Used by the Applications dialog and by Install applications of the Applications
/// page. A pick without a ZIP passes through unchanged. A refusal is an <see cref="InvalidEapException"/> with one plain
/// sentence for the file row. The temp folder goes on the next pick, on <see cref="Clear"/> and on <see cref="Dispose"/>;
/// while a <see cref="Hold"/> is open (a read, an upload) <see cref="Dispose"/> leaves it to the end of the hold.
/// </summary>
public sealed class EapPackageFiles : IDisposable
{
    /// <summary>Largest unpacked total of one pick: the default size limit of one upload (<c>ServerSettings.DefaultUploadsMaxMegabytes</c>).</summary>
    public const long MaxUnpackedBytes = 2048L * 1024 * 1024;

    /// <summary>A ZIP with more entries than this is refused: no package of one application needs them.</summary>
    public const int MaxZipEntries = 10_000;

    /// <summary>Longest file name kept from a ZIP (without the extension).</summary>
    public const int MaxNameLength = 120;

    /// <summary>Longest ZIP name in the file row: the button does not wrap, and the count after the name must stay visible.</summary>
    public const int MaxShownNameLength = 40;

    /// <summary>The name of a temp folder starts with this.</summary>
    public const string FolderPrefix = "oadm-eap-";

    /// <summary>A temp folder older than this is a leftover (a client that ended hard): <see cref="SweepOnce"/> takes it.</summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromDays(1);

    public const string PickerTitle = "Select ACAP packages or a ZIP file";

    public const string PickerFilter = "ACAP packages (*.eap, *.zip)";

    /// <summary>The tooltip of the choose button.</summary>
    public const string ChooseHint = "One .eap per architecture, or a ZIP";

    /// <summary>The progress row while a ZIP is unpacked.</summary>
    public const string Unpacking = "Unpacking the ZIP file";

    public const string UnpackCancelled = "Unpacking cancelled.";

    private const string Eap = ".eap";
    private const string Zip = ".zip";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static int _swept;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private string? _folder;
    private int _holds;
    private bool _disposed;

    /// <summary>The picker's file patterns.</summary>
    public static IReadOnlyList<string> PickerPatterns { get; } = ["*.eap", "*.zip"];

    /// <summary>Where the temp folder goes; tests set their own.</summary>
    public string TempRoot { get; init; } = Path.GetTempPath();

    /// <summary>Limit of one pick; tests set a smaller one.</summary>
    public long MaxBytes { get; init; } = MaxUnpackedBytes;

    /// <summary>Limit of one ZIP; tests set a smaller one.</summary>
    public int MaxEntries { get; init; } = MaxZipEntries;

    /// <summary>The .eap files of the current pick, in the order of the pick: empty after <see cref="Clear"/>.</summary>
    public IReadOnlyList<string> Paths { get; private set; } = [];

    /// <summary>The text of the file row for the current pick, see <see cref="Describe"/>.</summary>
    public string? Label { get; private set; }

    /// <summary>The temp folder of the current pick, null without a ZIP.</summary>
    public string? Folder
    {
        get
        {
            lock (_gate)
            {
                return _folder;
            }
        }
    }

    /// <summary>
    /// At the client's start: <see cref="SweepLeftovers"/> of the temp folder once per process, off the calling thread. The later
    /// calls do nothing.
    /// </summary>
    public static Task SweepOnce() =>
        Interlocked.Exchange(ref _swept, 1) == 0
            ? Task.Run(() => SweepLeftovers(Path.GetTempPath(), TimeProvider.System.GetUtcNow()))
            : Task.CompletedTask;

    /// <summary>Deletes the folders of <see cref="FolderPrefix"/> in <paramref name="root"/> older than <see cref="LeftoverAge"/>, as far as they can go; never throws.</summary>
    public static void SweepLeftovers(string root, DateTimeOffset now)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root, FolderPrefix + "*"))
            {
                if (now - Directory.GetLastWriteTimeUtc(folder) > LeftoverAge)
                {
                    Delete(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next start tries again.
        }
    }

    /// <summary>True when the file is a ZIP (by its extension, like the picker).</summary>
    public static bool IsZip(string path) => string.Equals(Path.GetExtension(path), Zip, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the pick has a ZIP, so it needs the unpacking and its progress.</summary>
    public static bool HasZip(IReadOnlyList<string> picked)
    {
        ArgumentNullException.ThrowIfNull(picked);
        return picked.Any(IsZip);
    }

    /// <summary>
    /// The file row's text: "app.eap" or "2 files" as without a ZIP; "app.zip  ·  2 files" with a ZIP, "app.zip and 1 more  ·  3
    /// files" with more picked next to it. <paramref name="files"/> is the number of .eap files, null while unpacking.
    /// </summary>
    public static string Describe(IReadOnlyList<string> picked, int? files)
    {
        ArgumentNullException.ThrowIfNull(picked);
        if (picked.FirstOrDefault(IsZip) is not { } zip)
        {
            return picked.Count == 1 ? Path.GetFileName(picked[0]) : Count(picked.Count);
        }

        var shown = Shorten(Path.GetFileName(zip));
        var name = picked.Count == 1 ? shown : string.Create(CultureInfo.InvariantCulture, $"{shown} and {picked.Count - 1} more");
        return files is { } count ? $"{name}  ·  {Count(count)}" : name;
    }

    /// <summary>
    /// Replaces the current pick: unpacks every ZIP off the calling thread, at most <see cref="AcapPayload.MaxFiles"/> .eap
    /// files and <see cref="MaxBytes"/> bytes (counted while writing: the sizes in a ZIP are not trusted), then
    /// <see cref="Paths"/> and <see cref="Label"/> hold the result. On a refusal or a cancel nothing stays in the temp folder.
    /// </summary>
    /// <param name="picked">The paths from the picker.</param>
    /// <param name="progress">0 to 1 over the bytes unpacked.</param>
    /// <param name="ct">Stops the unpacking.</param>
    public async Task PickAsync(IReadOnlyList<string> picked, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(picked);
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        Clear();
        if (!HasZip(picked))
        {
            Paths = [.. picked];
            Label = Describe(picked, null);
            return;
        }

        var folder = Path.Combine(TempRoot, FolderPrefix + Guid.NewGuid().ToString("N"));
        lock (_gate)
        {
            _folder = folder;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try
        {
            var paths = await Task.Run(() => UnpackAsync(picked, folder, progress, stop.Token), stop.Token).ConfigureAwait(false);
            Paths = paths;
            Label = Describe(picked, paths.Count);
        }
        catch
        {
            // Its own folder: a Dispose meanwhile forgot it while the files were still open.
            Clear();
            Delete(folder);
            throw;
        }
    }

    /// <summary>Keeps the temp folder over a read or an upload: a <see cref="Dispose"/> meanwhile takes effect at the end of it.</summary>
    public IDisposable Hold()
    {
        lock (_gate)
        {
            _holds++;
        }

        return new FilesHold(this);
    }

    /// <summary>Drops the current pick and deletes its temp folder now (a refused pick, a failed read).</summary>
    public void Clear()
    {
        string? folder;
        lock (_gate)
        {
            folder = _folder;
            _folder = null;
        }

        Paths = [];
        Label = null;
        if (folder is not null)
        {
            Delete(folder);
        }
    }

    /// <summary>The dialog closed: stops an unpacking, deletes the temp folder now or when the last hold ends.</summary>
    public void Dispose()
    {
        bool free;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            free = _holds == 0;
        }

        _stop.Cancel();
        if (free)
        {
            Clear();
        }

        _stop.Dispose();
    }

    /// <summary>
    /// An entry that holds a package: a file ending in .eap, except the resource copies macOS adds to a ZIP
    /// (<c>__MACOSX/._app.eap</c>), which only look like packages.
    /// </summary>
    internal static bool IsPackageEntry(string entryName)
    {
        var segments = entryName.Replace('\\', '/').Split('/');
        var name = segments[^1];
        return name.EndsWith(Eap, StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("._", StringComparison.Ordinal)
            && !segments.Contains("__MACOSX", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The file name to write an entry to. An entry's name is never a path: only its last segment counts, every character a
    /// file system could read as more than a name is replaced, and it always ends in .eap.
    /// </summary>
    internal static string SafeFileName(string entryName)
    {
        var path = entryName.Replace('\\', '/');
        var name = path[(path.LastIndexOf('/') + 1)..];
        var stem = name.EndsWith(Eap, StringComparison.OrdinalIgnoreCase) ? name[..^Eap.Length] : name;
        var safe = string.Create(Math.Min(stem.Length, MaxNameLength), stem, static (target, source) =>
        {
            for (var i = 0; i < target.Length; i++)
            {
                var c = source[i];
                target[i] = c < ' ' || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : c;
            }
        }).Trim(' ', '.');
        var first = safe.Split('.')[0];
        return (safe.Length == 0 || ReservedNames.Contains(first) ? "package" : safe) + Eap;
    }

    /// <summary>"vendor_application_long_name…2026.zip": the start and the end, the end says the version.</summary>
    private static string Shorten(string name)
    {
        const int Tail = 14;
        return name.Length <= MaxShownNameLength ? name : $"{name[..(MaxShownNameLength - Tail - 1)]}…{name[^Tail..]}";
    }

    private static void Delete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing is unpacked yet, or a virus scanner still reads a file: the folder stays in the user's temp folder.
        }
    }

    private static string Count(int files) =>
        string.Create(CultureInfo.InvariantCulture, $"{files} {(files == 1 ? "file" : "files")}");

    private static string Broken(string zip) => $"{zip} is damaged or not a ZIP file.";

    private static string TooLarge(string zip) => $"{zip} is too large to unpack.";

    private static void CreatePrivateFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            // The temp folder of a Windows user is already private to that user.
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private async Task<IReadOnlyList<string>> UnpackAsync(IReadOnlyList<string> picked, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        var archives = new List<ZipArchive>();
        var current = string.Empty;
        try
        {
            // First every ZIP is opened and counted, so a refusal writes nothing.
            var plan = new List<(string? Loose, string Zip, ZipArchiveEntry? Entry)>();
            long declared = 0;
            foreach (var path in picked)
            {
                if (!IsZip(path))
                {
                    plan.Add((path, string.Empty, null));
                    continue;
                }

                current = Path.GetFileName(path);
                var zip = await ZipFile.OpenReadAsync(path, ct).ConfigureAwait(false);
                archives.Add(zip);
                if (zip.Entries.Count > MaxEntries)
                {
                    throw new InvalidEapException($"{current} has too many files.");
                }

                var found = 0;
                foreach (var entry in zip.Entries)
                {
                    if (!IsPackageEntry(entry.FullName))
                    {
                        continue;
                    }

                    if (entry.IsEncrypted)
                    {
                        throw new InvalidEapException($"{current} is password protected.");
                    }

                    if (entry.Length > MaxBytes - declared)
                    {
                        throw new InvalidEapException(TooLarge(current));
                    }

                    declared += entry.Length;
                    found++;
                    plan.Add((null, current, entry));
                }

                if (found == 0)
                {
                    throw new InvalidEapException($"{current} has no .eap files.");
                }
            }

            if (plan.Count > AcapPayload.MaxFiles)
            {
                throw new InvalidEapException(AcapCompatibility.TooManyFiles);
            }

            CreatePrivateFolder(folder);
            var paths = new List<string>(plan.Count);
            var buffer = new byte[81920];
            long written = 0;
            var percent = -1L;
            for (var i = 0; i < plan.Count; i++)
            {
                var (loose, zipName, entry) = plan[i];
                if (entry is null)
                {
                    paths.Add(loose!);
                    continue;
                }

                // A folder per entry: two entries of the same name never overwrite each other.
                current = zipName;
                var target = Path.Combine(folder, i.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(target);
                var file = Path.Combine(target, SafeFileName(entry.FullName));
                long size = 0;
                var source = await entry.OpenAsync(ct).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await using (output.ConfigureAwait(false))
                    {
                        int read;
                        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            written += read;
                            if (written > MaxBytes)
                            {
                                throw new InvalidEapException(TooLarge(zipName));
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            size += read;
                            var done = Math.Min(100, written * 100 / Math.Max(1, declared));
                            if (done != percent)
                            {
                                percent = done;
                                progress?.Report(done / 100.0);
                            }
                        }
                    }
                }

                if (size != entry.Length)
                {
                    throw new InvalidEapException(Broken(zipName));
                }

                paths.Add(file);
            }

            return paths;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw new InvalidEapException(Broken(current), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidEapException($"Could not unpack {current}: {ex.Message}", ex);
        }
        finally
        {
            foreach (var archive in archives)
            {
                await archive.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void EndHold()
    {
        bool free;
        lock (_gate)
        {
            _holds--;
            free = _disposed && _holds == 0;
        }

        if (free)
        {
            Clear();
        }
    }

    private sealed class FilesHold(EapPackageFiles owner) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                owner.EndHold();
            }
        }
    }
}
