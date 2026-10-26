using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Device;

/// <summary>Which certificate API a device is managed with (<see cref="Tasks.PkiCompatibility.Decide"/>).</summary>
public enum CertificateApiKind
{
    /// <summary>REST <c>/config/rest/cert/v1</c> (<see cref="CertApi"/>): released from AXIS OS 11.11.135.</summary>
    Rest,

    /// <summary>SOAP at <c>/vapix/services</c> (<see cref="CertSoapApi"/>): AXIS OS 6.53 and later without the released REST API.</summary>
    Soap,
}

/// <summary>The outcome of installing a signed certificate.</summary>
/// <param name="Tidy">False when the SOAP key placeholder could not be deleted (the certificate is installed).</param>
/// <param name="WithChain">The device took the certificate with its chain.</param>
/// <param name="ChainRefusal">Why the device got the certificate alone after it refused the chain; null when it was not refused.</param>
public sealed record SignedInstall(bool Tidy, bool WithChain, string? ChainRefusal);

/// <summary>
/// The device's certificate store on either API: the same operations for the PKI tasks whichever API the device has. Keys
/// are always made on the device.
/// </summary>
public static class DeviceCertificates
{
    /// <summary>The suffix of the placeholder that holds a new key on the SOAP path until the signed certificate is loaded.</summary>
    public const string KeySuffix = " key";

    /// <summary>How long the placeholder certificate of a new SOAP key is valid (it is deleted right after the signed one is loaded).</summary>
    public static readonly TimeSpan PlaceholderValidity = TimeSpan.FromDays(365);

    public static Task<IReadOnlyList<DeviceCertificate>> ListAsync(IVapixClient vapix, CertificateApiKind api, CancellationToken ct) =>
        api == CertificateApiKind.Rest ? CertApi.ListCertificatesAsync(vapix, ct) : CertSoapApi.ListCertificatesAsync(vapix, ct);

    public static Task<IReadOnlyList<DeviceCertificate>> ListCaAsync(IVapixClient vapix, CertificateApiKind api, CancellationToken ct) =>
        api == CertificateApiKind.Rest ? CertApi.ListCaCertificatesAsync(vapix, ct) : CertSoapApi.ListCaCertificatesAsync(vapix, ct);

    public static Task AddCaAsync(IVapixClient vapix, CertificateApiKind api, string alias, string pem, CancellationToken ct) =>
        api == CertificateApiKind.Rest ? CertApi.AddCaCertificateAsync(vapix, alias, pem, ct) : CertSoapApi.LoadCaCertificateAsync(vapix, alias, pem, ct);

    /// <summary>Deletes a certificate and its key.</summary>
    public static Task DeleteAsync(IVapixClient vapix, CertificateApiKind api, string alias, CancellationToken ct) =>
        api == CertificateApiKind.Rest ? CertApi.DeleteCertificateAsync(vapix, alias, ct) : CertSoapApi.DeleteAsync(vapix, alias, "Delete certificate", ct);

    public static Task DeleteCaAsync(IVapixClient vapix, CertificateApiKind api, string alias, CancellationToken ct) =>
        api == CertificateApiKind.Rest ? CertApi.DeleteCaCertificateAsync(vapix, alias, ct) : CertSoapApi.DeleteAsync(vapix, alias, "Delete CA certificate", ct);

    /// <summary>Installs a certificate with its key from PKCS#12 (base64); returns the alias the device gave it.</summary>
    public static async Task<string> InstallPkcs12Async(IVapixClient vapix, CertificateApiKind api, string alias, string pkcs12Base64, string password, CancellationToken ct)
    {
        if (api == CertificateApiKind.Rest)
        {
            await CertApi.InstallFromPkcs12Async(vapix, alias, pkcs12Base64, password, ct).ConfigureAwait(false);
            return alias;
        }

        return await CertSoapApi.LoadPkcs12Async(vapix, alias, pkcs12Base64, password, ct).ConfigureAwait(false);
    }

    /// <summary>The alias that holds the new key until <see cref="InstallSignedAsync"/>: the alias itself on REST, "&lt;alias&gt; key" on SOAP.</summary>
    public static string KeyAlias(CertificateApiKind api, string alias) => api == CertificateApiKind.Rest ? alias : alias + KeySuffix;

    /// <summary>Creates a key pair with a self-signed placeholder under <see cref="KeyAlias"/>. SOAP makes RSA 2048 only.</summary>
    public static Task CreateKeyAsync(IVapixClient vapix, CertificateApiKind api, string alias, Ca.CertificateNames names, string keyType, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (api == CertificateApiKind.Rest)
        {
            return CertApi.CreateCertificateAsync(vapix, alias, names.Subject, names.DeviceSubjectAltNames, keyType, ct);
        }

        if (keyType != CertApi.KeyType)
        {
            throw new DeviceNotCompatibleException("The device cannot create an ECC key. Choose a policy with RSA. Nothing was changed.");
        }

        return CertSoapApi.CreateCertificateAsync(vapix, KeyAlias(api, alias), names.CommonName, now.AddDays(-1), now + PlaceholderValidity, ct);
    }

