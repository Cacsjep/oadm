using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Oadm.Plugins.Pki.Ca;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Certificates;

/// <summary>
/// The page methods of the Certificates card (docs/spec/pki.md "Certificates"): Create, Sign request, Import, Delete
/// (<c>InvokeAsync</c>) and the file of Export (<c>OpenDownloadAsync</c>). All of them: Admin with "Manage the certificate
/// authority" and all devices, audited by the host.
/// </summary>
public static class CertificateMethods
{
    /// <summary><see cref="CreateCertificateRequest"/> -> <see cref="CertificateActionReply"/>: OADM makes the key and the certificate.</summary>
    public const string Create = "createCertificate";

    /// <summary><see cref="SignCertificateRequest"/> -> <see cref="CertificateActionReply"/>: the OADM CA signs a request of another system.</summary>
    public const string Sign = "signRequest";

    /// <summary><see cref="ImportCertificateRequest"/> -> <see cref="CertificateActionReply"/>: a certificate, or a PKCS#12 with its key.</summary>
    public const string Import = "importCertificate";

    /// <summary><see cref="DeleteCertificatesRequest"/> -> <see cref="DeleteCertificatesReply"/>: certificates nothing uses (all or none).</summary>
    public const string Delete = "deleteCertificates";

    /// <summary>Download with <see cref="DownloadCertificateRequest"/>: PEM, DER or PKCS#12 with the key.</summary>
    public const string Download = "downloadCertificate";

    /// <summary>Every method of the card.</summary>
    public static IReadOnlyList<string> All { get; } = [Create, Sign, Import, Delete, Download];
}

/// <summary>The fields of the dialogs (the keys of <see cref="CertificateActionReply.Errors"/>: the property names of the dialogs).</summary>
public static class CertificateFields
{
    public const string Use = "Use";
    public const string Policy = "Policy";
    public const string Name = "Name";
    public const string Addresses = "Addresses";
    public const string File = "File";
    public const string Password = "Password";
}

/// <summary>The formats of Export.</summary>
public static class CertificateFormats
{
    public const string Pem = "pem";
    public const string Der = "der";
    public const string Pkcs12 = "pkcs12";
}

/// <summary>Who an OADM server certificate belongs to (<see cref="DeviceCertificateRequest.Source"/> of a request without a device).</summary>
public static class ServerCertificateSources
{
    /// <summary>The 802.1X server's own certificate.</summary>
    public const string Dot1x = "802.1X server";

    /// <summary>The EST server's own certificates (the source the EST server names).</summary>
    public const string Est = IssuedCertificateSources.Est;

    /// <summary>"802.1X server" or "EST server" (Used by).</summary>
    public static string NameOf(string? source) => source == Est ? "EST server" : Dot1x;
}

/// <summary>The payload of <see cref="CertificateMethods.Create"/>.</summary>
/// <param name="Use">HTTPS or IEEE 802.1X (<see cref="DeviceCertificatePurposes"/>).</param>
/// <param name="PolicyId">An issuing policy of the use's kind.</param>
/// <param name="Name">The certificate's name (common name).</param>
/// <param name="Addresses">Host names and IP addresses, separated by commas, spaces or lines.</param>
public sealed record CreateCertificateRequest(string? Use, Guid PolicyId, string? Name, string? Addresses);

/// <summary>The payload of <see cref="CertificateMethods.Sign"/>: the request file (PKCS#10, PEM or DER) and an issuing policy.</summary>
public sealed record SignCertificateRequest(Guid PolicyId, string? FileBase64, string? FileName);

/// <summary>The payload of <see cref="CertificateMethods.Import"/>: a certificate (PEM, DER) or a PKCS#12 file with its password.</summary>
public sealed record ImportCertificateRequest(string? FileBase64, string? FileName, string? Password);

/// <summary>The answer of Create, Sign request and Import: the new row's id, or the errors below their fields.</summary>
public sealed record CertificateActionReply
{
    /// <summary>The id of the new row (<see cref="IssuedCertificateInfo.Id"/>); null when refused.</summary>
    public string? Id { get; init; }

    /// <summary>The certificate's name.</summary>
    public string? Name { get; init; }

    /// <summary>Errors by field (<see cref="CertificateFields"/>).</summary>
    public IReadOnlyDictionary<string, string>? Errors { get; init; }

    /// <summary>An error of no field.</summary>
    public string? Error { get; init; }

    public bool Ok => Id is not null;
}

/// <summary>The payload of <see cref="CertificateMethods.Delete"/>: row ids (<see cref="IssuedCertificateInfo.Id"/> or the serial number).</summary>
public sealed record DeleteCertificatesRequest(IReadOnlyList<string> Ids);

/// <summary>What Delete did: every chosen row, or nothing and why.</summary>
public sealed record DeleteCertificatesReply
{
    public int Deleted { get; init; }

    public string? Error { get; init; }
}

