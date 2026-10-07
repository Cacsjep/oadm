namespace Oadm.Plugins.Network.Client;

/// <summary>
/// Texts of field errors: the shared validator's messages name their section ("IPv4: ..."); below the field
/// that prefix is noise. Field errors themselves go through <c>ValidatingViewModel</c> (Oadm.Sdk.Client).
/// </summary>
public static class FieldErrorText
{
    /// <summary>"IPv4: enter a subnet mask ..." becomes "Enter a subnet mask ...": the field already says what it is.</summary>
    public static string Clean(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var text = error;
        foreach (var prefix in new[] { "IPv4: ", "IPv6: ", "DNS: ", "Host name: " })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
