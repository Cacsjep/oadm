using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oadm.Sdk.Licensing;

/// <summary>
/// The license file of a commercial plugin (<c>.oadmlicense</c>): readable JSON, <c>{"license": {...}, "signature":
/// "..."}</c>. The vendor signs the exact text of the <c>license</c> object with an ECDSA P-256 key (SHA-256); OADM checks
/// it with the public key of the plugin's plugin.json, offline. Line endings do not matter (CR LF reads like LF).
/// <c>manage license keys</c> and <c>manage license sign</c> call <see cref="CreateKeys"/> and <see cref="Sign"/>.
/// </summary>
public static class LicenseFile
{
    /// <summary>The file name extension of a license file.</summary>
    public const string Extension = ".oadmlicense";

    /// <summary>Most characters of a license file; a longer text is refused unread.</summary>
    public const int MaxLength = 64 * 1024;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

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

    /// <summary>The license file for <paramref name="data"/>, signed with the vendor's private key (PEM).</summary>
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
            ["features"] = new JsonArray([.. data.Features.Select(f => (JsonNode?)JsonValue.Create(f))]),
            ["limits"] = limits,
        };
        var text = license.ToJsonString(Indented).ReplaceLineEndings("\n");
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(Indent(text)), HashAlgorithmName.SHA256));
        return "{\n  \"license\": " + Indent(text) + ",\n  \"signature\": \"" + signature + "\"\n}\n";
    }

    /// <summary>
    /// Checks a license file for a plugin on a server: the text, the signature with <paramref name="publicKey"/>, the
    /// plugin, the server and the last day (<paramref name="today"/> in the server's time). Never throws for a bad file:
    /// the result says what is wrong (<see cref="LicenseStatus.Invalid"/>, <see cref="LicenseStatus.WrongServer"/>,
    /// <see cref="LicenseStatus.Expired"/>) or <see cref="LicenseStatus.Valid"/> with the content.
    /// </summary>
    public static PluginLicenseInfo Verify(string text, string publicKey, string pluginId, string serverId, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxLength)
        {
            return Invalid("the file is too large.");
        }

        string signed;
        byte[] signature;
        try
        {
            using var document = JsonDocument.Parse(text.ReplaceLineEndings("\n"));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("license", out var license) || license.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("signature", out var sig) || sig.ValueKind != JsonValueKind.String)
            {
                return Invalid("it is not an OADM license file.");
            }

            signed = license.GetRawText();
            signature = Convert.FromBase64String(sig.GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Invalid("it is not an OADM license file.");
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (!key.VerifyData(Encoding.UTF8.GetBytes(signed), signature, HashAlgorithmName.SHA256))
            {
                return Invalid("its signature does not match. The file was changed or is from another vendor.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return Invalid("the plugin's public key cannot be read.");
        }

        LicenseData data;
        try
        {
            data = Read(signed);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
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

        return new PluginLicenseInfo(
            LicenseStatus.Valid,
            data.Expires is { } until ? $"Licensed to {data.Customer} until {Day(until)}." : $"Licensed to {data.Customer}.",
            data);
    }

    /// <summary>"2027-12-31".</summary>
    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static LicenseData Read(string licenseJson)
    {
        var node = JsonNode.Parse(licenseJson)!.AsObject();
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

    /// <summary>The object indented one level, as it stands inside the file.</summary>
    private static string Indent(string json) => json.Replace("\n", "\n  ", StringComparison.Ordinal);

    private static PluginLicenseInfo Invalid(string why) => new(LicenseStatus.Invalid, "The license is not valid: " + why);
}
