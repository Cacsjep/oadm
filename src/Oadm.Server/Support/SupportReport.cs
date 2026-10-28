using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Oadm.Contracts.Logging;
using Oadm.Core.Devices;
using Oadm.Core.Licensing;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Core.Sites;
using Oadm.Core.Tasks;
using Oadm.Sdk.Licensing;
using Oadm.Server.Backup;
using Oadm.Server.Hosting;

namespace Oadm.Server.Support;

/// <summary>
/// The support report (Settings page, card Support; docs/spec/settings-backup.md "Support report"): one ZIP with the
/// server's log files (<c>logs/</c>, newest first up to <see cref="LogArchive.MaxBytes"/>) and <c>summary.txt</c> (versions,
/// system, folders, counts of devices, tags, rules, tasks, plugins, sites, the license state and the server settings).
/// Never a secret: no passwords, credentials, keys, license key text, no database file. The summary lists no device addresses
/// or serial numbers; the logs, copied as they are, name devices by address and serial number.
/// Made in <c>&lt;datafolder&gt;/support</c>; the caller deletes it after the download, leftovers go after 30 minutes.
/// </summary>
public sealed class SupportReport(
    OadmPaths paths,
    ServerSettingsStore settings,
    IDbContextFactory<OadmDbContext> dbFactory,
    DeviceRepository devices,
    PluginRegistry plugins,
    ProLicense license,
    BackupManager backups,
    ServerRunMode runMode,
    TimeProvider time,
    ISiteStates? sites = null)
{
    /// <summary>Reports older than this in the work folder are leftovers of a broken download.</summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(30);

    /// <summary>Where the reports are made before the download.</summary>
    public string Folder => Path.Combine(paths.DataDirectory, "support");

    /// <summary>"oadm-support-acs-20261022-0930.zip" (server name with only letters, digits, '-' and '.').</summary>
    public static string FileName(string serverName, DateTimeOffset localNow)
    {
        var name = new string([.. (serverName ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '-')]).Trim('-', '.');
        return $"oadm-support-{(name.Length == 0 ? "server" : name)}-{localNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.zip";
    }

    /// <summary>Makes the report; returns its path and the file name for the download.</summary>
    public async Task<(string Path, string Name)> CreateAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        DeleteLeftovers();
        var server = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
        var name = FileName(server.ServerName, time.GetLocalNow());
        var path = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                var logs = await LogArchive.AddAsync(zip, paths.LogsDirectory, "*.log", "logs", LogArchive.MaxBytes, ct).ConfigureAwait(false);
                var summary = await SummaryAsync(server, logs, ct).ConfigureAwait(false);
                var entry = zip.CreateEntry("summary.txt", CompressionLevel.Optimal);
                var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                await using (writer.ConfigureAwait(false))
                {
                    await writer.WriteAsync(summary.AsMemory(), ct).ConfigureAwait(false);
                }
            }

            return (path, name);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private void DeleteLeftovers()
    {
        var before = time.GetUtcNow().UtcDateTime - LeftoverAge;
        foreach (var old in Directory.GetFiles(Folder, "*.zip").Where(f => File.GetLastWriteTimeUtc(f) < before))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
                // Still being downloaded; the next report tries again.
            }
        }
    }

    /// <summary>The text of summary.txt.</summary>
    public async Task<string> SummaryAsync(ServerSettings server, LogArchiveResult logs, CancellationToken ct)
    {
        var text = new StringBuilder();
        var now = time.GetLocalNow();
        void Line(string label, object? value) => text.Append(label).Append(": ").AppendLine(Convert.ToString(value, CultureInfo.InvariantCulture));
        void Head(string title) => text.AppendLine().AppendLine(title).AppendLine(new string('-', title.Length));

        text.AppendLine("OADM support report");
        Line("Created", now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));

        Head("Server");
        Line("OADM version", Version);
        Line("Server name", server.ServerName);
        Line("Runs as", runMode.IsService ? "service" : "console");
        Line("Operating system", RuntimeInformation.OSDescription);
        Line("Architecture", RuntimeInformation.OSArchitecture);
        Line(".NET", RuntimeInformation.FrameworkDescription);
        Line("Time zone", TimeZoneInfo.Local.Id);
        Line("Uptime", Uptime());

        Head("Folders");
        Line("Data folder", paths.DataDirectory);
        Line("Database", Size(SizeOfFiles(paths.DatabasePath, paths.DatabasePath + "-wal", paths.DatabasePath + "-shm")));
        Line("Plugin data", Size(SizeOfFolder(paths.PluginDataDirectory)));
        Line("Uploads", Size(SizeOfFolder(Path.Combine(paths.DataDirectory, "uploads"))));
        Line("Backups", Size(SizeOfFolder(backups.Folder)));
        Line("Logs", Size(SizeOfFolder(paths.LogsDirectory)));
        Line("Logs in this report", $"{logs.Included.Count} files, {Size(logs.Bytes)}");
        if (logs.LeftOut.Count > 0)
        {
            Line("Logs left out (size limit)", string.Join(", ", logs.LeftOut));
        }

        Head("Settings");
        Line("Status polling interval (s)", server.PollingIntervalSeconds);
        Line("Read device details every (min)", server.FullRefreshMinutes);
        Line("Allowed clock difference (s)", server.ClockToleranceSeconds);
        Line("Addresses scanned at once", server.ScanParallelism);
        Line("Scan timeout (ms)", server.ScanTimeoutMs);
        Line("Discovery duration (s)", server.ZeroConfSeconds);
        Line("Try waiting devices every (min)", server.WaitingRetryMinutes);
        Line("Parallel tasks per plugin", server.MaxParallelTasksPerPlugin);
        Line("Allow Basic login over HTTP", server.AllowBasicOverHttp ? "on" : "off");
        Line("Listen URL", server.ListenUrl);

        Head("Devices");
        var all = await devices.ListDevicesAsync(ct).ConfigureAwait(false);
        Line("Devices", all.Count);
        Counts(text, "By status", all.Select(d => d.Status.ToString()));
        Counts(text, "By model", all.Select(d => d.Model ?? "unknown"));
        Counts(text, "By AXIS OS", all.Select(d => d.FirmwareVersion ?? "unknown"));

        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            Line("Waiting devices", await db.WaitingDevices.CountAsync(ct).ConfigureAwait(false));
            Line("Tags", await db.TagDefinitions.CountAsync(ct).ConfigureAwait(false));
            Line("Event rules", await db.EventRules.CountAsync(ct).ConfigureAwait(false));
            Line("Compliance checks", await db.ComplianceChecks.CountAsync(ct).ConfigureAwait(false));

            Head("Tasks of the last 7 days");
            var since = time.GetUtcNow().UtcDateTime.AddDays(-7);
            var tasks = await db.Tasks.AsNoTracking().Where(t => t.CreatedUtc >= since)
                .Select(t => new { t.Status, t.PluginId }).ToListAsync(ct).ConfigureAwait(false);
            Line("Tasks", tasks.Count);
            Counts(text, "By state", tasks.Select(t => t.Status.ToString()));
            Counts(text, "By plugin", tasks.Select(t => t.PluginId));

            Head("Sites");
            var siteRows = await db.Sites.AsNoTracking().OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, s.Version, s.NotBound, s.LastSeenUtc })
                .ToListAsync(ct).ConfigureAwait(false);
            Line("Sites", siteRows.Count);
            foreach (var site in siteRows)
            {
                var state = site.NotBound ? "not bound" : sites?.IsOnline(site.Id) == true ? "online" : "offline";
                var seen = site.LastSeenUtc is { } last ? ", last seen " + last.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "";
                text.AppendLine(CultureInfo.InvariantCulture, $"  {site.Name}: {state}, node {site.Version ?? "unknown"}{seen}");
            }
        }

        Head("Plugins");
        foreach (var package in plugins.Packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {package.Id} {package.Origin.Version}: {(package.Enabled ? "on" : "off")}");
        }

        foreach (var error in plugins.Errors)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  Not loaded: {error.Source}: {error.Message}");
        }

        Head("OADM PRO license");
        var pro = license.Current;
        Line("State", pro.Status == ProLicenseStatus.None ? pro.Reason : $"{pro.Status}: {pro.Reason}");
        if (pro.Status != ProLicenseStatus.None)
        {
            Line("Plan", pro.Plan);
            Line("Trial", pro.IsTrial ? "yes" : "no");
        }

        Head("Backups");
        if (await backups.GetLastAutomaticAsync(ct).ConfigureAwait(false) is { } automatic)
        {
            Line("Last automatic backup", automatic.TimeUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" + (automatic.Error is null ? "" : ", failed: " + automatic.Error));
        }

        if (await backups.GetLastRestoreAsync(ct).ConfigureAwait(false) is { } restore)
        {
            Line("Last restore", $"{restore.AppliedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC from OADM {restore.FromVersion}{(restore.Error is null ? "" : ", failed: " + restore.Error)}");
        }

        Line("Backups on the server", backups.List().Count);
        return text.ToString();
    }

    private static void Counts(StringBuilder text, string label, IEnumerable<string> values)
    {
        text.AppendLine(label + ":");
        foreach (var group in values.GroupBy(v => v, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {group.Key}: {group.Count()}");
        }
    }

    private static string Version =>
        typeof(SupportReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";

    private string Uptime()
    {
        using var process = Process.GetCurrentProcess();
        var up = time.GetLocalNow() - new DateTimeOffset(process.StartTime);
        return up < TimeSpan.Zero ? "unknown" : $"{(int)up.TotalDays} d {up.Hours} h {up.Minutes} min";
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.00} GB"),
    };

    private static long SizeOfFiles(params string[] files) => files.Where(File.Exists).Sum(f => new FileInfo(f).Length);

    private static long SizeOfFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
