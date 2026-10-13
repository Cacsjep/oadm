using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Acap;

/// <summary>
/// One Axis license key file: XML <c>&lt;LicenseKey&gt;</c> with <c>ApplicationID</c>, <c>DeviceID</c> (the device's MAC
/// address), <c>ExpirationDate</c> and a signature. Format version 1, seen in a real key of AXIS Missing Features. The
/// device checks the signature; OADM only reads what decides which device and application a key belongs to.
/// </summary>
public sealed record LicenseKey
{
    /// <summary>A key file larger than this is not a key (the real one is 649 bytes).</summary>
    public const int MaxBytes = 8 * 1024;

    public required string ApplicationId { get; init; }

    /// <summary>The device's MAC address: 12 upper-case hex digits, no separators (= <c>IDeviceInfo.Serial</c>).</summary>
    public required string DeviceId { get; init; }

    /// <summary>Last day the key is valid; null when the key does not expire.</summary>
    public DateOnly? ExpirationDate { get; init; }

    /// <summary>The application versions the key is for (major.minor), null = any (-1 in the file).</summary>
    public Version? MinimumVersion { get; init; }

    public Version? MaximumVersion { get; init; }

    /// <summary>Whether the key fits this application version ("3.8.0"); an unreadable version counts as fitting (the device decides).</summary>
    public bool Fits(string? applicationVersion)
    {
        var parts = (applicationVersion ?? "").Split('.', '-');
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return true;
        }

        var version = new Version(major, minor);
        return (MinimumVersion is null || version >= MinimumVersion) && (MaximumVersion is null || version <= MaximumVersion);
    }

    /// <summary>"versions 3.0 to 3.9", "version 3.0 and later", "versions up to 3.9".</summary>
    public string VersionRangeText => (MinimumVersion, MaximumVersion) switch
    {
        ({ } min, { } max) => $"versions {Text(min)} to {Text(max)}",
        ({ } min, null) => $"version {Text(min)} and later",
        (null, { } max) => $"versions up to {Text(max)}",
        _ => "every version",
    };

    /// <summary>"2.3", or "2.x" for every minor version of 2.</summary>
    private static string Text(Version version) =>
        version.Minor == int.MaxValue ? $"{version.Major}.x" : $"{version.Major}.{version.Minor}";

    private static Version? VersionOf(System.Xml.Linq.XElement root, string major, string minor)
    {
        int Read(string name) => int.TryParse(root.Element(name)?.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : -1;
        var ma = Read(major);
        if (ma < 0)
        {
            return null;
        }

        var mi = Read(minor);
        return new Version(ma, mi < 0 ? 0 : mi);
    }

    /// <summary>The key file exactly as read; uploaded unchanged.</summary>
    public required string Xml { get; init; }

    public bool IsExpired(DateOnly today) => ExpirationDate is { } end && end < today;

    /// <summary>"valid until 2027-09-18" or "no end date".</summary>
    public string ValidityText => ExpirationDate is { } end
        ? "valid until " + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : "no end date";

    /// <summary>Reads a key file. Throws <see cref="FormatException"/> with a plain reason when it is not one.</summary>
    public static LicenseKey Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (Encoding.UTF8.GetByteCount(xml) > MaxBytes)
        {
            throw new FormatException("This is not a license key file (too large).");
        }

        System.Xml.Linq.XElement root;
        try
        {
            root = DeviceXml.ParseElement(xml.Trim().TrimStart('﻿'));
        }
        catch (XmlException ex)
        {
            throw new FormatException("This is not a license key file.", ex);
        }

        if (root.Name.LocalName != "LicenseKey")
        {
            throw new FormatException("This is not a license key file.");
        }

        var appId = root.Element("ApplicationID")?.Value.Trim();
        var deviceId = NormalizeMac(root.Element("DeviceID")?.Value);
        if (string.IsNullOrEmpty(appId) || deviceId is null || string.IsNullOrWhiteSpace(root.Element("Signature")?.Value))
        {
            throw new FormatException("The license key file has no application, device or signature.");
        }

        var expires = root.Element("ExpirationDate")?.Value.Trim();
        DateOnly? end = null;
        if (!string.IsNullOrEmpty(expires) && expires != "-1" && expires != "0")
        {
            end = DateOnly.TryParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : throw new FormatException("The license key file has an unreadable end date.");
        }

        return new LicenseKey
        {
            ApplicationId = appId,
            DeviceId = deviceId,
            ExpirationDate = end,
            MinimumVersion = VersionOf(root, "MinimumMajorVersion", "MinimumMinorVersion"),
            MaximumVersion = MaxVersionOf(root),
            Xml = xml,
        };
    }

    /// <summary>The upper bound: a maximum minor of -1 means every minor of that major.</summary>
    private static Version? MaxVersionOf(System.Xml.Linq.XElement root)
    {
        int Read(string name) => int.TryParse(root.Element(name)?.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : -1;
        var major = Read("MaximumMajorVersion");
        if (major < 0)
        {
            return null;
        }

        var minor = Read("MaximumMinorVersion");
        return new Version(major, minor < 0 ? int.MaxValue : minor);
    }

    /// <summary>"B8:A4:4F:63:13:39", "b8a44f631339" -> "B8A44F631339"; null when it is not a MAC address.</summary>
    public static string? NormalizeMac(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var hex = new StringBuilder(12);
        foreach (var c in value)
        {
            if (Uri.IsHexDigit(c))
            {
                hex.Append(char.ToUpperInvariant(c));
            }
            else if (c is not (':' or '-' or '.' or ' '))
            {
                return null;
            }
        }

        return hex.Length == 12 ? hex.ToString() : null;
    }
}

