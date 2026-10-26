using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using Oadm.Sdk.Certificates;

namespace Oadm.Client.Api;

/// <summary>
/// The PKI certificate lists in fake mode: the CA rows of the fake CAs (the Certificate authorities card) and one certificate
/// per fake device and use (HTTPS for every device, IEEE 802.1X for every third), one expired; Delete takes rows out of the
/// list (decision 2026-10-26: no revoke). View certificate describes a real certificate the fake CA signs for the row. JSON
/// like the plugin (camelCase, enums as numbers).
/// </summary>
public sealed partial class FakeOadmApi
{
    private List<FakeIssued>? _pkiIssued;

    private sealed class FakeIssued
    {
        public required string Serial { get; init; }

        public required string DeviceId { get; init; }

        public required string DeviceSerial { get; init; }

        public required string Device { get; init; }

        public required string Purpose { get; init; }

        public required string Source { get; init; }

        public DateTime NotBefore { get; init; }

        public DateTime NotAfter { get; init; }
    }

    /// <summary>The certificate lists' methods; null for other methods (called inside the gate).</summary>
    private JsonObject? FakePkiCertificates(string method, JsonNode? payload)
    {
        var issued = _pkiIssued ??= CreateFakeIssued();
        var now = DateTime.UtcNow;
        switch (method)
        {
            case "getCertificates":
            {
                int? state = payload?["state"]?.GetValue<int>();
                string? purpose = payload?["purpose"]?.GetValue<string>();
                string? source = payload?["source"]?.GetValue<string>();
                bool noAuthorities = payload?["noAuthorities"]?.GetValue<bool>() ?? false;
                string search = payload?["search"]?.GetValue<string>()?.Trim() ?? string.Empty;
                int offset = Math.Max(0, payload?["offset"]?.GetValue<int>() ?? 0);
                int limit = Math.Clamp(payload?["limit"]?.GetValue<int>() ?? 500, 1, 500);
                var authorities = noAuthorities || (purpose is not null && purpose != ListedCertificateUses.Ca) || source is not null
                    ? []
                    : _pkiAuthorities!.Where(a => search.Length == 0 || a.Ca.CommonName.Contains(search, StringComparison.OrdinalIgnoreCase))
                        .Select(a => FakeAuthorityRow(a, now)).Where(r => state is null || r["state"]!.GetValue<int>() == state).ToList();
                var devices = purpose == ListedCertificateUses.Ca
                    ? []
                    : issued
                        .Where(c => (state is null || (int)FakeIssuedState(c, now) == state)
                            && (purpose is null || c.Purpose == purpose)
                            && (source is null || c.Source == source)
                            && (search.Length == 0 || c.Device.Contains(search, StringComparison.OrdinalIgnoreCase)
                                || c.DeviceSerial.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Serial.Contains(search.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)))
                        .OrderByDescending(c => c.NotBefore)
                        .Select(c => FakeIssuedRow(c, now))
                        .ToList();
                var matching = authorities.Concat(devices).ToList();
                JsonArray rows = [.. matching.Skip(offset).Take(limit).Select(r => (JsonNode)r)];
                return new JsonObject { ["rows"] = rows, ["total"] = matching.Count, ["offset"] = offset, ["warningDays"] = 30 };
            }

            case "getCertificate":
            {
                string serial = payload?["serialNumber"]?.GetValue<string>() ?? string.Empty;
                byte[]? der = _pkiAuthorities!.Find(a => string.Equals("ca:" + a.Ca.Id, serial, StringComparison.OrdinalIgnoreCase))?.Ca.Certificate.RawData
                    ?? (issued.Find(c => c.Serial == serial) is { } found ? FakeLeaf(found) : null);
                if (der is null)
                {
                    return new JsonObject { ["error"] = "This certificate is not in the list." };
                }

                var description = CertificateDescriber.Describe(der);
                return new JsonObject { ["certificate"] = System.Text.Json.JsonSerializer.SerializeToNode(description, FakeJson) };
            }

            case "deleteCertificates":
            {
                var chosen = payload?["ids"]?.AsArray().Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
                int deleted = issued.RemoveAll(c => chosen.Contains(c.Serial));
                return deleted == 0
                    ? new JsonObject { ["deleted"] = 0, ["error"] = "These certificates are no longer in the list." }
                    : new JsonObject { ["deleted"] = deleted };
            }

            default:
                return null;
        }
    }

    private static List<FakeIssued> CreateFakeIssued()
    {
        var now = DateTime.UtcNow;
        var list = new List<FakeIssued>();
        for (var i = 0; i < 24; i++)
        {
            var serial = string.Create(CultureInfo.InvariantCulture, $"ACCC8E{0x123400 + i:X6}");
            var device = string.Create(CultureInfo.InvariantCulture, $"10.0.0.{40 + i}");
            var id = new Guid(i + 1, 0x7a1, 0x4b2, [1, 2, 3, 4, 5, 6, 7, 8]).ToString();
            list.Add(new FakeIssued
            {
                Serial = FakeSerial(i, 0), DeviceId = id, DeviceSerial = serial, Device = device, Purpose = "https", Source = i % 4 == 0 ? "EST" : "Task",
                NotBefore = now.AddDays(-20 - i), NotAfter = i == 5 ? now.AddDays(12) : now.AddDays(345 - i),
            });
            if (i % 3 == 0)
            {
                list.Add(new FakeIssued
                {
                    Serial = FakeSerial(i, 1), DeviceId = id, DeviceSerial = serial, Device = device, Purpose = "dot1x", Source = "EST",
                    NotBefore = now.AddDays(-10 - i), NotAfter = now.AddDays(355 - i),
                });
            }

            if (i % 5 == 0)
            {
                list.Add(new FakeIssued
                {
                    Serial = FakeSerial(i, 2), DeviceId = id, DeviceSerial = serial, Device = device, Purpose = "rtsps", Source = "EST",
                    NotBefore = now.AddDays(-380 - i), NotAfter = i == 0 ? now.AddDays(-15) : now.AddDays(15 + i),
                });
            }
        }

        return list;
    }

