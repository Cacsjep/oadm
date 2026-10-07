using System.Globalization;

namespace Oadm.Sdk.Client;

/// <summary>The one file size format of the dialogs (file rows): "87.0 MB", "412 KB".</summary>
public static class FileSizeText
{
    public static string Format(long bytes) =>
        bytes >= 1024 * 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB");
}