/// <summary>A key as it travels in the task payload: the device it is for and the file text (parsed again by the task).</summary>
public sealed record LicenseKeyEntry(string DeviceId, string Xml);

/// <summary>A file of the chosen ones that is not a usable key, with the reason.</summary>
public sealed record LicenseKeyProblem(string FileName, string Reason);

/// <summary>Every key found in the chosen files (one per device and application: the latest end date), and what was not usable.</summary>
public sealed record LicenseKeySet(IReadOnlyList<LicenseKey> Keys, IReadOnlyList<LicenseKeyProblem> Problems)
{
    public static readonly LicenseKeySet Empty = new([], []);

    /// <summary>At most this many keys are read from the chosen files (ZIP entries included).</summary>
    public const int MaxKeys = 10_000;

    /// <summary>A ZIP may unpack to at most this many bytes.</summary>
    public const long MaxZipBytes = 64L * 1024 * 1024;

    /// <summary>The key of <paramref name="mac"/> for <paramref name="applicationId"/> (null = any application); null when there is none.</summary>
    public LicenseKey? For(string mac, string? applicationId)
    {
        var device = LicenseKey.NormalizeMac(mac);
        return device is null ? null : Keys.FirstOrDefault(k => k.DeviceId == device && (applicationId is null || k.ApplicationId == applicationId));
    }

    /// <summary>Devices the keys name for more than one application (one pass).</summary>
    public HashSet<string> DevicesWithSeveralApplications()
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        var several = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            if (!first.TryAdd(key.DeviceId, key.ApplicationId) && first[key.DeviceId] != key.ApplicationId)
            {
                several.Add(key.DeviceId);
            }
        }

        return several;
    }

    /// <summary>Every key of each device for <paramref name="applicationId"/> (null = any application), one pass.</summary>
    public Dictionary<string, List<LicenseKey>> AllByDevice(string? applicationId)
    {
        var map = new Dictionary<string, List<LicenseKey>>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            if (applicationId is null || key.ApplicationId == applicationId)
            {
                if (!map.TryGetValue(key.DeviceId, out var list))
                {
                    map[key.DeviceId] = list = [];
                }

                list.Add(key);
            }
        }

        return map;
    }

    /// <summary>Index by device for many lookups (one pass).</summary>
    public Dictionary<string, LicenseKey> ByDevice(string? applicationId)
    {
        var map = new Dictionary<string, LicenseKey>(StringComparer.Ordinal);
        foreach (var key in Keys)
        {
            if (applicationId is null || key.ApplicationId == applicationId)
            {
                map.TryAdd(key.DeviceId, key);
            }
        }

        return map;
    }

    /// <summary>
    /// Reads key files (<c>.key</c>, <c>.xml</c>, any text) and ZIP files with keys. Problems of single files are listed, never
    /// thrown; the same device, application and version range twice keeps the key with the latest end date.
    /// </summary>
    public static LicenseKeySet Read(IEnumerable<(string Name, byte[] Content)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var best = new Dictionary<(string, string, string), LicenseKey>();
        var problems = new List<LicenseKeyProblem>();
        var count = 0;

        void Add(string name, byte[] bytes)
        {
            if (count >= MaxKeys)
            {
                problems.Add(new(name, $"More than {MaxKeys:N0} keys; the rest was not read."));
                return;
            }

            count++;
            try
            {
                if (bytes.Length > LicenseKey.MaxBytes)
                {
                    throw new FormatException("This is not a license key file (too large).");
                }

                var key = LicenseKey.Parse(Encoding.UTF8.GetString(bytes));
                var id = (key.DeviceId, key.ApplicationId, key.VersionRangeText);
                if (!best.TryGetValue(id, out var other) || Later(key, other))
                {
                    best[id] = key;
                }
            }
            catch (FormatException ex)
            {
                problems.Add(new(name, ex.Message));
            }
        }

        foreach (var (name, content) in files)
        {
            if (content.Length >= 4 && content[0] == 'P' && content[1] == 'K' && content[2] == 3 && content[3] == 4)
            {
                ReadZip(name, content, Add, () => count >= MaxKeys, problems);
            }
            else
            {
                Add(name, content);
            }
        }

        var keys = best.Values.OrderBy(k => k.DeviceId, StringComparer.Ordinal).ThenBy(k => k.ApplicationId, StringComparer.Ordinal).ToList();
        return new LicenseKeySet(keys, problems);
    }

    private static bool Later(LicenseKey key, LicenseKey other) =>
        other.ExpirationDate is { } end && (key.ExpirationDate is null || key.ExpirationDate > end);

    private static void ReadZip(string name, byte[] content, Action<string, byte[]> add, Func<bool> full, List<LicenseKeyProblem> problems)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.Length == 0)
                {
                    continue;
                }

                if (full())
                {
                    problems.Add(new(name, $"More than {MaxKeys:N0} keys; the rest was not read."));
                    return;
                }

                var label = name + ": " + entry.FullName;
                if (entry.Length > LicenseKey.MaxBytes)
                {
                    problems.Add(new(label, "This is not a license key file (too large)."));
                    continue;
                }

                total += entry.Length;
                if (total > MaxZipBytes)
                {
                    problems.Add(new(name, "The ZIP file is too large; the rest was not read."));
                    return;
                }

                // Read at most one byte more than a key may have: the declared length of an entry is not trusted.
                using var stream = entry.Open();
                var buffer = new byte[LicenseKey.MaxBytes + 1];
                var read = 0;
                int n;
                while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0)
                {
                    read += n;
                }

                add(label, buffer[..read]);
            }
        }
        catch (InvalidDataException)
        {
            problems.Add(new(name, "The ZIP file cannot be read."));
        }
    }
}
