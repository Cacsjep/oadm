using System.Globalization;
using System.Text;

using Oadm.Client.Devices;
using Oadm.Client.Infrastructure;

namespace Oadm.Client.Discovery;

/// <summary>One address line of an imported device list.</summary>
/// <param name="Line">Line in the file, 1-based.</param>
/// <param name="Address">IP address or host name, optional port and scheme, as written.</param>
/// <param name="UserName">From the "User name" column; null without credentials.</param>
/// <param name="Password">From the "Password" column; client memory only until it goes to the server.</param>
/// <param name="Problem">Why the line cannot be used (shown in its row), null when it can.</param>
public sealed record ImportLine(int Line, string Address, string? UserName, string? Password, string? Problem)
{
    public bool HasCredentials => UserName is not null && Password is not null;

    /// <summary>From the optional "Tags" column ("Building A; PTZ"): assigned to the device when it is added.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Never prints the password.</summary>
    public override string ToString() => $"Line {Line}: {Address}";
}

/// <summary>The whole file cannot be used; the message says why (shown in the message window).</summary>
public sealed class DeviceImportException : Exception
{
    public DeviceImportException()
        : base("The file could not be read.")
    {
    }

    public DeviceImportException(string message)
        : base(message)
    {
    }

    public DeviceImportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Reads a device list for "Import from file": the CSV of Export devices or any CSV with an address column
/// (header names case-insensitive: Address, IP address, IP, Host name; optional User name, Password and Tags
/// ("Building A; PTZ", assigned to the devices that get added; missing tags are created); other columns ignored), or a plain file with one address per line. Comma or semicolon separated
/// (Excel in many locales), UTF-8 (with or without BOM), UTF-16 with BOM, else Windows Latin-1.
/// Problems of single lines (invalid address, duplicate, too long) stay with their line; only an
/// unusable file throws <see cref="DeviceImportException"/>. O(n).
/// </summary>
public sealed class DeviceImportFile
{
    /// <summary>Largest file read (2 MB: 10,000 lines of an export are about 1.3 MB).</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>Most address lines of one file.</summary>
    public const int MaxLines = 10_000;

    /// <summary>Longest line (characters).</summary>
    public const int MaxLineLength = 1024;

    public const int MaxUserNameLength = 64;
    public const int MaxPasswordLength = 256;

    /// <summary>The user of a line with a password but no user name.</summary>
    public const string DefaultUserName = "root";

    private static readonly string[] AddressHeaders = ["address", "ip address", "ipaddress", "ip", "host name", "hostname", "host"];
    private static readonly string[] UserHeaders = ["user name", "username", "user"];
    private static readonly string[] PasswordHeaders = ["password", "pass"];
    private static readonly string[] TagHeaders = ["tags", "tag"];

    /// <summary>Longest tag name (the server's limit).</summary>
    public const int MaxTagLength = 32;

    private static readonly string[] KnownHeaders =
        [.. AddressHeaders, .. UserHeaders, .. PasswordHeaders, .. TagHeaders, .. DeviceListCsv.Columns.Select(c => c.ToLowerInvariant())];

    private DeviceImportFile(string fileName, IReadOnlyList<ImportLine> lines)
    {
        FileName = fileName;
        Lines = lines;
    }

    public string FileName { get; }

    /// <summary>Every address line in file order, including the ones with a problem.</summary>
    public IReadOnlyList<ImportLine> Lines { get; }

    /// <summary>Lines with a problem.</summary>
    public int ProblemCount => Lines.Count(l => l.Problem is not null);

    /// <summary>Reads a picked file.</summary>
    /// <exception cref="DeviceImportException">The file cannot be used at all.</exception>
    public static DeviceImportFile Parse(string fileName, byte[] content, bool isTooLarge)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (isTooLarge || content.Length > MaxBytes)
        {
            throw new DeviceImportException(string.Create(CultureInfo.InvariantCulture,
                $"The file is larger than {MaxBytes / (1024 * 1024)} MB. Split it into smaller files."));
        }

        return Parse(fileName, Decode(content));
    }

    /// <summary>Reads the text of a device list.</summary>
    /// <exception cref="DeviceImportException">The file cannot be used at all.</exception>
    public static DeviceImportFile Parse(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new DeviceImportException("The file is not a text file. Choose a CSV file or a text file with one address per line.");
        }

