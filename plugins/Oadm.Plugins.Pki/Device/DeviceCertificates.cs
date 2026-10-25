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
    public static async Task<bool> InstallSignedAsync(IVapixClient vapix, CertificateApiKind api, string alias, string pem, CancellationToken ct)
    {
        if (api == CertificateApiKind.Rest)
        {
            await CertApi.PatchCertificateAsync(vapix, alias, pem, ct).ConfigureAwait(false);
            return true;
        }

        await CertSoapApi.LoadCertificateAsync(vapix, alias, pem, ct).ConfigureAwait(false);
        try
        {
            await CertSoapApi.DeleteAsync(vapix, KeyAlias(api, alias), "Delete certificate", ct).ConfigureAwait(false);
            return true;
        }
        catch (PkiDeviceException)
        {
            return false;
        }
    }

    /// <summary>Removes an unfinished key again (the placeholder on SOAP, the alias on REST).</summary>
    public static Task DeleteUnfinishedKeyAsync(IVapixClient vapix, CertificateApiKind api, string alias, CancellationToken ct) =>
        DeleteAsync(vapix, api, KeyAlias(api, alias), ct);
}
