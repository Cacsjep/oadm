namespace Oadm.Core.Licensing;

/// <summary>
/// The public keys that sign OADM PRO licenses (docs/spec/licensing.md): base64 SubjectPublicKeyInfo of ECDSA P-256 keys.
/// The private keys never live in this repository. A new key is added here (the old ones stay, so their licenses keep
/// working); a license is valid when any of them verifies it.
/// </summary>
public static class ProLicenseKeys
{
    /// <summary>The keys by id, oldest first.</summary>
    public static IReadOnlyList<(string Id, string PublicKey)> All { get; } =
    [
        ("k1", "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFBTxEFuVuuvDRFEgI10Gu+eTfH/ZkaoU4no2c7Vu0f74weoqqO8uzoP0rMi3E2Q767OZSrOYkIkbGK8RBN5GZQ=="),
    ];
}
