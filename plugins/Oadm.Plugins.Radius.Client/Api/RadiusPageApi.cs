using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Radius.Client;

/// <summary>
/// What the page asks of the server part beyond the contract in <c>Api/RadiusApi.cs</c> (docs/spec/radius-server.md
/// "Code (page)"). Each is optional for the page: without <see cref="ListSites"/> it takes the sites of the devices and the
/// switches it knows; without <see cref="ListCertificates"/> the certificate choice of Revoke says it cannot read.
/// </summary>
public static class RadiusPageMethods
{
    /// <summary>() -> <see cref="SiteList"/>: every site of the server (the Site of a switch, VLAN rules by site).</summary>
    public const string ListSites = "listSites";

    /// <summary><see cref="CertificateListQuery"/> -> <see cref="CertificateListPage"/>: one page of PKI's list of issued certificates.</summary>
    public const string ListCertificates = "listCertificates";

    /// <summary><see cref="SerialRequest"/> -> <see cref="LogCertificate"/>: one issued certificate (DER, base64; empty when OADM kept none).</summary>
    public const string GetIssuedCertificate = "getIssuedCertificate";
}

/// <summary>Limits of the page.</summary>
public static class RadiusPageLimits
{
    /// <summary>The most open sessions (the session table's limit).</summary>
    public const int MaxSessions = 10_000;

    /// <summary>Ended sessions leave the Sessions tab after this (also without the server's removal).</summary>
    public static readonly TimeSpan EndedSessionShown = TimeSpan.FromSeconds(10);
}

/// <summary>The answer of <see cref="RadiusPageMethods.ListSites"/>.</summary>
public sealed record SiteList(IReadOnlyList<SiteRef> Sites);

/// <summary>The request of <see cref="RadiusPageMethods.ListCertificates"/> (PKI's filters, search and one page).</summary>
public sealed record CertificateListQuery
{
    public IssuedCertificateState? State { get; init; }

    public string? Purpose { get; init; }

    public string? Source { get; init; }

    public string? Search { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = IssuedCertificateQuery.MaxLimit;
}

/// <summary>The answer of <see cref="RadiusPageMethods.ListCertificates"/>.</summary>
public sealed record CertificateListPage
{
    public IReadOnlyList<IssuedCertificateInfo> Rows { get; init; } = [];

    public int Total { get; init; }

    public int Offset { get; init; }
}

/// <summary>The request of <see cref="RadiusPageMethods.GetIssuedCertificate"/>.</summary>
public sealed record SerialRequest(string SerialNumber);

/// <summary>The states of <see cref="SessionRow.State"/> the page colors (camelCase names of the protocol's states).</summary>
public static class SessionStates
{
    public const string Authenticated = "authenticated";
    public const string Failed = "failed";
    public const string TimedOut = "timedOut";
    public const string Abandoned = "abandoned";
}
