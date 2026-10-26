using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

using Grpc.Core;

namespace Oadm.Client.Api;

/// <summary>
/// Fake backend of the "PKI" core plugin (plugins/Oadm.Plugins.Pki) so <c>--fake</c> shows the page: CAs made in memory
/// (the first-start root CA, an intermediate CA it signed and a previous CA without its key; RSA 2048 and ECC P-256 to start
/// fast), Create CA (root or intermediate), Delete CA, 12 devices with issued certificates, a simulated server trust store,
/// real export and backup files. Nothing is installed on this computer (the state says <c>simulated</c>). The JSON matches
/// the plugin's models (camelCase, enums as numbers); the plugin tests check that.
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string PkiPluginId = "oadm.pki";

    private const int FakeIssuedDevices = 12;

    // CertificateAuthorityKind of Oadm.Sdk.Certificates as numbers (the JSON of the plugin).
    private const int FakeKindOadm = 0;
    private const int FakeKindPrevious = 1;
    private const int FakeKindImported = 3;

    private List<FakeAuthority>? _pkiAuthorities;
    private readonly HashSet<string> _pkiServerTrust = new(StringComparer.OrdinalIgnoreCase);
    private JsonNode _pkiConfig = JsonNode.Parse("""{"deviceCertValidityDays":365,"expiryWarningDays":30,"dot1x":{"eapolVersion":1,"identity":"mac","customIdentity":"","radiusCa":"oadm"}}""")!;

    /// <summary>A CA of the fake list: the first-start root CA ("oadm"), a previous CA or a created / imported one.</summary>
    private sealed record FakeAuthority(string Id, int Kind, FakeCa Ca, FakeCa? Issuer, bool HasKey);

    private Task<string?> InvokePkiAsync(string method, string? payloadJson)
    {
        JsonNode? payload = string.IsNullOrWhiteSpace(payloadJson) ? null : JsonNode.Parse(payloadJson);
        lock (_gate)
        {
            ThrowIfOffline();
            EnsureFakePki();
            if (FakePkiCertificates(method, payload) is { } certificates)
            {
                return Json(certificates);
            }

            switch (method)
            {
                case "getState":
                    return Json(FakePkiState());
                case "createAuthority":
                    return Json(FakeCreateAuthority(payload));
                case "deleteAuthority":
                    return Json(FakeDeleteAuthority(payload?["id"]?.GetValue<string>()));
                case "import":
                    return Json(new JsonObject { ["errors"] = new JsonObject { ["File"] = "Importing a CA is not available in fake mode." } });
                case "importAuthority":
                    return Json(new JsonObject { ["errors"] = new JsonObject { ["AuthorityFile"] = "Importing a CA is not available in fake mode." } });
                case "exportPublic":
                    return Json(FakeFirstStartCa() is { } exported
                        ? FakeExport(exported.Ca, payload?["format"]?.GetValue<string>())
                        : new JsonObject { ["error"] = "There is no certificate authority yet." });
                case "exportPrevious":
                {
                    string? id = payload?["id"]?.GetValue<string>();
                    return Json(_pkiAuthorities!.Find(a => a.Kind == FakeKindPrevious && string.Equals(a.Ca.Id, id, StringComparison.OrdinalIgnoreCase)) is { } previous
                        ? FakeExport(previous.Ca, payload?["format"]?.GetValue<string>())
                        : new JsonObject { ["error"] = "This previous CA no longer exists." });
                }

                case "backup":
                {
                    string password = payload?["password"]?.GetValue<string>() ?? string.Empty;
                    if (password.Length < 8)
                    {
                        return Json(new JsonObject { ["errors"] = new JsonObject { ["Password"] = "Use at least 8 characters." } });
                    }

                    if (FakeFirstStartCa() is not { } first)
                    {
                        return Json(new JsonObject { ["error"] = "There is no certificate authority yet." });
                    }

                    byte[] pfx = first.Ca.Certificate.Export(X509ContentType.Pkcs12, password);
                    return Json(new JsonObject { ["fileName"] = first.Ca.CommonName + ".pfx", ["dataBase64"] = Convert.ToBase64String(pfx) });
                }

                case "installServerTrust":
                {
                    string? fingerprint = payload?["fingerprint"]?.GetValue<string>() ?? FakeFirstStartCa()?.Ca.Id;
                    if (fingerprint is not null)
                    {
                        _pkiServerTrust.Add(fingerprint);
                    }

                    return Json(new JsonObject { ["installed"] = fingerprint is not null, ["state"] = FakePkiState() });
                }

                case "saveSettings":
                    _pkiConfig = payload?["config"]?.DeepClone() ?? _pkiConfig;
                    return Json(new JsonObject { ["ok"] = true, ["state"] = FakePkiState() });
                default:
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown method '{method}'."));
            }
        }
    }

    private void EnsureFakePki()
    {
        if (_pkiAuthorities is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var root = FakeCa.Create("OADM Root CA FAKE-SERVER", 10, now.AddDays(-90), "rsa2048", null);
        var issuing = FakeCa.Create("OADM Issuing CA FAKE-SERVER", 5, now.AddDays(-30), "ecc", root);
        var previous = FakeCa.Create("OADM Root CA OLD-SERVER", 5, now.AddYears(-2), "rsa2048", null);
        _pkiAuthorities =
        [
            new FakeAuthority("oadm", FakeKindOadm, root, null, true),
            new FakeAuthority("oadm-previous:" + previous.Id, FakeKindPrevious, previous, null, false),
            new FakeAuthority("imported:" + issuing.Id, FakeKindImported, issuing, root, true),
        ];
    }

    private FakeAuthority? FakeFirstStartCa() => _pkiAuthorities!.Find(a => a.Kind == FakeKindOadm);

    /// <summary>Create CA: a root CA or an intermediate CA signed by a CA of the list with its key; field errors like the plugin's.</summary>
    private JsonObject FakeCreateAuthority(JsonNode? payload)
    {
        string name = payload?["commonName"]?.GetValue<string>()?.Trim() ?? string.Empty;
        bool intermediate = payload?["intermediate"]?.GetValue<bool>() ?? false;
        string? issuerId = payload?["issuerId"]?.GetValue<string>();
        string key = payload?["key"]?.GetValue<string>() ?? string.Empty;
        int years = payload?["validityYears"]?.GetValue<int>() ?? 0;
        var errors = new JsonObject();
        if (name.Length == 0)
        {
            errors["CommonName"] = "Enter a name.";
        }
        else if (name.Length > 64)
        {
            errors["CommonName"] = "At most 64 characters.";
        }
        else if (_pkiAuthorities!.Exists(a => string.Equals(a.Ca.CommonName, name, StringComparison.OrdinalIgnoreCase)))
        {
            errors["CommonName"] = "Another CA has this name.";
        }

        if (years is < 1 or > 30)
        {
            errors["ValidityYears"] = "Enter a whole number from 1 to 30.";
        }

        if (key is not ("ecc" or "rsa2048" or "rsa3072" or "rsa4096"))
        {
            errors["Key"] = "Choose a key.";
        }

        FakeAuthority? issuer = null;
        if (intermediate)
        {
            issuer = _pkiAuthorities!.Find(a => a.HasKey && a.Id == issuerId);
            if (issuer is null)
            {
                errors["Issuer"] = "Choose a CA with its key.";
            }
        }

        if (errors.Count > 0)
        {
            return new JsonObject { ["errors"] = errors };
        }

        var ca = FakeCa.Create(name, years, DateTimeOffset.UtcNow, key, issuer?.Ca);
        _pkiAuthorities!.Add(new FakeAuthority("imported:" + ca.Id, FakeKindImported, ca, issuer?.Ca, true));
        return new JsonObject { ["ok"] = true, ["state"] = FakePkiState() };
    }

    /// <summary>Delete CA: refused while a policy names it (the first-start CA: the built-in policies) or while a CA it signed is listed.</summary>
    private JsonObject FakeDeleteAuthority(string? rowId)
    {
        var authority = _pkiAuthorities!.Find(a => string.Equals("ca:" + a.Ca.Id, rowId, StringComparison.OrdinalIgnoreCase));
        string? error = authority is null ? "This CA is no longer in the list."
            : authority.Kind == FakeKindOadm ? "Used by the policy HTTPS. Change or delete the policy first."
            : _pkiAuthorities.Find(a => ReferenceEquals(a.Issuer, authority.Ca)) is { } signed ? $"It signed {signed.Ca.CommonName}. Delete {signed.Ca.CommonName} first."
            : null;
        if (error is not null)
        {
            return new JsonObject { ["error"] = error, ["state"] = FakePkiState() };
        }

        _pkiAuthorities.Remove(authority!);
        _pkiServerTrust.Remove(authority!.Ca.Id);
        return new JsonObject { ["ok"] = true, ["state"] = FakePkiState() };
    }

    private JsonObject FakePkiState()
    {
        var first = FakeFirstStartCa();
        JsonArray previous = [];
        foreach (var old in _pkiAuthorities!.Where(a => a.Kind == FakeKindPrevious))
        {
            previous.Add(new JsonObject
            {
                ["id"] = old.Ca.Id,
                ["commonName"] = old.Ca.CommonName,
                ["subject"] = old.Ca.Certificate.Subject,
                ["notAfterUtc"] = old.Ca.Certificate.NotAfter.ToUniversalTime(),
                ["replacedUtc"] = old.Ca.Certificate.NotBefore.ToUniversalTime().AddYears(1),
                ["devices"] = 3,
            });
        }

        JsonNode? radiusPem = _pkiConfig["dot1x"]?["radiusCaPem"];
        JsonObject? radius = null;
        if (radiusPem?.GetValue<string>() is { Length: > 0 } pem)
        {
            using X509Certificate2? certificate = ReadFakeCertificate(Convert.ToBase64String(Encoding.ASCII.GetBytes(pem)));
            radius = certificate is null ? null : Summary(certificate);
        }

        JsonArray authorities = [.. _pkiAuthorities!.Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a.Id,
            ["name"] = a.Ca.CommonName,
            ["kind"] = a.Kind,
            ["notAfterUtc"] = a.Ca.Certificate.NotAfter.ToUniversalTime(),
            ["fingerprint"] = Colons(a.Ca.Id),
            ["usedBy"] = new JsonArray(),
            ["certificatesPem"] = new JsonArray([.. new[] { a.Ca }.Concat(a.Issuer is null ? [] : [a.Issuer]).Select(c => (JsonNode)JsonValue.Create(Pem(c.Certificate))!)]),
            ["issues"] = a.HasKey,
        })];
        JsonArray roots = [.. _pkiAuthorities!.Where(a => a.Issuer is null).Select(a => (JsonNode)new JsonObject
        {
            ["fingerprint"] = a.Ca.Id,
            ["name"] = a.Ca.CommonName,
            ["certificatePem"] = Pem(a.Ca.Certificate),
            ["serverInstalled"] = _pkiServerTrust.Contains(a.Ca.Id),
        })];

        var state = new JsonObject
        {
            ["status"] = first is null
                ? new JsonObject { ["kind"] = "neutral", ["text"] = "No certificate authority" }
                : new JsonObject { ["kind"] = "ok", ["text"] = $"CA valid until {first.Ca.Certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}" },
            ["previousCas"] = previous,
            ["config"] = _pkiConfig.DeepClone(),
            ["serverTrustInstalled"] = first is not null && _pkiServerTrust.Contains(first.Ca.Id),
            ["issuedDevices"] = FakeIssuedDevices,
            ["devicesWithCurrentCa"] = FakeIssuedDevices - 3,
            ["devicesWithPreviousCa"] = 3,
            ["expiringSoon"] = 2,
            ["radiusCa"] = radius,
            ["authorities"] = authorities,
            ["rootTrust"] = roots,
            ["simulated"] = true,
        };
        if (first is not null)
        {
            state["ca"] = new JsonObject
            {
                ["id"] = first.Ca.Id,
                ["subject"] = first.Ca.Certificate.Subject,
                ["commonName"] = first.Ca.CommonName,
                ["source"] = "generated",
                ["keyType"] = first.Ca.KeyText,
                ["notBeforeUtc"] = first.Ca.Certificate.NotBefore.ToUniversalTime(),
                ["notAfterUtc"] = first.Ca.Certificate.NotAfter.ToUniversalTime(),
                ["fingerprint"] = Colons(first.Ca.Id),
                ["isIntermediate"] = false,
                ["chainSubjects"] = new JsonArray(),
                ["certificatePem"] = Pem(first.Ca.Certificate),
            };
        }

        return state;
    }

    private static string Colons(string hex) => string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

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
            ["fingerprint"] = Colons(fingerprint),
        };
    }

    private static string Pem(X509Certificate2 certificate) => new string(PemEncoding.Write("CERTIFICATE", certificate.RawData)) + "\n";

    private static Task<string?> Json(JsonNode node) => Task.FromResult<string?>(node.ToJsonString(FakeJson));

    /// <summary>A CA made in memory for fake mode, with its key: a root CA or an intermediate CA signed by <c>issuer</c>.</summary>
    private sealed class FakeCa : IDisposable
    {
        private FakeCa(X509Certificate2 certificate, string commonName, string keyText)
        {
            Certificate = certificate;
            CommonName = commonName;
            KeyText = keyText;
            Id = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        }

        /// <summary>The certificate with its private key.</summary>
        public X509Certificate2 Certificate { get; }

        public string CommonName { get; }

        /// <summary>"ECC P-256", "RSA 2048".</summary>
        public string KeyText { get; }

        /// <summary>SHA-256 of the certificate (upper hex).</summary>
        public string Id { get; }

        /// <param name="key">"ecc", "rsa2048", "rsa3072" or "rsa4096" (the plugin's CaKeys).</param>
        public static FakeCa Create(string commonName, int years, DateTimeOffset notBefore, string key, FakeCa? issuer)
        {
            string subject = "CN=" + commonName.Trim().Replace(",", "\\,", StringComparison.Ordinal);
            int? rsaSize = key switch { "rsa2048" => 2048, "rsa3072" => 3072, "rsa4096" => 4096, _ => null };
            using RSA? rsa = rsaSize is { } size ? RSA.Create(size) : null;
            using ECDsa? ec = rsa is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
            var request = rsa is not null
                ? new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                : new CertificateRequest(subject, ec!, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var start = notBefore.AddMinutes(-5);
            var end = notBefore.AddYears(years);
            X509Certificate2 certificate;
            if (issuer is null)
            {
                certificate = request.CreateSelfSigned(start, end);
            }
            else
            {
                var issuerEnd = new DateTimeOffset(issuer.Certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                var serial = RandomNumberGenerator.GetBytes(16);
                serial[0] = (byte)((serial[0] & 0x7F) | 1);
                using var issuerRsa = issuer.Certificate.GetRSAPrivateKey();
                using var issuerEc = issuerRsa is null ? issuer.Certificate.GetECDsaPrivateKey() : null;
                var generator = issuerRsa is not null ? X509SignatureGenerator.CreateForRSA(issuerRsa, RSASignaturePadding.Pkcs1) : X509SignatureGenerator.CreateForECDsa(issuerEc!);
                using var signed = request.Create(issuer.Certificate.SubjectName, generator, start, end < issuerEnd ? end : issuerEnd, serial);
                certificate = rsa is not null ? signed.CopyWithPrivateKey(rsa) : signed.CopyWithPrivateKey(ec!);
            }

            return new FakeCa(certificate, commonName.Trim(), rsaSize is { } bits ? $"RSA {bits}" : "ECC P-256");
        }

        public void Dispose() => Certificate.Dispose();
    }
}