        char separator = DetectSeparator(text);
        int addressColumn = 0;
        int userColumn = -1;
        int passwordColumn = -1;
        int tagsColumn = -1;
        bool first = true;
        var lines = new List<ImportLine>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (CsvRecord record in Csv.Read(text, separator))
        {
            if (first)
            {
                first = false;
                string[] names = record.Cells.Select(c => c.Trim().ToLowerInvariant()).ToArray();
                if (names.Any(n => KnownHeaders.Contains(n)))
                {
                    addressColumn = IndexOf(names, AddressHeaders);
                    userColumn = IndexOf(names, UserHeaders);
                    passwordColumn = IndexOf(names, PasswordHeaders);
                    tagsColumn = IndexOf(names, TagHeaders);
                    if (addressColumn < 0)
                    {
                        throw new DeviceImportException("The file has no Address column. Name the column with the IP addresses or host names \"Address\".");
                    }

                    continue;
                }
            }

            if (record.Cells.All(c => c.Trim().Length == 0))
            {
                continue; // ",,," of a spreadsheet
            }

            if (lines.Count >= MaxLines)
            {
                throw new DeviceImportException(string.Create(CultureInfo.InvariantCulture,
                    $"The file has more than {MaxLines:N0} addresses. Split it into smaller files."));
            }

            lines.Add(ReadLine(record, addressColumn, userColumn, passwordColumn, tagsColumn, seen));
        }

        if (lines.Count == 0)
        {
            throw new DeviceImportException("The file contains no addresses.");
        }

        return new DeviceImportFile(fileName ?? "", lines);
    }

    /// <summary>Client-side check of an address: IP or host name, optional http/https scheme and port. Null when usable.</summary>
    public static string? AddressProblem(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        string text = address.Trim();
        if (text.Length == 0)
        {
            return "The line has no address.";
        }

        string candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (text.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return $"\"{Shorten(text)}\" is not an IP address or host name.";
        }

        return null;
    }

    private static ImportLine ReadLine(CsvRecord record, int addressColumn, int userColumn, int passwordColumn, int tagsColumn, Dictionary<string, int> seen)
    {
        string Cell(int index) => index >= 0 && index < record.Cells.Count ? Csv.Unguard(record.Cells[index].Trim()) : "";

        string address = Cell(addressColumn);
        if (record.Length > MaxLineLength)
        {
            return new ImportLine(record.Line, Shorten(address), null, null,
                string.Create(CultureInfo.InvariantCulture, $"Line {record.Line} is longer than {MaxLineLength:N0} characters."));
        }

        if (record.Unterminated)
        {
            return new ImportLine(record.Line, Shorten(address), null, null, $"Line {record.Line} has a quote (\") that is not closed.");
        }

        string user = Cell(userColumn);
        string password = passwordColumn >= 0 && passwordColumn < record.Cells.Count ? record.Cells[passwordColumn] : ""; // passwords keep their spaces
        string? problem = AddressProblem(address);
        if (problem is null && user.Length > MaxUserNameLength)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"The user name is longer than {MaxUserNameLength} characters.");
        }

        if (problem is null && password.Length > MaxPasswordLength)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"The password is longer than {MaxPasswordLength} characters.");
        }

        List<string> tags = ReadTags(Cell(tagsColumn));
        if (problem is null && tags.Find(t => t.Length > MaxTagLength || t.Any(char.IsControl)) is { } badTag)
        {
            problem = badTag.Length > MaxTagLength
                ? string.Create(CultureInfo.InvariantCulture, $"The tag \"{Shorten(badTag)}\" is longer than {MaxTagLength} characters.")
                : $"The tag \"{Shorten(badTag)}\" contains control characters.";
        }

        string key = Normalize(address);
        if (problem is null)
        {
            if (seen.TryGetValue(key, out int earlier))
            {
                problem = string.Create(CultureInfo.InvariantCulture, $"Listed before in line {earlier}.");
            }
            else
            {
                seen[key] = record.Line;
            }
        }

        // A password alone is for the default administrator; a user name alone is no credential.
        bool credentials = password.Length > 0;
        return new ImportLine(record.Line, address, credentials ? (user.Length > 0 ? user : DefaultUserName) : null, credentials ? password : null, problem)
        {
            Tags = tags,
        };
    }

    /// <summary>"Building A; PTZ" (the export's format): trimmed, distinct (case-insensitive), empty ones dropped.</summary>
    public static List<string> ReadTags(string cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();
        foreach (string part in cell.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(part))
            {
                tags.Add(part);
            }
        }

        return tags;
    }

    private static int IndexOf(string[] names, string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            int index = Array.IndexOf(names, candidate);
            if (index >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Semicolon when the first line has semicolons but no comma (Excel with a comma decimal separator).</summary>
    private static char DetectSeparator(string text)
    {
        int end = text.AsSpan().IndexOfAny('\r', '\n');
        ReadOnlySpan<char> firstLine = end < 0 ? text : text.AsSpan(0, end);
        return firstLine.Contains(';') && !firstLine.Contains(',') ? ';' : ',';
    }

    /// <summary>"HTTPS://Cam.example.com/" and "cam.example.com" are the same entry.</summary>
    private static string Normalize(string address)
    {
        string text = address.Trim().TrimEnd('/');
        int scheme = text.IndexOf("://", StringComparison.Ordinal);
        return scheme >= 0 ? text[(scheme + 3)..] : text;
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static string Decode(byte[] content)
    {
        if (content.Length >= 2 && (content[0], content[1]) is (0xFF, 0xFE) or (0xFE, 0xFF))
        {
            return (content[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(content, 2, content.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(content); // a spreadsheet's "CSV" in the Windows code page
        }
    }
}