    /// <summary>The certificate request (PEM) for the key made by <see cref="CreateKeyAsync"/>.</summary>
    public static Task<string> GetCsrAsync(IVapixClient vapix, CertificateApiKind api, string alias, Ca.CertificateNames names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        return api == CertificateApiKind.Rest
            ? CertApi.GetCsrAsync(vapix, alias, names.Subject, names.DeviceSubjectAltNames, ct)
            : CertSoapApi.GetCsrAsync(vapix, KeyAlias(api, alias), names.CommonName, ct);
    }

    /// <summary>
    /// Installs the signed certificate under <paramref name="alias"/>: REST replaces the placeholder of the same alias; SOAP
    /// loads it under the alias (the device finds the key) and then deletes the placeholder. Returns false when that
    /// placeholder could not be deleted (the certificate is installed).
    /// </summary>
    public static async Task<bool> InstallSignedAsync(IVapixClient vapix, CertificateApiKind api, string alias, string pem, CancellationToken ct) =>
        (await InstallSignedAsync(vapix, api, alias, pem, [], ct).ConfigureAwait(false)).Tidy;

    /// <summary>
    /// <see cref="InstallSignedAsync(IVapixClient, CertificateApiKind, string, string, CancellationToken)"/> with the issuer
    /// chain (decision 2026-10-26: a certificate of an intermediate CA goes with its chain; <paramref name="chainPems"/> the
    /// intermediate CAs from the issuer up, never a root; empty: the certificate alone, exactly as before). REST sends the
    /// certificate followed by the chain in <c>certificate</c>; SOAP loads the certificate followed by the chain as one DER
    /// sequence and then reads the entry back. A device that refuses the chain (an error answer or a SOAP fault), or SOAP that
    /// keeps anything but the certificate itself, gets the certificate alone; <see cref="SignedInstall.ChainRefusal"/> says why.
    /// </summary>
    public static async Task<SignedInstall> InstallSignedAsync(
        IVapixClient vapix, CertificateApiKind api, string alias, string pem, IReadOnlyList<string> chainPems, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chainPems);
        string? refusal = null;
        var withChain = false;
        if (api == CertificateApiKind.Rest)
        {
            if (chainPems.Count > 0)
            {
                try
                {
                    await CertApi.PatchCertificateAsync(vapix, alias, WithChain(pem, chainPems), ct).ConfigureAwait(false);
                    withChain = true;
                }
                catch (PkiDeviceException ex)
                {
                    refusal = ex.Message;
                }
            }

            if (!withChain)
            {
                await CertApi.PatchCertificateAsync(vapix, alias, pem, ct).ConfigureAwait(false);
            }

            return new SignedInstall(true, withChain, refusal);
        }

        if (chainPems.Count > 0)
        {
            try
            {
                await CertSoapApi.LoadCertificateChainAsync(vapix, alias, pem, chainPems, ct).ConfigureAwait(false);
                withChain = true;
            }
            catch (PkiDeviceException ex)
            {
                refusal = ex.Message;
            }

            if (withChain && await KeptOtherThanAsync(vapix, alias, pem, ct).ConfigureAwait(false))
            {
                // Taken, but not kept as the certificate itself: removed again (the key stays with its placeholder) and loaded alone.
                withChain = false;
                refusal = "Install certificate: the device did not keep the certificate as sent.";
                await CertSoapApi.DeleteAsync(vapix, alias, "Delete certificate", ct).ConfigureAwait(false);
            }
        }

        if (!withChain)
        {
            await CertSoapApi.LoadCertificateAsync(vapix, alias, pem, ct).ConfigureAwait(false);
        }

        try
        {
            await CertSoapApi.DeleteAsync(vapix, KeyAlias(api, alias), "Delete certificate", ct).ConfigureAwait(false);
            return new SignedInstall(true, withChain, refusal);
        }
        catch (PkiDeviceException)
        {
            return new SignedInstall(false, withChain, refusal);
        }
    }

    /// <summary>The certificate followed by its chain, one PEM block each.</summary>
    public static string WithChain(string pem, IReadOnlyList<string> chainPems)
    {
        ArgumentNullException.ThrowIfNull(pem);
        ArgumentNullException.ThrowIfNull(chainPems);
        return string.Concat(new[] { pem }.Concat(chainPems).Select(p => p.TrimEnd() + "\n"));
    }

    /// <summary>True when the SOAP entry <paramref name="alias"/> is missing or holds anything but <paramref name="pem"/> (read only).</summary>
    private static async Task<bool> KeptOtherThanAsync(IVapixClient vapix, string alias, string pem, CancellationToken ct)
    {
        var stored = (await CertSoapApi.ListCertificatesAsync(vapix, ct).ConfigureAwait(false)).FirstOrDefault(c => string.Equals(c.Alias, alias, StringComparison.Ordinal));
        if (stored is null || string.IsNullOrEmpty(stored.Pem))
        {
            return true;
        }

        try
        {
            return !CertSoapApi.Der(stored.Pem).AsSpan().SequenceEqual(CertSoapApi.Der(pem));
        }
        catch (FormatException)
        {
            return true;
        }
    }

    /// <summary>Removes an unfinished key again (the placeholder on SOAP, the alias on REST).</summary>
    public static Task DeleteUnfinishedKeyAsync(IVapixClient vapix, CertificateApiKind api, string alias, CancellationToken ct) =>
        DeleteAsync(vapix, api, KeyAlias(api, alias), ct);
}
