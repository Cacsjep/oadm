using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>What the import dialog sent (bytes already decoded).</summary>
public sealed record CaImportInput(byte[] File, string? FileName, string? Password, byte[]? KeyFile, string? KeyPassword);

/// <summary>An imported CA, or the field errors (<see cref="PkiFields"/>) when it cannot be used.</summary>
public sealed record CaImportResult(CaMaterial? Ca, IReadOnlyDictionary<string, string> Errors)
{
    public static CaImportResult Fail(string field, string message) => new(null, new Dictionary<string, string>(StringComparer.Ordinal) { [field] = message });
}

/// <summary>
/// Reads a customer CA: PKCS#12 (password) or a PEM certificate (may hold the chain and the key) plus an optional PEM key
/// (PKCS#8, PKCS#1, SEC1, encrypted PKCS#8). Checks before anything is stored: readable (password right), exactly one
/// certificate with a key that matches it, a CA (BasicConstraints CA, keyCertSign when KeyUsage is present), RSA &gt;= 2048 or
/// ECDSA P-256 / P-384, valid now and at least one year beyond the device certificate validity. The other certificates of
/// the file on the issuer path become the chain (nearest issuer first). The password is never stored or logged.
/// </summary>
public static class CaImporter
{
    public const string WrongPassword = "The password is wrong or the file is damaged.";
    public const string WrongKeyPassword = "The password is wrong or the key file is damaged.";
    public const string UnsupportedKey = "This key type is not supported. Use RSA 2048 or larger, or ECDSA P-256 or P-384.";

