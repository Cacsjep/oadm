using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Oadm.Plugins.Firmware.Online;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Firmware.Tests;

/// <summary>
/// axis.com from the recorded pages of Fixtures/Online (2026-11-02): the root listings (trimmed), P3265-V with three version
/// folders, P1455-LE (a version only in latest/), C1410 of PACS. Other version folders can be made up (<see cref="MakeVersionFolders"/>).
/// </summary>
internal sealed class FakeAxisSite : IAxisSite
{
    private readonly ConcurrentDictionary<string, string> _pages = new(StringComparer.Ordinal);
    private int _inFlight;

    public FakeAxisSite(bool recorded = true)
    {
        if (!recorded)
        {
            return;
        }

        Add("/ftp/pub_soft/MPQT/", "mpqt-root.html");
        Add("/ftp/pub_soft/PACS/", "pacs-root.html");
        Add("/ftp/pub_soft/MPQT/P3265-V/", "P3265-V.html");
        Add("/ftp/pub_soft/MPQT/P3265-V/latest/", "P3265-V-latest.html");
        Add("/ftp/pub_soft/MPQT/P3265-V/latest/ver.txt", "P3265-V-ver.txt");
        Add("/ftp/pub_soft/MPQT/P3265-V/12_11_147/", "P3265-V-12_11_147.html");
        Add("/ftp/pub_soft/MPQT/P3265-V/11_11_238/", "P3265-V-11_11_238.html");
        Add("/ftp/pub_soft/MPQT/P3265-V/10_12_363/", "P3265-V-10_12_363.html");
        Add("/ftp/pub_soft/MPQT/P1455-LE/", "P1455-LE.html");
        Add("/ftp/pub_soft/MPQT/P1455-LE/latest/", "P1455-LE-latest.html");
        Add("/ftp/pub_soft/MPQT/P1455-LE/latest/ver.txt", "P1455-LE-ver.txt");
        Add("/ftp/pub_soft/PACS/C1410/", "C1410.html");
        Add("/ftp/pub_soft/PACS/C1410/latest/", "C1410-latest.html");
        Add("/ftp/pub_soft/PACS/C1410/latest/ver.txt", "C1410-ver.txt");
    }

    /// <summary>Every path asked for, in order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Paths answered with an error (axis.com does not answer).</summary>
    public HashSet<string> Failing { get; } = [];

    /// <summary>Every request fails.</summary>
    public bool Down { get; set; }

    /// <summary>A version folder not recorded gets a made-up listing with one .bin of 100 MB.</summary>
    public bool MakeVersionFolders { get; set; }

    /// <summary>When set, every product folder read waits for it (concurrency tests).</summary>
    public TaskCompletionSource? ProductGate { get; set; }

    public int MaxInFlight { get; private set; }

    public int InFlight => Volatile.Read(ref _inFlight);

    public void Set(string path, string text) => _pages[path] = text;

    public void Remove(string path) => _pages.TryRemove(path, out _);

    private void Add(string path, string fixture) => _pages[path] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Online", fixture));

    public async Task<AxisPage> GetAsync(string path, CancellationToken ct)
    {
        Requests.Enqueue(path);
        Assert.DoesNotContain(".bin", path, StringComparison.OrdinalIgnoreCase); // listings only, never a firmware file
        if (Down || Failing.Contains(path))
        {
            throw new AxisSiteException("www.axis.com could not be reached (test).");
        }

        if (ProductGate is { } gate && IsProductFolder(path))
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (Requests)
            {
                MaxInFlight = Math.Max(MaxInFlight, now);
            }

            try
            {
                await gate.Task.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        if (_pages.TryGetValue(path, out var text))
        {
            return new AxisPage(true, text);
        }

        if (MakeVersionFolders && VersionFolder(path) is { } made)
        {
            return new AxisPage(true, made);
        }

        return AxisPage.Missing;
    }

    private static bool IsProductFolder(string path) => path.Count(c => c == '/') == 5 && path.StartsWith("/ftp/pub_soft/", StringComparison.Ordinal);

    /// <summary>"/ftp/pub_soft/PACS/C1410/10_12_356_2/" -> a listing with C1410_10_12_356_2.bin.</summary>
    private static string? VersionFolder(string path)
    {
        var parts = path.Trim('/').Split('/');
        if (parts.Length != 5 || OnlineListing.VersionOfFolder(parts[4]) is null)
        {
            return null;
        }

        var name = parts[3] + "_" + parts[4] + ".bin";
        return Listing(path, (name, "100.0M", Sha(name)));
    }

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A listing page in the format of axis.com with these files (name, size, SHA-256).</summary>
    public static string Listing(string folderPath, params (string Name, string Size, string? Sha)[] files)
    {
        var html = new StringBuilder("<table><tbody>\n");
        html.Append("<tr><td class=\"icon\"><img alt=\"[PARENTDIR]\"></td><td><a href=\"../\">Parent Directory</a></td><td class=\"modified\"></td><td class=\"size\">-</td><td class=\"sha256\"></td></tr>\n");
        foreach (var (name, size, sha) in files)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td class=\"icon\"><img alt=\"[FILE]\"></td><td><a href=\"{folderPath}{name}\">{name}</a></td><td class=\"modified\">2026-10-22 06:48</td><td class=\"size\">{size}</td><td class=\"sha256\">{sha}</td></tr>\n");
        }

        return html.Append("</tbody></table>").ToString();
    }

    /// <summary>A root or product listing with these folders.</summary>
    public static string Folders(string parentPath, IEnumerable<string> names)
    {
        var html = new StringBuilder("<table><tbody>\n");
        foreach (var name in names)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td class=\"icon\"><img alt=\"[DIR]\"></td><td><a href=\"{parentPath}{name}/\">{name}/</a></td><td class=\"modified\">2026-10-22 06:48</td><td class=\"size\">-</td><td class=\"sha256\"></td></tr>\n");
        }

        return html.Append("</tbody></table>").ToString();
    }
}

/// <summary>
/// The host's MyAxis downloads for a task, test-local (another change adds one to Oadm.Sdk.Testing): writes
/// <see cref="Content"/> to a temp file and hands it out, records every call and every release of a handle.
/// </summary>
internal sealed class FakeAxisDownloads : IAxisDownloads, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "oadm-fw-online-" + Guid.NewGuid().ToString("N"));

    public bool HasSession { get; set; } = true;

    public byte[] Content { get; set; } = Fixture.Image();

    public bool FromCache { get; set; }

    /// <summary>Thrown by <see cref="DownloadAsync"/> instead of a file.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Called after each progress report (to look at the step while it downloads).</summary>
    public Action<long>? AfterProgress { get; set; }

    public List<(Uri Uri, string Sha256, long Size)> Calls { get; } = [];

    public int Released { get; private set; }

    public int Handed { get; private set; }

    public async Task<AxisDownloadedFile> DownloadAsync(Uri uri, string sha256, long size, IProgress<long>? progress, CancellationToken ct)
    {
        Calls.Add((uri, sha256, size));
        if (Failure is { } failure)
        {
            throw failure;
        }

        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(path, Content, ct);
        foreach (var part in new[] { Content.Length / 3L, Content.Length * 2L / 3, Content.Length })
        {
            progress?.Report(part);
            AfterProgress?.Invoke(part);
        }

        Handed++;
        return new AxisDownloadedFile(path, Content.Length, FromCache, () =>
        {
            Released++;
            return ValueTask.CompletedTask;
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