    private static string FakeSerial(int device, int kind) =>
        "1" + Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(device * 10 + kind)))[..31];

    private static IssuedCertificateState FakeIssuedState(FakeIssued c, DateTime now) =>
        c.NotAfter <= now ? IssuedCertificateState.Expired
        : c.NotAfter <= now.AddDays(30) ? IssuedCertificateState.ExpiresSoon
        : IssuedCertificateState.Valid;

    private JsonObject FakeIssuedRow(FakeIssued c, DateTime now) => new()
    {
        ["serialNumber"] = c.Serial,
        ["deviceId"] = c.DeviceId,
        ["deviceSerial"] = c.DeviceSerial,
        ["device"] = c.Device,
        ["deviceManaged"] = true,
        ["purpose"] = c.Purpose,
        ["source"] = c.Source,
        ["notBeforeUtc"] = c.NotBefore,
        ["notAfterUtc"] = c.NotAfter,
        ["issuedUtc"] = c.NotBefore,
        ["state"] = (int)FakeIssuedState(c, now),
        ["kind"] = (int)ListedCertificateKind.Device,
        ["issuedBy"] = FakeSigningCa()?.Ca.CommonName,
        ["usedBy"] = c.Device,
    };

    /// <summary>A CA row of the Certificate authorities card, as the plugin builds it.</summary>
    private static JsonObject FakeAuthorityRow(FakeAuthority a, DateTime now)
    {
        var certificate = a.Ca.Certificate;
        var end = certificate.NotAfter.ToUniversalTime();
        var row = new JsonObject
        {
            ["id"] = "ca:" + a.Ca.Id,
            ["serialNumber"] = certificate.SerialNumber,
            ["kind"] = (int)ListedCertificateKind.Authority,
            ["authorityKind"] = a.Kind,
            ["authorityId"] = a.Id,
            ["isRootCa"] = a.Issuer is null,
            ["canIssue"] = a.HasKey,
            ["keyText"] = a.Ca.KeyText,
            ["name"] = a.Ca.CommonName,
            ["purpose"] = ListedCertificateUses.Ca,
            ["issuedBy"] = (a.Issuer ?? a.Ca).CommonName,
            ["notBeforeUtc"] = certificate.NotBefore.ToUniversalTime(),
            ["notAfterUtc"] = end,
            ["issuedUtc"] = certificate.NotBefore.ToUniversalTime(),
            ["state"] = (int)(end <= now ? IssuedCertificateState.Expired : end <= now.AddDays(30) ? IssuedCertificateState.ExpiresSoon : IssuedCertificateState.Valid),
        };
        if (a.Kind == FakeKindOadm)
        {
            row["usedIn"] = new JsonArray("HTTPS", "IEEE 802.1X", "802.1X server", "EST server");
            row["usedByBadges"] = new JsonArray("Issuing", "802.1X", "EST");
        }

        return row;
    }

    /// <summary>The CA the fake device certificates name: the first-start CA, else any CA with its key.</summary>
    private FakeAuthority? FakeSigningCa() => FakeFirstStartCa() ?? _pkiAuthorities!.Find(a => a.HasKey);

    /// <summary>A real certificate of the row, signed by the fake CA (EC key, made when the window opens).</summary>
    private byte[] FakeLeaf(FakeIssued c)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + c.Device, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(System.Net.IPAddress.Parse(c.Device));
        san.AddDnsName("axis-" + c.DeviceSerial.ToLowerInvariant() + ".local");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(c.Purpose == "dot1x" ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")], false));
        var notBefore = new DateTimeOffset(c.NotBefore);
        var notAfter = new DateTimeOffset(c.NotAfter);
        var serial = Convert.FromHexString(c.Serial.Length % 2 == 1 ? "0" + c.Serial : c.Serial);
        if (FakeSigningCa() is not { } ca)
        {
            using var self = request.CreateSelfSigned(notBefore, notAfter);
            return self.RawData;
        }

        using var caRsa = ca.Ca.Certificate.GetRSAPrivateKey();
        using var caEc = caRsa is null ? ca.Ca.Certificate.GetECDsaPrivateKey() : null;
        var generator = caRsa is not null ? X509SignatureGenerator.CreateForRSA(caRsa, RSASignaturePadding.Pkcs1) : X509SignatureGenerator.CreateForECDsa(caEc!);
        using var leaf = request.Create(ca.Ca.Certificate.SubjectName, generator, notBefore, notAfter, serial);
        return leaf.RawData;
    }
}
