using System.Collections.Frozen;
using Org.BouncyCastle.Tls;

namespace Oadm.Plugins.Radius.Protocol.Tls;

/// <summary>A TLS version of the "Minimum TLS version" setting. SSL is never offered.</summary>
public enum TlsVersion
{
    Tls10 = 10,
    Tls11 = 11,
    Tls12 = 12,
    Tls13 = 13,
}

/// <summary>Which cipher suite group a suite belongs to (docs/spec/pki.md "Kinds", 802.1X server policy).</summary>
public enum CipherSuiteGroup
{
    /// <summary>TLS 1.3 suites.</summary>
    Tls13,
    /// <summary>TLS 1.2 ECDHE with AES-GCM or ChaCha20-Poly1305 (RFC 9325 4.2).</summary>
    Tls12Aead,
    /// <summary>ECDHE with AES-CBC and SHA-1, which TLS 1.0 and 1.1 need; only listed when the minimum is lowered.</summary>
    LegacyCbc,
}

/// <summary>One cipher suite the server can offer.</summary>
/// <param name="Id">The IANA code point.</param>
/// <param name="Name">The IANA name, e.g. "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256".</param>
/// <param name="Group">Its group.</param>
/// <param name="Key">The server key it needs: null for TLS 1.3 suites (any key), else ECDSA or RSA.</param>
public sealed record TlsCipherSuiteInfo(int Id, string Name, CipherSuiteGroup Group, ServerKeyKind? Key);

/// <summary>
/// The cipher suites of Advanced. Only ECDHE key exchange (forward secrecy), never RC4, 3DES, NULL, export, anonymous or
/// RSA key exchange (RFC 9325 4.1). The secure default: the TLS 1.3 suites and the TLS 1.2 ECDHE AEAD suites
/// (RFC 9325 4.2); with a minimum of TLS 1.0 or 1.1 also the ECDHE AES-CBC SHA-1 suites those versions need.
/// </summary>
public static class TlsCipherSuites
{
    public static IReadOnlyList<TlsCipherSuiteInfo> All { get; } =
    [
        new(CipherSuite.TLS_AES_128_GCM_SHA256, "TLS_AES_128_GCM_SHA256", CipherSuiteGroup.Tls13, null),
        new(CipherSuite.TLS_AES_256_GCM_SHA384, "TLS_AES_256_GCM_SHA384", CipherSuiteGroup.Tls13, null),
        new(CipherSuite.TLS_CHACHA20_POLY1305_SHA256, "TLS_CHACHA20_POLY1305_SHA256", CipherSuiteGroup.Tls13, null),
        new(CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256, "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256", CipherSuiteGroup.Tls12Aead, ServerKeyKind.Ecdsa),
        new(CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384, "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384", CipherSuiteGroup.Tls12Aead, ServerKeyKind.Ecdsa),
        new(CipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256, "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256", CipherSuiteGroup.Tls12Aead, ServerKeyKind.Ecdsa),
        new(CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256, "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256", CipherSuiteGroup.Tls12Aead, ServerKeyKind.Rsa),
        new(CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384, "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384", CipherSuiteGroup.Tls12Aead, ServerKeyKind.Rsa),
        new(CipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256, "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256", CipherSuiteGroup.Tls12Aead, ServerKeyKind.Rsa),
        new(CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA, "TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA", CipherSuiteGroup.LegacyCbc, ServerKeyKind.Ecdsa),
        new(CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA, "TLS_ECDHE_ECDSA_WITH_AES_256_CBC_SHA", CipherSuiteGroup.LegacyCbc, ServerKeyKind.Ecdsa),
        new(CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA, "TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA", CipherSuiteGroup.LegacyCbc, ServerKeyKind.Rsa),
        new(CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA, "TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA", CipherSuiteGroup.LegacyCbc, ServerKeyKind.Rsa),
    ];

    private static readonly FrozenDictionary<int, TlsCipherSuiteInfo> ById = All.ToFrozenDictionary(s => s.Id);

    /// <summary>The suite of <paramref name="id"/>, or null for one the server never offers.</summary>
    public static TlsCipherSuiteInfo? Find(int id) => ById.GetValueOrDefault(id);

    /// <summary>The suites the page lists for <paramref name="minimum"/>: the old CBC suites only for TLS 1.0 or 1.1.</summary>
    public static IReadOnlyList<TlsCipherSuiteInfo> Listed(TlsVersion minimum) =>
        [.. All.Where(s => s.Group != CipherSuiteGroup.LegacyCbc || minimum < TlsVersion.Tls12)];