/// <summary>The payload of the Export download.</summary>
/// <param name="Id">The row's id (<see cref="IssuedCertificateInfo.Id"/> or the serial number).</param>
/// <param name="Format">One of <see cref="CertificateFormats"/>.</param>
/// <param name="Password">PKCS#12 only: the file's password (never stored, never logged).</param>
public sealed record DownloadCertificateRequest(string Id, string Format, string? Password = null);

/// <summary>The rules and texts of the card, the same on the server and the page.</summary>
public static class CertificateRules
{
    /// <summary>Most certificates created, signed or imported here.</summary>
    public const int MaxLocalCertificates = 1000;

    /// <summary>Most addresses of one certificate.</summary>
    public const int MaxAddresses = 20;

    public const string ChooseUse = "Choose HTTPS or IEEE 802.1X.";

    public const string ChoosePolicy = "Choose an issuing policy.";

    public const string PolicyGone = "The chosen policy no longer exists. Choose another one.";

    public const string NoCa = "There is no certificate authority yet.";

    public const string NoKeyStore = "This server cannot store keys safely.";

    public const string TooMany = "At most 1,000 certificates can be created, signed or imported. Delete one first.";

    public const string AlreadyListed = "This certificate is already in the list.";

    public const string NotListed = IssuedCertificateMessages.NotListed;

    public const string NeedsAddress = "Enter at least one address.";

    /// <summary>The uses of Create and Sign request (an issuing policy of each).</summary>
    public static IReadOnlyList<string> Uses { get; } = [DeviceCertificatePurposes.Https, DeviceCertificatePurposes.Dot1x];

    /// <summary>The issuing policy kind of a use.</summary>
    public static string? PolicyKindOf(string? use) => use switch
    {
        DeviceCertificatePurposes.Https => Policies.PolicyKinds.HttpsCertificate,
        DeviceCertificatePurposes.Dot1x => Policies.PolicyKinds.Dot1xCertificate,
        _ => null,
    };

    /// <summary>The use of an issuing policy kind (HTTPS, IEEE 802.1X); null for the others.</summary>
    public static string? UseOf(string? policyKind) => policyKind switch
    {
        Policies.PolicyKinds.HttpsCertificate => DeviceCertificatePurposes.Https,
        Policies.PolicyKinds.Dot1xCertificate => DeviceCertificatePurposes.Dot1x,
        _ => null,
    };

    /// <summary>The name of Create: 1 to 64 characters.</summary>
    public static string? Name(string? name) => PkiValidation.CommonName(name);

    /// <summary>The addresses of Create: host names and IP addresses; at least one for HTTPS.</summary>
    public static string? Addresses(string? text, string? use) => ParseAddresses(text, use).Error;

    /// <summary>The IP addresses and host names of <paramref name="text"/>, or the first problem.</summary>
    public static (IReadOnlyList<string> Ips, IReadOnlyList<string> Dns, string? Error) ParseAddresses(string? text, string? use)
    {
        var ips = new List<string>();
        var dns = new List<string>();
        foreach (var part in (text ?? string.Empty).Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim().Trim('[', ']');
            if (IPAddress.TryParse(name, out var ip) && ip.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            {
                if (!ips.Contains(ip.ToString()))
                {
                    ips.Add(ip.ToString());
                }
            }
            else if (CertificateNames.IsPlainHostName(name) && !name.All(c => char.IsAsciiDigit(c) || c == '.'))
            {
                if (!dns.Contains(name.TrimEnd('.'), StringComparer.OrdinalIgnoreCase))
                {
                    dns.Add(name.TrimEnd('.'));
                }
            }
            else
            {
                return ([], [], $"\"{part}\" is no host name or IP address.");
            }
        }

        if (ips.Count + dns.Count > MaxAddresses)
        {
            return ([], [], string.Create(CultureInfo.InvariantCulture, $"At most {MaxAddresses} addresses."));
        }

        if (ips.Count + dns.Count == 0 && use == DeviceCertificatePurposes.Https)
        {
            return ([], [], NeedsAddress);
        }

        return (ips, dns, null);
    }

    /// <summary>"The certificate of 10.0.0.48 is still valid. Revoke it first." (Delete of a device certificate).</summary>
    public static string StillValid(string name) => $"The certificate of {name} is still valid. Revoke it first.";

    /// <summary>"Used by the 802.1X server." (Delete of a server certificate in use).</summary>
    public static string UsedBy(string server) => $"Used by the {server}.";

    public const string RemoveCaThere = "Remove CAs in Certificate authorities.";

    public const string NothingDeleted = " Nothing was deleted.";

    public const string NoKeyHere = "OADM holds no key of this certificate. Export PEM or DER.";

    public const string NoCaKey = "The CA key is never exported on its own. Use Back up.";

    public const string ChooseFormat = "Choose PEM, DER or PKCS#12.";
}
