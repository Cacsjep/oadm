using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

using Oadm.Sdk.Certificates;

namespace Oadm.Client.Api;

/// <summary>
/// The PKI Certificates tab in fake mode: one certificate per fake device and use (HTTPS for every device, IEEE 802.1X for
/// every third), a few older ones replaced, one expired; Revoke and Revoke all of this device change the rows. View
/// certificate describes a real certificate the fake CA signs for the row. JSON like the plugin (camelCase, enums as numbers).
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

        public bool Replaced { get; init; }

        public (DateTime At, int Reason, string User)? Revoked { get; set; }
    }

    /// <summary>The Certificates tab's methods; null for other methods (called inside the gate).</summary>
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
                string search = payload?["search"]?.GetValue<string>()?.Trim() ?? string.Empty;
                int offset = Math.Max(0, payload?["offset"]?.GetValue<int>() ?? 0);
                int limit = Math.Clamp(payload?["limit"]?.GetValue<int>() ?? 500, 1, 500);
                var matching = issued
                    .Where(c => (state is null || (int)FakeIssuedState(c, now) == state)
                        && (purpose is null || c.Purpose == purpose)
                        && (source is null || c.Source == source)
                        && (search.Length == 0 || c.Device.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || c.DeviceSerial.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Serial.Contains(search.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(c => c.NotBefore)
                    .ToList();
                JsonArray rows = [.. matching.Skip(offset).Take(limit).Select(c => (JsonNode)FakeIssuedRow(c, now))];
                return new JsonObject { ["rows"] = rows, ["total"] = matching.Count, ["offset"] = offset, ["warningDays"] = 30 };
            }

            case "getCertificate":
            {
                string serial = payload?["serialNumber"]?.GetValue<string>() ?? string.Empty;
                if (issued.Find(c => c.Serial == serial) is not { } found)
                {
                    return new JsonObject { ["error"] = "This certificate is not in the list." };
                }

                var description = CertificateDescriber.Describe(FakeLeaf(found));
                var reply = new JsonObject { ["certificate"] = System.Text.Json.JsonSerializer.SerializeToNode(description, FakeJson) };
                if (FakeRevocation(found) is { } revocation)
                {
                    reply["revocation"] = revocation;
                }

                return reply;
            }

            case "revoke":
            case "revokeDevice":
            {
                int reason = payload?["reason"]?.GetValue<int>() ?? 0;
                var chosen = method == "revoke"
                    ? (payload?["serialNumbers"]?.AsArray().Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [])
                    : issued.Where(c => c.DeviceId == payload?["deviceId"]?.GetValue<string>()).Select(c => c.Serial).ToHashSet(StringComparer.Ordinal);
                int revoked = 0, already = 0;
                foreach (var certificate in issued.Where(c => chosen.Contains(c.Serial)))
                {
                    if (certificate.Revoked is not null)
                    {
                        already++;
                    }
                    else if (certificate.NotAfter > now)
                    {
                        certificate.Revoked = (now, reason, FakeUser);
                        revoked++;
                    }
                }

                return new JsonObject { ["revoked"] = revoked, ["alreadyRevoked"] = already, ["notFound"] = chosen.Count - revoked - already };
            }

            default:
                return null;
        }
    }

    private const string FakeUser = "admin";

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
                    Revoked = i == 9 ? (now.AddDays(-2), 1, FakeUser) : null,
                });
            }

            if (i % 5 == 0)
            {
                list.Add(new FakeIssued
                {
                    Serial = FakeSerial(i, 2), DeviceId = id, DeviceSerial = serial, Device = device, Purpose = "https", Source = "Task",
                    NotBefore = now.AddDays(-380 - i), NotAfter = i == 0 ? now.AddDays(-15) : now.AddDays(15 + i), Replaced = i != 0,
                });
            }
        }

        return list;
    }

    private static string FakeSerial(int device, int kind) =>
        "1" + Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(device * 10 + kind)))[..31];

    private static IssuedCertificateState FakeIssuedState(FakeIssued c, DateTime now) =>
        c.Revoked is not null ? IssuedCertificateState.Revoked
        : c.NotAfter <= now ? IssuedCertificateState.Expired
        : c.Replaced ? IssuedCertificateState.Replaced
        : c.NotAfter <= now.AddDays(30) ? IssuedCertificateState.ExpiresSoon
        : IssuedCertificateState.Valid;

    private static JsonObject FakeIssuedRow(FakeIssued c, DateTime now)
    {
        var row = new JsonObject
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
        };
        if (FakeRevocation(c) is { } revocation)
        {
            row["revocation"] = revocation;
        }

        return row;
    }

    private static JsonObject? FakeRevocation(FakeIssued c) => c.Revoked is { } r
        ? new JsonObject { ["serialNumber"] = c.Serial, ["revokedUtc"] = r.At, ["reason"] = r.Reason, ["userName"] = r.User, ["deviceId"] = c.DeviceId, ["notAfterUtc"] = c.NotAfter }
        : null;

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
        using var caKey = _pkiCa!.Certificate.GetRSAPrivateKey()!;
        var generator = X509SignatureGenerator.CreateForRSA(caKey, RSASignaturePadding.Pkcs1);
        var notBefore = new DateTimeOffset(c.NotBefore);
        var notAfter = new DateTimeOffset(c.NotAfter);
        using var leaf = request.Create(_pkiCa.Certificate.SubjectName, generator, notBefore, notAfter, Convert.FromHexString(c.Serial.Length % 2 == 1 ? "0" + c.Serial : c.Serial));
        return leaf.RawData;
    }
}
