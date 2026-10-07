using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

namespace Oadm.Client.Api;

/// <summary>
/// Fake backend of the "PKI" core plugin (plugins/Oadm.Plugins.Pki) so <c>--fake</c> shows the page: a CA generated in
/// memory (RSA 2048 to start fast) with one previous CA, 12 devices with issued certificates, simulated server trust
/// store, real export and backup files. Nothing is installed on this computer (the state says <c>simulated</c>).
/// The JSON matches the plugin's models (camelCase); the plugin tests check that.
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string PkiPluginId = "oadm.pki";

    private const int FakeIssuedDevices = 12;

    private FakeCa? _pkiCa;
    private FakeCa? _pkiPrevious;
    private DateTime _pkiPreviousReplaced;
    private bool _pkiServerTrust;
    private bool _pkiReplaced;
    private JsonNode _pkiConfig = JsonNode.Parse("""{"deviceCertValidityDays":365,"expiryWarningDays":30,"dot1x":{"eapolVersion":1,"identity":"mac","customIdentity":"","radiusCa":"oadm"}}""")!;

    private Task<string?> InvokePkiAsync(string method, string? payloadJson)
    {
        JsonNode? payload = string.IsNullOrWhiteSpace(payloadJson) ? null : JsonNode.Parse(payloadJson);
        lock (_gate)
        {
            ThrowIfOffline();
            EnsureFakePki();
            switch (method)
            {
                case "getState":
                    return Json(FakePkiState());
                case "previewReplace":
                    return Json(new JsonObject { ["devicesWithCurrentCa"] = _pkiReplaced ? 0 : FakeIssuedDevices });
                case "generate":
                {
                    string name = payload?["commonName"]?.GetValue<string>()?.Trim() ?? string.Empty;
                    int years = payload?["validityYears"]?.GetValue<int>() ?? 10;
                    bool confirmed = payload?["confirmed"]?.GetValue<bool>() ?? false;
                    if (name.Length is 0 or > 64 || years is < 1 or > 30)
                    {
                        return Json(new JsonObject { ["errors"] = new JsonObject { [name.Length is 0 or > 64 ? "CommonName" : "ValidityYears"] = name.Length == 0 ? "Enter a name." : name.Length > 64 ? "At most 64 characters." : "Enter a whole number from 1 to 30." } });
                    }

                    if (!confirmed)
                    {
                        return Json(new JsonObject { ["needsConfirmation"] = true, ["devicesWithCurrentCa"] = _pkiReplaced ? 0 : FakeIssuedDevices });
                    }

                    _pkiPrevious?.Dispose();
                    _pkiPrevious = _pkiCa;
                    _pkiPreviousReplaced = DateTime.UtcNow;
                    _pkiCa = FakeCa.Create(name, payload?["organization"]?.GetValue<string>(), years, DateTimeOffset.UtcNow);
                    _pkiServerTrust = false;
                    _pkiReplaced = true;
                    return Json(new JsonObject { ["ok"] = true, ["state"] = FakePkiState() });
                }

                case "import":
                    return Json(new JsonObject { ["errors"] = new JsonObject { ["File"] = "Importing a CA is not available in fake mode." } });
                case "exportPublic":
                    return Json(FakeExport(_pkiCa!, payload?["format"]?.GetValue<string>()));
                case "exportPrevious":
                    return Json(_pkiPrevious is null
                        ? new JsonObject { ["error"] = "This previous CA no longer exists." }
                        : FakeExport(_pkiPrevious, payload?["format"]?.GetValue<string>()));
                case "removePrevious":
                    _pkiPrevious?.Dispose();
                    _pkiPrevious = null;
                    return Json(new JsonObject { ["ok"] = true, ["state"] = FakePkiState() });
                case "backup":
                {
                    string password = payload?["password"]?.GetValue<string>() ?? string.Empty;
                    if (password.Length < 8)
                    {
                        return Json(new JsonObject { ["errors"] = new JsonObject { ["Password"] = "Use at least 8 characters." } });
                    }

                    byte[] pfx = _pkiCa!.Certificate.Export(X509ContentType.Pkcs12, password);
                    return Json(new JsonObject { ["fileName"] = _pkiCa.CommonName + ".pfx", ["dataBase64"] = Convert.ToBase64String(pfx) });
                }

                case "installServerTrust":
                    _pkiServerTrust = true;
                    return Json(new JsonObject { ["installed"] = true, ["state"] = FakePkiState() });
                case "saveSettings":
                    _pkiConfig = payload?["config"]?.DeepClone() ?? _pkiConfig;
                    return Json(new JsonObject { ["ok"] = true, ["state"] = FakePkiState() });
                case "importRadiusCa":
                {
                    using X509Certificate2? certificate = ReadFakeCertificate(payload?["fileBase64"]?.GetValue<string>());
                    if (certificate is null)
                    {
                        return Json(new JsonObject { ["errors"] = new JsonObject { ["RadiusCa"] = "The file is not a certificate (.crt or .cer, PEM or DER)." } });
                    }

                    return Json(new JsonObject { ["pem"] = Pem(certificate), ["summary"] = Summary(certificate) });
                }

                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
            }
        }
    }

    private void EnsureFakePki()
    {
        if (_pkiCa is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _pkiPrevious = FakeCa.Create("OADM Root CA OLD-SERVER", null, 5, now.AddYears(-2));
        _pkiPreviousReplaced = now.UtcDateTime.AddDays(-90);
        _pkiCa = FakeCa.Create("OADM Root CA FAKE-SERVER", null, 10, now.AddDays(-90));
    }

    private JsonObject FakePkiState()
    {
        FakeCa ca = _pkiCa!;
        DateTime notAfter = ca.Certificate.NotAfter.ToUniversalTime();
        JsonArray previous = [];
        if (_pkiPrevious is { } old)
        {
            previous.Add(new JsonObject
            {
                ["id"] = old.Id,
                ["commonName"] = old.CommonName,
                ["subject"] = old.Certificate.Subject,
                ["notAfterUtc"] = old.Certificate.NotAfter.ToUniversalTime(),
                ["replacedUtc"] = _pkiPreviousReplaced,
                ["devices"] = _pkiReplaced ? FakeIssuedDevices : 3,
            });
        }

        JsonNode? radiusPem = _pkiConfig["dot1x"]?["radiusCaPem"];
        JsonObject? radius = null;
        if (radiusPem?.GetValue<string>() is { Length: > 0 } pem)
        {
            using X509Certificate2? certificate = ReadFakeCertificate(Convert.ToBase64String(Encoding.ASCII.GetBytes(pem)));
            radius = certificate is null ? null : Summary(certificate);
        }

        return new JsonObject
        {
            ["status"] = new JsonObject { ["kind"] = "ok", ["text"] = $"CA valid until {notAfter:yyyy-MM-dd}" },
            ["ca"] = new JsonObject
            {
                ["id"] = ca.Id,
                ["subject"] = ca.Certificate.Subject,
                ["commonName"] = ca.CommonName,
                ["source"] = "generated",
                ["keyType"] = "RSA 2048",
                ["notBeforeUtc"] = ca.Certificate.NotBefore.ToUniversalTime(),
                ["notAfterUtc"] = notAfter,
                ["fingerprint"] = string.Join(':', Enumerable.Range(0, ca.Id.Length / 2).Select(i => ca.Id.Substring(i * 2, 2))),
                ["isIntermediate"] = false,
                ["chainSubjects"] = new JsonArray(),
                ["certificatePem"] = Pem(ca.Certificate),
            },
            ["previousCas"] = previous,
            ["config"] = _pkiConfig.DeepClone(),
            ["serverTrustInstalled"] = _pkiServerTrust,
            ["issuedDevices"] = FakeIssuedDevices,
            ["devicesWithCurrentCa"] = _pkiReplaced ? 0 : FakeIssuedDevices - 3,
            ["devicesWithPreviousCa"] = _pkiReplaced ? FakeIssuedDevices : 3,
            ["expiringSoon"] = 2,
            ["radiusCa"] = radius,
            ["simulated"] = true,
        };
    }

    private static JsonObject FakeExport(FakeCa ca, string? format)
    {
        bool der = string.Equals(format, "der", StringComparison.OrdinalIgnoreCase);
        byte[] data = der ? ca.Certificate.RawData : Encoding.ASCII.GetBytes(Pem(ca.Certificate));
        return new JsonObject { ["fileName"] = ca.CommonName + (der ? ".cer" : ".crt"), ["dataBase64"] = Convert.ToBase64String(data) };
    }

    private static X509Certificate2? ReadFakeCertificate(string? base64)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64 ?? string.Empty);
            string text = Encoding.ASCII.GetString(bytes);
            int start = text.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal);
            if (start >= 0)
            {
                return X509Certificate2.CreateFromPem(text.AsSpan(start));
            }

            return X509CertificateLoader.LoadCertificate(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static JsonObject Summary(X509Certificate2 certificate)
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return new JsonObject
        {
            ["subject"] = certificate.Subject,
            ["commonName"] = certificate.GetNameInfo(X509NameType.SimpleName, false),
            ["issuer"] = certificate.Issuer,
            ["notBeforeUtc"] = certificate.NotBefore.ToUniversalTime(),
            ["notAfterUtc"] = certificate.NotAfter.ToUniversalTime(),
            ["fingerprint"] = string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2))),
        };
    }

    private static string Pem(X509Certificate2 certificate) => new string(PemEncoding.Write("CERTIFICATE", certificate.RawData)) + "\n";

    private static Task<string?> Json(JsonNode node) => Task.FromResult<string?>(node.ToJsonString(FakeJson));

    /// <summary>A CA generated in memory for fake mode.</summary>
    private sealed class FakeCa : IDisposable
    {
        private FakeCa(X509Certificate2 certificate, string commonName)
        {
            Certificate = certificate;
            CommonName = commonName;
            Id = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        }

        public X509Certificate2 Certificate { get; }

        public string CommonName { get; }

        public string Id { get; }

        public static FakeCa Create(string commonName, string? organization, int years, DateTimeOffset notBefore)
        {
            using var key = RSA.Create(2048);
            string subject = "CN=" + commonName.Replace(",", "\\,", StringComparison.Ordinal)
                + (string.IsNullOrWhiteSpace(organization) ? string.Empty : ", O=" + organization.Trim().Replace(",", "\\,", StringComparison.Ordinal));
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            X509Certificate2 certificate = request.CreateSelfSigned(notBefore.AddMinutes(-5), notBefore.AddYears(years));
            return new FakeCa(certificate, commonName.Trim());
        }

        public void Dispose() => Certificate.Dispose();
    }
}
