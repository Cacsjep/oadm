using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Oadm.Plugins.Pki.Ca;

namespace Oadm.Plugins.Pki;

/// <summary>
/// One certificate of a CA certificate file of "Install CA certificates". <see cref="Problem"/> is the reason it cannot be
/// installed, <see cref="ProblemLabel"/> its short status ("Not a CA certificate", "Expired", "Not valid yet").
/// </summary>
public sealed record CaFileCertificate(
    string Fingerprint,
    string Pem,
    string Name,
    string IssuedBy,
    DateTime NotAfterUtc,
    string? Problem,
    string? ProblemLabel = null)
{
    /// <summary>A CA certificate that is valid now.</summary>
    public bool IsUsable => Problem is null;
}

/// <summary>What one chosen file holds: its certificates, or why it cannot be used.</summary>
public sealed record CaFileContents(IReadOnlyList<CaFileCertificate> Certificates, string? Error);

/// <summary>
/// Reads CA certificate files for "Install CA certificates" (shared by the dialog and the task): PEM (.crt, .pem, .cer, also
/// bundles with several certificates) and DER (.cer, .crt, .der). Each certificate must be a CA certificate (BasicConstraints
/// CA, keyCertSign when KeyUsage is present) and valid now.
/// </summary>
public static class CaCertificateFiles
{
    /// <summary>Largest file (a full public CA bundle is about 220 KB).</summary>
    public const int MaxBytes = CertificateFiles.MaxBytes;

    /// <summary>
    /// At most this many CA certificates per run: one step each, and a task has at most 200 steps
    /// (<c>TaskStepList.MaxSteps</c>).
    /// </summary>
    public const int MaxCertificates = 150;

    public const string NotACertificate = "The file is not a certificate (PEM or DER).";
    public const string NoCertificate = "The file contains no certificate.";

    /// <summary>Reads one file; <see cref="CaFileContents.Error"/> says why it cannot be used at all.</summary>
    public static CaFileContents Read(byte[] bytes, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            return new CaFileContents([], "The file is empty.");
        }

        if (bytes.Length > MaxBytes)
        {
            return new CaFileContents([], "The file is larger than 1 MB.");
        }

        List<X509Certificate2> certificates;
        try
        {
            if (CaCertificates.LooksLikePem(bytes))
            {
                certificates = CaCertificates.ReadPemCertificates(Encoding.ASCII.GetString(bytes));
            }
            else
            {
                certificates = [X509CertificateLoader.LoadCertificate(bytes)];
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return new CaFileContents([], NotACertificate);
        }

        try
        {
            return certificates.Count == 0
                ? new CaFileContents([], NoCertificate)
                : new CaFileContents([.. certificates.Select(c => Describe(c, nowUtc))], null);
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>One PEM certificate of the task payload; throws <see cref="InvalidOperationException"/> when it is not exactly one.</summary>
    public static CaFileCertificate ReadPem(string pem, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(pem);
        var contents = Read(Encoding.ASCII.GetBytes(pem), nowUtc);
        if (contents.Error is not null || contents.Certificates.Count != 1)
        {
            throw new InvalidOperationException(contents.Error ?? "Expected exactly one certificate.");
        }

        return contents.Certificates[0];
    }

    /// <summary>Name, issuer, validity and the CA / expiry check of one certificate.</summary>
    public static CaFileCertificate Describe(X509Certificate2 certificate, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var notAfter = certificate.NotAfter.ToUniversalTime();
        var notBefore = certificate.NotBefore.ToUniversalTime();
        var issuer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        string? problem = null;
        string? label = null;
        if (!CaCertificates.IsCa(certificate, out var caProblem))
        {
            (problem, label) = (caProblem, "Not a CA certificate");
        }
        else if (notAfter < nowUtc)
        {
            (problem, label) = (string.Create(CultureInfo.InvariantCulture, $"The certificate expired on {notAfter:yyyy-MM-dd}."), "Expired");
        }
        else if (notBefore > nowUtc)
        {
            (problem, label) = (string.Create(CultureInfo.InvariantCulture, $"The certificate is valid from {notBefore:yyyy-MM-dd}."), "Not valid yet");
        }

        return new CaFileCertificate(
            CaCertificates.Fingerprint(certificate),
            CaCertificates.ToPem(certificate),
            CaCertificates.CommonName(certificate),
            string.IsNullOrWhiteSpace(issuer) ? certificate.Issuer : issuer,
            notAfter,
            problem,
            label);
    }

    /// <summary>"3F:A2:B4:C1:9D:0E:77:12…" (the first 8 bytes) for the table; the tooltip shows the whole one.</summary>
    public static string ShortFingerprint(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return hex.Length <= 16 ? CaCertificates.ColonFingerprint(hex) : CaCertificates.ColonFingerprint(hex[..16]) + "…";
    }
}
