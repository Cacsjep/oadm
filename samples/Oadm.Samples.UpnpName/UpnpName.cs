using System.Text.Json;

namespace Oadm.Samples.UpnpName;

/// <summary>What the dialog sends and the query answers. Shared by the server and the client part.</summary>
public sealed record UpnpNamePayload(string Name);

public static class UpnpName
{
    public const string PluginId = "acme.upnp-name";
    public const string Parameter = "Network.UPnP.FriendlyName";
    public const int MaxLength = 64;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Why the name cannot be used, or null. The dialog and the task use the same rule.</summary>
    public static string? Problem(string? name)
    {
        var text = name?.Trim() ?? "";
        if (text.Length == 0)
        {
            return "Enter a name.";
        }

        if (text.Length > MaxLength)
        {
            return $"Use at most {MaxLength} characters.";
        }

        return text.Any(char.IsControl) ? "The name cannot contain control characters." : null;
    }
}
