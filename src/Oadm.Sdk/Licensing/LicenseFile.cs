using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oadm.Sdk.Licensing;

/// <summary>
/// The license key of a commercial plugin: one line <c>OADM1.&lt;content&gt;.&lt;signature&gt;</c>, both parts base64url, the
/// content the JSON of <see cref="LicenseData"/>. The vendor signs the content with an ECDSA P-256 key (SHA-256); OADM checks
/// it with the public key of the plugin's plugin.json, offline. The customer pastes the line or chooses a file that holds it
/// (<c>.oadmlicense</c>); spaces and line breaks inside it are ignored. <c>manage license keys</c> and <c>manage license
/// sign</c> call <see cref="CreateKeys"/> and <see cref="Sign"/>.
/// </summary>
public static class LicenseFile
{
    /// <summary>The file name extension of a file that holds a license key.</summary>
    public const string Extension = ".oadmlicense";

    /// <summary>The start of every license key (format version 1).</summary>
    public const string Prefix = "OADM1.";

    /// <summary>Most characters of a license key; a longer text is refused unread.</summary>
    public const int MaxLength = 16 * 1024;

    /// <summary>
    /// A new key pair for a vendor: the private key as PEM (PKCS#8, keep it secret, it signs every license) and the
    /// public key as base64 for <c>license.publicKey</c> in plugin.json.
    /// </summary>
    public static (string PrivateKeyPem, string PublicKey) CreateKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>The public key (base64) of a private key in PEM, e.g. to check that a key belongs to a plugin.</summary>
    public static string PublicKeyOf(string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The license key for <paramref name="data"/>, signed with the vendor's private key (PEM).</summary>
    public static string Sign(LicenseData data, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        var limits = new JsonObject();
        foreach (var (name, value) in data.Limits)
        {
            limits[name] = value;
        }

        var license = new JsonObject
        {
            ["plugin"] = data.Plugin,
            ["server"] = data.ServerId,
            ["customer"] = data.Customer,
            ["issued"] = Day(data.Issued),
            ["expires"] = data.Expires is { } expires ? Day(expires) : null,
        };
        if (data.Features.Count > 0)
        {
            license["features"] = new JsonArray([.. data.Features.Select(f => (JsonNode?)JsonValue.Create(f))]);
        }

        if (data.Limits.Count > 0)
        {
            license["limits"] = limits;
        }

        var content = Encoding.UTF8.GetBytes(license.ToJsonString());
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(content, HashAlgorithmName.SHA256);
        return Prefix + Base64Url.EncodeToString(content) + "." + Base64Url.EncodeToString(signature);
    }

    /// <summary>
    /// What a license key says, without checking its signature (the summary of the license dialog before the server checks
    /// it); null when the text is no license key.
    /// </summary>
    public static LicenseData? Peek(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Split(text) is { } parts ? TryRead(parts.Content) : null;
    }

    /// <summary>
    /// Checks a license key for a plugin on a server: the format, the signature with <paramref name="publicKey"/>, the
    /// plugin, the server and the last day (<paramref name="today"/> in the server's time). Never throws for a bad key: the
    /// result says what is wrong (<see cref="LicenseStatus.Invalid"/>, <see cref="LicenseStatus.WrongServer"/>,
    /// <see cref="LicenseStatus.Expired"/>) or <see cref="LicenseStatus.Valid"/> with the content.
    /// </summary>
    public static PluginLicenseInfo Verify(string text, string publicKey, string pluginId, string serverId, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Split(text) is not { } parts)
        {
            return Invalid("it is not an OADM license key.");
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (!key.VerifyData(parts.Content, parts.Signature, HashAlgorithmName.SHA256))
            {
                return Invalid("its signature does not match. The key was changed or is from another vendor.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return Invalid("the plugin's public key cannot be read.");
        }

        if (TryRead(parts.Content) is not { } data)
        {
            return Invalid("its content cannot be read.");
        }

        if (!string.Equals(data.Plugin, pluginId, StringComparison.OrdinalIgnoreCase))
        {
            return new PluginLicenseInfo(LicenseStatus.Invalid, $"The license is for another plugin ({data.Plugin}).");
        }

        if (!string.Equals(data.ServerId.Trim(), serverId, StringComparison.OrdinalIgnoreCase))
        {
            return new PluginLicenseInfo(LicenseStatus.WrongServer, $"The license is for another server ({data.ServerId}).", data);
        }

        if (data.Expires is { } expires && expires < today)
        {
            return new PluginLicenseInfo(LicenseStatus.Expired, $"The license expired on {Day(expires)}.", data);
        }

        return new PluginLicenseInfo(LicenseStatus.Valid, Summary(data), data);
    }

    /// <summary>"Licensed to ACME GmbH until 2027-12-31." or "Licensed to ACME GmbH."</summary>
    public static string Summary(LicenseData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Expires is { } until ? $"Licensed to {data.Customer} until {Day(until)}." : $"Licensed to {data.Customer}.";
    }

    /// <summary>"2027-12-31".</summary>
    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The content and the signature of a key; null when the text is no license key.</summary>
    private static (byte[] Content, byte[] Signature)? Split(string text)
    {
        if (text.Length > MaxLength)
        {
            return null;
        }

        var key = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (!key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = key[Prefix.Length..].Split('.');
        if (parts.Length != 2)
        {
            return null;
        }

        try
        {
            return (Base64Url.DecodeFromChars(parts[0]), Base64Url.DecodeFromChars(parts[1]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static LicenseData? TryRead(byte[] content)
    {
        try
        {
            var node = JsonNode.Parse(content) as JsonObject ?? throw new FormatException("not an object");
            string Text(string name) => node[name]?.GetValue<string>() ?? throw new FormatException(name);
            DateOnly? Date(string name) => node[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String
                ? DateOnly.ParseExact(value.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;
            var features = node["features"] is JsonArray array ? array.Select(f => f!.GetValue<string>()).ToList() : [];
            var limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (node["limits"] is JsonObject limitNodes)
            {
                foreach (var (name, value) in limitNodes)
                {
                    limits[name] = value!.GetValue<long>();
                }
            }

            return new LicenseData(Text("plugin"), Text("server"), Text("customer"), Date("issued") ?? throw new FormatException("issued"), Date("expires"), features, limits);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or NullReferenceException or ArgumentException
            or KeyNotFoundException)
        {
            return null;
        }
    }

    private static PluginLicenseInfo Invalid(string why) => new(LicenseStatus.Invalid, "The license is not valid: " + why);
}
