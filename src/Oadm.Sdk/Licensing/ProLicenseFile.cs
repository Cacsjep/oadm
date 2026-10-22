using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oadm.Sdk.Licensing;

/// <summary>
/// The OADM PRO license key (docs/spec/licensing.md): a license key in the plugin license format (<see cref="LicenseFile"/>:
/// <c>OADM1.&lt;content&gt;.&lt;signature&gt;</c>, ECDSA P-256 with SHA-256 over the content bytes) whose content names the
/// product <see cref="ProductId"/> and adds <c>plan</c>; a trial lists the feature <see cref="TrialFeature"/> and the plan's
/// devices are the limit <see cref="DevicesLimit"/>.
/// </summary>
public static class ProLicenseFile
{
    /// <summary>The "plugin" a PRO license names.</summary>
    public const string ProductId = "oadm.pro";

    /// <summary>The license feature of a trial license.</summary>
    public const string TrialFeature = "trial";

    /// <summary>The license limit that holds the devices of the plan.</summary>
    public const string DevicesLimit = "devices";

    /// <summary>
    /// What <paramref name="text"/> says for the server <paramref name="serverId"/> on <paramref name="today"/>: checked with
    /// each of <paramref name="publicKeys"/> (base64 SubjectPublicKeyInfo) until one matches. Never throws for a bad key.
    /// </summary>
    public static ProLicenseInfo Check(string text, IEnumerable<string> publicKeys, string serverId, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(publicKeys);
        foreach (var key in publicKeys)
        {
            var result = LicenseFile.Verify(text, key, ProductId, serverId, today);
            if (result.Data is not { } data)
            {
                continue; // signed with another key, unreadable or for another product
            }

            var trial = data.Features.Contains(TrialFeature, StringComparer.OrdinalIgnoreCase);
            long? devices = data.Limits.TryGetValue(DevicesLimit, out var max) ? max : null;
            var plan = PlanOf(text);
            return result.Status switch
            {
                LicenseStatus.Valid => new ProLicenseInfo(ProLicenseStatus.Active, ProLicenseTexts.Active(plan, data.Expires, trial), data.Customer, plan, devices, data.Expires, trial),
                LicenseStatus.Expired => new ProLicenseInfo(ProLicenseStatus.Expired, ProLicenseTexts.ExpiredOn(data.Expires!.Value), data.Customer, plan, devices, data.Expires, trial),
                LicenseStatus.WrongServer => new ProLicenseInfo(ProLicenseStatus.WrongServer, ProLicenseTexts.WrongServer, data.Customer, plan, devices, data.Expires, trial),
                _ => new ProLicenseInfo(ProLicenseStatus.Invalid, ProLicenseTexts.NotValid),
            };
        }

        return new ProLicenseInfo(ProLicenseStatus.Invalid, ProLicenseTexts.NotValid);
    }

    /// <summary>
    /// A PRO license key signed with <paramref name="privateKeyPem"/> (PKCS#8 PEM of an ECDSA P-256 key): the reference of the
    /// format the license server produces, and the signer of the tests and of fake mode.
    /// </summary>
    public static string Sign(string privateKeyPem, string serverId, string customer, string plan, long? maxDevices, DateOnly issued, DateOnly? expires, bool trial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        var license = new JsonObject
        {
            ["plugin"] = ProductId,
            ["server"] = serverId,
            ["customer"] = customer,
            ["issued"] = LicenseFile.Day(issued),
            ["expires"] = expires is { } day ? LicenseFile.Day(day) : null,
            ["plan"] = plan,
        };
        if (trial)
        {
            license["features"] = new JsonArray(TrialFeature);
        }

        if (maxDevices is { } max)
        {
            license["limits"] = new JsonObject { [DevicesLimit] = max };
        }

        var content = Encoding.UTF8.GetBytes(license.ToJsonString());
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(content, HashAlgorithmName.SHA256); // IEEE P1363: r || s, 64 bytes
        return LicenseFile.Prefix + Base64Url.EncodeToString(content) + "." + Base64Url.EncodeToString(signature);
    }

    /// <summary>The optional <c>plan</c> of a key's content, unchecked (read after the signature was checked).</summary>
    private static string? PlanOf(string text)
    {
        try
        {
            var key = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
            var content = key[LicenseFile.Prefix.Length..].Split('.')[0];
            return (JsonNode.Parse(Base64Url.DecodeFromChars(content)) as JsonObject)?["plan"] is JsonValue value
                   && value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