    /// <summary>The default set for <paramref name="minimum"/> ("Reset to default"; the CBC suites checked when the minimum is lowered).</summary>
    public static IReadOnlySet<int> DefaultFor(TlsVersion minimum) => Listed(minimum).Select(s => s.Id).ToHashSet();

    /// <summary>True when <paramref name="suite"/> can be negotiated in <paramref name="version"/>.</summary>
    public static bool Fits(TlsCipherSuiteInfo suite, TlsVersion version)
    {
        ArgumentNullException.ThrowIfNull(suite);
        return suite.Group switch
        {
            CipherSuiteGroup.Tls13 => version == TlsVersion.Tls13,
            CipherSuiteGroup.Tls12Aead => version == TlsVersion.Tls12,
            _ => version is TlsVersion.Tls10 or TlsVersion.Tls11 or TlsVersion.Tls12,
        };
    }
}

/// <summary>
/// The TLS part of Advanced: the minimum version (the maximum is always TLS 1.3) and the checked cipher suites.
/// </summary>
/// <param name="MinimumVersion">TLS 1.2 by default; 1.0 and 1.1 only for old devices (RFC 8996).</param>
/// <param name="CipherSuites">The checked suites (IANA ids).</param>
public sealed record TlsSettings(TlsVersion MinimumVersion, IReadOnlySet<int> CipherSuites)
{
    /// <summary>The refusal when no checked suite can be used (the page's text below the list).</summary>
    public const string NoUsableSuite = "Check at least one cipher suite for the chosen TLS versions.";

    /// <summary>TLS 1.2 and 1.3 with the secure default suites.</summary>
    public static TlsSettings Default { get; } = new(TlsVersion.Tls12, TlsCipherSuites.DefaultFor(TlsVersion.Tls12));

    /// <summary>The versions offered, newest first.</summary>
    public IReadOnlyList<TlsVersion> Versions =>
        [.. new[] { TlsVersion.Tls13, TlsVersion.Tls12, TlsVersion.Tls11, TlsVersion.Tls10 }.Where(v => v >= MinimumVersion)];

    /// <summary>
    /// The suites offered for a server key of <paramref name="key"/>, in the catalogue's order: checked, known, listed for
    /// the minimum (CBC only below TLS 1.2), usable in one of the offered versions and with that key.
    /// </summary>
    public IReadOnlyList<int> Offered(ServerKeyKind key) =>
        [.. TlsCipherSuites.Listed(MinimumVersion)
            .Where(s => CipherSuites.Contains(s.Id) && (s.Key is null || s.Key == key) && Versions.Any(v => TlsCipherSuites.Fits(s, v)))
            .Select(s => s.Id)];

    /// <summary>
    /// The versions offered with a server key of <paramref name="key"/>: only those for which a checked suite can be
    /// negotiated (TLS 1.3 without a checked TLS 1.3 suite would only fail with handshake_failure).
    /// </summary>
    public IReadOnlyList<TlsVersion> VersionsFor(ServerKeyKind key)
    {
        var offered = Offered(key).Select(id => TlsCipherSuites.Find(id)!).ToList();
        return [.. Versions.Where(v => offered.Any(s => TlsCipherSuites.Fits(s, v)))];
    }

    /// <summary>
    /// Null when the settings can be used with a server key of <paramref name="key"/>, else the reason for the page: an
    /// unknown suite, or no checked suite the key can use in the chosen versions (only ECDHE_RSA suites with an ECDSA key).
    /// </summary>
    public string? Validate(ServerKeyKind key)
    {
        if (Validate() is { } error)
        {
            return error;
        }

        return VersionsFor(key).Count > 0 ? null : NoUsableSuite;
    }

    /// <summary>
    /// Null when the settings can be used, else the reason for the page: an unknown suite (RC4, 3DES, NULL, export, RSA key
    /// exchange are never known) or no checked suite usable in the chosen versions.
    /// </summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(MinimumVersion))
        {
            return "Choose a TLS version.";
        }

        if (CipherSuites.Any(id => TlsCipherSuites.Find(id) is null))
        {
            return "Unknown cipher suite.";
        }

        var usable = TlsCipherSuites.Listed(MinimumVersion).Any(s => CipherSuites.Contains(s.Id) && Versions.Any(v => TlsCipherSuites.Fits(s, v)));
        return usable ? null : NoUsableSuite;
    }
}
