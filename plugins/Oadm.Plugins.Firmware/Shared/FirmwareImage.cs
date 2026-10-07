using System.Text.RegularExpressions;

namespace Oadm.Plugins.Firmware;

/// <summary>
/// What OADM could learn about a firmware file before sending it to a device. Axis does not publish
/// the layout of the signed AXIS OS image, so product and version come from the official download
/// name (<c>P3265-V_12_11_77.bin</c>); the header bytes are only used to reject files that are
/// clearly not an AXIS OS image (archives, ACAP packages, executables, ...). The device itself is the
/// final authority: it verifies the signature and the product before it installs anything
/// (fwmgr errors 415, 421, 422).
/// </summary>
public sealed record FirmwareImageInfo(
    string FileName,
    long Size,
    string? Product,
    AxisOsVersion? Version,
    string? RejectReason)
{
    public bool IsRejected => RejectReason is not null;

    /// <summary>Product and version were both recognized from the file name.</summary>
    public bool IsIdentified => Product is not null && Version is not null;
}

public static partial class FirmwareImageInspector
{
    /// <summary>Smallest plausible AXIS OS image. Real images are tens to hundreds of MB.</summary>
    public const long MinimumSize = 1024 * 1024;

    /// <summary>Largest accepted image (the biggest current images are around 250 MB).</summary>
    public const long MaximumSize = 2L * 1024 * 1024 * 1024;

    /// <summary>Bytes from the start of the file that <see cref="Inspect"/> looks at.</summary>
    public const int HeaderLength = 512;

    /// <summary>Inspects a file name, its size and the first bytes (<see cref="HeaderLength"/> are enough).</summary>
    public static FirmwareImageInfo Inspect(string fileName, long size, ReadOnlySpan<byte> header)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var name = Path.GetFileName(fileName);
        var (product, version) = ParseFileName(name);
        return new FirmwareImageInfo(name, size, product, version, CheckContent(name, size, header));
    }

    /// <summary>
    /// Reads product number and version from the Axis download naming scheme:
    /// <c>&lt;ProdNbr&gt;_&lt;major&gt;_&lt;minor&gt;_&lt;build&gt;[_suffix].bin</c>, dots also accepted
    /// (<c>P3265-LV_12.0.89.bin</c>), optional <c>AXIS_</c> prefix.
    /// </summary>
    public static (string? Product, AxisOsVersion? Version) ParseFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return (null, null);
        }

        var match = FileNamePattern().Match(Path.GetFileName(fileName));
        if (!match.Success)
        {
            return (null, null);
        }

        return (match.Groups["prod"].Value.ToUpperInvariant(), AxisOsVersion.TryParse(match.Groups["ver"].Value));
    }

    private static string? CheckContent(string name, long size, ReadOnlySpan<byte> header)
    {
        if (!name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return "Not an AXIS OS image: the file must be a .bin file.";
        }

        if (size < MinimumSize)
        {
            return "Not an AXIS OS image: the file is too small.";
        }

        if (size > MaximumSize)
        {
            return "Not an AXIS OS image: the file is too large.";
        }

        if (header.Length < 4)
        {
            return "Not an AXIS OS image: the file header could not be read.";
        }

        if (header.StartsWith("PK\u0003\u0004"u8))
        {
            return "This is a ZIP archive. Extract the .bin firmware file first.";
        }

        if (header[0] == 0x1F && header[1] == 0x8B)
        {
            return "This is a compressed archive (for example an ACAP .eap package), not an AXIS OS image.";
        }

        if (header.StartsWith("%PDF"u8) || header.StartsWith("MZ"u8) || header.StartsWith("\u007FELF"u8)
            || header.StartsWith("<"u8) || header.StartsWith("{"u8))
        {
            return "Not an AXIS OS image (document, program or text file).";
        }

        if (header.IndexOfAnyExcept((byte)0) < 0)
        {
            return "Not an AXIS OS image: the file starts with empty data.";
        }

        return null;
    }

    [GeneratedRegex(@"^(?:AXIS[_ -])?(?<prod>[A-Z]{1,3}\d{2,5}[A-Z0-9-]*?)_(?<ver>\d{1,2}(?:[._]\d{1,4}){1,3})(?:[_-][A-Za-z0-9]+)*\.bin$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex FileNamePattern();
}
