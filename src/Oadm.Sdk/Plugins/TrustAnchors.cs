namespace Oadm.Sdk.Plugins;

/// <summary>
/// Extra certificate authorities the server trusts when it rates device certificates (the Certificate column of the
/// device grid), e.g. the CA of the PKI core plugin: a device certificate that chains to one of them counts as Trusted
/// without any change to the OS trust store. Self-signed device certificates stay self-signed. Never affects
/// certificate pinning (whether OADM talks to a device).
/// </summary>
public interface ITrustAnchors
{
    /// <summary>
    /// Replaces this plugin's set of trust anchors (DER encoded certificates, public parts only; an empty list removes
    /// them). The host keeps one set per plugin and trusts the union of all sets. Devices show the new rating with their
    /// next full refresh.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "C# plugins only; the name is part of the documented SDK.")]
    void Set(IReadOnlyList<byte[]> derCertificates);
}
