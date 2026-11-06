using Oadm.Sdk.Formatting;

namespace Oadm.Sdk.Client;

/// <summary>
/// The file size text of plugins built before SDK 0.12.0; kept so that they keep loading. Use
/// <see cref="ByteSize.Text"/>: this is the same text.
/// </summary>
public static class FileSizeText
{
    /// <summary>A file size for people: "512 bytes", "412 KB", "87.0 MB", "2.1 GB" (<see cref="ByteSize.Text"/>).</summary>
    public static string Format(long bytes) => ByteSize.Text(bytes);
}