    public static CaImportResult Import(CaImportInput input, DateTimeOffset now, int deviceCertValidityDays)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.File.Length == 0)
        {
            return CaImportResult.Fail(PkiFields.File, "Choose a file.");
        }

        if (input.File.Length > PkiPluginInfo.MaxImportBytes || (input.KeyFile?.Length ?? 0) > PkiPluginInfo.MaxImportBytes)
        {
            return CaImportResult.Fail(input.File.Length > PkiPluginInfo.MaxImportBytes ? PkiFields.File : PkiFields.KeyFile, "The file is larger than 1 MB.");
        }

        var read = CaCertificates.LooksLikePem(input.File) || IsDerCertificate(input.File)
            ? ReadCertificateAndKey(input)
            : ReadPkcs12(input);
        if (read.Error is { } error)
        {
            return CaImportResult.Fail(error.Field, error.Message);
        }

        var ca = read.Ca!;
        var others = read.Others;
        var keyField = input.KeyFile is { Length: > 0 } ? PkiFields.KeyFile : PkiFields.File;
        var problem = Check(ca, now, deviceCertValidityDays, out var field, keyField);
        if (problem is not null)
        {
            ca.Dispose();
            DisposeAll(others);
            return CaImportResult.Fail(field, problem);
        }

        var chain = CaCertificates.ChainAbove(ca, others);
        DisposeAll(others.Except(chain));
        return new CaImportResult(new CaMaterial(ca, chain, read.KeyPem), new Dictionary<string, string>());
    }

    /// <summary>Whether the file is a certificate without its private key (the dialog then asks for the key file).</summary>
    public static bool NeedsKeyFile(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (CaCertificates.LooksLikePem(file))
        {
            var text = Encoding.ASCII.GetString(file);
            return text.Contains("CERTIFICATE-----", StringComparison.Ordinal) && !text.Contains("PRIVATE KEY-----", StringComparison.Ordinal);
        }

        return IsDerCertificate(file);
    }

    private static bool IsDerCertificate(byte[] bytes)
    {
        using var der = TryReadDer(bytes);
        return der is not null;
    }

    private static string? Check(X509Certificate2 ca, DateTimeOffset now, int deviceCertValidityDays, out string field, string keyField)
    {
        field = PkiFields.File;
        if (!CaCertificates.IsCa(ca, out var notCa))
        {
            return notCa;
        }

        using (var rsa = ca.GetRSAPublicKey())
        {
            if (rsa is not null && rsa.KeySize < 2048)
            {
                field = keyField;
                return $"RSA {rsa.KeySize} is too weak.";
            }
        }

        using (var ecdsa = ca.GetECDsaPublicKey())
        {
            if (ecdsa is not null && CaCertificates.CurveName(ecdsa) is not ("P-256" or "P-384"))
            {
                field = keyField;
                return UnsupportedKey;
            }
        }

        var notBefore = ca.NotBefore.ToUniversalTime();
        var notAfter = ca.NotAfter.ToUniversalTime();
        if (now.UtcDateTime < notBefore)
        {
            return $"This CA is not valid before {notBefore:yyyy-MM-dd}.";
        }

        if (now.UtcDateTime > notAfter)
        {
            return $"This CA expired on {notAfter:yyyy-MM-dd}.";
        }

        if (notAfter < now.UtcDateTime.AddDays(Math.Max(1, deviceCertValidityDays)).AddYears(1))
        {
            return $"This CA expires on {notAfter:yyyy-MM-dd}, before certificates it would issue.";
        }

        return null;
    }

    private static Read ReadPkcs12(CaImportInput input)
    {
        X509Certificate2Collection collection;
        try
        {
            var flags = X509KeyStorageFlags.Exportable;
            if (!OperatingSystem.IsMacOS())
            {
                flags |= X509KeyStorageFlags.EphemeralKeySet; // keys never touch the OS key store (not supported on macOS)
            }

            collection = X509CertificateLoader.LoadPkcs12Collection(input.File, input.Password ?? string.Empty, flags);
        }
        catch (CryptographicException)
        {
            return Read.Fail(string.IsNullOrEmpty(input.Password) ? PkiFields.File : PkiFields.Password, WrongPassword);
        }

        var all = collection.Cast<X509Certificate2>().ToList();
        try
        {
            var withKey = all.Where(c => c.HasPrivateKey).ToList();
            if (withKey.Count == 0)
            {
                return Read.Fail(PkiFields.File, "The file contains no private key.");
            }

            if (withKey.Count > 1)
            {
                return Read.Fail(PkiFields.File, "The file contains more than one certificate with a private key.");
            }

            var keyPem = ExportKey(withKey[0]);
            if (keyPem is null)
            {
                return Read.Fail(PkiFields.File, UnsupportedKey);
            }

            var ca = X509CertificateLoader.LoadCertificate(withKey[0].RawData);
            var others = all.Where(c => !ReferenceEquals(c, withKey[0])).Select(c => X509CertificateLoader.LoadCertificate(c.RawData)).ToList();
            return new Read(ca, others, keyPem, null);
        }
        catch (CryptographicException)
        {
            return Read.Fail(PkiFields.File, "The private key cannot be read from the file.");
        }
        finally
        {
            DisposeAll(all);
        }
    }

    private static string? ExportKey(X509Certificate2 certificate)
    {
        using (var rsa = certificate.GetRSAPrivateKey())
        {
            if (rsa is not null)
            {
                return rsa.ExportPkcs8PrivateKeyPem();
            }
        }

        using var ecdsa = certificate.GetECDsaPrivateKey();
        return ecdsa?.ExportPkcs8PrivateKeyPem();
    }

    private static Read ReadCertificateAndKey(CaImportInput input)
    {
        List<X509Certificate2> certificates;
        try
        {
            certificates = CaCertificates.LooksLikePem(input.File)
                ? CaCertificates.ReadPemCertificates(Encoding.ASCII.GetString(input.File))
                : [TryReadDer(input.File)!];
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return Read.Fail(PkiFields.File, "The file cannot be read.");
        }

        if (certificates.Count == 0)
        {
            return Read.Fail(PkiFields.File, "The file contains no certificate.");
        }

        var separate = input.KeyFile is { Length: > 0 };
        var keyField = separate ? PkiFields.KeyFile : PkiFields.File;
        var passwordField = separate ? PkiFields.KeyPassword : PkiFields.Password;
        string keyText;
        if (separate)
        {
            if (!CaCertificates.LooksLikePem(input.KeyFile!))
            {
                DisposeAll(certificates);
                return Read.Fail(PkiFields.KeyFile, "The key file is not a PEM private key.");
            }

            keyText = Encoding.ASCII.GetString(input.KeyFile!);
        }
        else
        {
            keyText = CaCertificates.LooksLikePem(input.File) ? Encoding.ASCII.GetString(input.File) : string.Empty;
            if (!keyText.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            {
                DisposeAll(certificates);
                return Read.Fail(PkiFields.KeyFile, "Choose the private key of this certificate.");
            }
        }

        var key = LoadKey(keyText, separate ? input.KeyPassword : input.Password, keyField, passwordField, out var keyError);
        if (key is null)
        {
            DisposeAll(certificates);
            return Read.Fail(keyError!.Value.Field, keyError.Value.Message);
        }

        using (key)
        {
            var spki = key.ExportSubjectPublicKeyInfo();
            var match = certificates.FirstOrDefault(c => c.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki));
            if (match is null)
            {
                DisposeAll(certificates);
                return Read.Fail(keyField, "The private key does not belong to the certificate.");
            }

            var withKey = certificates.Count(c => c.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki));
            if (withKey > 1)
            {
                DisposeAll(certificates);
                return Read.Fail(PkiFields.File, "The file contains more than one certificate with this key.");
            }

            return new Read(match, [.. certificates.Where(c => !ReferenceEquals(c, match))], key.ExportPkcs8PrivateKeyPem(), null);
        }
    }

    private static AsymmetricAlgorithm? LoadKey(string text, string? password, string keyField, string passwordField, out (string Field, string Message)? error)
    {
        error = null;
        var encrypted = text.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal);
        if (encrypted && string.IsNullOrEmpty(password))
        {
            error = (passwordField, "The key is encrypted: enter its password.");
            return null;
        }

        if (text.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal)
            && text.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal))
        {
            error = (keyField, "This encrypted key format is not supported. Convert it to PKCS#8 (openssl pkcs8 -topk8).");
            return null;
        }

        foreach (var create in new Func<AsymmetricAlgorithm>[] { () => RSA.Create(), () => ECDsa.Create() })
        {
            var key = create();
            try
            {
                if (encrypted)
                {
                    key.ImportFromEncryptedPem(text, password);
                }
                else
                {
                    key.ImportFromPem(text);
                }

                return key;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                key.Dispose();
            }
        }

        error = encrypted ? (passwordField, WrongKeyPassword) : (keyField, "The private key cannot be read.");
        return null;
    }

    private static X509Certificate2? TryReadDer(byte[] bytes)
    {
        try
        {
            return X509CertificateLoader.LoadCertificate(bytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static void DisposeAll(IEnumerable<X509Certificate2> certificates)
    {
        foreach (var certificate in certificates)
        {
            certificate.Dispose();
        }
    }

    private sealed record Read(X509Certificate2? Ca, List<X509Certificate2> Others, string? KeyPem, (string Field, string Message)? Error)
    {
        public static Read Fail(string field, string message) => new(null, [], null, (field, message));
    }
}
